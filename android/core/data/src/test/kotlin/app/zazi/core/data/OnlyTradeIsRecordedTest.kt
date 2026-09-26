package app.zazi.core.data

import app.zazi.core.data.capture.CaptureOutcome
import app.zazi.core.data.capture.SmsCaptureRequest
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.repository.CaptureRepository
import app.zazi.core.data.repository.DashboardRepository
import com.google.common.truth.Truth.assertThat
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/**
 * A vendor's takings are deposits and withdrawals. Nothing else is.
 *
 * <p>Everything a network sends arrives on the same handset: transfers between friends,
 * airtime top-ups, merchant payments, commission credits, balance replies, one-time codes and
 * prize draws. All of it is real, none of it is the trade an agent is paid for, and putting
 * any of it in front of them — recorded or queued — buries the deposit that matters.</p>
 *
 * <p>The one thing that must never be silent is a message that says deposit or withdrawal and
 * still could not be read. That is money that arrived.</p>
 */
@RunWith(RobolectricTestRunner::class)
class OnlyTradeIsRecordedTest {

    private lateinit var database: ZaziDatabase
    private lateinit var capture: CaptureRepository
    private lateinit var dashboard: DashboardRepository
    private var clock = 1_700_000_000_000L

    @Before
    fun setUp() {
        database = createTestDatabase()
        capture = CaptureRepository(
            database = database,
            deviceInstallationId = "installation-abc",
            organizationId = "22222222-2222-2222-2222-222222222222",
            branchId = "33333333-3333-3333-3333-333333333333",
            deviceId = "44444444-4444-4444-4444-444444444444",
            now = { clock }
        )
        dashboard = DashboardRepository(database)
    }

    @After
    fun tearDown() = database.close()

    private suspend fun arrive(body: String, sender: String = "MobileMoney"): CaptureOutcome {
        clock += 60_000
        return capture.captureSms(
            SmsCaptureRequest(sender, body, clock, null)
        )
    }

    // ─── Recorded ────────────────────────────────────────────────────────────

    @Test
    fun `a cash-in is recorded without anybody being asked`() = runTest {
        val outcome = arrive(
            "Cash In of GHS 500.00 from 0241000001 JOHN. Ref: MP240815.1201.A00001. " +
                "Your MoMo agent balance is GHS 12,340.00"
        )

        assertThat(outcome).isInstanceOf(CaptureOutcome.Queued::class.java)
    }

    @Test
    fun `a cash-out is recorded without anybody being asked`() = runTest {
        val outcome = arrive(
            "Cash Out of GHS 250.50 to 0241000002 AMA. Ref: MP240815.1202.A00002. " +
                "Your MoMo agent balance is GHS 12,089.50"
        )

        assertThat(outcome).isInstanceOf(CaptureOutcome.Queued::class.java)
    }

    @Test
    fun `deposit and withdrawal are the same two movements under other names`() = runTest {
        val deposit = arrive(
            "Deposit of GHS 300.00 from 0241000003. Ref: MP1. Balance GHS 900.00"
        )
        val withdrawal = arrive(
            "Withdrawal of GHS 120.00 by 0241000004. Ref: MP2. Balance GHS 780.00"
        )

        assertThat(deposit).isInstanceOf(CaptureOutcome.Queued::class.java)
        assertThat(withdrawal).isInstanceOf(CaptureOutcome.Queued::class.java)
    }

    // ─── Ignored, silently ───────────────────────────────────────────────────

    @Test
    fun `everything that is not the vendor's trade is dropped without a word`() = runTest {
        val notTrading = listOf(
            "Payment received for GHS 295.00 from AARON LARTEY. Current Balance: GHS 1337.16. " +
                "Transaction ID: 90079732268.",
            "Payment made for GHS 50.00 to BADAMASI MUHAMMED. Current Balance: GHS 488.95. " +
                "Transaction ID: 90256019922.",
            "Your payment of GHS 20.00 to MTN AIRTIME has been completed at 2026-09-22 " +
                "23:21:53. Your new balance: GHS 1274.37.",
            "Transfer of GHS 80.00 sent to 0241000003. Ref: MP240815.1205.T00005",
            "You have received Commission of GHS 12.75 for MoMo transactions. Ref: MP3.",
            "Your MTN MoMo balance is GHS 1339.37 as at 26 Sept.",
            "Your MTN verification code is 481923.",
            "BIG NEWS! your number qualifies for today's VIP spin challenge. Reply VIP now"
        )

        notTrading.forEach { body ->
            assertThat(arrive(body)).isInstanceOf(CaptureOutcome.Ignored::class.java)
        }

        // Nothing recorded, and nothing put in front of the agent either.
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
        assertThat(dashboard.observeHeldCount().first()).isEqualTo(0)
    }

    @Test
    fun `a message from somewhere that is not a network is dropped`() = runTest {
        val outcome = arrive(
            body = "You have received GHS 100.00 from ACCOUNT 123456. Ref: SB0001",
            sender = "SomeBank"
        )

        assertThat(outcome).isInstanceOf(CaptureOutcome.Ignored::class.java)
    }

    // ─── Held, because it is money that arrived ──────────────────────────────

    @Test
    fun `a deposit the parser cannot read still reaches the agent`() = runTest {
        // Says deposit, from a network, and nothing else could be recovered. Silence here
        // would be the original fault: money in, nothing on any screen.
        val outcome = arrive("Deposit completed. See your statement for details.")

        assertThat(outcome).isInstanceOf(CaptureOutcome.HeldForReview::class.java)
        assertThat(dashboard.observeHeldCount().first()).isEqualTo(1)
    }

    @Test
    fun `a reversal of a trade is never silently dropped`() = runTest {
        // A reversal undoes money that was counted. It cannot post by itself — that needs the
        // original, which a parser cannot supply — so it goes to the agent rather than being
        // ignored, and the day's figures stay answerable.
        val outcome = arrive(
            "Reversal of Cash In of GHS 500.00 has been processed. Ref: MP240815.1204.R00004"
        )

        assertThat(outcome).isInstanceOf(CaptureOutcome.HeldForReview::class.java)
    }
}
