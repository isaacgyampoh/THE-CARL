package app.zazi.core.data.session

import app.zazi.core.domain.model.DeviceType
import app.zazi.core.domain.model.PlatformCapability

/**
 * Where the application is in its authentication and enrolment lifecycle.
 *
 * <p>Modelled explicitly rather than as a pile of booleans, because the states have
 * genuinely different consequences: [DeviceRevoked] must stop sync while
 * [NeedsEnrolment] must not, and neither may touch the outbox.</p>
 */
sealed interface SessionState {
    /** Startup has not finished reading stored credentials. */
    data object Initialising : SessionState

    /** No stored credentials. The user must log in. */
    data object SignedOut : SessionState

    /**
     * Authenticated, but this handset is not a registered device.
     *
     * Sync cannot run — the server validates the device on every batch — but capture can,
     * and queued work waits.
     */
    data class NeedsEnrolment(val user: AuthenticatedUser) : SessionState

    /** Fully authenticated and enrolled. Normal operation. */
    data class Active(
        val user: AuthenticatedUser,
        val device: DeviceContext
    ) : SessionState

    /**
     * The server no longer trusts this device or session.
     *
     * <p>Credentials are cleared, but unsynced financial work is <b>preserved</b>. The agent
     * re-authenticates and their queued transactions resume.</p>
     */
    data class DeviceRevoked(val queuedWorkCount: Int) : SessionState
}

data class AuthenticatedUser(
    val userId: String,
    val organizationId: String,
    val branchId: String?,
    val email: String,
    val fullName: String,
    val roles: List<String>
)

/**
 * Server-authoritative device context.
 *
 * <p>Every field here comes from <c>GET /devices/me</c>. The client stores none of it as its
 * own opinion: an app that decided its own branch or capabilities could enrol itself
 * sideways into another branch or offer SMS capture on hardware that cannot do it.</p>
 */
data class DeviceContext(
    val deviceId: String,
    val deviceIdentifier: String,
    val organizationId: String,
    val branchId: String,
    val deviceType: DeviceType,
    val capabilities: List<PlatformCapability>,
    val isRevoked: Boolean,
    val maxBatchSize: Int
) {
    /**
     * Whether the platform can observe SMS at all.
     *
     * <p><b>Permission to attempt, not proof of access.</b> The Android runtime permission is
     * a separate question the server cannot answer; a client must still request it and
     * handle refusal by falling back to manual capture.</p>
     */
    val canAttemptSmsCapture: Boolean
        get() = !isRevoked && capabilities.contains(PlatformCapability.SMS_CAPTURE)

    val canCaptureManually: Boolean
        get() = !isRevoked && capabilities.contains(PlatformCapability.MANUAL_TRANSACTION_CAPTURE)
}

/** Outcome of a login attempt, distinguished so the UI can respond appropriately. */
sealed interface LoginResult {
    data class Success(val state: SessionState) : LoginResult
    data object InvalidCredentials : LoginResult
    data class RateLimited(val retryAfterSeconds: Long?) : LoginResult
    data object NetworkUnavailable : LoginResult
    data class ServerError(val status: Int) : LoginResult
}

/** Outcome of redeeming an enrolment code. */
sealed interface EnrolmentResult {
    data class Success(val device: DeviceContext) : EnrolmentResult
    data object InvalidOrExpiredCode : EnrolmentResult
    data object AlreadyEnrolled : EnrolmentResult
    data object NetworkUnavailable : EnrolmentResult
    data class ServerError(val status: Int) : EnrolmentResult
}
