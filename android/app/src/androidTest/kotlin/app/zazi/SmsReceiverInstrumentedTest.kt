package app.zazi

import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import app.zazi.core.data.session.LoginResult
import app.zazi.core.data.session.SessionState
import app.zazi.core.domain.model.EvidenceSourceType
import app.zazi.core.domain.sync.OutboxState
import com.google.common.truth.Truth.assertThat
import java.io.File
import kotlinx.coroutines.runBlocking
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The SMS receiver against real messages delivered by the platform.
 *
 * <p>Run in two halves as separate {@code am instrument} invocations, with real SMS injected
 * between them by {@code adb emu sms send}. That injection produces a genuine
 * {@code SMS_RECEIVED} broadcast, so what is exercised here is the manifest registration, the
 * receiver, PDU reassembly and the capture pipeline — not a directly invoked function.</p>
 *
 * <p>Skips rather than fails when credentials are not supplied, so an ordinary
 * {@code connectedCheck} on a machine with no seeded backend is unaffected.</p>
 */
@RunWith(AndroidJUnit4::class)
class SmsReceiverInstrumentedTest {

    private val application: ZaziApplication get() = ApplicationProvider.getApplicationContext()
    private val container: AppContainer get() = application.container

    private val arguments get() = InstrumentationRegistry.getArguments()
    private val email: String? get() = arguments.getString("carlEmail")
    private val password: String? get() = arguments.getString("carlPassword")

    @Test
    fun signsInAndRecordsTheBaseline() = runBlocking {
        assumeTrue("carlEmail/carlPassword not supplied", email != null && password != null)

        val login = container.sessionRepository.login(email!!, password!!)
        assertThat(login).isInstanceOf(LoginResult.Success::class.java)

        // A fresh emulator image has no enrolled device. Enrolment is part of the real flow,
        // so it is performed here rather than assumed.
        if (container.sessionRepository.state.value is SessionState.NeedsEnrolment) {
            val code = arguments.getString("carlEnrolmentCode")
            assumeTrue("carlEnrolmentCode not supplied for an unenrolled device", code != null)
            container.sessionRepository.enrolDevice(code!!)
        }

        assertThat(container.sessionRepository.restore())
            .isInstanceOf(SessionState.Active::class.java)

        // Handed to the next process, which cannot share memory with this one.
        baselineFile().writeText(
            listOf(
                container.database.localTransactionDao().count(),
                container.database.evidenceDao().count(),
                container.outboxRepository.countByState(OutboxState.PENDING),
                container.outboxRepository.countByState(OutboxState.SYNCED)
            ).joinToString(",")
        )
    }

    @Test
    fun theInjectedMessageBecameExactlyOneTransaction() = runBlocking {
        assumeTrue("carlEmail/carlPassword not supplied", email != null && password != null)

        val (transactionsBefore, evidenceBefore, pendingBefore, syncedBefore) =
            baselineFile().readText().split(",").map { it.trim().toInt() }

        val transactionsAfter = container.database.localTransactionDao().count()
        val evidenceAfter = container.database.evidenceDao().count()
        val pendingAfter = container.outboxRepository.countByState(OutboxState.PENDING)

        // The same message was delivered twice. Android genuinely redelivers, and the
        // existing fingerprint is what has to recognise it — one transaction, not two.
        assertThat(transactionsAfter - transactionsBefore).isEqualTo(1)
        assertThat(evidenceAfter - evidenceBefore).isEqualTo(1)

        // Queued *or* already synced. The receiver wakes the sync worker on a successful
        // capture, so on a connected device the item can legitimately have left PENDING
        // before this assertion runs. What must never happen is it vanishing.
        val queuedOrSynced = pendingAfter - pendingBefore +
            container.outboxRepository.countByState(OutboxState.SYNCED) - syncedBefore
        assertThat(queuedOrSynced).isEqualTo(1)

        // Recorded as observed automatically, not as something a person typed.
        val smsEvidence = container.database.evidenceDao()
            .findByState("ACCEPTED")
            .filter { it.sourceType == EvidenceSourceType.ANDROID_SMS.name }

        assertThat(smsEvidence).isNotEmpty()
        val newest = smsEvidence.maxByOrNull { it.observedAtUtcMillis }!!
        assertThat(newest.amountMinor).isEqualTo(75_000L)
        assertThat(newest.rawMessage).isNotNull()
    }

    private fun baselineFile() = File(application.filesDir, "sms-receiver-baseline.txt")
}
