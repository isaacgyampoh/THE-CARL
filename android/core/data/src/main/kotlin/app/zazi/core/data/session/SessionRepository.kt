package app.zazi.core.data.session

import app.zazi.core.data.network.ZaziApi
import app.zazi.core.data.network.ZaziAuthApi
import app.zazi.core.data.network.DeviceSelfResponse
import app.zazi.core.data.network.EnrolDeviceRequest
import app.zazi.core.data.network.LoginRequest
import app.zazi.core.data.repository.OutboxRepository
import app.zazi.core.domain.telemetry.TelemetryStatus
import app.zazi.core.domain.telemetry.TelemetrySeverity
import app.zazi.core.domain.telemetry.TelemetryEventType
import app.zazi.core.domain.telemetry.TelemetryEvent
import app.zazi.core.domain.telemetry.TelemetryErrorCode
import app.zazi.core.data.telemetry.TelemetryRecorder
import app.zazi.core.data.telemetry.NoOpTelemetryRecorder
import app.zazi.core.domain.model.DeviceType
import app.zazi.core.domain.model.PlatformCapability
import app.zazi.core.domain.security.CredentialStore
import app.zazi.core.domain.security.StoredCredentials
import app.zazi.core.domain.sync.OutboxState
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
    private val authApi: ZaziAuthApi,
    private val api: ZaziApi,
    private val outbox: OutboxRepository,
    private val deviceInstallationId: String,
    private val appVersion: String,
    private val osVersion: String,
    private val deviceName: String,
    /**
     * Best-effort reporting. Defaults to discarding, so existing callers and tests are
     * unaffected and no code path can fail for want of telemetry.
     */
    private val telemetry: TelemetryRecorder = NoOpTelemetryRecorder
) {
    /**
     * Reports an outcome without letting it matter.
     *
     * <p>Wrapped so a reporting failure cannot turn a successful sign-in into a failed one,
     * and deliberately carries no credential, token or server response text.</p>
     */
    /** The correlation id the server echoed, when it sent one. */
    private fun retrofit2.Response<*>.correlationId(): String? =
        headers()["X-Correlation-Id"]?.takeIf { it.isNotBlank() }

    private suspend fun report(
        eventType: TelemetryEventType,
        errorCode: TelemetryErrorCode? = null,
        status: TelemetryStatus? = null,
        correlationId: String? = null
    ) {
        runCatching {
            telemetry.record(
                TelemetryEvent(
                    eventType = eventType,
                    severity = if (errorCode == null) TelemetrySeverity.INFORMATION else TelemetrySeverity.WARNING,
                    status = status ?: if (errorCode == null) null else TelemetryStatus.FAILED,
                    errorCode = errorCode,
                    correlationId = correlationId
                )
            )
        }
    }

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
            // Revocation clears credentials, so "nothing stored" is ambiguous: it means
            // either never signed in, or cut off. Only the second deserves an explanation
            // and a queued-work count.
            return if (credentialStore.wasDeviceRevoked()) revoked() else update(SessionState.SignedOut)
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
            report(TelemetryEventType.LOGIN_FAILURE, TelemetryErrorCode.ANDROID_NO_NETWORK)
            return LoginResult.NetworkUnavailable
        } catch (_: Exception) {
            return LoginResult.ServerError(0)
        }

        val body = response.body()

        if (!response.isSuccessful || body == null) {
            return when (response.code()) {
                // Classified, never the response body: a rejection can echo the submitted
                // address, and the password is never anywhere near this.
                401 -> {
                    report(
                        TelemetryEventType.LOGIN_FAILURE,
                        TelemetryErrorCode.ANDROID_INVALID_CREDENTIALS,
                        correlationId = response.correlationId()
                    )
                    LoginResult.InvalidCredentials
                }
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

        // A fresh sign-in supersedes any earlier revocation notice. Whether this device is
        // still permitted is then decided by the server, not by the absence of this marker.
        credentialStore.clearDeviceRevokedMark()

        // The server's own correlation id, taken from the response it just sent. This is what
        // puts the handset's LOGIN_SUCCESS and the server's LOGIN entry on one timeline
        // instead of two unrelated ones.
        report(
            TelemetryEventType.LOGIN_SUCCESS,
            errorCode = null,
            status = TelemetryStatus.SUCCEEDED,
            correlationId = response.correlationId()
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
        // The code itself is never reported: it is a single-use credential.
        report(TelemetryEventType.ENROLMENT_STARTED)

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
        // A deliberate sign-out is not a revocation. Leaving the marker set would greet the
        // next start with "device access revoked" after an ordinary logout.
        credentialStore.clearDeviceRevokedMark()
        update(SessionState.SignedOut)
    }

    /** Called when refresh fails terminally. Preserves queued work and reports how much. */
    suspend fun onSessionRevoked() = revoked().let { }

    /**
     * The single revoked path, whichever way revocation was discovered — a terminal refresh
     * failure or <c>/devices/me</c> reporting it.
     *
     * <p>Tokens are discarded rather than kept alongside a "revoked" flag, so there is no
     * usable credential left for a retry loop to pick up. The durable marker that replaces
     * them carries no authority: it only lets the next start explain what happened instead
     * of showing a bare sign-in form. Queued work is never touched.</p>
     */
    private suspend fun revoked(): SessionState {
        credentialStore.clear()
        credentialStore.markDeviceRevoked()

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
        branchName = branchName,
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
