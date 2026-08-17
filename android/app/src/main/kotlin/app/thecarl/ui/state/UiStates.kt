package app.thecarl.ui.state

import app.thecarl.core.data.database.OutboxItemEntity
import app.thecarl.core.data.session.DeviceContext
import java.math.BigDecimal

/**
 * Presentation state for the sign-in screen.
 *
 * <p>The password lives here only while the form is on screen and is cleared the moment
 * authentication fails or succeeds. It is never persisted, logged, or carried into another
 * state.</p>
 */
data class LoginUiState(
    val email: String = "",
    val password: String = "",
    val isSubmitting: Boolean = false,
    val error: LoginError? = null
) {
    /** Cheap client-side gate. The server remains the authority on credentials. */
    val canSubmit: Boolean
        get() = !isSubmitting && email.isNotBlank() && password.isNotBlank()
}

/**
 * Sign-in failures, as states rather than strings.
 *
 * <p>Kept distinct because they need different words: telling an agent their password is
 * wrong when the server is down sends them to reset a password that was never the problem.
 * Backend exception text is never surfaced.</p>
 */
enum class LoginError {
    INVALID_CREDENTIALS,
    RATE_LIMITED,
    NETWORK_UNAVAILABLE,
    SERVER_ERROR;

    val message: String
        get() = when (this) {
            INVALID_CREDENTIALS -> "Email or password is incorrect."
            RATE_LIMITED -> "Too many attempts. Please wait a moment and try again."
            NETWORK_UNAVAILABLE -> "No connection. Check your network and try again."
            SERVER_ERROR -> "THE CARL is unavailable right now. Please try again shortly."
        }
}

data class EnrolmentUiState(
    val code: String = "",
    val isSubmitting: Boolean = false,
    val error: EnrolmentError? = null
) {
    val canSubmit: Boolean get() = !isSubmitting && code.isNotBlank()
}

enum class EnrolmentError {
    INVALID_OR_EXPIRED,
    ALREADY_ENROLLED,
    NETWORK_UNAVAILABLE,
    SERVER_ERROR;

    val message: String
        get() = when (this) {
            INVALID_OR_EXPIRED -> "That enrolment code is not valid or has expired. Ask your manager for a new one."
            ALREADY_ENROLLED -> "This device is already registered."
            NETWORK_UNAVAILABLE -> "No connection. Check your network and try again."
            SERVER_ERROR -> "Enrolment is unavailable right now. Please try again shortly."
        }
}

/**
 * Dashboard state.
 *
 * <p>Every figure comes from the local database. Where a value cannot be calculated
 * correctly yet it is absent rather than zero — a fabricated total on a financial dashboard
 * is worse than an empty one.</p>
 */
data class DashboardUiState(
    val device: DeviceContext? = null,
    val pendingCount: Int = 0,
    val syncingCount: Int = 0,
    val retryingCount: Int = 0,
    val conflictCount: Int = 0,
    val deadLetterCount: Int = 0,
    val syncedTodayCount: Int = 0,
    val todayCashMinor: Long? = null,
    val todayFloatMinor: Long? = null,
    val isOnline: Boolean = true,
    val isSyncing: Boolean = false
) {
    val unsyncedCount: Int get() = pendingCount + syncingCount + retryingCount

    /** Items a person must look at. Never auto-resolved and never hidden. */
    val needsAttentionCount: Int get() = conflictCount + deadLetterCount

    val hasAnyActivity: Boolean
        get() = unsyncedCount > 0 || syncedTodayCount > 0 || needsAttentionCount > 0
}

/**
 * Manual capture form state.
 *
 * <p>The amount is held as the raw string the agent typed, and converted once at submission.
 * Parsing on every keystroke would either reject partial input like "12." or silently
 * reinterpret it.</p>
 */
data class CaptureUiState(
    val transactionType: CaptureTransactionType = CaptureTransactionType.CASH_IN,
    val amountInput: String = "",
    val provider: CaptureProvider = CaptureProvider.MTN,
    val customerPhone: String = "",
    val reference: String = "",
    val notes: String = "",
    val isSubmitting: Boolean = false,
    val error: CaptureError? = null,
    val lastResult: CaptureConfirmation? = null
) {
    val canSubmit: Boolean get() = !isSubmitting && amountInput.isNotBlank()
}

/**
 * Transaction types offerable on a manual capture form.
 *
 * <p>Deliberately narrower than the domain's full set. Reversal needs a referenced original
 * and Adjustment needs explicit signed deltas — neither is something a capture form can
 * supply, and Unknown must never be chosen deliberately.</p>
 */
enum class CaptureTransactionType(val label: String) {
    CASH_IN("Cash in"),
    CASH_OUT("Cash out"),
    TRANSFER("Transfer"),
    COMMISSION("Commission")
}

enum class CaptureProvider(val label: String) {
    MTN("MTN"),
    TELECEL("Telecel"),
    AIRTELTIGO("AirtelTigo")
}

enum class CaptureError {
    INVALID_AMOUNT,
    AMOUNT_TOO_SMALL,
    SUB_PESEWA_PRECISION,
    DUPLICATE_ON_DEVICE,
    HELD_FOR_REVIEW,
    NO_ACTIVE_DEVICE,
    LOCAL_SAVE_FAILED;

    val message: String
        get() = when (this) {
            INVALID_AMOUNT -> "Enter a valid amount."
            AMOUNT_TOO_SMALL -> "Amount must be at least GHS 0.01."
            SUB_PESEWA_PRECISION -> "Amount cannot be smaller than one pesewa."
            DUPLICATE_ON_DEVICE -> "This transaction is already recorded on this device."
            HELD_FOR_REVIEW -> "Saved for review. It will not affect balances until checked."
            NO_ACTIVE_DEVICE -> "This device is not registered yet."
            // The one genuinely alarming case: local persistence failed, so nothing was
            // recorded. Distinct from any network problem.
            LOCAL_SAVE_FAILED -> "Could not save on this device. Please try again."
        }
}

/**
 * Confirmation after a capture.
 *
 * <p><b>Saved locally is not the same as accepted by the server.</b> The wording here says
 * only what is true — the transaction is on this device and queued — because claiming
 * server acceptance before sync confirms it would be a lie an agent might act on.</p>
 */
data class CaptureConfirmation(
    val clientTransactionId: String,
    val transactionType: CaptureTransactionType,
    val amountMinor: Long,
    val isQueued: Boolean,
    val isOnline: Boolean
) {
    val headline: String
        get() = if (isQueued) "Saved on this device" else "Saved for review"

    val syncMessage: String
        get() = when {
            !isQueued -> "Held for review. It will not sync until checked."
            isOnline -> "Waiting to sync"
            else -> "Saved offline — will sync automatically."
        }

    /** Short handle an agent can quote to support. */
    val shortReference: String get() = clientTransactionId.takeLast(8)
}

/** How a queued item is presented. Mirrors OutboxState; deliberately not a second enum. */
data class SyncItemPresentation(
    val clientTransactionId: String,
    val state: String,
    val attemptCount: Int,
    val reasonCode: String?
) {
    companion object {
        fun from(item: OutboxItemEntity) = SyncItemPresentation(
            clientTransactionId = item.clientTransactionId,
            state = item.state,
            attemptCount = item.attemptCount,
            reasonCode = item.lastReasonCode
        )
    }
}

/** Formats minor units for display. Never used for arithmetic. */
object MoneyFormat {
    fun format(minor: Long): String {
        val sign = if (minor < 0) "-" else ""
        val absolute = kotlin.math.abs(minor)
        return "$sign₵${absolute / 100}.${(absolute % 100).toString().padStart(2, '0')}"
    }

    fun format(amount: BigDecimal): String = "₵${amount.setScale(2)}"
}
