package app.zazi.core.data

import app.zazi.core.data.capture.CaptureOutcome
import app.zazi.core.data.capture.ManualCaptureRequest
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.network.ZaziApi
import app.zazi.core.data.repository.CaptureRepository
import app.zazi.core.data.repository.OutboxRepository
import app.zazi.core.data.sync.BlockedReason
import app.zazi.core.data.sync.SyncEngine
import app.zazi.core.domain.model.Provider
import app.zazi.core.domain.model.TransactionType
import app.zazi.core.domain.sync.OutboxState
import com.google.common.truth.Truth.assertThat
import java.math.BigDecimal
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.SocketPolicy
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory

/**
 * The offline sync pipeline end to end: real Room, real HTTP, real batching.
 *
 * <p>MockWebServer rather than a mocked Retrofit interface — the point is to exercise the
 * actual wire contract, status handling and header parsing, not a stub that agrees with our
 * assumptions.</p>
 */
@RunWith(RobolectricTestRunner::class)
class SyncEngineTest {

    private lateinit var server: MockWebServer
    private lateinit var database: ZaziDatabase
    private lateinit var capture: CaptureRepository
    private lateinit var outbox: OutboxRepository
    private lateinit var engine: SyncEngine

    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }

    @Before
    fun setUp() {
        server = MockWebServer().apply { start() }
        database = createTestDatabase()

        capture = CaptureRepository(
            database = database,
            deviceInstallationId = "installation-abc",
            organizationId = "22222222-2222-2222-2222-222222222222",
            branchId = "33333333-3333-3333-3333-333333333333",
            deviceId = "44444444-4444-4444-4444-444444444444"
        )
        outbox = OutboxRepository(database)

        val api = Retrofit.Builder()
            .baseUrl(server.url("/"))
            .client(
                OkHttpClient.Builder()
                    .callTimeout(2, TimeUnit.SECONDS)
                    .readTimeout(2, TimeUnit.SECONDS)
                    .build()
            )
            .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
            .build()
            .create(ZaziApi::class.java)

        engine = SyncEngine(database, outbox, api)
    }

    @After
    fun tearDown() {
        database.close()
        server.shutdown()
    }

    // ─── Happy path ──────────────────────────────────────────────────────────

    @Test
    fun `an accepted item becomes synced with the server transaction id`() = runTest {
        val queued = queue("500.00")
        enqueueResults(accepted(queued, "srv-1"))

        val result = engine.runOnce()

        assertThat(result.accepted).isEqualTo(1)
        assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCED.name)
        assertThat(database.localTransactionDao().findByClientId(queued)!!.serverTransactionId)
            .isEqualTo("srv-1")
    }

    @Test
    fun `the request carries no organization identifier`() = runTest {
        queue("100.00")
        enqueueResults()

        engine.runOnce()

        val body = server.takeRequest().body.readUtf8()

        // Tenancy comes from the access token. A client that could name an organization
        // would be a tenant-isolation hole.
        assertThat(body).doesNotContain("organizationId")
        assertThat(body).contains("clientTransactionId")
    }

    @Test
    fun `amounts are sent as strings so no double enters the chain`() = runTest {
        queue("1250.75")
        enqueueResults()

        engine.runOnce()

        assertThat(server.takeRequest().body.readUtf8()).contains("\"amount\":\"1250.75\"")
    }

    // ─── Lost response — the most important rule ─────────────────────────────

    @Test
    fun `a lost response converges to one transaction via duplicate`() = runTest {
        val queued = queue("400.00")

        // Attempt one: the server commits, then the connection dies before the response.
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.DISCONNECT_DURING_RESPONSE_BODY))
        val first = engine.runOnce()

        assertThat(first.blockedReason).isEqualTo(BlockedReason.OFFLINE)
        assertThat(stateOf(queued)).isEqualTo(OutboxState.RETRYABLE_FAILURE.name)

        // Attempt two: same ClientTransactionId, so the server recognises it.
        makeEligibleNow(queued)
        enqueueResults(duplicate(queued, "srv-original"))
        val second = engine.runOnce()

        assertThat(second.duplicate).isEqualTo(1)

        // DUPLICATE is a success: the server already holds it.
        assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCED.name)
        assertThat(database.localTransactionDao().findByClientId(queued)!!.serverTransactionId)
            .isEqualTo("srv-original")

        // Exactly one local transaction throughout — no second record, no second submission
        // identity.
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
    }

    @Test
    fun `the same client transaction id is resent on retry`() = runTest {
        val queued = queue("75.00")

        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        engine.runOnce()
        val firstBody = server.takeRequest().body.readUtf8()

        makeEligibleNow(queued)
        enqueueResults(accepted(queued, "srv-2"))
        engine.runOnce()
        val secondBody = server.takeRequest().body.readUtf8()

        // Regenerating the id on retry is exactly how one transaction becomes two.
        assertThat(firstBody).contains(queued)
        assertThat(secondBody).contains(queued)
    }

    @Test
    fun `a timeout never deletes queued work`() = runTest {
        val queued = queue("60.00")

        repeat(3) {
            server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
            engine.runOnce()
            makeEligibleNow(queued)
        }

        // A missing response is not proof the server did not commit, and is never proof that
        // work should be discarded.
        assertThat(database.outboxDao().findByClientId(queued)).isNotNull()
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
    }

    // ─── Partial batch ───────────────────────────────────────────────────────

    @Test
    fun `each item in a mixed batch gets its own final state`() = runTest {
        val a = queue("10.00")
        val b = queue("20.00")
        val c = queue("30.00")
        val d = queue("40.00")
        val e = queue("50.00")

        enqueueResults(
            accepted(a, "srv-a"),
            duplicate(b, "srv-b"),
            conflict(c, "conflict-1"),
            rejectedRetryable(d),
            accepted(e, "srv-e")
        )

        val result = engine.runOnce()

        assertThat(result.submitted).isEqualTo(5)

        // One failure must not drag the successes back into the queue.
        assertThat(stateOf(a)).isEqualTo(OutboxState.SYNCED.name)
        assertThat(stateOf(b)).isEqualTo(OutboxState.SYNCED.name)
        assertThat(stateOf(c)).isEqualTo(OutboxState.CONFLICT.name)
        assertThat(stateOf(d)).isEqualTo(OutboxState.RETRYABLE_FAILURE.name)
        assertThat(stateOf(e)).isEqualTo(OutboxState.SYNCED.name)
    }

    @Test
    fun `a conflict is preserved with its server identifier and stops retrying`() = runTest {
        val queued = queue("90.00")
        enqueueResults(conflict(queued, "conflict-42"))

        engine.runOnce()

        val item = database.outboxDao().findByClientId(queued)!!
        assertThat(item.state).isEqualTo(OutboxState.CONFLICT.name)
        assertThat(item.conflictId).isEqualTo("conflict-42")
        assertThat(item.nextAttemptAtUtcMillis).isNull()

        // Surfaced rather than silently dropped.
        assertThat(outbox.findNeedingAttention()).hasSize(1)
    }

    @Test
    fun `an item the server did not mention is not assumed accepted`() = runTest {
        val queued = queue("55.00")

        // Response omits the item entirely.
        enqueueResults()

        engine.runOnce()

        // Assuming success would silently mark unsent work as done.
        assertThat(stateOf(queued)).isEqualTo(OutboxState.RETRYABLE_FAILURE.name)
    }

    // ─── HTTP failure policy ─────────────────────────────────────────────────

    @Test
    fun `a server fault is retryable and preserves the item`() = runTest {
        val queued = queue("35.00")
        server.enqueue(MockResponse().setResponseCode(503))

        val result = engine.runOnce()

        assertThat(result.blockedReason).isEqualTo(BlockedReason.SERVER_UNAVAILABLE)
        assertThat(stateOf(queued)).isEqualTo(OutboxState.RETRYABLE_FAILURE.name)
    }

    @Test
    fun `rate limiting honours retry-after`() = runTest {
        val queued = queue("25.00")
        val before = System.currentTimeMillis()

        server.enqueue(MockResponse().setResponseCode(429).setHeader("Retry-After", "120"))

        engine.runOnce()

        val item = database.outboxDao().findByClientId(queued)!!

        // The server knows when it will be ready better than our backoff curve does.
        assertThat(item.state).isEqualTo(OutboxState.RETRYABLE_FAILURE.name)
        assertThat(item.nextAttemptAtUtcMillis!!).isAtLeast(before + 120_000)
    }

    @Test
    fun `an oversized batch rejection dead-letters rather than looping`() = runTest {
        val queued = queue("15.00")
        server.enqueue(MockResponse().setResponseCode(413))

        engine.runOnce()

        // Retrying the same payload unchanged can never succeed.
        assertThat(stateOf(queued)).isEqualTo(OutboxState.DEAD_LETTER.name)
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
    }

    @Test
    fun `revocation blocks the pass but preserves the work`() = runTest {
        val queued = queue("45.00")
        server.enqueue(MockResponse().setResponseCode(403))

        val result = engine.runOnce()

        assertThat(result.blockedReason).isEqualTo(BlockedReason.REVOKED)

        // A revoked device keeps its financial work; it simply cannot submit it.
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
        assertThat(database.outboxDao().findByClientId(queued)).isNotNull()
    }

    // ─── Batching and concurrency ────────────────────────────────────────────

    @Test
    fun `the batch size limit is respected`() = runTest {
        repeat(12) { queue("1.00") }
        val engineWithSmallBatches = SyncEngine(database, outbox, apiFor(), batchSizeProvider = { 5 })

        enqueueResults()
        engineWithSmallBatches.runOnce()

        val body = server.takeRequest().body.readUtf8()

        assertThat(Regex("clientTransactionId").findAll(body).count()).isEqualTo(5)
    }

    @Test
    fun `remaining work is reported so a follow-up run can be scheduled`() = runTest {
        repeat(8) { queue("2.00") }
        val engineWithSmallBatches = SyncEngine(database, outbox, apiFor(), batchSizeProvider = { 3 })

        enqueueResults()
        val result = engineWithSmallBatches.runOnce()

        // An agent back from a day offline should not drain 3 at a time on a slow schedule.
        assertThat(result.hasMoreWork).isTrue()
    }

    @Test
    fun `claiming marks items in flight so a second pass cannot take them`() = runTest {
        queue("5.00")

        // First pass claims and hangs; the item is in flight.
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.NO_RESPONSE))
        val claimed = outbox.claimBatch(10)
        assertThat(claimed).hasSize(1)

        // The database is the authority, not an in-memory lock: a concurrent worker finds
        // nothing to take.
        assertThat(outbox.claimBatch(10)).isEmpty()
    }

    @Test
    fun `an item stranded in flight past its lease is recovered`() = runTest {
        val queued = queue("8.00")

        outbox.claimBatch(10)
        assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCING.name)

        // Nothing recovered while the lease is live — a worker may genuinely be mid-request.
        assertThat(outbox.recoverStrandedItems(leaseMillis = 5 * 60 * 1000L)).isEqualTo(0)

        // Once the lease expires, the item is presumed stranded by process death.
        assertThat(outbox.recoverStrandedItems(leaseMillis = 0L)).isEqualTo(1)
        assertThat(stateOf(queued)).isEqualTo(OutboxState.PENDING.name)
    }

    @Test
    fun `a zero lease recovers an item claimed in the same millisecond`() {
        // The regression that produced the inclusive comparison in recoverStrandedInFlight.
        //
        // A frozen clock is the point: it forces the claim and the recovery pass to share a
        // timestamp, which on a real device happens when a process restarts immediately after
        // a capture. With a strict comparison the row was skipped, stayed SYNCING, and could
        // not be re-claimed — so a captured transaction sat stranded for the full lease. The
        // wall clock hid it, because a spare millisecond usually elapsed.
        kotlinx.coroutines.test.runTest {
            val queued = queue("8.00")
            // Frozen just past the row's scheduled attempt, so it is eligible — and frozen,
            // so the claim and the recovery pass share one timestamp, which is the whole
            // point of the test.
            val instant = System.currentTimeMillis() + 1_000L
            val frozen = OutboxRepository(database, now = { instant })

            frozen.claimBatch(10)
            assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCING.name)

            assertThat(frozen.recoverStrandedItems(leaseMillis = 0L)).isEqualTo(1)
            assertThat(stateOf(queued)).isEqualTo(OutboxState.PENDING.name)
        }
    }

    @Test
    fun `a live lease still protects an item claimed in the same millisecond`() {
        // The other half of the boundary: making the comparison inclusive must not let an
        // ordinary pass snatch a row from a worker that is genuinely mid-request.
        kotlinx.coroutines.test.runTest {
            val queued = queue("8.00")
            // Frozen just past the row's scheduled attempt, so it is eligible — and frozen,
            // so the claim and the recovery pass share one timestamp, which is the whole
            // point of the test.
            val instant = System.currentTimeMillis() + 1_000L
            val frozen = OutboxRepository(database, now = { instant })

            frozen.claimBatch(10)

            assertThat(frozen.recoverStrandedItems(leaseMillis = 5 * 60 * 1000L)).isEqualTo(0)
            assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCING.name)
        }
    }

    @Test
    fun `a row exactly at the lease boundary is recovered`() {
        // The inclusive edge, stated precisely rather than inferred from the zero-lease case.
        // Claim at T, then recover with a lease chosen so the boundary lands exactly on T.
        kotlinx.coroutines.test.runTest {
            val queued = queue("3.00")
            val claimedAt = System.currentTimeMillis() + 1_000L

            OutboxRepository(database, now = { claimedAt }).claimBatch(10)
            assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCING.name)

            // boundary = (claimedAt + 60_000) - 60_000 = claimedAt
            val later = OutboxRepository(database, now = { claimedAt + 60_000L })
            assertThat(later.recoverStrandedItems(leaseMillis = 60_000L)).isEqualTo(1)
            assertThat(stateOf(queued)).isEqualTo(OutboxState.PENDING.name)
        }
    }

    @Test
    fun `a row one millisecond inside the lease is not recovered`() {
        // The other side of the same edge. Making the comparison inclusive must widen the
        // claim by exactly one millisecond and no more — a row still inside its lease belongs
        // to a worker that may genuinely be mid-request, and taking it would cost a wasted
        // batch round trip.
        kotlinx.coroutines.test.runTest {
            val queued = queue("3.00")
            val claimedAt = System.currentTimeMillis() + 1_000L

            OutboxRepository(database, now = { claimedAt }).claimBatch(10)

            // boundary = (claimedAt + 60_000) - 60_001 = claimedAt - 1, so the row's last
            // attempt is one millisecond newer than the boundary and must be left alone.
            val later = OutboxRepository(database, now = { claimedAt + 60_000L })
            assertThat(later.recoverStrandedItems(leaseMillis = 60_001L)).isEqualTo(0)
            assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCING.name)
        }
    }

    @Test
    fun `an empty outbox does no work`() = runTest {
        val result = engine.runOnce()

        assertThat(result.submitted).isEqualTo(0)
        assertThat(result.hasMoreWork).isFalse()
        assertThat(server.requestCount).isEqualTo(0)
    }

    // ─── Attempt history ─────────────────────────────────────────────────────

    @Test
    fun `every attempt is recorded for support investigation`() = runTest {
        val queued = queue("12.00")

        repeat(2) {
            server.enqueue(MockResponse().setResponseCode(500))
            engine.runOnce()
            makeEligibleNow(queued)
        }

        assertThat(database.syncAttemptDao().countForTransaction(queued)).isAtLeast(2)
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private fun apiFor(): ZaziApi = Retrofit.Builder()
        .baseUrl(server.url("/"))
        .client(OkHttpClient.Builder().callTimeout(2, TimeUnit.SECONDS).build())
        .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
        .build()
        .create(ZaziApi::class.java)

    private suspend fun queue(amount: String): String {
        val outcome = capture.captureManual(
            ManualCaptureRequest(
                transactionType = TransactionType.CASH_IN,
                amount = BigDecimal(amount),
                provider = Provider.MTN,
                occurredAtUtcMillis = System.currentTimeMillis() - 60_000,
                reference = "REF-${System.nanoTime()}",
                customerPhoneNumber = "0241234567"
            )
        )
        return (outcome as CaptureOutcome.Queued).clientTransactionId
    }

    private suspend fun stateOf(clientTransactionId: String): String =
        database.outboxDao().findByClientId(clientTransactionId)!!.state

    /** Clears backoff so the next pass may claim the item immediately. */
    private suspend fun makeEligibleNow(clientTransactionId: String) {
        val item = database.outboxDao().findByClientId(clientTransactionId)!!
        database.outboxDao().update(item.copy(nextAttemptAtUtcMillis = 0L))
    }

    private fun enqueueResults(vararg results: String) {
        server.enqueue(
            MockResponse()
                .setResponseCode(200)
                .setHeader("Content-Type", "application/json")
                .setBody(
                    """
                    {
                      "batchId": "11111111-1111-1111-1111-111111111111",
                      "submitted": ${results.size},
                      "accepted": 0, "duplicate": 0, "rejected": 0, "conflict": 0,
                      "serverReceivedAtUtc": "2026-08-16T09:31:00+00:00",
                      "results": [${results.joinToString(",")}]
                    }
                    """.trimIndent()
                )
        )
    }

    private fun accepted(clientId: String, serverId: String) =
        """{"clientTransactionId":"$clientId","status":"Accepted","transactionId":"$serverId","isRetryable":false}"""

    private fun duplicate(clientId: String, serverId: String) =
        """{"clientTransactionId":"$clientId","status":"Duplicate","transactionId":"$serverId",
            "reasonCode":"DUPLICATE_CLIENT_TRANSACTION_ID","isRetryable":false}""".trimIndent()

    private fun conflict(clientId: String, conflictId: String) =
        """{"clientTransactionId":"$clientId","status":"Conflict",
            "reasonCode":"ALREADY_REVERSED","conflictId":"$conflictId","isRetryable":false}""".trimIndent()

    private fun rejectedRetryable(clientId: String) =
        """{"clientTransactionId":"$clientId","status":"Rejected",
            "reasonCode":"TEMPORARY_SERVER_ERROR","isRetryable":true}""".trimIndent()
}
