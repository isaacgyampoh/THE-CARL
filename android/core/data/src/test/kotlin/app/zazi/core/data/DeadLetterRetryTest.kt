package app.zazi.core.data

import app.zazi.core.data.capture.CaptureOutcome
import app.zazi.core.data.capture.ManualCaptureRequest
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.network.ZaziApi
import app.zazi.core.data.repository.CaptureRepository
import app.zazi.core.data.repository.DashboardRepository
import app.zazi.core.data.repository.OutboxRepository
import app.zazi.core.data.sync.SyncEngine
import app.zazi.core.domain.model.Provider
import app.zazi.core.domain.model.TransactionType
import app.zazi.core.domain.sync.OutboxState
import app.zazi.core.domain.sync.RetryPolicy
import com.google.common.truth.Truth.assertThat
import java.math.BigDecimal
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory

/**
 * The dead-letter retry an agent can perform, end to end through the real pipeline.
 *
 * <p>Real Room, real HTTP, the real SyncEngine and the real RetryPolicy. The clock is the
 * only thing supplied, because exhausting a genuine ten-attempt budget otherwise means
 * waiting out seventeen minutes of exponential backoff — the delays are real and are what
 * is being exercised, they are simply not waited for.</p>
 */
@RunWith(RobolectricTestRunner::class)
class DeadLetterRetryTest {

    private lateinit var server: MockWebServer
    private lateinit var database: ZaziDatabase
    private lateinit var capture: CaptureRepository
    private lateinit var outbox: OutboxRepository
    private lateinit var dashboard: DashboardRepository
    private lateinit var engine: SyncEngine

    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }

    /** Supplied clock. Advanced explicitly; never read from the machine. */
    private var clock = 1_700_000_000_000L

    @Before
    fun setUp() {
        server = MockWebServer().apply { start() }
        database = createTestDatabase()

        capture = CaptureRepository(
            database = database,
            deviceInstallationId = "installation-abc",
            organizationId = "22222222-2222-2222-2222-222222222222",
            branchId = "33333333-3333-3333-3333-333333333333",
            deviceId = "44444444-4444-4444-4444-444444444444",
            // The same supplied clock as the outbox. Capture stamps the row's first
            // nextAttemptAtUtcMillis, so two different clocks leave the item permanently
            // ineligible and every pass silently does nothing.
            now = { clock }
        )
        outbox = OutboxRepository(database, now = { clock })
        dashboard = DashboardRepository(database)

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

    // ─── Reaching a dead letter honestly ─────────────────────────────────────

    @Test
    fun `a transaction dead-letters only once its retry budget is exhausted`() = runTest {
        val queued = queue("310.00")

        // Every attempt but the last must leave the item retryable. A dead letter that
        // arrived early would strand an agent's transaction while budget remained.
        repeat(RetryPolicy.MAX_ATTEMPTS - 1) { pass ->
            failOnce()

            val item = itemFor(queued)
            assertThat(item.state).isEqualTo(OutboxState.RETRYABLE_FAILURE.name)
            assertThat(item.attemptCount).isEqualTo(pass + 1)
        }

        failOnce()

        val exhausted = itemFor(queued)
        assertThat(exhausted.state).isEqualTo(OutboxState.DEAD_LETTER.name)
        assertThat(exhausted.attemptCount).isEqualTo(RetryPolicy.MAX_ATTEMPTS)

        // The agent's own record is untouched by the delivery failure. Losing the
        // transaction because it could not be posted would be the worst possible outcome.
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
    }

    // ─── The retry itself ────────────────────────────────────────────────────

    @Test
    fun `retrying a dead letter requeues it, resets the budget and leaves one transaction`() = runTest {
        val queued = deadLetter("120.25")

        val attemptsBefore = database.syncAttemptDao().countForTransaction(queued)
        assertThat(attemptsBefore).isEqualTo(RetryPolicy.MAX_ATTEMPTS)

        val requeued = dashboard.retryDeadLettered(queued, clock)

        assertThat(requeued).isTrue()

        val item = itemFor(queued)
        assertThat(item.state).isEqualTo(OutboxState.PENDING.name)
        // Reset, because this is a fresh decision by a person rather than a continuation of
        // the schedule that gave up. Leaving it at ten would dead-letter again on first fail.
        assertThat(item.attemptCount).isEqualTo(0)
        assertThat(item.nextAttemptAtUtcMillis).isEqualTo(clock)

        // No second transaction, and no invented attempt history: a retry re-queues the
        // existing row, it does not resubmit a copy.
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
        assertThat(database.syncAttemptDao().countForTransaction(queued)).isEqualTo(attemptsBefore)
    }

    @Test
    fun `the requeued transaction goes on to sync normally and records the attempt`() = runTest {
        val queued = deadLetter("77.50")
        dashboard.retryDeadLettered(queued, clock)

        val attemptsBefore = database.syncAttemptDao().countForTransaction(queued)

        enqueueAccepted(queued, "srv-after-retry")
        engine.runOnce()

        assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCED.name)
        assertThat(database.localTransactionDao().findByClientId(queued)!!.serverTransactionId)
            .isEqualTo("srv-after-retry")

        // The successful attempt is recorded too, so the history shows the whole story
        // rather than only the failures.
        assertThat(database.syncAttemptDao().countForTransaction(queued))
            .isEqualTo(attemptsBefore + 1)

        // Still one transaction. The same ClientTransactionId went back on the wire, which
        // is what lets the server deduplicate if it had in fact committed earlier.
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
    }

    @Test
    fun `retrying twice does nothing the second time`() = runTest {
        val queued = deadLetter("64.00")

        assertThat(dashboard.retryDeadLettered(queued, clock)).isTrue()
        // The row is PENDING now, so the guarded UPDATE matches nothing. A double tap, or a
        // stale screen, cannot reset the budget of an item already back in the queue.
        assertThat(dashboard.retryDeadLettered(queued, clock)).isFalse()

        assertThat(stateOf(queued)).isEqualTo(OutboxState.PENDING.name)
    }

    // ─── The conflict boundary ───────────────────────────────────────────────

    @Test
    fun `a conflict cannot be retried, even by calling the database directly`() = runTest {
        val queued = queue("410.00")

        enqueueResults(conflict(queued, "conflict-1"))
        engine.runOnce()

        assertThat(stateOf(queued)).isEqualTo(OutboxState.CONFLICT.name)

        // Bypassing the UI entirely and invoking the retry against the DAO: the WHERE clause
        // is the authority, not the screen that decided which button to draw. The server
        // disagreed, and re-sending the same payload would be refused identically.
        val rows = database.outboxDao().requeueDeadLettered(queued, clock)

        assertThat(rows).isEqualTo(0)
        assertThat(stateOf(queued)).isEqualTo(OutboxState.CONFLICT.name)
    }

    @Test
    fun `a state change between drawing the screen and tapping cannot retry a conflict`() = runTest {
        val queued = deadLetter("88.00")

        // The screen was drawn while this was a dead letter, so it offered "Try again". By
        // the time the tap arrives the item has become a conflict — a later pass reached the
        // server and it disagreed. The guard is in the UPDATE, so the stale tap does nothing.
        val item = itemFor(queued)
        database.outboxDao().update(item.copy(state = OutboxState.CONFLICT.name))

        assertThat(dashboard.retryDeadLettered(queued, clock)).isFalse()
        assertThat(stateOf(queued)).isEqualTo(OutboxState.CONFLICT.name)
        // And the budget was not reset behind the conflict's back.
        assertThat(itemFor(queued).attemptCount).isEqualTo(RetryPolicy.MAX_ATTEMPTS)
    }

    @Test
    fun `an in-flight item cannot be retried`() = runTest {
        val queued = deadLetter("33.00")

        val item = itemFor(queued)
        database.outboxDao().update(item.copy(state = OutboxState.SYNCING.name))

        // Re-queueing a row a worker is mid-request on would submit it twice.
        assertThat(dashboard.retryDeadLettered(queued, clock)).isFalse()
        assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCING.name)
    }

    @Test
    fun `a synced item cannot be retried`() = runTest {
        val queued = queue("12.00")
        enqueueAccepted(queued, "srv-done")
        engine.runOnce()

        assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCED.name)
        assertThat(dashboard.retryDeadLettered(queued, clock)).isFalse()
        assertThat(stateOf(queued)).isEqualTo(OutboxState.SYNCED.name)
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /** Drives one failing pass, advancing the supplied clock past the backoff it sets. */
    private suspend fun failOnce() {
        server.enqueue(MockResponse().setResponseCode(503))
        engine.runOnce()

        // Honour the backoff the policy actually set rather than clearing it: the schedule
        // is part of what dead-lettering depends on.
        val next = database.outboxDao().findByClientId(lastQueuedId)!!.nextAttemptAtUtcMillis
        if (next != null && next > clock) {
            clock = next
        }
    }

    /** Takes a transaction all the way to DEAD_LETTER through real failures. */
    private suspend fun deadLetter(amount: String): String {
        val queued = queue(amount)
        repeat(RetryPolicy.MAX_ATTEMPTS) { failOnce() }
        check(stateOf(queued) == OutboxState.DEAD_LETTER.name) {
            "Expected a dead letter, got ${stateOf(queued)}"
        }
        return queued
    }

    private var lastQueuedId: String = ""

    private suspend fun queue(amount: String): String {
        val outcome = capture.captureManual(
            ManualCaptureRequest(
                transactionType = TransactionType.CASH_IN,
                amount = BigDecimal(amount),
                provider = Provider.MTN,
                occurredAtUtcMillis = clock - 60_000,
                reference = "REF-${System.nanoTime()}",
                customerPhoneNumber = "0241234567"
            )
        )
        lastQueuedId = (outcome as CaptureOutcome.Queued).clientTransactionId
        return lastQueuedId
    }

    private suspend fun itemFor(clientTransactionId: String) =
        database.outboxDao().findByClientId(clientTransactionId)!!

    private suspend fun stateOf(clientTransactionId: String): String =
        itemFor(clientTransactionId).state

    private fun enqueueAccepted(clientId: String, serverId: String) = enqueueResults(
        """{"clientTransactionId":"$clientId","status":"Accepted","transactionId":"$serverId","isRetryable":false}"""
    )

    private fun conflict(clientId: String, conflictId: String) =
        """{"clientTransactionId":"$clientId","status":"Conflict",
            "reasonCode":"ALREADY_REVERSED","conflictId":"$conflictId","isRetryable":false}""".trimIndent()

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
}
