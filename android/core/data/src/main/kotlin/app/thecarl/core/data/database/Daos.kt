package app.thecarl.core.data.database

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction
import androidx.room.Update
import kotlinx.coroutines.flow.Flow

@Dao
interface EvidenceDao {
    /**
     * ABORT, not REPLACE. Silently overwriting evidence would destroy the record of what was
     * originally observed, which is the one thing evidence exists to preserve.
     */
    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insert(evidence: EvidenceEntity)

    @Update
    suspend fun update(evidence: EvidenceEntity)

    @Query("SELECT * FROM transaction_evidence WHERE evidenceId = :evidenceId")
    suspend fun findById(evidenceId: String): EvidenceEntity?

    /** Local pre-check only. The server's organization-scoped constraint is authoritative. */
    @Query("SELECT * FROM transaction_evidence WHERE fingerprint = :fingerprint LIMIT 1")
    suspend fun findByFingerprint(fingerprint: String): EvidenceEntity?

    @Query("SELECT * FROM transaction_evidence WHERE state = :state ORDER BY observedAtUtcMillis DESC")
    suspend fun findByState(state: String): List<EvidenceEntity>

    /** Evidence awaiting human classification, newest first. */
    @Query("SELECT * FROM transaction_evidence WHERE state = 'PENDING_REVIEW' ORDER BY observedAtUtcMillis DESC")
    fun observePendingReview(): Flow<List<EvidenceEntity>>

    @Query("SELECT COUNT(*) FROM transaction_evidence")
    suspend fun count(): Int

    /**
     * Purges raw message bodies past their retention window while keeping the evidence row.
     * Duplicate detection uses the fingerprint, so the record stays useful without the text.
     */
    @Query(
        """
        UPDATE transaction_evidence
        SET rawMessage = NULL, rawMessagePurgedAtUtcMillis = :nowUtcMillis
        WHERE rawMessage IS NOT NULL AND observedAtUtcMillis < :olderThanUtcMillis
        """
    )
    suspend fun purgeRawMessagesOlderThan(olderThanUtcMillis: Long, nowUtcMillis: Long): Int
}

@Dao
interface LocalTransactionDao {
    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insert(transaction: LocalTransactionEntity)

    @Update
    suspend fun update(transaction: LocalTransactionEntity)

    @Query("SELECT * FROM local_transactions WHERE clientTransactionId = :clientTransactionId")
    suspend fun findByClientId(clientTransactionId: String): LocalTransactionEntity?

    @Query("SELECT * FROM local_transactions WHERE fingerprint = :fingerprint LIMIT 1")
    suspend fun findByFingerprint(fingerprint: String): LocalTransactionEntity?

    @Query("SELECT * FROM local_transactions ORDER BY transactionAtUtcMillis DESC LIMIT :limit")
    fun observeRecent(limit: Int): Flow<List<LocalTransactionEntity>>

    @Query(
        """
        SELECT * FROM local_transactions
        WHERE transactionAtUtcMillis >= :fromUtcMillis AND transactionAtUtcMillis < :toUtcMillis
        ORDER BY transactionAtUtcMillis DESC
        """
    )
    suspend fun findBetween(fromUtcMillis: Long, toUtcMillis: Long): List<LocalTransactionEntity>

    /**
     * Local cash projection for a window, summed from stored deltas.
     *
     * Sums the deltas rather than re-deriving direction from the type, so this cannot drift
     * from what LedgerProjection decided when the transaction was recorded.
     */
    @Query(
        """
        SELECT COALESCE(SUM(cashDeltaMinor), 0) FROM local_transactions
        WHERE transactionAtUtcMillis >= :fromUtcMillis AND transactionAtUtcMillis < :toUtcMillis
        """
    )
    suspend fun sumCashDeltaMinor(fromUtcMillis: Long, toUtcMillis: Long): Long

    @Query(
        """
        SELECT COALESCE(SUM(floatDeltaMinor), 0) FROM local_transactions
        WHERE transactionAtUtcMillis >= :fromUtcMillis AND transactionAtUtcMillis < :toUtcMillis
        """
    )
    suspend fun sumFloatDeltaMinor(fromUtcMillis: Long, toUtcMillis: Long): Long

    /** Records the authoritative server identity once it is known. */
    @Query("UPDATE local_transactions SET serverTransactionId = :serverTransactionId WHERE clientTransactionId = :clientTransactionId")
    suspend fun setServerTransactionId(clientTransactionId: String, serverTransactionId: String)

    @Query("SELECT COUNT(*) FROM local_transactions")
    suspend fun count(): Int
}

@Dao
interface OutboxDao {
    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insert(item: OutboxItemEntity)

    @Update
    suspend fun update(item: OutboxItemEntity)

    @Query("SELECT * FROM outbox_items WHERE clientTransactionId = :clientTransactionId")
    suspend fun findByClientId(clientTransactionId: String): OutboxItemEntity?

    /**
     * Work the sync worker may pick up now.
     *
     * Ordered by creation so the outbox drains in the order an agent recorded transactions.
     * SYNCING is excluded: an in-flight item must not be sent twice concurrently.
     */
    @Query(
        """
        SELECT * FROM outbox_items
        WHERE state IN ('PENDING', 'RETRYABLE_FAILURE')
          AND (nextAttemptAtUtcMillis IS NULL OR nextAttemptAtUtcMillis <= :nowUtcMillis)
        ORDER BY createdAtUtcMillis ASC
        LIMIT :limit
        """
    )
    suspend fun findReadyForSync(nowUtcMillis: Long, limit: Int): List<OutboxItemEntity>

    @Query("SELECT COUNT(*) FROM outbox_items WHERE state IN ('PENDING', 'RETRYABLE_FAILURE', 'SYNCING')")
    fun observeUnsyncedCount(): Flow<Int>

    @Query("SELECT COUNT(*) FROM outbox_items WHERE state = :state")
    suspend fun countByState(state: String): Int

    @Query("SELECT * FROM outbox_items WHERE state = :state ORDER BY updatedAtUtcMillis DESC")
    suspend fun findByState(state: String): List<OutboxItemEntity>

    /**
     * Returns items stranded in SYNCING to PENDING at startup.
     *
     * <p>SYNCING is written before the HTTP call, so a process death mid-request leaves the
     * row in that state with nothing driving it. Recovery is safe because retrying reuses the
     * same ClientTransactionId: if the server did commit, the retry resolves as a duplicate.
     * The alternative — leaving them — strands the transaction permanently.</p>
     */
    @Query(
        """
        UPDATE outbox_items
        SET state = 'PENDING', nextAttemptAtUtcMillis = :nowUtcMillis, updatedAtUtcMillis = :nowUtcMillis
        WHERE state = 'SYNCING'
        """
    )
    suspend fun recoverStrandedInFlight(nowUtcMillis: Long): Int

    @Query("SELECT COUNT(*) FROM outbox_items")
    suspend fun count(): Int
}

@Dao
interface SyncAttemptDao {
    @Insert
    suspend fun insert(attempt: SyncAttemptEntity): Long

    @Query("SELECT * FROM sync_attempts WHERE clientTransactionId = :clientTransactionId ORDER BY attemptNumber ASC")
    suspend fun findForTransaction(clientTransactionId: String): List<SyncAttemptEntity>

    @Query("SELECT COUNT(*) FROM sync_attempts WHERE clientTransactionId = :clientTransactionId")
    suspend fun countForTransaction(clientTransactionId: String): Int

    /** Bounded history: attempt records are diagnostics, not financial records. */
    @Query("DELETE FROM sync_attempts WHERE attemptedAtUtcMillis < :olderThanUtcMillis")
    suspend fun deleteOlderThan(olderThanUtcMillis: Long): Int
}

/**
 * Writes that must succeed or fail together.
 *
 * <p>A transaction without its outbox row would never sync; an outbox row without its
 * transaction would sync nothing. Room's @Transaction makes the pair atomic, which is the
 * local equivalent of the server's per-item database transaction.</p>
 */
@Dao
interface CaptureDao {
    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insertEvidence(evidence: EvidenceEntity)

    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insertTransaction(transaction: LocalTransactionEntity)

    @Insert(onConflict = OnConflictStrategy.ABORT)
    suspend fun insertOutboxItem(item: OutboxItemEntity)

    /**
     * Records a capture and queues it in one atomic step.
     *
     * Evidence may be recorded alone — held for review, rejected — in which case there is no
     * transaction and nothing to queue.
     */
    @Transaction
    suspend fun recordCapture(
        evidence: EvidenceEntity,
        transaction: LocalTransactionEntity?,
        outboxItem: OutboxItemEntity?
    ) {
        insertEvidence(evidence)
        transaction?.let { insertTransaction(it) }
        outboxItem?.let { insertOutboxItem(it) }
    }
}
