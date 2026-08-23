package app.thecarl.sms

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.provider.Telephony
import app.thecarl.CarlApplication
import app.thecarl.core.data.capture.CaptureOutcome
import app.thecarl.core.data.capture.SmsCaptureRequest
import app.thecarl.core.data.session.SessionState
import app.thecarl.core.data.sync.SyncWorker
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

/**
 * Turns an incoming SMS into evidence for the existing capture pipeline.
 *
 * <p>This receiver decides nothing financial. It reassembles the message, hands it to
 * {@code TransactionCapture}, and stops. Provider, transaction type, amount, direction and
 * whether anything may post at all are decided by the parser registry,
 * {@code LedgerProjection} and the evidence-quality rules that manual capture already uses.
 * It never calls the sync API and never touches the server ledger.</p>
 *
 * <p><b>Work is handed off, not done here.</b> {@code onReceive} runs on the main thread with
 * a short deadline, so the database write happens inside {@code goAsync()} on the IO
 * dispatcher. The pending result is always finished, including on failure, or the process
 * would be held open.</p>
 */
class SmsBroadcastReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Telephony.Sms.Intents.SMS_RECEIVED_ACTION) {
            return
        }

        // A malformed or hostile PDU must not crash the receiver — a crash here is a crash
        // of the agent's app on an incoming text.
        val message = try {
            readMessage(intent)
        } catch (_: Exception) {
            null
        } ?: return

        val application = context.applicationContext as? CarlApplication ?: return

        val pending = goAsync()

        CoroutineScope(Dispatchers.IO).launch {
            try {
                // An incoming SMS usually starts the process: the agent is not in the app
                // when their transaction alert arrives. The application restores the session
                // asynchronously, so at this moment it is normally still Initialising, and
                // reading the session now would find no active device and silently drop a
                // real transaction. Restoring first is what makes automatic capture work in
                // the case that actually matters. It is idempotent and cheap.
                if (application.container.sessionRepository.state.value is SessionState.Initialising) {
                    runCatching { application.container.sessionRepository.restore() }
                }

                // Capture needs an authenticated session for its organization, branch and
                // device. Without one there is nobody to attribute the transaction to, and
                // inventing a placeholder tenant would be worse than dropping it. A
                // signed-out, unenrolled or revoked device records nothing new — and loses
                // nothing already queued.
                val capture = application.container.captureRepository() ?: return@launch

                val outcome = capture.captureSms(
                    SmsCaptureRequest(
                        senderIdentity = message.sender,
                        body = message.body,
                        receivedAtUtcMillis = message.receivedAtUtcMillis
                    )
                )

                // Only a queued transaction is worth waking the sync worker for. Held and
                // duplicate outcomes have nothing to send.
                if (outcome is CaptureOutcome.Queued) {
                    SyncWorker.enqueue(application)
                }
            } catch (_: Exception) {
                // Swallowed deliberately: an exception here must not crash the app on an
                // incoming message. Nothing is lost that was already committed, because the
                // capture write is atomic.
            } finally {
                pending.finish()
            }
        }
    }

    /**
     * Reassembles the message the platform delivered.
     *
     * <p>A long SMS arrives as several PDUs in one broadcast. Each fragment is meaningless
     * on its own — parsing them separately would turn one transaction into several partial
     * ones, or silently truncate an amount. They are concatenated in delivery order, which
     * is the order the sender's handset split them.</p>
     */
    private fun readMessage(intent: Intent): ReceivedSms? {
        val parts = Telephony.Sms.Intents.getMessagesFromIntent(intent)
        if (parts.isNullOrEmpty()) {
            return null
        }

        val body = parts.joinToString("") { it.displayMessageBody ?: it.messageBody ?: "" }
        if (body.isBlank()) {
            return null
        }

        val first = parts.first()

        return ReceivedSms(
            sender = first.displayOriginatingAddress ?: first.originatingAddress,
            body = body,
            // The service centre timestamp, not the clock. It is identical when the network
            // redelivers the same message, which is what lets the existing evidence
            // fingerprint recognise a redelivery instead of creating a second transaction.
            receivedAtUtcMillis = first.timestampMillis
        )
    }

    private data class ReceivedSms(
        val sender: String?,
        val body: String,
        val receivedAtUtcMillis: Long
    )
}
