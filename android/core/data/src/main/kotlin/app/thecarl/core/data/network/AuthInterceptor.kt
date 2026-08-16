package app.thecarl.core.data.network

import app.thecarl.core.domain.security.CredentialStore
import app.thecarl.core.domain.security.StoredCredentials
import java.time.Instant
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import okhttp3.Interceptor
import okhttp3.Response

/**
 * Why authentication failed, when it did.
 *
 * <p>Distinguishing these matters: a refreshable expiry is routine, while revocation is
 * terminal and must stop the client retrying forever against a device that will never be
 * readmitted.</p>
 */
enum class AuthFailure {
    /** Refresh succeeded; the request can proceed. */
    NONE,

    /** No credentials stored. The user must log in. */
    NOT_AUTHENTICATED,

    /**
     * The session, device or token family was revoked server-side. Local credentials are
     * cleared; queued work is preserved and blocked rather than retried.
     */
    REVOKED
}

/**
 * Attaches the access token and refreshes it once on 401.
 *
 * <p><b>Single-flight refresh.</b> When a batch of requests fails together — which is what
 * happens when a token expires while the outbox is draining — every one of them would
 * otherwise try to refresh. That both stampedes the server and, worse, races on refresh-token
 * rotation: the backend revokes an entire token family when a rotated token is reused, so
 * concurrent refreshes would log the user out. A mutex plus a re-read of stored credentials
 * means only the first caller refreshes and the rest pick up the result.</p>
 *
 * <p>Tokens are read from the [CredentialStore] on each request and never cached in a field,
 * never logged, and never placed anywhere but the Authorization header.</p>
 */
class AuthInterceptor(
    private val credentialStore: CredentialStore,
    private val tokenRefresher: TokenRefresher,
    private val onAuthFailure: (AuthFailure) -> Unit = {}
) : Interceptor {

    private val refreshMutex = Mutex()

    override fun intercept(chain: Interceptor.Chain): Response {
        val credentials = runBlocking { credentialStore.read() }
            ?: run {
                onAuthFailure(AuthFailure.NOT_AUTHENTICATED)
                return chain.proceed(chain.request())
            }

        // Refresh proactively when the token is about to expire, so a request is not started
        // with a token that dies mid-flight.
        val usable = if (credentials.accessTokenNeedsRefresh()) {
            runBlocking { refreshOnce(credentials) } ?: credentials
        } else {
            credentials
        }

        val response = chain.proceed(chain.request().withBearer(usable.accessToken))

        if (response.code != HTTP_UNAUTHORIZED) {
            return response
        }

        // The server disagrees about the token's validity. Refresh once, then retry once.
        // Exactly once: an unbounded loop against a revoked session is a battery drain that
        // can never succeed.
        val refreshed = runBlocking { refreshOnce(usable) }
            ?: return response

        response.close()
        return chain.proceed(chain.request().withBearer(refreshed.accessToken))
    }

    /**
     * Refreshes under a mutex, re-reading stored credentials first.
     *
     * <p>The re-read is what makes this single-flight: a caller that waited on the mutex
     * finds the token another caller already obtained and returns it instead of spending the
     * refresh token a second time. Reusing a rotated refresh token would revoke the whole
     * family.</p>
     */
    private suspend fun refreshOnce(attempted: StoredCredentials): StoredCredentials? =
        refreshMutex.withLock {
            val current = credentialStore.read() ?: run {
                onAuthFailure(AuthFailure.NOT_AUTHENTICATED)
                return@withLock null
            }

            // Another caller already refreshed while this one waited.
            if (current.accessToken != attempted.accessToken) {
                return@withLock current
            }

            when (val result = tokenRefresher.refresh(current.refreshToken)) {
                is RefreshResult.Success -> {
                    val updated = current.copy(
                        accessToken = result.accessToken,
                        refreshToken = result.refreshToken,
                        accessTokenExpiresAtUtcMillis = result.expiresAtUtcMillis
                    )
                    credentialStore.save(updated)
                    updated
                }

                RefreshResult.Revoked -> {
                    // The device or session was revoked. Local credentials are useless, so
                    // they are cleared — but nothing in the outbox is touched. Financial work
                    // survives a revocation; it simply cannot be submitted until a fresh
                    // login re-establishes authority.
                    credentialStore.clear()
                    onAuthFailure(AuthFailure.REVOKED)
                    null
                }

                RefreshResult.TransientFailure -> null
            }
        }

    private fun okhttp3.Request.withBearer(token: String) =
        newBuilder().header("Authorization", "Bearer $token").build()

    companion object {
        private const val HTTP_UNAUTHORIZED = 401
    }
}

/** Outcome of a refresh attempt. */
sealed interface RefreshResult {
    data class Success(
        val accessToken: String,
        val refreshToken: String,
        val expiresAtUtcMillis: Long
    ) : RefreshResult

    /** Session, device or token family revoked. Terminal — do not retry. */
    data object Revoked : RefreshResult

    /** Network or server fault. The caller may try again later. */
    data object TransientFailure : RefreshResult
}

/**
 * Exchanges a refresh token for a new pair.
 *
 * <p>Deliberately separate from [AuthInterceptor] so refresh uses its own OkHttp client
 * without the interceptor attached — otherwise a 401 on refresh would recurse into refresh.</p>
 */
interface TokenRefresher {
    suspend fun refresh(refreshToken: String): RefreshResult
}

/** [TokenRefresher] backed by the real endpoint. */
class ApiTokenRefresher(private val authApi: CarlAuthApi) : TokenRefresher {

    override suspend fun refresh(refreshToken: String): RefreshResult = try {
        val response = authApi.refresh(RefreshTokenRequest(refreshToken))
        val body = response.body()

        when {
            response.isSuccessful && body != null -> RefreshResult.Success(
                accessToken = body.accessToken,
                refreshToken = body.refreshToken,
                expiresAtUtcMillis = Instant.parse(body.expiresAtUtc).toEpochMilli()
            )

            // The backend returns 401 for an invalid, expired, reused or revoked refresh
            // token, and 403 when the device itself is no longer permitted. Neither is
            // retryable with the credentials this device holds.
            response.code() == 401 || response.code() == 403 -> RefreshResult.Revoked

            else -> RefreshResult.TransientFailure
        }
    } catch (_: Exception) {
        // A network failure during refresh says nothing about whether the session is valid.
        RefreshResult.TransientFailure
    }
}
