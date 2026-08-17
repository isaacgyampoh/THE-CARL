package app.thecarl.core.data.repository

import app.thecarl.core.data.capture.CaptureOutcome
import app.thecarl.core.data.capture.ManualCaptureRequest
import app.thecarl.core.data.capture.MinorUnits
import app.thecarl.core.data.database.CarlDatabase
import app.thecarl.core.data.database.EvidenceEntity
import app.thecarl.core.data.database.LocalTransactionEntity
import app.thecarl.core.data.database.OutboxItemEntity
import app.thecarl.core.domain.identity.ClientTransactionId
import app.thecarl.core.domain.identity.EvidenceFingerprint
import app.thecarl.core.domain.ledger.LedgerProjection
import app.thecarl.core.domain.model.EvidenceSourceType
import app.thecarl.core.domain.model.EvidenceState
import app.thecarl.core.domain.model.TransactionType
import app.thecarl.core.domain.sync.OutboxState
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
    private val database: CarlDatabase,
    private val deviceInstallationId: String,
    private val organizationId: String,
    private val branchId: String?,
    private val deviceId: String?,
    private val now: () -> Long = System::currentTimeMillis
) : TransactionCapture {

    override suspend fun captureManual(request: ManualCaptureRequest): CaptureOutcome {
        val timestamp = now()

        val amountMinor = try {
            MinorUnits.fromDecimal(request.amount)
        } catch (exception: IllegalArgumentException) {
            return CaptureOutcome.Rejected(exception.message ?: "Amount is not a valid money value.")
        }

        if (amountMinor < MINIMUM_AMOUNT_MINOR) {
            return CaptureOutcome.Rejected("Amount must be at least GHS 0.01.")
        }

        // The shared algorithm, byte-identical to the server's. A manual entry describing the
        // same event as a later SMS must collide with it, or the agent gets two ledger rows.
        val fingerprint = EvidenceFingerprint.compute(
            organizationId = organizationId,
            provider = request.provider.code,
            transactionType = request.transactionType,
            amount = request.amount,
            providerReference = request.reference,
            customerPhone = request.customerPhoneNumber,
            occurredAtUtcMillis = request.occurredAtUtcMillis
        )

        // Courtesy check only — it saves a round trip. The server's organization-scoped
        // constraint is what actually prevents double-posting, because only it can see what
        // other devices in the branch submitted.
        database.localTransactionDao().findByFingerprint(fingerprint)?.let { existing ->
            return CaptureOutcome.DuplicateOnThisDevice(existing.clientTransactionId, fingerprint)
        }

        val postable = LedgerProjection.canPostAutomatically(request.transactionType) &&
            request.transactionType != TransactionType.UNKNOWN

        val evidenceId = UUID.randomUUID().toString()

        val evidence = EvidenceEntity(
            evidenceId = evidenceId,
            localTransactionId = null,
            sourceType = EvidenceSourceType.MANUAL_ENTRY.name,
            provider = request.provider.code,
            senderIdentity = null,
            transactionType = request.transactionType.name,
            amountMinor = amountMinor,
            currency = request.currency,
            reference = request.reference,
            customerPhoneNumber = request.customerPhoneNumber,
            occurredAtUtcMillis = request.occurredAtUtcMillis,
            observedAtUtcMillis = timestamp,
            fingerprint = fingerprint,
            fingerprintVersion = EvidenceFingerprint.VERSION,
            parserName = "ManualEntry",
            parserVersion = MANUAL_PARSER_VERSION,
            // A person deliberately entered these figures. Not uncertain the way a parse is,
            // but still not provider-verified — which the source type records.
            confidence = 1.0,
            state = if (postable) EvidenceState.ACCEPTED.name else EvidenceState.PENDING_REVIEW.name,
            outcomeReason = if (postable) {
                null
            } else {
                "'${request.transactionType.name}' requires review before it can be posted."
            },
            rawMessage = null,
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

        val movement = LedgerProjection.movementFor(request.transactionType, request.amount)

        val localTransaction = LocalTransactionEntity(
            clientTransactionId = clientTransactionId,
            serverTransactionId = null,
            evidenceId = evidenceId,
            branchId = branchId,
            deviceId = deviceId,
            sessionId = request.sessionId,
            provider = request.provider.code,
            transactionType = request.transactionType.name,
            amountMinor = amountMinor,
            currency = request.currency,
            customerPhoneNumber = request.customerPhoneNumber,
            reference = request.reference,
            transactionAtUtcMillis = request.occurredAtUtcMillis,
            deviceRecordedAtUtcMillis = timestamp,
            sourceType = EvidenceSourceType.MANUAL_ENTRY.name,
            parserVersion = MANUAL_PARSER_VERSION,
            fingerprint = fingerprint,
            // Direction comes from LedgerProjection, never from the caller.
            cashDeltaMinor = MinorUnits.fromDecimal(movement.cashDelta),
            floatDeltaMinor = MinorUnits.fromDecimal(movement.floatDelta),
            notes = request.notes,
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

    companion object {
        private const val MINIMUM_AMOUNT_MINOR = 1L
        private const val MANUAL_PARSER_VERSION = "manual-v1"
    }
}
