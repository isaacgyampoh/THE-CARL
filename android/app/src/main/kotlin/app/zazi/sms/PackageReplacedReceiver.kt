package app.zazi.sms

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import app.zazi.ZaziApplication
import app.zazi.scheduleSync
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

/**
 * Catches up the moment this app is updated.
 *
 * <p>An agent does not open Zazi to see whether it is working; they open it when the money
 * looks wrong. So a fix that only takes effect on the next message leaves everything the fault
 * already cost still missing. This replays the alerts the phone received while they were not
 * being recorded, without anybody being asked to do anything.</p>
 */
class PackageReplacedReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) {
            return
        }

        val application = context.applicationContext as? ZaziApplication ?: return
        val pending = goAsync()

        CoroutineScope(Dispatchers.IO).launch {
            try {
                // The session is restored first for the same reason the SMS receiver does it:
                // nothing has opened the app, so there is no tenant to attribute anything to
                // until it is read back from storage.
                runCatching { application.container.sessionRepository.restore() }
                val capture = application.container.captureRepository() ?: return@launch
                InboxBackfill.run(context.applicationContext, capture)
                application.scheduleSync()
            } catch (_: Exception) {
                // A catch-up that fails must not crash the update. The agent still has the
                // messages, and opening the app runs this again.
            } finally {
                pending.finish()
            }
        }
    }
}
