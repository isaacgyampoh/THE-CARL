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
import kotlinx.coroutines.flow.first
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
 * A detail screen must follow the row it is showing, not a copy of it.
 *
 * <p>The bug: the screen held the snapshot it was opened with. Retrying from that screen
 * left it reading "Sending" after the engine had delivered the transaction, because the row
 * had moved on and nothing re-read it. These assert against the same observed query the
 * screen collects, so they fail if it ever goes back to a one-shot read.</p>
 */
@RunWith(RobolectricTestRunner::class)
class DetailObservationTest {

    private lateinit var server: MockWebServer
    private lateinit var database: ZaziDatabase
    private lateinit var capture: CaptureRepository
    private lateinit var outbox: OutboxRepository
    private lateinit var dashboard: DashboardRepository
    private lateinit var engine: SyncEngine

    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }

    /** Supplied clock, as in DeadLetterRetryTest: real backoff without waiting it out. */
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

    @Test
    fun `a dead letter seen through the observed detail reports itself retryable`() = runTest {
        val queued = deadLetter("120.25")

        val detail = dashboard.observeDetail(queued).first()!!

        assertThat(detail.outboxState).isEqualTo(OutboxState.DEAD_LETTER.name)
        assertThat(detail.attemptCount).isEqualTo(RetryPolicy.MAX_ATTEMPTS)
    }

    @Test
    fun `retrying moves the observed detail off dead letter without anything reopening it`() = runTest {
        val queued = deadLetter("64.00")

        assertThat(dashboard.observeDetail(queued).first()!!.outboxState)
            .isEqualTo(OutboxState.DEAD_LETTER.name)

        dashboard.retryDeadLettered(queued, clock)

        // The same query, read again from the same source of truth. This is what the open
        // screen collects, so an emission here is the screen updating in place.
        val afterRetry = dashboard.observeDetail(queued).first()!!

        assertThat(afterRetry.outboxState).isEqualTo(OutboxState.PENDING.name)
        // The retry action must disappear the moment the row is no longer a dead letter.
        assertThat(afterRetry.outboxState).isNotEqualTo(OutboxState.DEAD_LETTER.name)
        assertThat(afterRetry.attemptCount).isEqualTo(0)
    }

    @Test
    fun `a delivered transaction reaches the observed detail as synced`() = runTest {
        val queued = deadLetter("77.50")
        dashboard.retryDeadLettered(queued, clock)

        enqueueAccepted(queued, "srv-observed")
        engine.runOnce()

        // This is the state the open screen was previously stuck short of: it kept showing
        // the snapshot taken at retry time and never saw the delivery.
        val delivered = dashboard.observeDetail(queued).first()!!

        assertThat(delivered.outboxState).isEqualTo(OutboxState.SYNCED.name)
        assertThat(delivered.transaction.serverTransactionId).isEqualTo("srv-observed")
    }

    @Test
    fun `a conflict stays a conflict through the observed detail`() = runTest {
        val queued = queue("410.00")
        enqueueResults(conflict(queued, "conflict-observed"))
        engine.runOnce()

        val detail = dashboard.observeDetail(queued).first()!!
        assertThat(detail.outboxState).isEqualTo(OutboxState.CONFLICT.name)

        // Observing must not make a conflict retryable. The guard is still the UPDATE.
        assertThat(dashboard.retryDeadLettered(queued, clock)).isFalse()
        assertThat(dashboard.observeDetail(queued).first()!!.outboxState)
            .isEqualTo(OutboxState.CONFLICT.name)
    }

    @Test
    fun `observing the detail sends no request of its own`() = runTest {
        val queued = queue("18.00")
        val before = server.requestCount

        // Several reads of the observed query, as a screen redrawing would produce.
        repeat(5) { dashboard.observeDetail(queued).first() }

        // Room is a local read. A screen that follows a row must not turn redraws into
        // traffic, and must not trigger a second sync of the same transaction.
        assertThat(server.requestCount).isEqualTo(before)
        assertThat(database.syncAttemptDao().countForTransaction(queued)).isEqualTo(0)
    }

    @Test
    fun `one retry remains one retry even while the detail is being observed`() = runTest {
        val queued = deadLetter("31.00")

        dashboard.observeDetail(queued).first()
        assertThat(dashboard.retryDeadLettered(queued, clock)).isTrue()
        dashboard.observeDetail(queued).first()
        // The row is PENDING now, so the guarded UPDATE matches nothing on a second tap.
        assertThat(dashboard.retryDeadLettered(queued, clock)).isFalse()

        enqueueAccepted(queued, "srv-once")
        engine.runOnce()

        // Exactly one transaction and one delivery, regardless of how often it was observed.
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
        assertThat(server.requestCount).isEqualTo(RetryPolicy.MAX_ATTEMPTS + 1)
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private suspend fun failOnce() {
        server.enqueue(MockResponse().setResponseCode(503))
        engine.runOnce()
        val next = database.outboxDao().findByClientId(lastQueuedId)!!.nextAttemptAtUtcMillis
        if (next != null && next > clock) clock = next
    }

    private suspend fun deadLetter(amount: String): String {
        val queued = queue(amount)
        repeat(RetryPolicy.MAX_ATTEMPTS) { failOnce() }
        check(database.outboxDao().findByClientId(queued)!!.state == OutboxState.DEAD_LETTER.name)
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
