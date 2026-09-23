package app.zazi.core.data.repository

import app.zazi.core.domain.model.GhanaPhoneNumber
import app.zazi.core.data.capture.CaptureOutcome
import app.zazi.core.data.capture.ManualCaptureRequest
import app.zazi.core.data.capture.MinorUnits
import app.zazi.core.data.capture.SmsCaptureRequest
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.database.EvidenceEntity
import app.zazi.core.data.database.LocalTransactionEntity
import app.zazi.core.data.database.OutboxItemEntity
import app.zazi.core.domain.identity.ClientTransactionId
import app.zazi.core.domain.identity.EvidenceFingerprint
import app.zazi.core.domain.ledger.LedgerProjection
import app.zazi.core.domain.model.EvidenceSourceType
import app.zazi.core.domain.model.EvidenceState
import app.zazi.core.domain.model.Provider
import app.zazi.core.domain.model.TransactionType
import app.zazi.core.domain.parser.BaseSmsParser
import app.zazi.core.domain.parser.SmsParserRegistry
import app.zazi.core.domain.sync.OutboxState
import java.math.BigDecimal
import java.util.UUID

/**
 * Records a captured transaction locally.
 *
 * <p>An interface so presentation code depends on the capability rather than the concrete
 * repository, which keeps view-model tests free of a database. The implementation remains
 * the single capture pipeline — this is dependency inversion, not a second path.</p>
 */
interface TransactionCapture {
    suspend fun captureManual(request: ManualCaptureRequest): CaptureOutcome

    /**
     * Records an observed SMS. The parser decides what it says; this decides nothing
     * financial that manual capture does not also decide.
     */
    suspend fun captureSms(request: SmsCaptureRequest): CaptureOutcome
}

/**
 * Records captured transactions locally and queues them for synchronisation.
 *
 * <p><b>One pipeline.</b> Manual capture and (later) SMS capture both arrive here and follow
 * the same path: evidence → classification → local transaction → outbox. There is no
 * separate manual flow, because two pipelines would eventually disagree about what money
 * moved.</p>
 *
 * <p>The device never decides financial truth. It records what it observed, projects a local
 * balance so an offline agent can see their own day, and queues the fact for the server to
 * accept or reject. Where the two disagree, the server wins.</p>
 */
class CaptureRepository(
    private val database: ZaziDatabase,
    private val deviceInstallationId: String,
    private val organizationId: String,
    private val branchId: String?,
    private val deviceId: String?,
    private val now: () -> Long = System::currentTimeMillis,
    // The same registry the shared fixture corpus pins against the server. Injectable so a
    // test can supply a narrower set, never so a caller can substitute different rules.
    private val parsers: SmsParserRegistry = SmsParserRegistry()
) : TransactionCapture {

    override suspend fun captureManual(request: ManualCaptureRequest): CaptureOutcome = record(
        sourceType = EvidenceSourceType.MANUAL_ENTRY,
        provider = request.provider,
        senderIdentity = null,
        transactionType = request.transactionType,
        amount = request.amount,
        currency = request.currency,
        reference = request.reference,
        // One spelling for every number, however it arrived, so a search finds them all.
        customerPhoneNumber = GhanaPhoneNumber.normalise(request.customerPhoneNumber)
            ?: request.customerPhoneNumber,
        occurredAtUtcMillis = request.occurredAtUtcMillis,
        parserName = "ManualEntry",
        parserVersion = MANUAL_PARSER_VERSION,
        // A person deliberately entered these figures. Not uncertain the way a parse is,
        // but still not provider-verified — which the source type records.
        confidence = 1.0,
        evidenceQualityAllowsPosting = true,
        rawMessage = null,
        sessionId = request.sessionId,
        notes = request.notes
    )

    /**
     * Records an observed SMS.
     *
     * <p>The message is interpreted by the existing parser registry — the same one the
     * contract fixture corpus pins against the server — and then follows exactly the path a
     * manual capture follows. Nothing here decides direction, amount or provider.</p>
     *
     * <p><b>Messages that are not transactions are not stored.</b> An unrecognised template
     * with no type and no amount is somebody's one-time code or a personal message; keeping
     * it would put unrelated private text in the evidence table for no financial purpose.
     * Anything a parser could make sense of is kept, including the ones it refuses to
     * post.</p>
     */
    override suspend fun captureSms(request: SmsCaptureRequest): CaptureOutcome {
        val parsed = parsers.parse(request.senderIdentity, request.body)

        // Keyed on what the parser could make of the message, not on which parser ran. A
        // provider's own shortcode also sends one-time codes and marketing, so selecting the
        // MTN parser says nothing about whether this particular text is financial.
        //
        // The exception is a message that a real provider claimed AND that mentions money: a
        // template we cannot read yet. Discarding those is how an agent's deposit disappears
        // with nothing on screen to say so — there is no row, no warning, and, because
        // reporting a mistake hangs off a transaction, no way to tell us either. It is kept as
        // evidence so it reaches the agent, who can record it by hand in seconds.
        val normalized = BaseSmsParser.normalize(request.body)

        // Advertising from the network's own shortcode, whatever figures it quotes. Checked
        // before anything else: a promotion is not a transaction even when a parser managed to
        // read an amount out of "GHS 1.4 MILLION in prizes".
        if (BaseSmsParser.looksPromotional(normalized)) {
            return CaptureOutcome.Ignored("Promotional message, not a transaction.")
        }

        val unreadable = parsed.transactionType == TransactionType.UNKNOWN && parsed.amount == null
        if (unreadable) {
            val fromAProvider = parsed.provider != Provider.UNKNOWN
            val aboutMoney = BaseSmsParser.looksFinancial(normalized)
            if (!fromAProvider || !aboutMoney) {
                return CaptureOutcome.Ignored("Not a recognisable transaction message.")
            }
        }

        // Two independent gates, both already owned elsewhere: evidence quality is the
        // parser's verdict, and whether the *type* may post without review is
        // LedgerProjection's. A reversal with perfect confidence still must not post.
        val qualityAllowsPosting = parsed.isUsable

        return record(
            sourceType = EvidenceSourceType.ANDROID_SMS,
            provider = parsed.provider,
            senderIdentity = request.senderIdentity,
            transactionType = parsed.transactionType,
            amount = parsed.amount,
            currency = DEFAULT_CURRENCY,
            reference = parsed.reference,
            // Provider SMS write numbers as 233…; stored as 0… like everything else. Kept as
            // read if it will not normalise, rather than dropped — a number is evidence.
            customerPhoneNumber = GhanaPhoneNumber.normalise(parsed.customerPhoneNumber)
                ?: parsed.customerPhoneNumber,
            // An SMS carries no trustworthy send time, so the moment the handset saw it is
            // the honest answer. It is also what the fingerprint uses, so a redelivery of
            // the same message must reuse it — see the duplicate check below.
            occurredAtUtcMillis = request.receivedAtUtcMillis,
            parserName = parsed.parserName,
            parserVersion = parsed.parserVersion,
            confidence = parsed.confidence,
            evidenceQualityAllowsPosting = qualityAllowsPosting,
            rawMessage = request.body,
            sessionId = request.sessionId,
            notes = null
        )
    }

    @Suppress("LongParameterList")
    private suspend fun record(
        sourceType: EvidenceSourceType,
        provider: Provider,
        senderIdentity: String?,
        transactionType: TransactionType,
        amount: BigDecimal?,
        currency: String,
        reference: String?,
        customerPhoneNumber: String?,
        occurredAtUtcMillis: Long,
        parserName: String,
        parserVersion: String,
        confidence: Double,
        evidenceQualityAllowsPosting: Boolean,
        rawMessage: String?,
        sessionId: String?,
        notes: String?
    ): CaptureOutcome {
        val timestamp = now()

        val amountMinor = amount?.let {
            try {
                MinorUnits.fromDecimal(it)
            } catch (exception: IllegalArgumentException) {
                return CaptureOutcome.Rejected(exception.message ?: "Amount is not a valid money value.")
            }
        }

        // A manual entry with no usable amount is a mistake worth reporting. A parsed message
        // with none is ordinary — it becomes evidence and waits for a person.
        if (amountMinor == null || amountMinor < MINIMUM_AMOUNT_MINOR) {
            if (sourceType == EvidenceSourceType.MANUAL_ENTRY) {
                return CaptureOutcome.Rejected("Amount must be at least GHS 0.01.")
            }
        }

        // The shared algorithm, byte-identical to the server's. A manual entry describing the
        // same event as a later SMS must collide with it, or the agent gets two ledger rows.
        val fingerprint = EvidenceFingerprint.compute(
            organizationId = organizationId,
            provider = provider.code,
            transactionType = transactionType,
            amount = amount ?: BigDecimal.ZERO,
            providerReference = reference,
            customerPhone = customerPhoneNumber,
            occurredAtUtcMillis = occurredAtUtcMillis
        )

        // Courtesy check only — it saves a round trip. The server's organization-scoped
        // constraint is what actually prevents double-posting, because only it can see what
        // other devices in the branch submitted.
        database.localTransactionDao().findByFingerprint(fingerprint)?.let { existing ->
            return CaptureOutcome.DuplicateOnThisDevice(existing.clientTransactionId, fingerprint)
        }

        val typeAllowsPosting = LedgerProjection.canPostAutomatically(transactionType) &&
            transactionType != TransactionType.UNKNOWN

        val postable = typeAllowsPosting &&
            evidenceQualityAllowsPosting &&
            amountMinor != null &&
            amountMinor >= MINIMUM_AMOUNT_MINOR

        val evidenceId = UUID.randomUUID().toString()

        val evidence = EvidenceEntity(
            evidenceId = evidenceId,
            localTransactionId = null,
            sourceType = sourceType.name,
            provider = provider.code,
            senderIdentity = senderIdentity,
            transactionType = transactionType.name,
            amountMinor = amountMinor,
            currency = currency,
            reference = reference,
            customerPhoneNumber = customerPhoneNumber,
            occurredAtUtcMillis = occurredAtUtcMillis,
            observedAtUtcMillis = timestamp,
            fingerprint = fingerprint,
            fingerprintVersion = EvidenceFingerprint.VERSION,
            parserName = parserName,
            parserVersion = parserVersion,
            confidence = confidence,
            state = if (postable) EvidenceState.ACCEPTED.name else EvidenceState.PENDING_REVIEW.name,
            outcomeReason = if (postable) null else heldReason(transactionType, amountMinor, typeAllowsPosting),
            rawMessage = rawMessage,
            rawMessagePurgedAtUtcMillis = null,
            deviceId = deviceId,
            createdAtUtcMillis = timestamp
        )

        if (!postable) {
            // Unknown, Reversal and Adjustment never post from a capture: the first is
            // unclassified, the others need a referenced original or explicit signed deltas.
            database.captureDao().recordCapture(evidence, transaction = null, outboxItem = null)

            return CaptureOutcome.HeldForReview(
                evidenceId = evidenceId,
                fingerprint = fingerprint,
                reason = evidence.outcomeReason.orEmpty()
            )
        }

        // Generated once and never regenerated on retry. Regenerating after a timeout is
        // exactly how one transaction becomes two.
        val clientTransactionId = ClientTransactionId.create(deviceInstallationId, timestamp)

        val movement = LedgerProjection.movementFor(transactionType, amount!!)

        val localTransaction = LocalTransactionEntity(
            clientTransactionId = clientTransactionId,
            serverTransactionId = null,
            evidenceId = evidenceId,
            branchId = branchId,
            deviceId = deviceId,
            sessionId = sessionId,
            provider = provider.code,
            transactionType = transactionType.name,
            amountMinor = amountMinor,
            currency = currency,
            customerPhoneNumber = customerPhoneNumber,
            reference = reference,
            transactionAtUtcMillis = occurredAtUtcMillis,
            deviceRecordedAtUtcMillis = timestamp,
            sourceType = sourceType.name,
            parserVersion = parserVersion,
            fingerprint = fingerprint,
            // Direction comes from LedgerProjection, never from the caller.
            cashDeltaMinor = MinorUnits.fromDecimal(movement.cashDelta),
            floatDeltaMinor = MinorUnits.fromDecimal(movement.floatDelta),
            notes = notes,
            createdAtUtcMillis = timestamp
        )

        val outboxItem = OutboxItemEntity(
            clientTransactionId = clientTransactionId,
            state = OutboxState.PENDING.name,
            attemptCount = 0,
            nextAttemptAtUtcMillis = timestamp,
            lastAttemptAtUtcMillis = null,
            lastReasonCode = null,
            lastHttpStatus = null,
            serverTransactionId = null,
            conflictId = null,
            createdAtUtcMillis = timestamp,
            updatedAtUtcMillis = timestamp
        )

        // Atomic: a transaction without its outbox row would never sync, and an outbox row
        // without its transaction would sync nothing.
        database.captureDao().recordCapture(
            evidence.copy(localTransactionId = clientTransactionId),
            localTransaction,
            outboxItem
        )

        return CaptureOutcome.Queued(clientTransactionId, evidenceId, fingerprint)
    }

    /** Names the specific deficiency, so a reviewer sees why this was not posted. */
    private fun heldReason(
        transactionType: TransactionType,
        amountMinor: Long?,
        typeAllowsPosting: Boolean
    ): String = when {
        !typeAllowsPosting -> "'${transactionType.name}' requires review before it can be posted."
        amountMinor == null -> "No usable amount was recovered from the message."
        amountMinor < MINIMUM_AMOUNT_MINOR -> "Amount is below GHS 0.01."
        // The truncation case: a recognised provider and a plausible amount, but nothing
        // identifying the transaction. Posting this is how "Cash In of GHS 500.00 ... Ref:
        // MP240815..." truncated to "Cash In of GHS 5" silently understates a till.
        else -> "Evidence is incomplete — a provider reference is required before posting."
    }

    companion object {
        private const val MINIMUM_AMOUNT_MINOR = 1L
        private const val MANUAL_PARSER_VERSION = "manual-v1"
        private const val DEFAULT_CURRENCY = "GHS"
    }
}
