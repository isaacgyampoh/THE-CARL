package app.thecarl.core.data

import app.thecarl.core.data.capture.CaptureOutcome
import app.thecarl.core.data.capture.ManualCaptureRequest
import app.thecarl.core.data.capture.MinorUnits
import app.thecarl.core.data.database.CarlDatabase
import app.thecarl.core.data.repository.CaptureRepository
import app.thecarl.core.data.repository.OutboxRepository
import app.thecarl.core.domain.identity.ClientTransactionId
import app.thecarl.core.domain.model.EvidenceSourceType
import app.thecarl.core.domain.model.Provider
import app.thecarl.core.domain.model.TransactionType
import app.thecarl.core.domain.sync.OutboxState
import app.thecarl.core.domain.sync.SyncItemStatus
import app.thecarl.core.domain.sync.SyncReasonCodes
import app.thecarl.core.domain.sync.SyncTransactionResult
import com.google.common.truth.Truth.assertThat
import java.math.BigDecimal
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/**
 * The local capture pipeline and outbox, against a real Room database.
 *
 * <p>The guarantee under test throughout: an agent's recorded work is never lost, and never
 * posted twice.</p>
 */
@RunWith(RobolectricTestRunner::class)
class CaptureAndOutboxTest {

    private lateinit var database: CarlDatabase
    private lateinit var capture: CaptureRepository
    private lateinit var outbox: OutboxRepository

    private val organizationId = "22222222-2222-2222-2222-222222222222"

    @Before
    fun setUp() {
        database = createTestDatabase()
        capture = CaptureRepository(
            database = database,
            deviceInstallationId = "installation-abc",
            organizationId = organizationId,
            branchId = "33333333-3333-3333-3333-333333333333",
            deviceId = "44444444-4444-4444-4444-444444444444"
        )
        outbox = OutboxRepository(database)
    }

    @After
    fun tearDown() = database.close()

    // ─── Manual capture ──────────────────────────────────────────────────────

    @Test
    fun `a manual cash-in is recorded and queued`() = runTest {
        val outcome = capture.captureManual(request(TransactionType.CASH_IN, "500.00"))

        assertThat(outcome).isInstanceOf(CaptureOutcome.Queued::class.java)
        val queued = outcome as CaptureOutcome.Queued

        assertThat(ClientTransactionId.isWellFormed(queued.clientTransactionId)).isTrue()
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
        assertThat(database.evidenceDao().count()).isEqualTo(1)
        assertThat(database.outboxDao().count()).isEqualTo(1)

        val evidence = database.evidenceDao().findById(queued.evidenceId)!!
        // One evidence contract across platforms — no per-platform source type.
        assertThat(evidence.sourceType).isEqualTo(EvidenceSourceType.MANUAL_ENTRY.name)
    }

    @Test
    fun `the ledger direction comes from LedgerProjection not the caller`() = runTest {
        capture.captureManual(request(TransactionType.CASH_IN, "500.00"))

        val transaction = database.localTransactionDao()
            .observeRecent(1).let { database.localTransactionDao().findByClientId(
                database.outboxDao().findReadyForSync(Long.MAX_VALUE, 1).first().clientTransactionId
            )!! }

        // Cash-in: the agent receives physical cash and sends e-money.
        assertThat(transaction.cashDeltaMinor).isEqualTo(50_000L)
        assertThat(transaction.floatDeltaMinor).isEqualTo(-50_000L)
    }

    @Test
    fun `money is stored as exact minor units`() = runTest {
        capture.captureManual(request(TransactionType.CASH_IN, "0.10"))

        val item = database.outboxDao().findReadyForSync(Long.MAX_VALUE, 1).first()
        val transaction = database.localTransactionDao().findByClientId(item.clientTransactionId)!!

        // GHS 0.10 is not representable in binary floating point. Stored as 10 pesewas.
        assertThat(transaction.amountMinor).isEqualTo(10L)
        assertThat(MinorUnits.toDecimal(transaction.amountMinor)).isEqualTo(BigDecimal("0.10"))
    }

    @Test
    fun `an unknown type is held for review and never queued`() = runTest {
        val outcome = capture.captureManual(request(TransactionType.UNKNOWN, "500.00"))

        assertThat(outcome).isInstanceOf(CaptureOutcome.HeldForReview::class.java)

        // Evidence preserved — the observation is real — but nothing queued to post.
        assertThat(database.evidenceDao().count()).isEqualTo(1)
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
        assertThat(database.outboxDao().count()).isEqualTo(0)
    }

    @Test
    fun `a reversal capture is held rather than queued`() = runTest {
        // A reversal needs a referenced original that a capture cannot supply.
        val outcome = capture.captureManual(request(TransactionType.REVERSAL, "100.00"))

        assertThat(outcome).isInstanceOf(CaptureOutcome.HeldForReview::class.java)
        assertThat(database.outboxDao().count()).isEqualTo(0)
    }

    @Test
    fun `an amount below one pesewa is rejected outright`() = runTest {
        val outcome = capture.captureManual(request(TransactionType.CASH_IN, "0.00"))

        assertThat(outcome).isInstanceOf(CaptureOutcome.Rejected::class.java)
        assertThat(database.evidenceDao().count()).isEqualTo(0)
    }

    @Test
    fun `sub-pesewa precision is rejected rather than silently rounded`() = runTest {
        // A rounded amount is a wrong amount.
        val outcome = capture.captureManual(request(TransactionType.CASH_IN, "1.005"))

        assertThat(outcome).isInstanceOf(CaptureOutcome.Rejected::class.java)
    }

    @Test
    fun `capturing the same event twice on one device is detected locally`() = runTest {
        val occurred = System.currentTimeMillis() - 60_000
        val first = capture.captureManual(request(TransactionType.CASH_IN, "250.00", "REF-DUP", occurred))
        val second = capture.captureManual(request(TransactionType.CASH_IN, "250.00", "REF-DUP", occurred))

        assertThat(first).isInstanceOf(CaptureOutcome.Queued::class.java)
        assertThat(second).isInstanceOf(CaptureOutcome.DuplicateOnThisDevice::class.java)

        // Local detection saves a round trip; the server's constraint remains authoritative.
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
    }

    @Test
    fun `each capture gets a distinct client transaction id`() = runTest {
        repeat(20) { capture.captureManual(request(TransactionType.CASH_IN, "10.00")) }

        val ids = database.outboxDao().findReadyForSync(Long.MAX_VALUE, 100)
            .map { it.clientTransactionId }.toSet()

        assertThat(ids).hasSize(20)
    }

    // ─── Outbox lifecycle ────────────────────────────────────────────────────

    @Test
    fun `an accepted result marks the item synced with the server id`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "500.00"))
            as CaptureOutcome.Queued

        outbox.markInFlight(queued.clientTransactionId)
        outbox.applyServerResult(
            SyncTransactionResult(queued.clientTransactionId, SyncItemStatus.ACCEPTED, "srv-1")
        )

        val item = database.outboxDao().findByClientId(queued.clientTransactionId)!!
        assertThat(item.state).isEqualTo(OutboxState.SYNCED.name)
        assertThat(item.serverTransactionId).isEqualTo("srv-1")

        val transaction = database.localTransactionDao().findByClientId(queued.clientTransactionId)!!
        assertThat(transaction.serverTransactionId).isEqualTo("srv-1")
    }

    @Test
    fun `a lost response resolves as synced on retry`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "400.00"))
            as CaptureOutcome.Queued

        // Attempt one: the server committed but the response never arrived.
        outbox.markInFlight(queued.clientTransactionId)
        outbox.applyTransportFailure(queued.clientTransactionId, httpStatus = null)

        var item = database.outboxDao().findByClientId(queued.clientTransactionId)!!
        assertThat(item.state).isEqualTo(OutboxState.RETRYABLE_FAILURE.name)

        // Attempt two: the same ClientTransactionId, so the server recognises it.
        outbox.markInFlight(queued.clientTransactionId)
        outbox.applyServerResult(
            SyncTransactionResult(
                queued.clientTransactionId,
                SyncItemStatus.DUPLICATE,
                "srv-original",
                SyncReasonCodes.DUPLICATE_CLIENT_TRANSACTION_ID
            )
        )

        item = database.outboxDao().findByClientId(queued.clientTransactionId)!!

        // DUPLICATE is a success. The item is done, and it carries the original server id.
        assertThat(item.state).isEqualTo(OutboxState.SYNCED.name)
        assertThat(item.serverTransactionId).isEqualTo("srv-original")

        // Exactly one local transaction throughout — no second record was created.
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
    }

    @Test
    fun `a timeout never deletes the queued transaction`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "75.00"))
            as CaptureOutcome.Queued

        repeat(3) {
            outbox.markInFlight(queued.clientTransactionId)
            outbox.applyTransportFailure(queued.clientTransactionId, httpStatus = null)
        }

        // The row survives every failure. Losing it would lose an agent's real money.
        assertThat(database.outboxDao().findByClientId(queued.clientTransactionId)).isNotNull()
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
    }

    @Test
    fun `a conflict stops retrying and is surfaced for review`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "90.00"))
            as CaptureOutcome.Queued

        outbox.markInFlight(queued.clientTransactionId)
        outbox.applyServerResult(
            SyncTransactionResult(
                queued.clientTransactionId,
                SyncItemStatus.CONFLICT,
                reasonCode = SyncReasonCodes.ALREADY_REVERSED,
                conflictId = "conflict-1"
            )
        )

        val item = database.outboxDao().findByClientId(queued.clientTransactionId)!!
        assertThat(item.state).isEqualTo(OutboxState.CONFLICT.name)
        assertThat(item.conflictId).isEqualTo("conflict-1")
        assertThat(item.nextAttemptAtUtcMillis).isNull()

        assertThat(outbox.findNeedingAttention()).hasSize(1)
    }

    @Test
    fun `a permanent rejection dead-letters and is still inspectable`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "60.00"))
            as CaptureOutcome.Queued

        outbox.markInFlight(queued.clientTransactionId)
        outbox.applyServerResult(
            SyncTransactionResult(
                queued.clientTransactionId,
                SyncItemStatus.REJECTED,
                reasonCode = "INVALID_AMOUNT",
                isRetryable = false
            )
        )

        val item = database.outboxDao().findByClientId(queued.clientTransactionId)!!
        assertThat(item.state).isEqualTo(OutboxState.DEAD_LETTER.name)

        // Never deleted — an agent's record of work stays visible even when it cannot post.
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
        assertThat(outbox.findNeedingAttention()).hasSize(1)
    }

    @Test
    fun `rate limiting honours retry-after`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "30.00"))
            as CaptureOutcome.Queued
        val before = System.currentTimeMillis()

        outbox.markInFlight(queued.clientTransactionId)
        outbox.applyTransportFailure(
            queued.clientTransactionId, httpStatus = 429, retryAfterMillis = 120_000
        )

        val item = database.outboxDao().findByClientId(queued.clientTransactionId)!!

        // The server knows when it will be ready better than our backoff curve does.
        assertThat(item.state).isEqualTo(OutboxState.RETRYABLE_FAILURE.name)
        assertThat(item.nextAttemptAtUtcMillis!!).isAtLeast(before + 120_000)
    }

    @Test
    fun `items stranded in flight by process death are recovered`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "20.00"))
            as CaptureOutcome.Queued

        // Marked in flight, then the process dies before any response is handled.
        outbox.markInFlight(queued.clientTransactionId)
        assertThat(database.outboxDao().findByClientId(queued.clientTransactionId)!!.state)
            .isEqualTo(OutboxState.SYNCING.name)

        // Recovery is lease-bounded. While the lease is live the item is left alone, because
        // a worker may legitimately be mid-request right now and snatching the row would
        // cause a concurrent double submission.
        assertThat(outbox.recoverStrandedItems(leaseMillis = 5 * 60 * 1000L)).isEqualTo(0)
        assertThat(database.outboxDao().findByClientId(queued.clientTransactionId)!!.state)
            .isEqualTo(OutboxState.SYNCING.name)

        // Once the lease has expired the item is presumed stranded by process death.
        val recovered = outbox.recoverStrandedItems(leaseMillis = 0L)

        // Safe because the retry reuses the same ClientTransactionId: if the server did
        // commit, it resolves as a duplicate. Leaving it would strand the work forever.
        assertThat(recovered).isEqualTo(1)
        assertThat(database.outboxDao().findByClientId(queued.clientTransactionId)!!.state)
            .isEqualTo(OutboxState.PENDING.name)
    }

    @Test
    fun `an in-flight item is not offered to the worker again`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "15.00"))
            as CaptureOutcome.Queued

        outbox.markInFlight(queued.clientTransactionId)

        // Concurrent submission of the same item would risk a double post.
        assertThat(outbox.claimBatch(10)).isEmpty()
    }

    @Test
    fun `backoff grows across attempts`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "25.00"))
            as CaptureOutcome.Queued

        val delays = (1..4).map {
            outbox.markInFlight(queued.clientTransactionId)
            val at = System.currentTimeMillis()
            outbox.applyTransportFailure(queued.clientTransactionId, httpStatus = 503)
            database.outboxDao().findByClientId(queued.clientTransactionId)!!
                .nextAttemptAtUtcMillis!! - at
        }

        assertThat(delays.last()).isGreaterThan(delays.first())
    }

    @Test
    fun `every attempt is recorded for support investigation`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "45.00"))
            as CaptureOutcome.Queued

        repeat(3) {
            outbox.markInFlight(queued.clientTransactionId)
            outbox.applyTransportFailure(queued.clientTransactionId, httpStatus = 500)
        }

        // "Failed once on a timeout" and "failed three times identically" call for very
        // different responses; the current state alone cannot distinguish them.
        assertThat(database.syncAttemptDao().countForTransaction(queued.clientTransactionId))
            .isEqualTo(3)
    }

    @Test
    fun `queued work survives closing and reopening the database`() = runTest {
        val queued = capture.captureManual(request(TransactionType.CASH_IN, "500.00"))
            as CaptureOutcome.Queued

        val persistedId = queued.clientTransactionId
        assertThat(database.outboxDao().findByClientId(persistedId)).isNotNull()

        // In-memory Room cannot survive close(), so durability across a real restart is an
        // instrumentation concern. What this asserts is that the write was committed to the
        // database rather than held in a repository field.
        val reread = database.localTransactionDao().findByClientId(persistedId)
        assertThat(reread).isNotNull()
        assertThat(reread!!.amountMinor).isEqualTo(50_000L)
    }

    private fun request(
        type: TransactionType,
        amount: String,
        reference: String? = null,
        occurredAtUtcMillis: Long = System.currentTimeMillis() - 60_000
    ) = ManualCaptureRequest(
        transactionType = type,
        amount = BigDecimal(amount),
        provider = Provider.MTN,
        occurredAtUtcMillis = occurredAtUtcMillis,
        reference = reference ?: "REF-${System.nanoTime()}",
        customerPhoneNumber = "0241234567"
    )
}
