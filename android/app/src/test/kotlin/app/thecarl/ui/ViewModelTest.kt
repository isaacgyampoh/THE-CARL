package app.thecarl.ui

import app.thecarl.core.data.capture.CaptureOutcome
import app.thecarl.core.data.capture.ManualCaptureRequest
import app.thecarl.core.data.repository.TransactionCapture
import app.thecarl.core.data.session.EnrolmentResult
import app.thecarl.core.data.session.LoginResult
import app.thecarl.core.data.session.SessionState
import app.thecarl.ui.state.CaptureError
import app.thecarl.ui.state.CaptureProvider
import app.thecarl.ui.state.CaptureTransactionType
import app.thecarl.ui.state.EnrolmentError
import app.thecarl.ui.state.LoginError
import app.thecarl.ui.state.MoneyFormat
import app.thecarl.ui.viewmodel.CaptureViewModel
import com.google.common.truth.Truth.assertThat
import kotlinx.coroutines.test.runTest
import org.junit.Test

/**
 * Presentation logic for the capture form.
 *
 * <p>Drives the real [CaptureViewModel] against a stubbed repository. What is under test is
 * the form's behaviour — validation, error mapping, and the honesty of its confirmation
 * wording — not the domain, which is already covered in :core:data.</p>
 */
class CaptureViewModelTest {

    private val requests = mutableListOf<ManualCaptureRequest>()
    private var nextOutcome: CaptureOutcome = CaptureOutcome.Queued(
        clientTransactionId = "CTX-9f3a1c07-01HQ8Z7K3M4N5P6Q7R8S9T0V1W",
        evidenceId = "evidence-1",
        fingerprint = "a".repeat(64)
    )
    private var repositoryAvailable = true
    private var throwOnCapture = false

    @Test
    fun `a valid cash-in is captured and confirmed as saved locally`() = runTest {
        val viewModel = newViewModel()
        viewModel.onAmountChanged("500.00")

        assertThat(viewModel.submit(isOnline = true)).isTrue()

        val confirmation = viewModel.state.value.lastResult!!
        assertThat(confirmation.amountMinor).isEqualTo(50_000L)
        assertThat(confirmation.isQueued).isTrue()

        // The wording must claim only what is true. The server has not seen this yet.
        assertThat(confirmation.headline).isEqualTo("Saved on this device")
        assertThat(confirmation.syncMessage).isEqualTo("Waiting to sync")
    }

    @Test
    fun `an offline capture says it was saved offline rather than failed`() = runTest {
        val viewModel = newViewModel()
        viewModel.onAmountChanged("120.00")

        assertThat(viewModel.submit(isOnline = false)).isTrue()

        // The transaction succeeded — it is on the device. Telling the agent it failed
        // because the network is down would be false and would invite a duplicate entry.
        val confirmation = viewModel.state.value.lastResult!!
        assertThat(confirmation.syncMessage).isEqualTo("Saved offline — will sync automatically.")
        assertThat(viewModel.state.value.error).isNull()
    }

    @Test
    fun `the domain receives the amount and type the form collected`() = runTest {
        val viewModel = newViewModel()
        viewModel.onTypeChanged(CaptureTransactionType.CASH_OUT)
        viewModel.onProviderChanged(CaptureProvider.TELECEL)
        viewModel.onAmountChanged("75.50")
        viewModel.onCustomerPhoneChanged("0241234567")

        viewModel.submit()

        val request = requests.single()
        assertThat(request.amount.toPlainString()).isEqualTo("75.50")
        assertThat(request.transactionType.name).isEqualTo("CASH_OUT")
        assertThat(request.provider.name).isEqualTo("TELECEL")
    }

    @Test
    fun `sub-pesewa precision is rejected rather than rounded`() = runTest {
        val viewModel = newViewModel()
        viewModel.onAmountChanged("1.005")

        assertThat(viewModel.submit()).isFalse()

        // A rounded amount is a wrong amount, and the agent would never know.
        assertThat(viewModel.state.value.error).isEqualTo(CaptureError.SUB_PESEWA_PRECISION)
        assertThat(requests).isEmpty()
    }

    @Test
    fun `non-numeric and zero amounts are refused`() = runTest {
        val viewModel = newViewModel()

        viewModel.onAmountChanged("abc")
        assertThat(viewModel.submit()).isFalse()
        assertThat(viewModel.state.value.error).isEqualTo(CaptureError.INVALID_AMOUNT)

        viewModel.onAmountChanged("0")
        assertThat(viewModel.submit()).isFalse()
        assertThat(viewModel.state.value.error).isEqualTo(CaptureError.AMOUNT_TOO_SMALL)

        viewModel.onAmountChanged("-10")
        assertThat(viewModel.submit()).isFalse()

        assertThat(requests).isEmpty()
    }

    @Test
    fun `a local save failure is distinguished from a network problem`() = runTest {
        throwOnCapture = true
        val viewModel = newViewModel()
        viewModel.onAmountChanged("300.00")

        assertThat(viewModel.submit()).isFalse()

        // The only case where the transaction genuinely was not recorded. It must never be
        // confused with "queued but not yet synced".
        assertThat(viewModel.state.value.error).isEqualTo(CaptureError.LOCAL_SAVE_FAILED)
        assertThat(viewModel.state.value.lastResult).isNull()
    }

    @Test
    fun `held-for-review evidence does not present as a completed capture`() = runTest {
        nextOutcome = CaptureOutcome.HeldForReview("evidence-2", "b".repeat(64), "needs review")
        val viewModel = newViewModel()
        viewModel.onAmountChanged("400.00")

        assertThat(viewModel.submit()).isFalse()

        assertThat(viewModel.state.value.error).isEqualTo(CaptureError.HELD_FOR_REVIEW)
        assertThat(viewModel.state.value.lastResult).isNull()
    }

    @Test
    fun `a local duplicate is surfaced rather than silently accepted`() = runTest {
        nextOutcome = CaptureOutcome.DuplicateOnThisDevice("CTX-existing", "c".repeat(64))
        val viewModel = newViewModel()
        viewModel.onAmountChanged("250.00")

        assertThat(viewModel.submit()).isFalse()
        assertThat(viewModel.state.value.error).isEqualTo(CaptureError.DUPLICATE_ON_DEVICE)
    }

    @Test
    fun `capture is refused when the device is not registered`() = runTest {
        repositoryAvailable = false
        val viewModel = newViewModel()
        viewModel.onAmountChanged("100.00")

        assertThat(viewModel.submit()).isFalse()
        assertThat(viewModel.state.value.error).isEqualTo(CaptureError.NO_ACTIVE_DEVICE)
    }

    @Test
    fun `an empty form cannot be submitted`() = runTest {
        val viewModel = newViewModel()

        assertThat(viewModel.state.value.canSubmit).isFalse()
        assertThat(viewModel.submit()).isFalse()
        assertThat(requests).isEmpty()
    }

    @Test
    fun `the form resets after a successful capture but keeps type and provider`() = runTest {
        val viewModel = newViewModel()
        viewModel.onTypeChanged(CaptureTransactionType.CASH_OUT)
        viewModel.onProviderChanged(CaptureProvider.AIRTELTIGO)
        viewModel.onAmountChanged("60.00")
        viewModel.onCustomerPhoneChanged("0241234567")

        viewModel.submit()

        // An agent recording a run of similar transactions should not reselect the type
        // every time, but the amount must always be entered deliberately.
        val state = viewModel.state.value
        assertThat(state.amountInput).isEmpty()
        assertThat(state.customerPhone).isEmpty()
        assertThat(state.transactionType).isEqualTo(CaptureTransactionType.CASH_OUT)
        assertThat(state.provider).isEqualTo(CaptureProvider.AIRTELTIGO)
    }

    @Test
    fun `the capture form offers no type that cannot be posted from a form`() {
        val offered = CaptureTransactionType.entries.map { it.name }

        // Reversal needs a referenced original and Adjustment needs explicit signed deltas;
        // neither can come from a capture form. Unknown must never be chosen deliberately.
        assertThat(offered).doesNotContain("REVERSAL")
        assertThat(offered).doesNotContain("ADJUSTMENT")
        assertThat(offered).doesNotContain("UNKNOWN")
    }

    private fun newViewModel() = CaptureViewModel(
        captureRepositoryProvider = {
            if (repositoryAvailable) StubCaptureRepository() else null
        },
        now = { 1_755_248_400_000L }
    )

    /** Records what the form sent and returns a configured outcome. */
    private inner class StubCaptureRepository : TransactionCapture {
        override suspend fun captureManual(request: ManualCaptureRequest): CaptureOutcome {
            if (throwOnCapture) throw IllegalStateException("local save failed")
            requests += request
            return nextOutcome
        }
    }
}

class MoneyFormatTest {

    @Test
    fun `minor units render with two decimal places`() {
        assertThat(MoneyFormat.format(50_000L)).isEqualTo("₵500.00")
        assertThat(MoneyFormat.format(10L)).isEqualTo("₵0.10")
        assertThat(MoneyFormat.format(5L)).isEqualTo("₵0.05")
        assertThat(MoneyFormat.format(0L)).isEqualTo("₵0.00")
    }

    @Test
    fun `negative amounts keep their sign`() {
        // Reversals and adjustments produce negative deltas; dropping the sign would make a
        // refund look like a receipt.
        assertThat(MoneyFormat.format(-25_050L)).isEqualTo("-₵250.50")
    }

    @Test
    fun `large amounts are not truncated`() {
        assertThat(MoneyFormat.format(125_000_075L)).isEqualTo("₵1250000.75")
    }
}

class LoginErrorMessagingTest {

    @Test
    fun `error messages never expose backend detail`() {
        LoginError.entries.forEach { error ->
            assertThat(error.message).isNotEmpty()
            // A stack trace or raw HTTP body in front of an agent is both useless and a
            // disclosure risk.
            assertThat(error.message).doesNotContain("Exception")
            assertThat(error.message).doesNotContain("http")
            assertThat(error.message).doesNotContain("SQL")
        }
    }

    @Test
    fun `a server fault is not reported as a wrong password`() {
        // Telling an agent their password is wrong when the server is down sends them to
        // reset a password that was never the problem.
        assertThat(LoginError.SERVER_ERROR.message)
            .isNotEqualTo(LoginError.INVALID_CREDENTIALS.message)
        assertThat(LoginError.NETWORK_UNAVAILABLE.message)
            .isNotEqualTo(LoginError.INVALID_CREDENTIALS.message)
    }

    @Test
    fun `enrolment errors guide the agent to a next step`() {
        assertThat(EnrolmentError.INVALID_OR_EXPIRED.message).contains("manager")
        EnrolmentError.entries.forEach {
            assertThat(it.message).doesNotContain("Exception")
        }
    }
}
