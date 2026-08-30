package app.zazi

import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.work.WorkInfo
import androidx.work.WorkManager
import app.zazi.core.data.capture.CaptureOutcome
import app.zazi.core.data.capture.ManualCaptureRequest
import app.zazi.core.data.session.LoginResult
import app.zazi.core.data.session.SessionState
import app.zazi.core.data.sync.SyncWorker
import app.zazi.core.domain.model.Provider
import app.zazi.core.domain.model.TransactionType
import app.zazi.core.domain.sync.OutboxState
import com.google.common.truth.Truth.assertThat
import java.math.BigDecimal
import kotlinx.coroutines.runBlocking
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Twenty transactions through the real application graph, on a device.
 *
 * <p>Deliberately not driven through the UI. Screen-coordinate automation proved unreliable
 * over twenty repetitions — a stray tap logged the session out mid-run — and text entry
 * through the IME could not be made repeatable. This drives the same production objects the
 * screens drive: the real {@link AppContainer}, the real Keystore-backed credential store,
 * the real SQLCipher database, the real outbox, and the real {@link SyncWorker} executed by
 * WorkManager. Nothing is faked and no assertion is relaxed; only the taps are absent.</p>
 *
 * <p><b>The two halves run as separate processes.</b> {@code capturesTwentyTransactions} and
 * {@code drainsAllTwentyAfterProcessRestart} are invoked by two distinct
 * {@code am instrument} runs, so the second genuinely opens a database written by a process
 * that has since died. Running them in one process would prove persistence across a function
 * call, which is not the property that matters.</p>
 *
 * <p>Requires a seeded backend and an enrolled device; skips rather than fails when the
 * credentials are not supplied, so an ordinary {@code connectedCheck} on a developer machine
 * is unaffected.</p>
 */
@RunWith(AndroidJUnit4::class)
class BulkOfflineSyncInstrumentedTest {

    private val application: ZaziApplication get() = ApplicationProvider.getApplicationContext()
    private val container: AppContainer get() = application.container

    private val arguments get() = InstrumentationRegistry.getArguments()
    private val email: String? get() = arguments.getString("carlEmail")
    private val password: String? get() = arguments.getString("carlPassword")

    /**
     * Ten cash-ins and ten cash-outs, every amount distinct.
     *
     * <p>Distinct amounts mean a duplicate row cannot hide behind an identical sibling, and
     * the two directions make the expected balance a signed sum rather than a total that a
     * sign error would still satisfy.</p>
     */
    private val cashIns = (11..20).map { BigDecimal("$it.00") }
    private val cashOuts = (21..30).map { BigDecimal("$it.00") }

    @Test
    fun capturesTwentyTransactions() = runBlocking {
        assumeTrue("carlEmail/carlPassword not supplied", email != null && password != null)

        // A real login against the real backend. No token is forged and no authentication
        // step is skipped to make this test possible.
        val login = container.sessionRepository.login(email!!, password!!)
        assertThat(login).isInstanceOf(LoginResult.Success::class.java)

        // A fresh install has no enrolled device, and a changed applicationId makes this a
        // fresh install. Enrolment is part of the real flow, so it is performed rather than
        // assumed.
        if (container.sessionRepository.state.value is SessionState.NeedsEnrolment) {
            val code = arguments.getString("carlEnrolmentCode")
            assumeTrue("carlEnrolmentCode not supplied for an unenrolled device", code != null)
            container.sessionRepository.enrolDevice(code!!)
        }

        val state = container.sessionRepository.restore()
        assertThat(state).isInstanceOf(SessionState.Active::class.java)

        val capture = container.captureRepository()
        assertThat(capture).isNotNull()

        val before = container.outboxRepository.countByState(OutboxState.PENDING)

        val clientIds = mutableSetOf<String>()
        (cashIns.map { TransactionType.CASH_IN to it } + cashOuts.map { TransactionType.CASH_OUT to it })
            .forEach { (type, amount) ->
                val outcome = capture!!.captureManual(
                    ManualCaptureRequest(
                        transactionType = type,
                        amount = amount,
                        provider = Provider.MTN,
                        occurredAtUtcMillis = System.currentTimeMillis(),
                        reference = "BULK-${type.name}-${amount.toPlainString()}"
                    )
                )

                assertThat(outcome).isInstanceOf(CaptureOutcome.Queued::class.java)
                clientIds += (outcome as CaptureOutcome.Queued).clientTransactionId
            }

        // Twenty submissions must carry twenty distinct idempotency keys. A collision here
        // would silently become a single server transaction.
        assertThat(clientIds).hasSize(20)
        assertThat(container.outboxRepository.countByState(OutboxState.PENDING)).isEqualTo(before + 20)

        // Handed to the next process, which is a different run of this class and cannot
        // share memory with this one.
        capturedIdsFile().writeText(clientIds.joinToString("\n"))
    }

    @Test
    fun drainsAllTwentyAfterProcessRestart() = runBlocking {
        assumeTrue("carlEmail/carlPassword not supplied", email != null && password != null)

        val clientIds = capturedIdsFile().readLines().filter { it.isNotBlank() }
        assertThat(clientIds).hasSize(20)

        // Written by a process that no longer exists: every one of the twenty is still on
        // disk, still queued, and still carrying its original idempotency key.
        val dao = container.database.outboxDao()
        clientIds.forEach { id ->
            val item = dao.findByClientId(id)
            assertThat(item).isNotNull()
            assertThat(item!!.state).isEqualTo(OutboxState.PENDING.name)
        }

        assertThat(container.sessionRepository.restore())
            .isInstanceOf(SessionState.Active::class.java)

        // The real worker, executed by WorkManager, not SyncEngine called directly.
        val manager = WorkManager.getInstance(application)
        var settled = false

        repeat(MAX_DRAIN_ROUNDS) {
            if (!settled) {
                SyncWorker.enqueue(application)
                awaitWorkIdle(manager)

                settled = clientIds.all { dao.findByClientId(it)?.state == OutboxState.SYNCED.name }
            }
        }

        // Asserted per client id rather than on whole-outbox counts. The outbox legitimately
        // carries rows from earlier work, and a count-based assertion would either be
        // polluted by them or have to be loosened until it proved nothing.
        val finalStates = clientIds.associateWith { dao.findByClientId(it)?.state }

        assertThat(finalStates.values.filterNotNull()).hasSize(20)
        assertThat(finalStates.values.toSet()).containsExactly(OutboxState.SYNCED.name)

        // Each carries the server's identifier, which is what makes the local row reconciled
        // rather than merely stopped.
        val transactions = container.database.localTransactionDao()
        clientIds.forEach { id ->
            assertThat(transactions.findByClientId(id)?.serverTransactionId).isNotNull()
        }
    }

    /** Survives the process boundary between the two halves of this test. */
    private fun capturedIdsFile() = java.io.File(application.filesDir, "bulk-sync-client-ids.txt")

    private fun awaitWorkIdle(manager: WorkManager) {
        val deadline = System.currentTimeMillis() + WORK_TIMEOUT_MILLIS

        while (System.currentTimeMillis() < deadline) {
            val infos = manager.getWorkInfosForUniqueWork(SyncWorker.UNIQUE_WORK_NAME).get()
            if (infos.isEmpty() || infos.all { it.state in FINISHED }) {
                return
            }
            Thread.sleep(POLL_MILLIS)
        }
    }

    private companion object {
        const val MAX_DRAIN_ROUNDS = 6
        const val WORK_TIMEOUT_MILLIS = 90_000L
        const val POLL_MILLIS = 500L

        val FINISHED = setOf(
            WorkInfo.State.SUCCEEDED,
            WorkInfo.State.FAILED,
            WorkInfo.State.CANCELLED
        )
    }
}
