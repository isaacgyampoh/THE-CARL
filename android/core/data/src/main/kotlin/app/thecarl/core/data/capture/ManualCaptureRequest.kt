package app.thecarl.core.data.capture

import app.thecarl.core.domain.model.EvidenceSourceType
import app.thecarl.core.domain.model.Provider
import app.thecarl.core.domain.model.TransactionType
import java.math.BigDecimal

/**
 * A transaction a person is recording by hand.
 *
 * <p>Manual capture is a <b>first-class</b> evidence source, not a fallback. It is the only
 * capture method available on iOS and Web, and the one Android relies on when SMS permission
 * is denied or the transaction happened on a phone THE CARL is not installed on.</p>
 *
 * <p>There is deliberately no platform in this type. The source is always
 * [EvidenceSourceType.MANUAL_ENTRY]; which device it came from is already recorded on the
 * device itself. A per-platform source type would fragment the evidence contract for nothing.</p>
 */
data class ManualCaptureRequest(
    val transactionType: TransactionType,
    val amount: BigDecimal,
    val provider: Provider,
    val occurredAtUtcMillis: Long,
    val reference: String? = null,
    val customerPhoneNumber: String? = null,
    val currency: String = "GHS",
    val sessionId: String? = null,
    val notes: String? = null
)

/** Outcome of a local capture, before the server has seen it. */
sealed interface CaptureOutcome {
    /** Recorded locally and queued. [clientTransactionId] is the idempotency key. */
    data class Queued(
        val clientTransactionId: String,
        val evidenceId: String,
        val fingerprint: String
    ) : CaptureOutcome

    /**
     * Recorded as evidence but not queued — an unclassified or non-postable type.
     * The observation is preserved; a person decides what it is.
     */
    data class HeldForReview(
        val evidenceId: String,
        val fingerprint: String,
        val reason: String
    ) : CaptureOutcome

    /**
     * A local duplicate of something already recorded on this device.
     *
     * Local detection is a courtesy that saves a round trip. The server's
     * organization-scoped constraint remains authoritative — only it can see what other
     * devices in the same branch have already submitted.
     */
    data class DuplicateOnThisDevice(
        val existingClientTransactionId: String?,
        val fingerprint: String
    ) : CaptureOutcome

    /** Rejected outright. Nothing was recorded. */
    data class Rejected(val reason: String) : CaptureOutcome
}
