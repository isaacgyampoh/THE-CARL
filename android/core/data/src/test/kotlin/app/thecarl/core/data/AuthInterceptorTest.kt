package app.thecarl.core.data

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import app.thecarl.core.data.network.AuthFailure
import app.thecarl.core.data.network.AuthInterceptor
import app.thecarl.core.data.network.RefreshResult
import app.thecarl.core.data.network.TokenRefresher
import app.thecarl.core.data.security.KeystoreCredentialStore
import app.thecarl.core.domain.security.StoredCredentials
import com.google.common.truth.Truth.assertThat
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/**
 * Access-token expiry and refresh.
 *
 * <p>Time is made deterministic by storing an expiry that has already passed rather than by
 * waiting or by injecting a clock into production code. {@code accessTokenNeedsRefresh}
 * already compares against a supplied instant, so nothing in the authentication path is
 * relaxed to make these tests possible — no token is forged, and no signature check is
 * bypassed.</p>
 */
@RunWith(RobolectricTestRunner::class)
class AuthInterceptorTest {

    private val context: Context = ApplicationProvider.getApplicationContext()

    private lateinit var server: MockWebServer
    private lateinit var credentialStore: KeystoreCredentialStore

    @Before
    fun setUp() {
        server = MockWebServer().apply { start() }
        credentialStore = KeystoreCredentialStore(
            context.getSharedPreferences("auth-interceptor-test", Context.MODE_PRIVATE),
            FakeCryptoBox()
        )
    }

    @After
    fun tearDown() = server.shutdown()

    // ─── Expiry drives refresh ───────────────────────────────────────────────

    @Test
    fun `an expired access token is refreshed and the original request succeeds`() = runBlocking {
        store(accessToken = "expired-token", expiresAtUtcMillis = past())
        val refresher = CountingRefresher(RefreshResult.Success("fresh-token", "refresh-2", future()))

        server.enqueue(MockResponse().setResponseCode(200).setBody("""{"ok":true}"""))
        val response = call(refresher)

        assertThat(response.code).isEqualTo(200)
        assertThat(refresher.calls.get()).isEqualTo(1)

        // The request must go out carrying the new token, not the dead one.
        assertThat(server.takeRequest().getHeader("Authorization")).isEqualTo("Bearer fresh-token")
    }

    @Test
    fun `a token that is still valid is used without refreshing`() = runBlocking {
        store(accessToken = "good-token", expiresAtUtcMillis = future())
        val refresher = CountingRefresher(RefreshResult.Success("unused", "unused", future()))

        server.enqueue(MockResponse().setResponseCode(200))
        call(refresher)

        // Refreshing a healthy token spends the refresh token for nothing and rotates the
        // family more often than necessary.
        assertThat(refresher.calls.get()).isEqualTo(0)
        assertThat(server.takeRequest().getHeader("Authorization")).isEqualTo("Bearer good-token")
    }

    @Test
    fun `a 401 on a token believed valid triggers exactly one refresh and one retry`() = runBlocking {
        store(accessToken = "stale-token", expiresAtUtcMillis = future())
        val refresher = CountingRefresher(RefreshResult.Success("fresh-token", "refresh-2", future()))

        server.enqueue(MockResponse().setResponseCode(401))
        server.enqueue(MockResponse().setResponseCode(200))

        val response = call(refresher)

        assertThat(response.code).isEqualTo(200)
        assertThat(refresher.calls.get()).isEqualTo(1)
        assertThat(server.requestCount).isEqualTo(2)
    }

    @Test
    fun `a persistent 401 does not loop forever`() = runBlocking {
        store(accessToken = "stale-token", expiresAtUtcMillis = future())
        val refresher = CountingRefresher(RefreshResult.Success("fresh-token", "refresh-2", future()))

        server.enqueue(MockResponse().setResponseCode(401))
        server.enqueue(MockResponse().setResponseCode(401))

        val response = call(refresher)

        // Retry once, then surrender. An unbounded loop against a session that will never be
        // readmitted drains the battery and never succeeds.
        assertThat(response.code).isEqualTo(401)
        assertThat(server.requestCount).isEqualTo(2)
    }

    // ─── Credential protection ───────────────────────────────────────────────

    @Test
    fun `a refreshed token is persisted so the next start does not need the password`() =
        runBlocking {
            store(accessToken = "expired-token", expiresAtUtcMillis = past())
            server.enqueue(MockResponse().setResponseCode(200))

            call(CountingRefresher(RefreshResult.Success("fresh-token", "refresh-2", future())))

            val stored = credentialStore.read()!!
            assertThat(stored.accessToken).isEqualTo("fresh-token")
            assertThat(stored.refreshToken).isEqualTo("refresh-2")
        }

    @Test
    fun `no token is written to disk in clear text`() = runBlocking {
        store(accessToken = "expired-token", expiresAtUtcMillis = past())
        server.enqueue(MockResponse().setResponseCode(200))

        call(CountingRefresher(RefreshResult.Success("fresh-token", "refresh-2", future())))

        val raw = context.getSharedPreferences("auth-interceptor-test", Context.MODE_PRIVATE)
            .all.values.joinToString(" ")

        assertThat(raw).doesNotContain("fresh-token")
        assertThat(raw).doesNotContain("refresh-2")
    }

    @Test
    fun `a revoked refresh token clears credentials and reports revocation`() = runBlocking {
        store(accessToken = "expired-token", expiresAtUtcMillis = past())
        var failure: AuthFailure? = null

        server.enqueue(MockResponse().setResponseCode(200))
        call(CountingRefresher(RefreshResult.Revoked)) { failure = it }

        assertThat(failure).isEqualTo(AuthFailure.REVOKED)
        assertThat(credentialStore.read()).isNull()
    }

    @Test
    fun `a transient refresh failure keeps credentials for a later attempt`() = runBlocking {
        store(accessToken = "expired-token", expiresAtUtcMillis = past())

        server.enqueue(MockResponse().setResponseCode(200))
        call(CountingRefresher(RefreshResult.TransientFailure))

        // A network blip is not a revocation. Discarding credentials here would sign an agent
        // out — and strand their queued work — because of one failed request.
        assertThat(credentialStore.read()).isNotNull()
    }

    // ─── Single-flight ───────────────────────────────────────────────────────

    @Test
    fun `concurrent requests refresh once so the token family is not revoked`() {
        runBlocking { store(accessToken = "expired-token", expiresAtUtcMillis = past()) }

        val concurrency = 8
        val refresher = CountingRefresher(
            RefreshResult.Success("fresh-token", "refresh-2", future()),
            // Hold the first caller inside refresh so the others pile up on the mutex, which
            // is the situation a draining outbox actually creates.
            delayMillis = 200
        )

        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest) = MockResponse().setResponseCode(200)
        }

        val client = clientWith(refresher)
        val start = CountDownLatch(1)
        val done = CountDownLatch(concurrency)

        repeat(concurrency) {
            Thread {
                start.await()
                client.newCall(Request.Builder().url(server.url("/api/v1/ping")).build())
                    .execute().close()
                done.countDown()
            }.start()
        }

        start.countDown()
        assertThat(done.await(30, TimeUnit.SECONDS)).isTrue()

        // The backend revokes an entire token family when a rotated refresh token is reused,
        // so a stampede here would log the agent out rather than merely waste a request.
        assertThat(refresher.calls.get()).isEqualTo(1)
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private fun past() = System.currentTimeMillis() - 60_000L
    private fun future() = System.currentTimeMillis() + 3_600_000L

    private suspend fun store(accessToken: String, expiresAtUtcMillis: Long) {
        credentialStore.save(
            StoredCredentials(
                accessToken = accessToken,
                refreshToken = "refresh-1",
                accessTokenExpiresAtUtcMillis = expiresAtUtcMillis,
                userId = "user-1",
                organizationId = "org-1",
                branchId = "branch-1",
                deviceId = "device-1",
                deviceInstallationId = "install-1"
            )
        )
    }

    private fun clientWith(
        refresher: TokenRefresher,
        onFailure: (AuthFailure) -> Unit = {}
    ) = OkHttpClient.Builder()
        .addInterceptor(AuthInterceptor(credentialStore, refresher, onFailure))
        .build()

    private fun call(
        refresher: TokenRefresher,
        onFailure: (AuthFailure) -> Unit = {}
    ) = clientWith(refresher, onFailure)
        .newCall(Request.Builder().url(server.url("/api/v1/ping")).build())
        .execute()

    /** Counts refreshes so single-flight can be asserted rather than assumed. */
    private class CountingRefresher(
        private val result: RefreshResult,
        private val delayMillis: Long = 0
    ) : TokenRefresher {
        val calls = AtomicInteger(0)

        override suspend fun refresh(refreshToken: String): RefreshResult {
            calls.incrementAndGet()
            if (delayMillis > 0) Thread.sleep(delayMillis)
            return result
        }
    }
}
