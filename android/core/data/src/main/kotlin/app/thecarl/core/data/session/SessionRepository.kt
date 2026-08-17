package app.thecarl.core.data.session

import app.thecarl.core.data.network.CarlApi
import app.thecarl.core.data.network.CarlAuthApi
import app.thecarl.core.data.network.DeviceSelfResponse
import app.thecarl.core.data.network.EnrolDeviceRequest
import app.thecarl.core.data.network.LoginRequest
import app.thecarl.core.data.repository.OutboxRepository
import app.thecarl.core.domain.model.DeviceType
import app.thecarl.core.domain.model.PlatformCapability
import app.thecarl.core.domain.security.CredentialStore
import app.thecarl.core.domain.security.StoredCredentials
import app.thecarl.core.domain.sync.OutboxState
import java.io.IOException
import java.time.Instant
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Owns the authentication and enrolment lifecycle.
 *
 * <p>The single writer to [CredentialStore]. Everything else reads. Concentrating writes
 * here is what makes it possible to state, and test, that a password is never persisted and
 * that logout never touches financial data.</p>
 *
 * <p><b>Financial work outlives authentication.</b> Logout, revocation and refresh failure
 * all clear credentials; none of them delete an outbox row. An agent who is signed out still
 * owns the transactions they captured.</p>
 */
class SessionRepository(
    private val credentialStore: CredentialStore,
    private val authApi: CarlAuthApi,
    private val api: CarlApi,
    private val outbox: OutboxRepository,
    private val deviceInstallationId: String,
    private val appVersion: String,
    private val osVersion: String,
    private val deviceName: String
) {
    private val _state = MutableStateFlow<SessionState>(SessionState.Initialising)
    val state: StateFlow<SessionState> = _state.asStateFlow()

    /**
     * Restores session state at startup from stored credentials.
     *
     * <p>Must never throw: an unreachable network at launch is normal for an offline-first
     * client, and corrupt credential ciphertext must degrade to "log in again" rather than a
     * crash loop the user cannot escape.</p>
     */
    suspend fun restore(): SessionState {
        val credentials = try {
            credentialStore.read()
        } catch (_: Exception) {
            null
        }

        if (credentials == null) {
            return update(SessionState.SignedOut)
        }

        val user = credentials.toUser()

        // Offline start: trust the stored credentials and let the device check happen when
        // connectivity returns. Refusing to start without a network would make the app
        // useless in exactly the conditions it exists for.
        val deviceIdentifier = credentials.deviceInstallationId

        return when (val device = fetchDeviceContext(deviceIdentifier)) {
            is DeviceFetch.Found -> {
                if (device.context.isRevoked) {
                    revoked()
                } else {
                    // Persist the server-assigned device id whenever it is confirmed. Without
                    // this a later offline start cannot tell an enrolled device from an
                    // unenrolled one, and would wrongly send an already-enrolled agent back
                    // through enrolment with no network to complete it.
                    if (credentials.deviceId != device.context.deviceId) {
                        credentialStore.save(credentials.copy(deviceId = device.context.deviceId))
                    }

                    update(SessionState.Active(user, device.context))
                }
            }

            DeviceFetch.NotEnrolled -> update(SessionState.NeedsEnrolment(user))
            DeviceFetch.Revoked -> revoked()

            // Cannot reach the server. Stay usable: capture continues and sync waits.
            DeviceFetch.Unavailable ->
                if (credentials.deviceId != null) {
                    update(SessionState.Active(user, offlineDeviceContext(credentials)))
                } else {
                    update(SessionState.NeedsEnrolment(user))
                }
        }
    }

    suspend fun login(email: String, password: String): LoginResult {
        val response = try {
            authApi.login(
                LoginRequest(
                    email = email.trim(),
                    password = password,
                    // Binds the session to this device when it is already enrolled, so
                    // revoking the device immediately stops refresh and sync.
                    deviceIdentifier = credentialStore.read()?.deviceId?.let { deviceInstallationId }
                )
            )
        } catch (_: IOException) {
            return LoginResult.NetworkUnavailable
        } catch (_: Exception) {
            return LoginResult.ServerError(0)
        }

        val body = response.body()

        if (!response.isSuccessful || body == null) {
            return when (response.code()) {
                401 -> LoginResult.InvalidCredentials
                429 -> LoginResult.RateLimited(
                    response.headers()["Retry-After"]?.trim()?.toLongOrNull()
                )
                else -> LoginResult.ServerError(response.code())
            }
        }

        // The password is used for this request and nothing else. It is never written to
        // storage, never cached in a field, and never logged.
        val existing = credentialStore.read()

        credentialStore.save(
            StoredCredentials(
                accessToken = body.accessToken,
                refreshToken = body.refreshToken,
                accessTokenExpiresAtUtcMillis = Instant.parse(body.expiresAtUtc).toEpochMilli(),
                userId = body.user.id,
                organizationId = body.user.organizationId,
                branchId = body.user.branchId,
                // Preserved across logins so a re-login does not orphan an enrolled device.
                deviceId = existing?.deviceId,
                deviceInstallationId = deviceInstallationId
            )
        )

        return LoginResult.Success(restore())
    }

    /**
     * Redeems an enrolment code for this handset.
     *
     * <p>Scope — organization, branch, role, device type — comes entirely from the code. The
     * client sends only its own hardware details.</p>
     */
    suspend fun enrolDevice(code: String): EnrolmentResult {
        val credentials = credentialStore.read() ?: return EnrolmentResult.ServerError(401)

        val response = try {
            api.enrolDevice(
                EnrolDeviceRequest(
                    code = code.trim(),
                    deviceIdentifier = deviceInstallationId,
                    name = deviceName,
                    platform = "Android",
                    network = "MTN",
                    appVersion = appVersion,
                    osVersion = osVersion
                )
            )
        } catch (_: IOException) {
            return EnrolmentResult.NetworkUnavailable
        } catch (_: Exception) {
            return EnrolmentResult.ServerError(0)
        }

        val body = response.body()

        if (!response.isSuccessful || body == null) {
            return when (response.code()) {
                401 -> EnrolmentResult.InvalidOrExpiredCode
                409 -> EnrolmentResult.AlreadyEnrolled
                else -> EnrolmentResult.ServerError(response.code())
            }
        }

        // Record the server-assigned device id. It is an identifier, not a credential — the
        // server re-validates the device on every sync batch regardless.
        credentialStore.save(credentials.copy(deviceId = body.deviceId))

        // Re-query rather than trusting the enrolment response for capabilities: /devices/me
        // is the authority, and it is what the client will consult from now on.
        return when (val device = fetchDeviceContext(deviceInstallationId)) {
            is DeviceFetch.Found -> {
                update(SessionState.Active(credentials.toUser(), device.context))
                EnrolmentResult.Success(device.context)
            }
            else -> EnrolmentResult.ServerError(0)
        }
    }

    /**
     * Signs out.
     *
     * <p>Clears credentials and stops authenticated work. <b>Does not</b> delete the
     * database, the outbox, or any evidence: unsynced transactions are an agent's financial
     * record, not cache, and survive until they reach the server.</p>
     */
    suspend fun logout() {
        credentialStore.clear()
        update(SessionState.SignedOut)
    }

    /** Called when refresh fails terminally. Preserves queued work and reports how much. */
    suspend fun onSessionRevoked() {
        credentialStore.clear()
        revoked()
    }

    private suspend fun revoked(): SessionState {
        val queued = outbox.countByState(OutboxState.PENDING) +
            outbox.countByState(OutboxState.RETRYABLE_FAILURE) +
            outbox.countByState(OutboxState.SYNCING)

        return update(SessionState.DeviceRevoked(queued))
    }

    private suspend fun fetchDeviceContext(deviceIdentifier: String): DeviceFetch = try {
        val response = api.deviceSelf(deviceIdentifier)
        val body = response.body()

        when {
            response.isSuccessful && body != null ->
                if (body.isRevoked) DeviceFetch.Revoked else DeviceFetch.Found(body.toContext())

            response.code() == 404 -> DeviceFetch.NotEnrolled
            response.code() == 403 -> DeviceFetch.Revoked
            else -> DeviceFetch.Unavailable
        }
    } catch (_: Exception) {
        DeviceFetch.Unavailable
    }

    /**
     * Last-known device context for offline start.
     *
     * <p>Capabilities are deliberately empty rather than remembered: claiming SMS capture
     * without confirming with the server risks acting on a capability that has since been
     * revoked. Manual capture stays available because it always is.</p>
     */
    private fun offlineDeviceContext(credentials: StoredCredentials) = DeviceContext(
        deviceId = credentials.deviceId.orEmpty(),
        deviceIdentifier = credentials.deviceInstallationId,
        organizationId = credentials.organizationId,
        branchId = credentials.branchId.orEmpty(),
        deviceType = DeviceType.ANDROID_PHONE,
        capabilities = listOf(PlatformCapability.MANUAL_TRANSACTION_CAPTURE),
        isRevoked = false,
        maxBatchSize = DEFAULT_BATCH_SIZE
    )

    private fun DeviceSelfResponse.toContext() = DeviceContext(
        deviceId = deviceId,
        deviceIdentifier = deviceIdentifier,
        organizationId = organizationId,
        branchId = branchId,
        deviceType = DeviceType.fromWire(deviceType),
        capabilities = platformCapabilities.mapNotNull { PlatformCapability.fromWire(it) },
        isRevoked = isRevoked,
        // Server-authoritative. Hardcoding a batch size means discovering a mismatch only by
        // being rejected with 413.
        maxBatchSize = syncConfiguration?.maxBatchSize ?: DEFAULT_BATCH_SIZE
    )

    private fun StoredCredentials.toUser() = AuthenticatedUser(
        userId = userId,
        organizationId = organizationId,
        branchId = branchId,
        email = "",
        fullName = "",
        roles = emptyList()
    )

    private fun update(next: SessionState): SessionState {
        _state.value = next
        return next
    }

    private sealed interface DeviceFetch {
        data class Found(val context: DeviceContext) : DeviceFetch
        data object NotEnrolled : DeviceFetch
        data object Revoked : DeviceFetch
        data object Unavailable : DeviceFetch
    }

    companion object {
        private const val DEFAULT_BATCH_SIZE = 50
    }
}
