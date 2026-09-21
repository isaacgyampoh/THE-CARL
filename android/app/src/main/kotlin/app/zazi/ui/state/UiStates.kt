package app.zazi.ui.state

import app.zazi.core.data.database.OutboxItemEntity
import app.zazi.core.data.session.DeviceContext
import java.math.BigDecimal
import app.zazi.core.data.session.ActivatedIdentity

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
            SERVER_ERROR -> "Zazi is unavailable right now. Please try again shortly."
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
    /** What this device has recorded, newest first. Empty until the first capture. */
    val activity: List<ActivityItem> = emptyList(),
    val activityFilter: ActivityFilter = ActivityFilter.TODAY,
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

/**
 * One line in the agent's own record of what they captured.
 *
 * <p>Delivery is presented as three states rather than six, because the outbox's distinctions
 * — pending, syncing, retrying — are all the same fact to the person holding the phone: it is
 * on its way and nothing is required of them. The states that do require something, a
 * conflict or an exhausted retry, are kept separate precisely because they need a person.</p>
 *
 * <p>Deliberately narrow. This model is rebuilt on every refresh while sync progresses, so it
 * carries only what a row draws — no customer number, and not the provider reference, which
 * only the detail screen shows and which is read on demand when it does.</p>
 */
data class ActivityItem(
    val clientTransactionId: String,
    val label: String,
    val provider: String,
    val amountMinor: Long,
    /** Signed cash movement, so the list shows direction without re-deriving it. */
    val cashDeltaMinor: Long,
    val atUtcMillis: Long,
    val capturedAutomatically: Boolean,
    val delivery: ActivityDelivery
) {
    val shortReference: String get() = clientTransactionId.takeLast(8)
}

enum class ActivityDelivery {
    /** On this device and on its way. Nothing is required of the agent. */
    SENDING,

    /** The server has it. */
    SENT,

    /** Stopped, and a person has to look. Never resolved by waiting. */
    NEEDS_REVIEW;

    val label: String
        get() = when (this) {
            SENDING -> "Sending"
            SENT -> "Sent"
            NEEDS_REVIEW -> "Needs review"
        }

    companion object {
        /**
         * Maps an outbox state to what the agent needs to know.
         *
         * <p>A null state means the outbox row has been pruned after delivery, which is
         * settled, not missing — treating it as unknown would show a delivered transaction
         * as though something were wrong with it.</p>
         */
        fun fromOutboxState(state: String?): ActivityDelivery = when (state) {
            null, "SYNCED" -> SENT
            "CONFLICT", "DEAD_LETTER" -> NEEDS_REVIEW
            else -> SENDING
        }
    }
}

/**
 * Which slice of the agent's own record is on screen.
 *
 * <p>Windows are computed from a supplied "now" rather than read from the clock inside, so
 * the boundary is testable and every figure on one screen refers to the same instant.</p>
 */
enum class ActivityFilter(val label: String) {
    TODAY("Today"),
    YESTERDAY("Yesterday"),
    LAST_SEVEN_DAYS("Last 7 days");

    /** Half-open window in UTC millis. Ghana observes UTC+0, so this is the business day. */
    fun windowUtcMillis(nowUtcMillis: Long): LongRange {
        val dayStart = nowUtcMillis / DAY_MILLIS * DAY_MILLIS
        return when (this) {
            TODAY -> dayStart until dayStart + DAY_MILLIS
            YESTERDAY -> dayStart - DAY_MILLIS until dayStart
            // Seven days including today, so "last 7 days" never excludes what just happened.
            LAST_SEVEN_DAYS -> dayStart - 6 * DAY_MILLIS until dayStart + DAY_MILLIS
        }
    }

    private companion object {
        const val DAY_MILLIS = 24 * 60 * 60 * 1000L
    }
}

/**
 * One transaction, in full, with why it is where it is.
 *
 * <p>Exists so an agent can answer a customer standing in front of them — what was the
 * reference, what number was it, has it actually gone — without calling anyone.</p>
 */
data class TransactionDetail(
    val clientTransactionId: String,
    val label: String,
    val provider: String,
    val amountMinor: Long,
    val cashDeltaMinor: Long,
    val atUtcMillis: Long,
    val customerPhone: String?,
    val reference: String?,
    val capturedAutomatically: Boolean,
    val delivery: ActivityDelivery,
    val attemptCount: Int,
    val lastReasonCode: String?,
    val isRetryable: Boolean
) {
    val shortReference: String get() = clientTransactionId.takeLast(8)

    /**
     * What the agent should do, in their terms.
     *
     * <p>A conflict is deliberately not offered a retry. The server disagreeing is not
     * something re-sending the same payload can fix — it would fail identically and leave a
     * second audit entry — so the honest instruction is that somebody with more authority
     * has to look.</p>
     */
    val guidance: String?
        get() = when {
            delivery == ActivityDelivery.SENT -> null
            delivery == ActivityDelivery.SENDING -> null
            // Pluralised, because the single-attempt case is the common one for a batch
            // rejected outright and "after 1 attempts" reads as a bug to the person holding
            // the phone — which undermines the rest of what this screen is telling them.
            isRetryable -> {
                val attempts = if (attemptCount == 1) "1 attempt" else "$attemptCount attempts"
                "This stopped after $attempts. You can try again."
            }
            else -> "The server did not accept this. Ask your manager to review it — " +
                "sending it again would be refused the same way."
        }
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

/**
 * The worker's first screen: one field, one button.
 *
 * <p>No email, no password, no registration. A worker is given a code by the person who
 * employs them and types it in — that is the whole of first-time setup.</p>
 */
data class ActivationUiState(
    val code: String = "",
    val isSubmitting: Boolean = false,
    val error: ActivationError? = null,
    /** Set once the server has confirmed who this handset belongs to. */
    val activated: ActivatedIdentity? = null
) {
    val canSubmit: Boolean get() = !isSubmitting && code.isNotBlank()
}

enum class ActivationError {
    CODE_NOT_VALID,
    ALREADY_ACTIVATED,
    RATE_LIMITED,
    NETWORK_UNAVAILABLE,
    SERVER_ERROR;

    val message: String
        get() = when (this) {
            // One message for invalid, expired, revoked, already-used, attempt-limited and
            // worker-disabled, because the server deliberately does not distinguish them.
            // Saying "expired" when the real cause was revocation would be a guess, and
            // distinguishing them properly would tell an attacker which codes exist. It names
            // the recovery instead, which is the same in every case.
            CODE_NOT_VALID ->
                "That activation code cannot be used. Ask your business owner for a new one."
            ALREADY_ACTIVATED ->
                "This phone is already set up for Zazi. Ask your business owner to reset it."
            RATE_LIMITED ->
                "Too many attempts. Wait a moment and try again."
            NETWORK_UNAVAILABLE ->
                "No connection. Activation needs the internet — check your network and try again."
            SERVER_ERROR ->
                "Activation is unavailable right now. Please try again shortly."
        }
}

/**
 * What the transaction screen knows about reporting this transaction.
 *
 * <p>[canReport] is false for a transaction typed in by hand, which has no provider message
 * behind it, and for one whose message the retention purge has cleared. In both cases the
 * button is not shown at all rather than shown and then refusing — an action that appears and
 * then declines teaches an agent to stop trying.</p>
 */
data class ParsingReportUiState(
    val canReport: Boolean = false,
    val isSending: Boolean = false,
    /** True once the server has it, including when this agent had already sent it. */
    val sent: Boolean = false,
    /** Set when the send failed, phrased for the agent rather than quoting a status code. */
    val failure: String? = null
)
