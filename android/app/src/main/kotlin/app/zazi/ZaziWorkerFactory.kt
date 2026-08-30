package app.zazi

import android.content.Context
import androidx.work.ListenableWorker
import androidx.work.WorkerFactory
import androidx.work.WorkerParameters
import app.zazi.core.data.sync.SyncEngine
import app.zazi.core.data.sync.SyncWorker

/**
 * Supplies workers their dependencies.
 *
 * <p>WorkManager instantiates workers reflectively and can only call a two-argument
 * constructor, so a worker that needs collaborators must be built by a factory. Without this
 * [SyncWorker] would have to reach for a global singleton — exactly the shortcut that makes
 * a sync engine untestable.</p>
 */
class ZaziWorkerFactory(private val syncEngine: SyncEngine) : WorkerFactory() {

    override fun createWorker(
        appContext: Context,
        workerClassName: String,
        workerParameters: WorkerParameters
    ): ListenableWorker? = when (workerClassName) {
        SyncWorker::class.java.name -> SyncWorker(appContext, workerParameters, syncEngine)

        // Returning null delegates to the default factory rather than failing, so a worker
        // added later without a factory entry still runs.
        else -> null
    }
}
