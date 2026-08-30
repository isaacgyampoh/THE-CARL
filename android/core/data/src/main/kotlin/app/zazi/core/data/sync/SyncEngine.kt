package app.zazi.core.data.sync

import app.zazi.core.data.capture.MinorUnits
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.database.LocalTransactionEntity
import app.zazi.core.data.database.OutboxItemEntity
import app.zazi.core.data.network.ZaziApi
import app.zazi.core.data.repository.OutboxRepository
import app.zazi.core.domain.model.EvidenceSourceType
import app.zazi.core.domain.model.TransactionType
import app.zazi.core.domain.sync.OutboxState
import app.zazi.core.domain.sync.SyncTransactionRequestItem
import app.zazi.core.domain.sync.SyncTransactionsRequest
import java.time.Instant
import java.time.format.DateTimeFormatter

/**
 * Outcome of one drain pass.
 *
 * [hasMoreWork] tells the caller whether to schedule another run rather than waiting for the
 * next periodic trigger — an agent returning from a day offline should not drain 100
 * transactions at a time on an hourly schedule.
 */
data class SyncPassResult(
    val submitted: Int,
    val accepted: Int,
    val duplicate: Int,
    val conflict: Int,
    val retryable: Int,
    val deadLettered: Int,
    val hasMoreWork: Boolean,
    val blockedReason: BlockedReason? = null
) {
    val isSuccess: Boolean get() = blockedReason == null

    companion object {
        fun blocked(reason: BlockedReason) =
            SyncPassResult(0, 0, 0, 0, 0, 0, hasMoreWork = true, blockedReason = reason)

        val NOTHING_TO_DO = SyncPassResult(0, 0, 0, 0, 0, 0, hasMoreWork = false)
    }
}

/** Why a pass could not proceed. Queued work is preserved in every case. */
enum class BlockedReason {
    /** No credentials. The user must log in; the outbox waits. */
    NOT_AUTHENTICATED,

    /** Device or session revoked. Work is preserved but cannot be submitted. */
    REVOKED,

    /** No usable network. Normal for an offline-first client, not an error. */
    OFFLINE,

    /** Server unreachable or failing. Retryable. */
    SERVER_UNAVAILABLE
}

/**
 * Drains the outbox against <c>POST /api/v1/sync/transactions</c>.
 *
 * <p>Deliberately a plain class rather than the WorkManager worker itself, so the whole
 * drain — batching, per-item mapping, failure classification — is testable against a real
 * database and a real HTTP server on the JVM. The worker is a thin adapter over this.</p>
 *
 * <p><b>The invariant this exists to hold:</b> a transaction captured offline survives
 * process death, network failure, timeout, token expiry, retry and app restart, and
 * converges to exactly one authoritative server transaction — without losing or inventing
 * money.</p>
 */
class SyncEngine(
    private val database: ZaziDatabase,
    private val outbox: OutboxRepository,
    private val api: ZaziApi,
    private val batchSizeProvider: () -> Int = { DEFAULT_BATCH_SIZE },
    private val now: () -> Long = System::currentTimeMillis
) {

    /**
     * Runs one drain pass: recover stranded items, claim a batch, submit it, apply results.
     */
    suspend fun runOnce(): SyncPassResult {
        // Stranded items first. A process death mid-request leaves rows in SYNCING with
        // nothing driving them; without this they would never be retried.
        outbox.recoverStrandedItems()

        val batchSize = batchSizeProvider().coerceIn(1, MAX_BATCH_SIZE)
        val claimed = outbox.claimBatch(batchSize)

        if (claimed.isEmpty()) {
            return SyncPassResult.NOTHING_TO_DO
        }

        val items = claimed.mapNotNull { buildRequestItem(it) }

        if (items.isEmpty()) {
            // Claimed rows whose transactions have vanished — should not happen, but must
            // not leave the rows stuck in SYNCING forever.
            claimed.forEach { outbox.applyTransportFailure(it.clientTransactionId, httpStatus = null) }
            return SyncPassResult.NOTHING_TO_DO
        }

        return try {
            val response = api.syncTransactions(SyncTransactionsRequest(items))
            val body = response.body()

            when {
                response.isSuccessful && body != null -> applyResults(claimed, body.results, body.batchId)

                // No per-item body: the whole request failed. Every claimed item is
                // classified by TransportFailurePolicy, which never discards work.
                else -> applyTransportFailure(
                    claimed,
                    httpStatus = response.code(),
                    retryAfterMillis = retryAfterMillis(response.headers()["Retry-After"])
                )
            }
        } catch (exception: Exception) {
            // A timeout or dropped connection says nothing about whether the server
            // committed. Items stay retryable carrying the same ClientTransactionId, and the
            // server resolves the ambiguity as a duplicate on the next attempt.
            applyTransportFailure(claimed, httpStatus = null, retryAfterMillis = null)
        }
    }

    /**
     * Applies per-item results.
     *
     * <p>HTTP 200 does not mean the batch succeeded — it means the batch was processed. Each
     * item carries its own outcome, and one rejected item must not cause the accepted ones to
     * be retried.</p>
     */
    private suspend fun applyResults(
        claimed: List<OutboxItemEntity>,
        results: List<app.zazi.core.domain.sync.SyncTransactionResult>,
        batchId: String
    ): SyncPassResult {
        val byClientId = results.associateBy { it.clientTransactionId }

        claimed.forEach { item ->
            val result = byClientId[item.clientTransactionId]

            if (result == null) {
                // The server did not mention this item. Treated as an unresolved attempt, not
                // a success: assuming acceptance would silently mark unsent work as done.
                outbox.applyTransportFailure(item.clientTransactionId, httpStatus = null)
            } else {
                outbox.applyServerResult(result, correlationId = batchId)
            }
        }

        val states = claimed.mapNotNull {
            database.outboxDao().findByClientId(it.clientTransactionId)?.state
        }

        return SyncPassResult(
            submitted = claimed.size,
            accepted = results.count { it.status == ACCEPTED },
            duplicate = results.count { it.status == DUPLICATE },
            conflict = states.count { it == OutboxState.CONFLICT.name },
            retryable = states.count { it == OutboxState.RETRYABLE_FAILURE.name },
            deadLettered = states.count { it == OutboxState.DEAD_LETTER.name },
            hasMoreWork = outbox.claimBatchWouldFindWork()
        )
    }

    private suspend fun applyTransportFailure(
        claimed: List<OutboxItemEntity>,
        httpStatus: Int?,
        retryAfterMillis: Long?
    ): SyncPassResult {
        claimed.forEach {
            outbox.applyTransportFailure(it.clientTransactionId, httpStatus, retryAfterMillis)
        }

        val blocked = when (httpStatus) {
            null -> BlockedReason.OFFLINE
            401 -> BlockedReason.NOT_AUTHENTICATED
            403 -> BlockedReason.REVOKED
            else -> BlockedReason.SERVER_UNAVAILABLE
        }

        return SyncPassResult(
            submitted = claimed.size,
            accepted = 0, duplicate = 0, conflict = 0,
            retryable = claimed.size, deadLettered = 0,
            hasMoreWork = true,
            blockedReason = blocked
        )
    }

    /**
     * Builds the wire item from local rows.
     *
     * <p>The ClientTransactionId is taken from the stored row and never regenerated —
     * regenerating on retry is exactly how one transaction becomes two.</p>
     */
    private suspend fun buildRequestItem(item: OutboxItemEntity): SyncTransactionRequestItem? {
        val transaction = database.localTransactionDao()
            .findByClientId(item.clientTransactionId) ?: return null

        return SyncTransactionRequestItem(
            clientTransactionId = transaction.clientTransactionId,
            transactionType = TransactionType.valueOf(transaction.transactionType).wireValue,
            amount = MinorUnits.toDecimal(transaction.amountMinor).toPlainString(),
            provider = transaction.provider,
            transactionTimestamp = iso(transaction.transactionAtUtcMillis),
            deviceReceivedAt = iso(transaction.deviceRecordedAtUtcMillis),
            branchId = transaction.branchId,
            deviceId = transaction.deviceId,
            sessionId = transaction.sessionId,
            currency = transaction.currency,
            customerPhone = transaction.customerPhoneNumber,
            transactionReference = transaction.reference,
            evidenceFingerprint = transaction.fingerprint,
            parserVersion = transaction.parserVersion,
            sourceType = EvidenceSourceType.valueOf(transaction.sourceType).wireValue,
            notes = transaction.notes
        )
        // No organizationId: tenancy comes from the access token. There is no field for it.
    }

    private fun iso(millis: Long): String =
        DateTimeFormatter.ISO_INSTANT.format(Instant.ofEpochMilli(millis))

    /** Retry-After is seconds or an HTTP date; only the seconds form is honoured. */
    private fun retryAfterMillis(header: String?): Long? =
        header?.trim()?.toLongOrNull()?.times(1_000L)

    companion object {
        /** Matches the backend default; overridden by the server's reported configuration. */
        const val DEFAULT_BATCH_SIZE = 50

        /** The backend's hard cap. Sending more is refused whole with 413. */
        const val MAX_BATCH_SIZE = 100

        private const val ACCEPTED = "Accepted"
        private const val DUPLICATE = "Duplicate"
    }
}

/** True when at least one item is eligible right now. */
internal suspend fun OutboxRepository.claimBatchWouldFindWork(): Boolean =
    countByState(OutboxState.PENDING) > 0 || countByState(OutboxState.RETRYABLE_FAILURE) > 0
