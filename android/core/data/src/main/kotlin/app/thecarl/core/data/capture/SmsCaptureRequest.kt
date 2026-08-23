package app.thecarl.core.data.capture

/**
 * One observed SMS, handed to the capture pipeline exactly as the handset received it.
 *
 * <p>Deliberately carries no financial fields. Provider, transaction type, amount, reference
 * and confidence are all decided by the existing parser registry and
 * {@link app.thecarl.core.domain.ledger.LedgerProjection} — never by the caller, and never by
 * anything the message itself claims. A sender address is an identifier, not an authority.</p>
 *
 * <p>Organization, branch and device are equally absent: they come from the authenticated
 * session, so a crafted message cannot attribute a transaction to another tenant.</p>
 */
data class SmsCaptureRequest(
    /** Originating address as reported by the platform. Used for parser selection only. */
    val senderIdentity: String?,

    /** Full message body, already reassembled if the message arrived in parts. */
    val body: String,

    /** When the handset received it. The only timestamp an SMS reliably provides. */
    val receivedAtUtcMillis: Long,

    val sessionId: String? = null
)
