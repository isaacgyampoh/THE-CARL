package app.zazi.core.data

import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.telemetry.LocalTelemetryRecorder
import app.zazi.core.data.telemetry.TelemetryInterceptor
import app.zazi.core.domain.telemetry.TelemetryErrorCode
import app.zazi.core.domain.telemetry.TelemetryEvent
import app.zazi.core.domain.telemetry.TelemetryEventType
import app.zazi.core.domain.telemetry.TelemetrySeverity
import com.google.common.truth.Truth.assertThat
import java.net.SocketTimeoutException
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.runTest
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.SocketPolicy
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/**
 * Client telemetry: what it records, what it refuses to record, and what it must never break.
 *
 * <p>The security assertions here are the important ones. Telemetry travels off the device to
 * a table an operator can read, so a field that could carry a token or a message body is a
 * data-exfiltration path, not a diagnostic.</p>
 */
@RunWith(RobolectricTestRunner::class)
class TelemetryTest {

    private lateinit var database: ZaziDatabase
    private lateinit var server: MockWebServer

    @Before
    fun setUp() {
        database = createTestDatabase()
        server = MockWebServer().apply { start() }
    }

    @After
    fun tearDown() {
        database.close()
        runCatching { server.shutdown() }
    }

    // ─── Recording ───────────────────────────────────────────────────────────

    @Test
    fun `an event is queued for later delivery`() = runTest {
        LocalTelemetryRecorder(database, ioDispatcher = kotlinx.coroutines.Dispatchers.Unconfined).record(
            TelemetryEvent(TelemetryEventType.APP_START)
        )

        assertThat(database.telemetryDao().count()).isEqualTo(1)
        assertThat(database.telemetryDao().oldest(10).first().eventType).isEqualTo("APP_START")
    }

    @Test
    fun `the queue is bounded and keeps the newest events`() = runTest {
        val recorder = LocalTelemetryRecorder(database, maxEvents = 5, ioDispatcher = kotlinx.coroutines.Dispatchers.Unconfined)

        // Recent timestamps: anything outside the retention window is discarded on age
        // before the size limit is ever reached.
        val now = System.currentTimeMillis()

        repeat(20) { index ->
            recorder.record(
                TelemetryEvent(
                    eventType = TelemetryEventType.API_REQUEST,
                    details = "request $index",
                    occurredAtUtcMillis = now + index
                )
            )
        }

        // A handset out of signal for a week must not fill its own storage with diagnostics.
        assertThat(database.telemetryDao().count()).isEqualTo(5)

        // The newest are kept, because they describe whatever is wrong now.
        val remaining = database.telemetryDao().oldest(10).map { it.details }
        assertThat(remaining).contains("request 19")
        assertThat(remaining).doesNotContain("request 0")
    }

    @Test
    fun `events older than the retention window are discarded`() = runTest {
        val now = 10_000_000_000L
        val recorder = LocalTelemetryRecorder(
            database, retentionMillis = 1_000L, now = { now },
            ioDispatcher = kotlinx.coroutines.Dispatchers.Unconfined)

        recorder.record(
            TelemetryEvent(TelemetryEventType.APP_START, occurredAtUtcMillis = now - 50_000L))
        recorder.record(
            TelemetryEvent(TelemetryEventType.APP_START, occurredAtUtcMillis = now))

        assertThat(database.telemetryDao().count()).isEqualTo(1)
    }

    @Test
    fun `a recorder whose database is gone does not throw`() = runTest {
        val recorder = LocalTelemetryRecorder(database, ioDispatcher = kotlinx.coroutines.Dispatchers.Unconfined)
        database.close()

        // The operation being described has already happened. Telemetry failing afterwards
        // must not surface as an error to the caller.
        recorder.record(TelemetryEvent(TelemetryEventType.LOGIN_SUCCESS))
    }

    // ─── HTTP instrumentation ────────────────────────────────────────────────

    @Test
    fun `a correlation id is generated, sent, and recorded against the request`() {
        val scope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.Dispatchers.Unconfined)
        val client = clientWith(scope)

        server.enqueue(MockResponse().setResponseCode(200))
        client.newCall(Request.Builder().url(server.url("/api/v1/devices/me")).build())
            .execute().close()

        // The header the backend already reads. No competing scheme is introduced.
        val sent = server.takeRequest().getHeader(TelemetryInterceptor.CORRELATION_HEADER)
        assertThat(sent).isNotEmpty()

        val recorded = awaitEvents(1)
        assertThat(recorded).isNotEmpty()
        assertThat(recorded.first().correlationId).isEqualTo(sent)
        assertThat(recorded.first().durationMillis).isNotNull()
    }

    @Test
    fun `server refusals and unreachable servers are classified differently`() {
        val scope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.Dispatchers.Unconfined)
        val client = clientWith(scope)

        server.enqueue(MockResponse().setResponseCode(500))
        client.newCall(Request.Builder().url(server.url("/api/v1/sync/transactions")).build())
            .execute().close()

        server.enqueue(MockResponse().setResponseCode(404))
        client.newCall(Request.Builder().url(server.url("/api/v1/devices/me")).build())
            .execute().close()

        // A port nothing is listening on: the phone cannot reach a server at all, which is a
        // different problem from a server that answered and refused.
        runCatching {
            client.newCall(
                Request.Builder().url("http://127.0.0.1:1/api/v1/sync/transactions").build()
            ).execute().close()
        }

        val codes = awaitEvents(3).mapNotNull { it.errorCode }

        // The distinction field support needs first. All three look like "it didn't work" to
        // an agent and mean entirely different things to whoever is investigating.
        assertThat(codes).contains(TelemetryErrorCode.ANDROID_API_5XX.name)
        assertThat(codes).contains(TelemetryErrorCode.ANDROID_API_4XX.name)
        assertThat(codes.any { it.startsWith("ANDROID_") && it !in HTTP_CODES }).isTrue()
    }

    @Test
    fun `a successful request is not reported as a failure`() {
        val scope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.Dispatchers.Unconfined)
        val client = clientWith(scope)

        server.enqueue(MockResponse().setResponseCode(200))
        client.newCall(Request.Builder().url(server.url("/api/v1/devices/me")).build())
            .execute().close()

        val recorded = awaitEvents(1).first()
        assertThat(recorded.errorCode).isNull()
        assertThat(recorded.severity).isEqualTo(TelemetrySeverity.INFORMATION.name)
    }

    // ─── Security ────────────────────────────────────────────────────────────

    @Test
    fun `no header, token or body reaches the telemetry queue`() {
        val scope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.Dispatchers.Unconfined)
        val client = clientWith(scope)

        server.enqueue(MockResponse().setResponseCode(401).setBody("""{"error":"bad password"}"""))

        client.newCall(
            Request.Builder()
                .url(server.url("/api/v1/auth/login?email=agent@example.test"))
                .header("Authorization", "Bearer super-secret-token")
                .header("Cookie", "session=secret-cookie")
                .post("""{"password":"hunter2"}""".toRequestBody())
                .build()
        ).execute().close()

        val stored = awaitEvents(1)
            .joinToString(" ") { listOfNotNull(it.details, it.errorCode, it.correlationId).joinToString(" ") }

        // Telemetry must never become a way to move secrets off the device.
        assertThat(stored).doesNotContain("super-secret-token")
        assertThat(stored).doesNotContain("Bearer")
        assertThat(stored).doesNotContain("secret-cookie")
        assertThat(stored).doesNotContain("hunter2")
        assertThat(stored).doesNotContain("bad password")

        // The query string carried an address; only the path is kept.
        assertThat(stored).doesNotContain("agent@example.test")
        assertThat(stored).contains("/api/v1/auth/login")
    }

    @Test
    fun `identifiers in a path are normalised so grouping stays stable`() {
        val scope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.Dispatchers.Unconfined)
        val client = clientWith(scope)

        server.enqueue(MockResponse().setResponseCode(200))
        client.newCall(
            Request.Builder()
                .url(server.url("/api/v1/transactions/8f14e45f-ceea-467a-9575-9f0b9692a0d1"))
                .build()
        ).execute().close()

        // Without this, one failing endpoint becomes one error group per transaction and the
        // recurring problem disappears into the noise.
        val details = awaitEvents(1).first().details!!
        assertThat(details).doesNotContain("8f14e45f")
        assertThat(details).contains("{id}")
    }

    // A short read timeout so a withheld response surfaces as a timeout promptly and
    // deterministically, rather than after OkHttp's ten-second default.
    /**
     * Waits for [expected] events to be durable.
     *
     * <p>Recording is deliberately asynchronous — it must never block the operation it
     * describes — so reading straight after a request races the write. Polling here tests the
     * behaviour that matters without pretending the write is synchronous.</p>
     */
    private fun awaitEvents(expected: Int, timeoutMillis: Long = 5_000): List<app.zazi.core.data.database.TelemetryEventEntity> {
        val deadline = System.currentTimeMillis() + timeoutMillis

        while (System.currentTimeMillis() < deadline) {
            val rows = runBlocking { database.telemetryDao().oldest(50) }
            if (rows.size >= expected) {
                return rows
            }
            Thread.sleep(25)
        }

        return runBlocking { database.telemetryDao().oldest(50) }
    }

    private companion object {
        val HTTP_CODES = setOf(
            TelemetryErrorCode.ANDROID_API_4XX.name,
            TelemetryErrorCode.ANDROID_API_5XX.name
        )
    }

    private fun clientWith(scope: kotlinx.coroutines.CoroutineScope) = OkHttpClient.Builder()
        .readTimeout(java.time.Duration.ofMillis(400))
        .addInterceptor(
            TelemetryInterceptor(
                LocalTelemetryRecorder(
                    database,
                    ioDispatcher = kotlinx.coroutines.Dispatchers.Unconfined
                ),
                scope
            )
        )
        .build()
}
