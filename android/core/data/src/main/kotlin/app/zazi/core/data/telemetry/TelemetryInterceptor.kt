package app.zazi.core.data.telemetry

import app.zazi.core.domain.telemetry.TelemetryErrorCode
import app.zazi.core.domain.telemetry.TelemetryEvent
import app.zazi.core.domain.telemetry.TelemetryEventType
import app.zazi.core.domain.telemetry.TelemetrySeverity
import app.zazi.core.domain.telemetry.TelemetryStatus
import java.io.IOException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.util.UUID
import javax.net.ssl.SSLException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import okhttp3.Interceptor
import okhttp3.Request
import okhttp3.Response

/**
 * Tags every request with a correlation id and records how it went.
 *
 * <p>Placed on the client rather than at each call site so a new endpoint is observable the
 * day it is added. It records method, a normalised route, duration and outcome — never a
 * URL with query values, never a header, never a body.</p>
 *
 * <p>The correlation id is generated here when the caller has not supplied one, sent as
 * <c>X-Correlation-Id</c>, and echoed by the server onto its own audit entry. That is what
 * puts a handset's attempt and the server's answer on one timeline.</p>
 *
 * <p><b>Recording happens off the request's thread and cannot fail it.</b> A request that had
 * to wait for its own telemetry would be slower for no benefit to the agent.</p>
 */
class TelemetryInterceptor(
    private val recorder: TelemetryRecorder,
    private val scope: CoroutineScope,
    private val newCorrelationId: () -> String = { UUID.randomUUID().toString() }
) : Interceptor {

    override fun intercept(chain: Interceptor.Chain): Response {
        val correlationId = chain.request().header(CORRELATION_HEADER) ?: newCorrelationId()

        val request = chain.request().newBuilder()
            .header(CORRELATION_HEADER, correlationId)
            .build()

        val startedAt = System.nanoTime()

        return try {
            val response = chain.proceed(request)
            report(request, correlationId, elapsedMillis(startedAt), response.code, failure = null)
            response
        } catch (failure: Exception) {
            // Not only IOException. A failure raised by another interceptor, or a runtime
            // failure while connecting, is exactly as invisible to an operator and exactly as
            // worth reporting; catching narrowly left those requests with no event at all.
            report(request, correlationId, elapsedMillis(startedAt), status = null, failure = failure)
            throw failure
        }
    }

    private fun report(
        request: Request,
        correlationId: String,
        durationMillis: Long,
        status: Int?,
        failure: Exception?
    ) {
        val code = classify(status, failure)

        val event = TelemetryEvent(
            eventType = TelemetryEventType.API_REQUEST,
            severity = when {
                code == null -> TelemetrySeverity.INFORMATION
                code == TelemetryErrorCode.ANDROID_API_4XX -> TelemetrySeverity.WARNING
                else -> TelemetrySeverity.ERROR
            },
            status = if (code == null) TelemetryStatus.SUCCEEDED else TelemetryStatus.FAILED,
            errorCode = code,
            // Method and route only. A URL can carry identifiers in its query string, and a
            // path with ids in it would also fragment error grouping into one group per id.
            details = "${request.method} ${route(request)}" + (status?.let { " -> $it" } ?: ""),
            durationMillis = durationMillis,
            correlationId = correlationId
        )

        // Fire and forget: the caller already has its response.
        scope.launch { recorder.record(event) }
    }

    /**
     * Distinguishes "the server refused" from "the phone could not reach the server".
     *
     * <p>That distinction is the one field support needs first, and it is invisible in a
     * generic "request failed". A timeout, a dead DNS lookup and an HTTP 500 all look the
     * same to an agent and mean entirely different things to whoever is investigating.</p>
     */
    private fun classify(status: Int?, failure: Exception?): TelemetryErrorCode? = when {
        failure is SSLException -> TelemetryErrorCode.ANDROID_TLS_FAILURE
        failure is SocketTimeoutException -> TelemetryErrorCode.ANDROID_NETWORK_TIMEOUT
        failure is UnknownHostException -> TelemetryErrorCode.ANDROID_DNS_FAILURE
        failure != null -> TelemetryErrorCode.ANDROID_CONNECTION_FAILURE
        status == null -> TelemetryErrorCode.ANDROID_UNKNOWN
        status >= 500 -> TelemetryErrorCode.ANDROID_API_5XX
        status >= 400 -> TelemetryErrorCode.ANDROID_API_4XX
        else -> null
    }

    /**
     * The request path with identifiers removed.
     *
     * <p>Grouping needs a stable route: `/devices/me` is one endpoint, but a path carrying a
     * transaction id would produce a separate error group per transaction and bury the fact
     * that one endpoint is failing repeatedly.</p>
     */
    private fun route(request: Request): String =
        request.url.encodedPath
            .split('/')
            .joinToString("/") { segment ->
                when {
                    segment.length >= 32 && segment.none { it == ' ' } -> "{id}"
                    segment.length in 8..40 && segment.count { it == '-' } >= 4 -> "{id}"
                    segment.all { it.isDigit() } && segment.isNotEmpty() -> "{n}"
                    else -> segment
                }
            }

    private fun elapsedMillis(startedAtNanos: Long): Long =
        (System.nanoTime() - startedAtNanos) / 1_000_000

    companion object {
        /** The header the backend already reads. No competing scheme is introduced. */
        const val CORRELATION_HEADER = "X-Correlation-Id"
    }
}
