package app.thecarl.core.data.repository

import app.thecarl.core.data.database.CarlDatabase
import app.thecarl.core.data.database.OutboxItemEntity
import app.thecarl.core.data.database.SyncAttemptEntity
import app.thecarl.core.domain.sync.OutboxState
import app.thecarl.core.domain.sync.RetryPolicy
import app.thecarl.core.domain.sync.SyncResultInterpreter
import app.thecarl.core.domain.sync.SyncTransactionResult
import app.thecarl.core.domain.sync.TransportFailurePolicy
import kotlin.random.Random
import kotlinx.coroutines.flow.Flow

/**
 * Durable outbox.
 *
 * <p><b>The rule this class exists to enforce:</b> an item is never deleted because a request
 * failed. A lost transaction is unrecoverable and represents an agent's real money; a stale
 * outbox row is merely untidy. Every path here either advances the state machine or leaves
 * the row retryable.</p>
 *
 * <p>State transitions come from the shared [SyncResultInterpreter] and
 * [TransportFailurePolicy], and backoff from the shared [RetryPolicy], so the device cannot
 * develop its own opinion about what a server response means.</p>
 */
class OutboxRepository(
    private val database: CarlDatabase,
    private val now: () -> Long = System::currentTimeMillis,
    private val random: Random = Random.Default
) {

    /** Unsynced count, for the sync indicator. */
    fun observeUnsyncedCount(): Flow<Int> = database.outboxDao().observeUnsyncedCount()

    /**
     * Atomically claims a batch, marking each item SYNCING before it is sent.
     *
     * <p>Claiming and marking happen in one database transaction so two overlapping workers
     * cannot both take the same item. Marking happens <b>before</b> the request: a crash
     * mid-request must leave evidence that an attempt was made, otherwise recovery cannot
     * distinguish "never sent" from "possibly committed".</p>
     */
    suspend fun claimBatch(limit: Int): List<OutboxItemEntity> =
        database.outboxDao().claimBatchForSync(now(), limit)

    /**
     * Marks a single item in flight. Returns false when another worker already claimed it.
     */
    suspend fun markInFlight(clientTransactionId: String): Boolean =
        database.outboxDao().markInFlight(clientTransactionId, now()) > 0

    /**
     * Applies a per-item server result.
     *
     * <p><b>DUPLICATE is a success.</b> It means an earlier attempt reached the server and
     * committed — typically one whose response was lost. The item becomes SYNCED using the
     * server's transaction id, and the client stops retrying. Treating it as a failure is
     * what turns one transaction into an endless retry loop.</p>
     */
    suspend fun applyServerResult(result: SyncTransactionResult, correlationId: String? = null) {
        val item = database.outboxDao().findByClientId(result.clientTransactionId) ?: return
        val timestamp = now()

        val nextState = SyncResultInterpreter.nextState(result, item.attemptCount)
        val serverTransactionId = SyncResultInterpreter.serverTransactionId(result)

        // Present for both acceptances and duplicates, which is what lets a client reconcile
        // after losing a response.
        serverTransactionId?.let {
            database.localTransactionDao().setServerTransactionId(result.clientTransactionId, it)
        }

        database.outboxDao().update(
            item.copy(
                state = nextState.name,
                nextAttemptAtUtcMillis = backoffFor(nextState, item.attemptCount, timestamp),
                lastReasonCode = result.reasonCode,
                serverTransactionId = serverTransactionId ?: item.serverTransactionId,
                conflictId = result.conflictId ?: item.conflictId,
                updatedAtUtcMillis = timestamp
            )
        )

        recordAttempt(item, nextState, result.reasonCode, httpStatus = 200, correlationId, timestamp)
    }

    /**
     * Applies a transport failure — a timeout, a reset connection, or an HTTP error with no
     * per-item body.
     *
     * <p><b>A timeout is not a failure to record.</b> The server may have committed. The item
     * stays retryable carrying the same ClientTransactionId, and the server's idempotency
     * constraint resolves it as a duplicate on the next attempt.</p>
     */
    suspend fun applyTransportFailure(
        clientTransactionId: String,
        httpStatus: Int?,
        retryAfterMillis: Long? = null,
        correlationId: String? = null
    ) {
        val item = database.outboxDao().findByClientId(clientTransactionId) ?: return
        val timestamp = now()

        val nextState = TransportFailurePolicy.nextState(httpStatus, item.attemptCount)

        // Honour Retry-After when the server sent one: it knows better than our backoff curve
        // when it will be ready.
        val nextAttempt = retryAfterMillis?.let { timestamp + it }
            ?: backoffFor(nextState, item.attemptCount, timestamp)

        database.outboxDao().update(
            item.copy(
                state = nextState.name,
                nextAttemptAtUtcMillis = nextAttempt,
                lastHttpStatus = httpStatus,
                updatedAtUtcMillis = timestamp
            )
        )

        recordAttempt(item, nextState, reasonCode = null, httpStatus, correlationId, timestamp)
    }

    /**
     * Returns items stranded in SYNCING back to PENDING.
     *
     * <p>Called at startup. A process death mid-request leaves rows in SYNCING with nothing
     * driving them; without this they would sit there forever. Recovery is safe because the
     * retry reuses the same ClientTransactionId — if the server did commit, it resolves as a
     * duplicate.</p>
     */
    suspend fun recoverStrandedItems(leaseMillis: Long = DEFAULT_LEASE_MILLIS): Int {
        val timestamp = now()
        return database.outboxDao().recoverStrandedInFlight(timestamp, timestamp - leaseMillis)
    }

    suspend fun countByState(state: OutboxState): Int =
        database.outboxDao().countByState(state.name)

    /** Items needing a person: conflicts and exhausted retries. */
    suspend fun findNeedingAttention(): List<OutboxItemEntity> =
        database.outboxDao().findByState(OutboxState.CONFLICT.name) +
            database.outboxDao().findByState(OutboxState.DEAD_LETTER.name)

    /**
     * Next attempt time, or null for terminal states.
     *
     * Jitter is applied because a branch's devices reconnect together when a network
     * returns; identical backoff makes them retry in lockstep and hammer the server.
     */
    private fun backoffFor(state: OutboxState, attemptCount: Int, timestamp: Long): Long? =
        if (state.isEligibleForSync) {
            timestamp + RetryPolicy.delayMillisFor(
                attempt = (attemptCount + 1).coerceAtLeast(1),
                jitterFraction = random.nextDouble()
            )
        } else {
            null
        }

    companion object {
        /**
         * How long an item may sit in SYNCING before it is presumed stranded.
         *
         * Comfortably longer than any realistic request, so a worker that is genuinely
         * mid-flight is never interrupted, but short enough that a process death does not
         * strand an agent's transaction for hours.
         */
        const val DEFAULT_LEASE_MILLIS = 5 * 60 * 1000L
    }

    private suspend fun recordAttempt(
        item: OutboxItemEntity,
        resultState: OutboxState,
        reasonCode: String?,
        httpStatus: Int?,
        correlationId: String?,
        timestamp: Long
    ) {
        database.syncAttemptDao().insert(
            SyncAttemptEntity(
                clientTransactionId = item.clientTransactionId,
                attemptNumber = item.attemptCount,
                attemptedAtUtcMillis = timestamp,
                resultState = resultState.name,
                reasonCode = reasonCode,
                httpStatus = httpStatus,
                correlationId = correlationId,
                durationMillis = item.lastAttemptAtUtcMillis?.let { timestamp - it }
            )
        )
    }
}
