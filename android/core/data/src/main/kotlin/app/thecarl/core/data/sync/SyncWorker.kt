package app.thecarl.core.data.sync

import android.content.Context
import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import java.util.concurrent.TimeUnit

/**
 * Drains the outbox in the background.
 *
 * <p>A thin adapter over [SyncEngine]: WorkManager decides <i>when</i>, the engine decides
 * <i>what</i>. Keeping the logic out of the worker is what allows the whole drain to be
 * tested on the JVM against a real database and HTTP server.</p>
 *
 * <p><b>Result semantics.</b> WorkManager's own backoff is a coarse outer loop; per-item
 * backoff lives in the outbox and is what actually paces retries. The worker returns:</p>
 * <ul>
 *   <li><b>success</b> — the pass completed. Items needing another attempt carry their own
 *       next-attempt time, and a follow-up run is enqueued when work remains.</li>
 *   <li><b>retry</b> — the pass could not proceed at all (offline, server down), so
 *       WorkManager should try the whole thing again later.</li>
 * </ul>
 * <p>It never returns failure: failure would drop the work, and an outbox row represents an
 * agent's real money.</p>
 */
class SyncWorker(
    context: Context,
    parameters: WorkerParameters,
    private val syncEngine: SyncEngine
) : CoroutineWorker(context, parameters) {

    override suspend fun doWork(): Result {
        val result = try {
            syncEngine.runOnce()
        } catch (_: Exception) {
            // Nothing is lost by retrying: the outbox is durable and every item keeps its
            // ClientTransactionId.
            return Result.retry()
        }

        return when {
            // Revocation and missing credentials cannot be resolved by retrying. The work is
            // preserved; it resumes after a fresh login, which enqueues a new run.
            result.blockedReason == BlockedReason.REVOKED ||
                result.blockedReason == BlockedReason.NOT_AUTHENTICATED -> Result.success()

            result.blockedReason != null -> Result.retry()

            // Drain the backlog promptly rather than one batch per scheduled interval — an
            // agent returning from a day offline should not wait hours to catch up.
            result.hasMoreWork -> {
                enqueueFollowUp(applicationContext)
                Result.success()
            }

            else -> Result.success()
        }
    }

    companion object {
        const val UNIQUE_WORK_NAME = "thecarl.sync"

        /**
         * Enqueues a drain.
         *
         * <p>KEEP, not REPLACE: a capture happening while a sync is already running must not
         * cancel it mid-flight. The running pass will pick up the new item, or the follow-up
         * will.</p>
         */
        fun enqueue(context: Context) {
            WorkManager.getInstance(context).enqueueUniqueWork(
                UNIQUE_WORK_NAME,
                ExistingWorkPolicy.KEEP,
                request()
            )
        }

        /** Chases the remainder of a backlog immediately after a successful pass. */
        fun enqueueFollowUp(context: Context) {
            WorkManager.getInstance(context).enqueueUniqueWork(
                UNIQUE_WORK_NAME,
                ExistingWorkPolicy.APPEND_OR_REPLACE,
                request()
            )
        }

        private fun request() = OneTimeWorkRequestBuilder<SyncWorker>()
            .setConstraints(
                Constraints.Builder()
                    // CONNECTED, not UNMETERED: an agent on mobile data must still sync. The
                    // payload is small and the money is real.
                    .setRequiredNetworkType(NetworkType.CONNECTED)
                    .build()
            )
            // Outer-loop backoff only; per-item pacing lives in the outbox.
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 30, TimeUnit.SECONDS)
            .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
            .build()
    }
}
