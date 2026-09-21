package app.zazi.core.data

import app.zazi.core.data.capture.CaptureOutcome
import app.zazi.core.data.capture.ManualCaptureRequest
import app.zazi.core.data.capture.SmsCaptureRequest
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.network.ZaziApi
import app.zazi.core.data.repository.CaptureRepository
import app.zazi.core.data.repository.ParsingReportRepository
import app.zazi.core.data.repository.ParsingVerdict
import app.zazi.core.data.repository.ReportOutcome
import app.zazi.core.data.repository.ReportUnavailable
import app.zazi.core.domain.model.Provider
import app.zazi.core.domain.model.TransactionType
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
 * The one path by which a provider's message leaves this handset.
 *
 * <p>Sync carries the parsed result and never the text, which is the right default for
 * software sitting on other people's financial correspondence — and it is also why nobody who
 * could fix the parser can see the message that caused a wrong reading. Both parsing defects
 * found this week produced high-confidence wrong answers, which the evidence-quality gate
 * cannot catch, so the agent is the only party who knows.</p>
 *
 * <p>These assert the properties that make the exception safe to make: the body goes verbatim,
 * it goes only when asked, and nothing else goes with it.</p>
 */
@RunWith(RobolectricTestRunner::class)
class ParsingReportTest {

    /** The message that caused the direction defect: "cash in" inside a cash-out. */
    private val messageThatWasMisread =
        "Confirmed. Cash Out of GHS 250.00 to 0241000002. " +
            "Your cash in hand is now GHS 1,750.00. Ref: MM240815003"

    private lateinit var server: MockWebServer
    private lateinit var database: ZaziDatabase
    private lateinit var capture: CaptureRepository
    private lateinit var reports: ParsingReportRepository

    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }
    private val receivedAt = 1_700_000_000_000L

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
            now = { receivedAt }
        )

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

        reports = ParsingReportRepository(database, api, appVersion = "2.0.0")
    }

    @After
    fun tearDown() {
        database.close()
        server.shutdown()
    }

    @Test
    fun `the message is sent exactly as it arrived`() = runTest {
        val queued = captureSms(messageThatWasMisread)
        server.enqueue(accepted())

        val outcome = reports.report(queued, ParsingVerdict.WRONG_DIRECTION, note = "It was a withdrawal.")

        assertThat(outcome).isInstanceOf(ReportOutcome.Sent::class.java)

        val body = server.takeRequest().body.readUtf8()
        val sent = json.parseToJsonElement(body)

        // Character for character. The clause that caused the defect sits at the very end of
        // the message, which is exactly what a trim or a normalise would take.
        assertThat(sent.stringField("rawMessage")).isEqualTo(messageThatWasMisread)
        assertThat(sent.stringField("verdict")).isEqualTo("WrongDirection")
        assertThat(sent.stringField("note")).isEqualTo("It was a withdrawal.")
    }

    @Test
    fun `what the parser made of it is sent alongside, because the report is a comparison`() = runTest {
        val queued = captureSms(messageThatWasMisread)
        server.enqueue(accepted())

        reports.report(queued, ParsingVerdict.WRONG_DIRECTION)

        val sent = json.parseToJsonElement(server.takeRequest().body.readUtf8())

        // Without this, whoever reads the report has the message but not the reading it is
        // complaining about, and has to guess which build produced what.
        assertThat(sent.stringField("observedNetwork")).isNotEmpty()
        assertThat(sent.stringField("observedType")).isNotEmpty()
        assertThat(sent.stringField("parserVersion")).isNotEmpty()
        assertThat(sent.stringField("appVersion")).isEqualTo("2.0.0")
    }

    @Test
    fun `nothing is sent until an agent asks`() = runTest {
        captureSms(messageThatWasMisread)

        // Capturing, storing and queueing a transaction for sync must not, on its own, put a
        // customer's message on the wire. The report call below is the only thing that does.
        assertThat(server.requestCount).isEqualTo(0)
    }

    @Test
    fun `a transaction typed in by hand has no message to report`() = runTest {
        val outcome = capture.captureManual(
            ManualCaptureRequest(
                provider = Provider.MTN,
                transactionType = TransactionType.CASH_OUT,
                amount = BigDecimal("250.00"),
                occurredAtUtcMillis = receivedAt,
                reference = "typed-by-hand",
                customerPhoneNumber = "0241234567"
            )
        )
        val queued = (outcome as CaptureOutcome.Queued).clientTransactionId

        assertThat(reports.canReport(queued)).isEqualTo(ReportUnavailable.NOT_FROM_A_MESSAGE)
        assertThat(reports.report(queued, ParsingVerdict.WRONG_AMOUNT))
            .isEqualTo(ReportOutcome.Unavailable(ReportUnavailable.NOT_FROM_A_MESSAGE))

        // And nothing was sent on the way to finding that out.
        assertThat(server.requestCount).isEqualTo(0)
    }

    @Test
    fun `a message cleared by the retention purge cannot be reported`() = runTest {
        val queued = captureSms(messageThatWasMisread)

        // What the purge does on its schedule: the transaction stays, the body goes.
        database.evidenceDao().purgeRawMessagesOlderThan(
            olderThanUtcMillis = receivedAt + 1,
            nowUtcMillis = receivedAt + 1
        )

        assertThat(reports.canReport(queued)).isEqualTo(ReportUnavailable.MESSAGE_PURGED)

        // Sending an empty report would spend the agent's goodwill and tell nobody anything.
        assertThat(reports.report(queued, ParsingVerdict.WRONG_DIRECTION))
            .isEqualTo(ReportOutcome.Unavailable(ReportUnavailable.MESSAGE_PURGED))
        assertThat(server.requestCount).isEqualTo(0)
    }

    @Test
    fun `a report the server already had is still a success`() = runTest {
        val queued = captureSms(messageThatWasMisread)
        server.enqueue(
            MockResponse().setResponseCode(202)
                .setHeader("Content-Type", "application/json")
                .setBody("""{"reportId":"11111111-1111-1111-1111-111111111111","alreadyReported":true}""")
        )

        val outcome = reports.report(queued, ParsingVerdict.WRONG_DIRECTION)

        // A second tap on a slow connection is the likeliest cause. Telling an agent their
        // report failed when it did not is how they learn to stop reporting.
        assertThat(outcome).isEqualTo(ReportOutcome.Sent(alreadyReported = true))
    }

    @Test
    fun `a refused report is not retried behind the agent's back`() = runTest {
        val queued = captureSms(messageThatWasMisread)
        server.enqueue(MockResponse().setResponseCode(500))

        val outcome = reports.report(queued, ParsingVerdict.WRONG_DIRECTION)

        assertThat(outcome).isEqualTo(ReportOutcome.Failed(statusCode = 500))

        // Exactly one attempt. The outbox retries indefinitely, and a customer's message
        // re-sending itself while the network flaps is not what the agent agreed to.
        assertThat(server.requestCount).isEqualTo(1)
    }

    @Test
    fun `an unreachable server fails without throwing at the screen`() = runTest {
        val queued = captureSms(messageThatWasMisread)
        server.shutdown()

        val outcome = reports.report(queued, ParsingVerdict.WRONG_DIRECTION)

        assertThat(outcome).isEqualTo(ReportOutcome.Failed(statusCode = null))
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private suspend fun captureSms(body: String, sender: String = "MTN"): String {
        val outcome = capture.captureSms(
            SmsCaptureRequest(
                senderIdentity = sender,
                body = body,
                receivedAtUtcMillis = receivedAt
            )
        )
        return (outcome as CaptureOutcome.Queued).clientTransactionId
    }

    private fun accepted() = MockResponse().setResponseCode(202)
        .setHeader("Content-Type", "application/json")
        .setBody("""{"reportId":"11111111-1111-1111-1111-111111111111","alreadyReported":false}""")

    private fun kotlinx.serialization.json.JsonElement.stringField(name: String): String =
        (this as kotlinx.serialization.json.JsonObject)[name]
            ?.let { (it as kotlinx.serialization.json.JsonPrimitive).content }
            ?: ""
}
