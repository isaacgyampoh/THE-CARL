package app.zazi

import android.app.Application
import androidx.work.Configuration
import androidx.work.WorkManager
import app.zazi.core.data.session.SessionState
import app.zazi.core.data.sync.SyncWorker
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * Application entry point and owner of the object graph.
 *
 * <p><b>Startup is deliberately non-blocking and failure-tolerant.</b> Reading credentials
 * and contacting the server both happen off the main thread and neither can prevent the
 * process starting. An offline-first application that refuses to launch without a network
 * fails in exactly the conditions it exists for.</p>
 */
class ZaziApplication : Application(), Configuration.Provider {

    lateinit var container: AppContainer
        private set

    private val applicationScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    override fun onCreate() {
        super.onCreate()
        container = AppContainer(this, BuildConfig.API_BASE_URL)

        applicationScope.launch {
            // Restore the session, then drain anything the outbox is still holding. The
            // outbox — not the worker schedule — is the source of truth: work queued before
            // a crash is discovered here even though nothing re-enqueued it.
            val state = runCatching { container.sessionRepository.restore() }.getOrNull()

            if (state is SessionState.Active) {
                SyncWorker.enqueue(this@ZaziApplication)
            }
        }
    }

    /**
     * WorkManager configuration.
     *
     * <p>Provided rather than left to the default initialiser so [ZaziWorkerFactory] can
     * inject the sync engine. The container is constructed in [onCreate] before WorkManager
     * first asks for this.</p>
     */
    override val workManagerConfiguration: Configuration
        get() = Configuration.Builder()
            .setWorkerFactory(ZaziWorkerFactory(container.syncEngine))
            .build()
}

/**
 * Schedules a sync pass.
 *
 * <p>Called after every capture. Scheduling is best-effort and explicitly <b>not</b> the
 * source of truth: if enqueueing fails, the transaction is already committed to the outbox
 * and the next startup or periodic trigger will find it. A capture is never reported as
 * failed because a scheduler call did not land.</p>
 */
fun Application.scheduleSync() {
    runCatching { SyncWorker.enqueue(this) }
}

/** True when WorkManager has been initialised, so tests can assert scheduling occurred. */
internal fun Application.isWorkManagerInitialised(): Boolean =
    runCatching { WorkManager.getInstance(this) }.isSuccess
