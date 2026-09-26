package app.zazi.sms

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.provider.Telephony
import androidx.core.content.ContextCompat
import app.zazi.core.data.capture.SmsCaptureRequest
import app.zazi.core.data.repository.CaptureRepository

/**
 * Reads back transaction alerts that arrived while they were not being recorded.
 *
 * <p>A fault stopped messages reaching the books. The messages themselves are still on the
 * phone, in the inbox, and nowhere else — so the only way an agent's takings come back is to
 * read them again. Without this, a fix reaches the app and the missing money stays missing.</p>
 *
 * <p>Deliberately narrow. It reads a bounded window of recent messages, hands each to the same
 * capture path an arriving message takes, and records nothing the ordinary path would not have
 * recorded. Duplicates are impossible rather than unlikely: capture fingerprints every message
 * and a fingerprint already in the database is refused, so running this twice, or running it
 * over messages that were recorded correctly, changes nothing.</p>
 */
object InboxBackfill {

    /**
     * How far back to look.
     *
     * <p>Long enough to cover a fault noticed a few days late, short enough that a phone with
     * years of messages is not trawled. Bounded by time rather than count because what matters
     * is the trading that was missed, not how chatty the handset is.</p>
     */
    private const val WINDOW_DAYS = 14L
    private const val WINDOW_MILLIS = WINDOW_DAYS * 24 * 60 * 60 * 1000

    /** The most messages to examine, so a busy inbox cannot hold the phone up. */
    private const val MAXIMUM_MESSAGES = 2_000

    data class Result(val examined: Int, val recorded: Int, val skipped: Int)

    fun canRead(context: Context): Boolean =
        ContextCompat.checkSelfPermission(context, Manifest.permission.READ_SMS) ==
            PackageManager.PERMISSION_GRANTED

    /**
     * Replays recent inbox messages through capture.
     *
     * @param since only messages at or after this instant are considered; callers pass the
     *   later of the window and whatever they already know was recorded.
     */
    suspend fun run(
        context: Context,
        capture: CaptureRepository,
        now: Long = System.currentTimeMillis(),
        since: Long = now - WINDOW_MILLIS
    ): Result {
        if (!canRead(context)) {
            return Result(0, 0, 0)
        }

        // Messages already on file get their own identity first. Skipping this would read
        // every one of them back in as new on this very run.
        capture.identifyStoredMessages()

        var examined = 0
        var recorded = 0
        var skipped = 0

        val columns = arrayOf(
            Telephony.Sms.ADDRESS,
            Telephony.Sms.BODY,
            Telephony.Sms.DATE
        )

        // Oldest first, so a day's messages are replayed in the order they happened and the
        // running balances the reconciliation reads are built the right way round.
        val cursor = context.contentResolver.query(
            Telephony.Sms.Inbox.CONTENT_URI,
            columns,
            "${Telephony.Sms.DATE} >= ?",
            arrayOf(since.toString()),
            "${Telephony.Sms.DATE} ASC"
        ) ?: return Result(0, 0, 0)

        cursor.use {
            val address = it.getColumnIndex(Telephony.Sms.ADDRESS)
            val body = it.getColumnIndex(Telephony.Sms.BODY)
            val date = it.getColumnIndex(Telephony.Sms.DATE)

            while (it.moveToNext() && examined < MAXIMUM_MESSAGES) {
                examined++

                val text = if (body >= 0) it.getString(body) else null
                if (text.isNullOrBlank()) {
                    skipped++
                    continue
                }

                val outcome = runCatching {
                    capture.captureSms(
                        SmsCaptureRequest(
                            senderIdentity = if (address >= 0) it.getString(address) else null,
                            body = text,
                            // The time the message arrived, not the time it is being read
                            // back. It is what the fingerprint is built from, so a message
                            // replayed here matches the one the live path would have stored
                            // and cannot be recorded a second time.
                            receivedAtUtcMillis = if (date >= 0) it.getLong(date) else now,
                            sessionId = null
                        )
                    )
                }

                if (outcome.isSuccess) recorded++ else skipped++
            }
        }

        return Result(examined, recorded, skipped)
    }
}
