package app.zazi.core.data.database

import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * What the device observed.
 *
 * <p>Mirrors the backend's TransactionEvidence. Every observation is recorded, including
 * ones that will never post: the record of what was seen and why it was rejected is itself
 * auditable, and an agent needs to see why a message did not become a transaction.</p>
 *
 * <p>Amounts are stored as <b>minor units</b> (pesewas) in a Long. SQLite has no decimal
 * type, and REAL is binary floating point — storing GHS 0.10 as a double and adding it a
 * hundred times does not give GHS 10.00. Long pesewas are exact.</p>
 */
@Entity(
    tableName = "transaction_evidence",
    indices = [
        Index("fingerprint"),
        Index("state"),
        Index("observedAtUtcMillis"),
        Index("localTransactionId")
    ]
)
data class EvidenceEntity(
    @PrimaryKey val evidenceId: String,

    /** Links to the local transaction this evidence produced, when it was accepted. */
    val localTransactionId: String?,

    /** EvidenceSourceType.name — AndroidSms, ManualEntry, ImportedEvidence, … */
    val sourceType: String,

    val provider: String,
    val senderIdentity: String?,

    /** TransactionType.name. UNKNOWN is a first-class outcome, never a failure. */
    val transactionType: String,

    /** Minor units. Null when no amount could be recovered. */
    val amountMinor: Long?,

    val currency: String,
    val reference: String?,
    val customerPhoneNumber: String?,

    /** Provider-reported event time. */
    val occurredAtUtcMillis: Long?,

    /** When this device observed it. */
    val observedAtUtcMillis: Long,

    /** Canonical fingerprint from the shared algorithm. */
    val fingerprint: String,
    val fingerprintVersion: String,

    val parserName: String,
    val parserVersion: String,

    /** 0.0–1.0, as scored by the shared evidence policy. */
    val confidence: Double,

    /** EvidenceState.name — DETECTED, PARSED, PENDING_REVIEW, ACCEPTED, REJECTED. */
    val state: String,

    val outcomeReason: String?,

    /**
     * Raw message body, retained only while needed for review.
     *
     * Nullable so it can be purged on its retention schedule: duplicate detection uses the
     * fingerprint, which is derived from extracted fields, so purging text does not break it.
     */
    val rawMessage: String?,

    val rawMessagePurgedAtUtcMillis: Long?,
    val deviceId: String?,
    val createdAtUtcMillis: Long
)

/**
 * What this device believes should be submitted.
 *
 * <p><b>Not</b> a second FinancialTransaction. The backend decides what enters the ledger;
 * this is a local record awaiting that decision, plus enough detail to show the agent their
 * own day's work while offline. [serverTransactionId] is populated once the server accepts
 * or recognises it, and is the authoritative identity from that point.</p>
 */
@Entity(
    tableName = "local_transactions",
    indices = [
        Index(value = ["clientTransactionId"], unique = true),
        Index("fingerprint"),
        Index("transactionAtUtcMillis"),
        Index("serverTransactionId"),
        Index("sessionId")
    ]
)
data class LocalTransactionEntity(
    /**
     * Device-generated identity. Primary key because it is the idempotency key the server
     * deduplicates on — the same value must survive every retry.
     */
    @PrimaryKey val clientTransactionId: String,

    /** Server identity, once known. Null until the server has accepted or recognised it. */
    val serverTransactionId: String?,

    val evidenceId: String?,
    val branchId: String?,
    val deviceId: String?,
    val sessionId: String?,

    val provider: String,
    val transactionType: String,

    /** Minor units. Always a positive magnitude; direction comes from the type. */
    val amountMinor: Long,
    val currency: String,

    val customerPhoneNumber: String?,
    val reference: String?,

    /** Provider-reported event time; drives session attribution and reporting. */
    val transactionAtUtcMillis: Long,

    /** When this device recorded it. Diagnostic only — the server never trusts it. */
    val deviceRecordedAtUtcMillis: Long,

    /** EvidenceSourceType.name. */
    val sourceType: String,
    val parserVersion: String?,
    val fingerprint: String?,

    /**
     * Local balance projection deltas, in minor units, from LedgerProjection.
     *
     * Stored so the offline dashboard can sum them without re-deriving direction. The server
     * remains authoritative; a disagreement is surfaced, never silently overwritten.
     */
    val cashDeltaMinor: Long,
    val floatDeltaMinor: Long,

    val notes: String?,
    val createdAtUtcMillis: Long
)

/**
 * What still needs synchronising.
 *
 * <p>Separate from the transaction so sync state can change without touching financial
 * fields, and so an outbox row is never deleted merely because a request failed. A lost
 * transaction is unrecoverable; a stale outbox row is merely untidy.</p>
 */
@Entity(
    tableName = "outbox_items",
    indices = [
        Index(value = ["clientTransactionId"], unique = true),
        Index("state"),
        Index("nextAttemptAtUtcMillis")
    ]
)
data class OutboxItemEntity(
    @PrimaryKey val clientTransactionId: String,

    /** OutboxState.name — PENDING, SYNCING, SYNCED, RETRYABLE_FAILURE, CONFLICT, DEAD_LETTER. */
    val state: String,

    val attemptCount: Int,

    /** Earliest time the worker may try again; set from the shared RetryPolicy backoff. */
    val nextAttemptAtUtcMillis: Long?,

    val lastAttemptAtUtcMillis: Long?,

    /** Stable server reason code, never a human-readable message. */
    val lastReasonCode: String?,

    /** HTTP status of the last attempt, when there was a response at all. */
    val lastHttpStatus: Int?,

    /** Server transaction id, including when the outcome was DUPLICATE. */
    val serverTransactionId: String?,

    /** Conflict id when the server raised one, so the agent can be shown it. */
    val conflictId: String?,

    val createdAtUtcMillis: Long,
    val updatedAtUtcMillis: Long
)

/**
 * One synchronisation attempt.
 *
 * <p>Append-only history, kept separate from the outbox row's current state. Without it a
 * support engineer investigating a stuck transaction sees only the latest failure, not the
 * pattern — and "failed once on a timeout" and "failed nine times with the same rejection"
 * call for very different responses.</p>
 */
@Entity(
    tableName = "sync_attempts",
    indices = [Index("clientTransactionId"), Index("attemptedAtUtcMillis")]
)
data class SyncAttemptEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val clientTransactionId: String,
    val attemptNumber: Int,
    val attemptedAtUtcMillis: Long,

    /** Resulting OutboxState.name. */
    val resultState: String,

    val reasonCode: String?,
    val httpStatus: Int?,

    /** Server correlation id, for cross-referencing with backend logs. */
    val correlationId: String?,

    val durationMillis: Long?
)

/**
 * One observed event waiting to be reported.
 *
 * <p>Separate from the outbox on purpose. The outbox holds financial work that must never be
 * dropped; this holds telemetry, which must be dropped rather than allowed to grow without
 * limit. Mixing them would put a retention policy on money, or remove one from diagnostics.</p>
 *
 * <p>Carries no token, no payload and no message content — see
 * [app.zazi.core.domain.telemetry.TelemetryEvent].</p>
 */
@Entity(tableName = "telemetry_events")
data class TelemetryEventEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val eventType: String,
    val severity: String,
    val status: String?,
    val errorCode: String?,
    val details: String?,
    val durationMillis: Long?,
    val correlationId: String?,
    val occurredAtUtcMillis: Long
)

/**
 * A recent transaction together with how far it has got towards the server.
 *
 * <p>A query projection rather than a table: it joins what the agent recorded with where it
 * has reached, which are deliberately separate rows. [outboxState] is null once the outbox
 * row has been pruned, which means delivered and tidied — not missing.</p>
 */
data class RecentTransactionRow(
    val clientTransactionId: String,
    val transactionType: String,
    val provider: String,
    val amountMinor: Long,
    val cashDeltaMinor: Long,
    val transactionAtUtcMillis: Long,
    val reference: String?,
    val sourceType: String,
    val outboxState: String?
)
