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
 * "N sent today" must mean today.
 *
 * <p>The bug: the dashboard counted every outbox row this handset had ever delivered and
 * labelled it "sent today". On a day with one transaction and four behind it, an agent read
 * "5 sent today" above a list holding one row, which is the kind of disagreement that makes
 * a person stop trusting the screen at closing time.</p>
 */
@RunWith(RobolectricTestRunner::class)
class SentTodayCountTest {

    private lateinit var server: MockWebServer
    private lateinit var database: ZaziDatabase
    private lateinit var capture: CaptureRepository
    private lateinit var dashboard: DashboardRepository
    private lateinit var engine: SyncEngine

    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }

    /** Midday, so "yesterday" and "tomorrow" are unambiguous either side of it. */
    private val dayStart = 1_700_000_000_000L / DAY_MILLIS * DAY_MILLIS
    private val now = dayStart + DAY_MILLIS / 2

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
            now = { now }
        )
        dashboard = DashboardRepository(database)

        val outbox = OutboxRepository(database, now = { now })
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
    fun `yesterday's delivered transactions are not counted as sent today`() = runTest {
        deliver(queue("50.00", at = now - DAY_MILLIS))
        deliver(queue("100.00", at = now - DAY_MILLIS))
        deliver(queue("75.00", at = now))

        // Three delivered in all, one of them today. This is the figure the dashboard prints.
        assertThat(dashboard.syncedCount(dayStart, dayStart + DAY_MILLIS)).isEqualTo(1)
        assertThat(dashboard.syncedCount(dayStart - DAY_MILLIS, dayStart)).isEqualTo(2)
    }

    @Test
    fun `a transaction still waiting to sync is not counted as sent`() = runTest {
        deliver(queue("30.00", at = now))
        queue("20.00", at = now)

        assertThat(dashboard.syncedCount(dayStart, dayStart + DAY_MILLIS)).isEqualTo(1)
    }

    @Test
    fun `the count agrees with the list the agent is looking at`() = runTest {
        deliver(queue("40.00", at = now - DAY_MILLIS))
        deliver(queue("60.00", at = now))
        deliver(queue("80.00", at = now))

        val listed = database.localTransactionDao()
            .observeBetweenWithDelivery(dayStart, dayStart + DAY_MILLIS, 25)
            .first()

        assertThat(dashboard.syncedCount(dayStart, dayStart + DAY_MILLIS)).isEqualTo(listed.size)
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private suspend fun queue(amount: String, at: Long): String {
        val outcome = capture.captureManual(
            ManualCaptureRequest(
                transactionType = TransactionType.CASH_IN,
                amount = BigDecimal(amount),
                provider = Provider.MTN,
                occurredAtUtcMillis = at,
                reference = "REF-${System.nanoTime()}",
                customerPhoneNumber = "0241234567"
            )
        )
        return (outcome as CaptureOutcome.Queued).clientTransactionId
    }

    private suspend fun deliver(clientId: String) {
        server.enqueue(
            MockResponse()
                .setResponseCode(200)
                .setHeader("Content-Type", "application/json")
                .setBody(
                    """
                    {
                      "batchId": "11111111-1111-1111-1111-111111111111",
                      "submitted": 1,
                      "accepted": 1, "duplicate": 0, "rejected": 0, "conflict": 0,
                      "serverReceivedAtUtc": "2026-08-16T09:31:00+00:00",
                      "results": [
                        {"clientTransactionId":"$clientId","status":"Accepted",
                         "transactionId":"srv-$clientId","isRetryable":false}
                      ]
                    }
                    """.trimIndent()
                )
        )
        engine.runOnce()
    }

    private companion object {
        const val DAY_MILLIS = 24L * 60 * 60 * 1000
    }
}
