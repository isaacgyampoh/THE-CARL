package app.zazi.core.data.telemetry

import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.network.ClientTelemetryEvent
import app.zazi.core.data.network.TelemetryBatchRequest
import app.zazi.core.data.network.ZaziApi

/**
 * Sends queued events to the server, or gives up quietly.
 *
 * <p>Runs alongside the existing sync pass rather than on a schedule of its own, so telemetry
 * never wakes the device by itself. If it cannot be delivered it stays queued until the
 * bounded queue discards it — there is no retry counter, because a diagnostic event is not
 * worth tracking failures about.</p>
 *
 * <p>Events are deleted only after the server has accepted them. Deleting first would lose
 * exactly the evidence describing the connectivity problem that caused the failure.</p>
 */
class TelemetryUploader(
    private val database: ZaziDatabase,
    private val api: ZaziApi,
    private val batchSize: Int = BATCH_SIZE
) {

    /** Returns how many events were accepted. Never throws. */
    suspend fun uploadOnce(): Int {
        return try {
            val dao = database.telemetryDao()
            val pending = dao.oldest(batchSize)

            if (pending.isEmpty()) {
                return 0
            }

            val response = api.recordTelemetry(
                TelemetryBatchRequest(
                    events = pending.map { event ->
                        ClientTelemetryEvent(
                            eventType = event.eventType,
                            severity = event.severity,
                            status = event.status,
                            errorCode = event.errorCode,
                            details = event.details,
                            durationMs = event.durationMillis?.toInt(),
                            correlationId = event.correlationId,
                            occurredAtUtc = java.time.Instant
                                .ofEpochMilli(event.occurredAtUtcMillis).toString()
                        )
                    }
                )
            )

            if (!response.isSuccessful) {
                // Left queued. A server that is refusing telemetry is itself worth reporting
                // once it recovers.
                return 0
            }

            dao.deleteByIds(pending.map { it.id })
            pending.size
        } catch (_: Throwable) {
            // Total by design: an upload failure must not surface anywhere near the sync pass
            // that carries an agent's financial work.
            0
        }
    }

    private companion object {
        /** Small enough that a backlog never dominates a sync pass on a slow connection. */
        const val BATCH_SIZE = 50
    }
}
