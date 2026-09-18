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
    /** Display name for [branchId]; absent offline and from servers that predate it. */
    val branchName: String? = null,
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

/**
 * Who the server says this handset belongs to, shown on the confirmation screen.
 *
 * <p>Every field comes from the activation response. Nothing here is composed on the device:
 * the worker is told who the server thinks they are, which is the only version that matters.</p>
 */
data class ActivatedIdentity(
    val workerName: String,
    val organizationName: String,
    val branchName: String
)

/**
 * Outcome of redeeming an activation code.
 *
 * <p>Separate from [EnrolmentResult] because the two differ in what they can fail at.
 * Enrolment happens inside an existing session and can report an expired one; activation has
 * no session yet, and its failures are all about the code.</p>
 */
sealed interface ActivationResult {
    data class Success(val identity: ActivatedIdentity, val device: DeviceContext) : ActivationResult

    /**
     * The server's single answer for invalid, expired, revoked, already-used, attempt-limited
     * and worker-disabled. It does not distinguish them, deliberately — telling a caller
     * which codes exist is an oracle — so neither does this.
     */
    data object CodeNotValid : ActivationResult

    /** This handset is already registered to a device record. */
    data object AlreadyActivated : ActivationResult

    data class RateLimited(val retryAfterSeconds: Long?) : ActivationResult
    data object NetworkUnavailable : ActivationResult
    data class ServerError(val status: Int) : ActivationResult
}
