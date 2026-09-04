package app.zazi.core.data.telemetry

import app.zazi.core.data.database.TelemetryEventEntity
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.domain.telemetry.TelemetryEvent
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Records what the handset observed, and never gets in the way.
 *
 * <p><b>Every failure here is swallowed.</b> Telemetry describes business operations; it must
 * not be able to fail one. A recorder that could throw would mean a full disk turning a
 * successful cash-in into a failed one, which is precisely the wrong trade.</p>
 *
 * <p>The queue is bounded. A handset out of signal for a week keeps the most recent events
 * and discards the rest rather than filling its own storage with diagnostics — the newest are
 * kept because they describe whatever is wrong now.</p>
 */
interface TelemetryRecorder {
    suspend fun record(event: TelemetryEvent)
}

/** Discards everything. Used where telemetry is not wanted, and in tests. */
object NoOpTelemetryRecorder : TelemetryRecorder {
    override suspend fun record(event: TelemetryEvent) = Unit
}

class LocalTelemetryRecorder(
    private val database: ZaziDatabase,
    private val maxEvents: Int = MAX_EVENTS,
    private val retentionMillis: Long = RETENTION_MILLIS,
    private val now: () -> Long = System::currentTimeMillis,
    /** Injectable so tests observe writes deterministically. */
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO
) : TelemetryRecorder {

    override suspend fun record(event: TelemetryEvent) = withContext(ioDispatcher) {
        // Explicitly off whatever thread called this. Callers include a view model running on
        // the main dispatcher, and three round trips to an encrypted database there would be
        // jank at best. Relying on Room to dispatch its own queries would leave the guarantee
        // implicit and easy to lose.
        try {
            val dao = database.telemetryDao()

            dao.insert(
                TelemetryEventEntity(
                    eventType = event.eventType.name,
                    severity = event.severity.name,
                    status = event.status?.name,
                    errorCode = event.errorCode?.name,
                    details = event.details?.take(MAX_DETAIL_LENGTH),
                    durationMillis = event.durationMillis,
                    correlationId = event.correlationId,
                    occurredAtUtcMillis = event.occurredAtUtcMillis
                )
            )

            // Trimming on write rather than on a timer: there is no other moment guaranteed
            // to happen on a device that is never opened again.
            dao.deleteOlderThan(now() - retentionMillis)
            dao.trimTo(maxEvents)
        } catch (_: Throwable) {
            // Deliberately total. Telemetry is diagnostic; the operation it describes has
            // already happened and must not be affected by whether it was recorded.
        }
    }

    private companion object {
        /** Roughly a day of ordinary activity, and a few kilobytes on disk. */
        const val MAX_EVENTS = 500

        const val RETENTION_MILLIS = 7L * 24 * 60 * 60 * 1000

        /** Details are built from fixed vocabulary; the cap is a backstop, not a filter. */
        const val MAX_DETAIL_LENGTH = 200
    }
}
