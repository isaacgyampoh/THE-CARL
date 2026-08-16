package app.thecarl.core.domain.model

import java.math.BigDecimal

/**
 * Classification of a financial transaction, stated from the agent's books.
 *
 * The [wireValue] values mirror the backend enum exactly and are what travel over the API.
 * They are part of the persisted and transmitted contract: renumbering here silently
 * reclassifies transactions on the server.
 */
enum class TransactionType(val wireValue: Int) {
    /** Customer hands physical cash to the agent and receives e-money. */
    CASH_IN(0),

    /** Agent hands physical cash to the customer and receives e-money. */
    CASH_OUT(1),

    /** E-money movement with no derivable till-side cash effect. */
    TRANSFER(2),

    /** Cancels a previously accepted transaction; must reference the original. */
    REVERSAL(3),

    /** Commission credited by the provider to the agent's e-money float. */
    COMMISSION(4),

    /** Deliberate correction carrying explicit signed deltas. */
    ADJUSTMENT(5),

    /**
     * The classifier could not determine a type. Never posts, always requires review.
     * This is a first-class outcome, not an error: guessing would corrupt balances.
     */
    UNKNOWN(6);

    companion object {
        fun fromWire(value: Int): TransactionType =
            entries.firstOrNull { it.wireValue == value } ?: UNKNOWN
    }
}

/** How a transaction came to THE CARL's attention. Mirrors the backend enum. */
enum class EvidenceSourceType(val wireValue: Int) {
    ANDROID_SMS(0),
    MANUAL_ENTRY(1),
    GSM_GATEWAY(2),
    RELAY(3),
    PROVIDER_API(4)
}

/**
 * Local lifecycle of an observation.
 *
 * SMS *detected* is not the same as a financial transaction *accepted*. Everything up to
 * and including [PENDING_REVIEW] is evidence; only [ACCEPTED] and beyond may be submitted
 * to the server as a financial record.
 */
enum class EvidenceState {
    DETECTED,
    PARSED,
    PENDING_REVIEW,
    ACCEPTED,
    REJECTED
}

/** Mobile-money networks THE CARL recognises in Ghana. */
enum class Provider(val code: String) {
    MTN("MTN"),
    TELECEL("TELECEL"),
    AIRTELTIGO("AIRTELTIGO"),
    UNKNOWN("UNKNOWN");

    companion object {
        fun fromCode(code: String?): Provider =
            entries.firstOrNull { it.code.equals(code?.trim(), ignoreCase = true) } ?: UNKNOWN
    }
}

/**
 * What was observed — an unverified account of a transaction.
 *
 * Deliberately separate from [FinancialTransaction]. Evidence is a claim; a financial
 * transaction is a claim THE CARL has accepted. Conflating them means a parser failure or a
 * duplicate delivery becomes accounting data.
 */
data class TransactionEvidence(
    val evidenceId: String,
    val deviceId: String?,
    val observedAtUtc: Long,
    val provider: Provider,
    val senderIdentity: String?,
    val messageFingerprint: String,
    val parserName: String,
    val parserVersion: String,
    val state: EvidenceState,
    val transactionType: TransactionType,
    val amount: BigDecimal?,
    val reference: String?,
    val customerPhoneNumber: String?,
    val currency: String = "GHS",
    val originalTransactionReference: String? = null,
    val confidence: Double = 0.0,
    val outcomeReason: String? = null
)

/**
 * A transaction THE CARL has accepted locally and will submit to the server.
 *
 * The server remains the financial authority. This is a local projection that lets an agent
 * keep working offline; where it disagrees with the server, the server wins and the
 * difference is surfaced rather than silently overwritten.
 */
data class FinancialTransaction(
    val clientTransactionId: String,
    val evidenceId: String?,
    val organizationScopedServerId: String? = null,
    val branchId: String?,
    val deviceId: String?,
    val sessionId: String?,
    val provider: Provider,
    val type: TransactionType,
    val amount: BigDecimal,
    val currency: String = "GHS",
    val customerPhoneNumber: String?,
    val reference: String?,
    val transactionAtUtc: Long,
    val deviceReceivedAtUtc: Long,
    val sourceType: EvidenceSourceType,
    val parserVersion: String?,
    val reversesTransactionId: String? = null,
    val correctionReason: String? = null,
    val notes: String? = null
)
