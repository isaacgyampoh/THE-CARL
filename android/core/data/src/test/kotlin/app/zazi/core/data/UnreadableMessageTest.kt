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
 * A mobile money message that arrives must never vanish without trace.
 *
 * <p>The fault: a message whose template the parser could not read produced neither a type nor
 * an amount, and was discarded as "not a recognisable transaction message". No row, no
 * warning, and — because reporting a parsing mistake hangs off a transaction's detail screen —
 * no way for the agent to tell anyone. An agent took a deposit and it appeared nowhere in
 * their day, which at a counter is indistinguishable from the app being broken.</p>
 *
 * <p>The privacy rule it was protecting is real and still holds: a one-time code or a personal
 * message must not be kept. The line is now drawn at whether a provider claimed the message
 * <i>and</i> it mentions money, not at whether the parser happened to succeed.</p>
 */
@RunWith(RobolectricTestRunner::class)
class UnreadableMessageTest {

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

    private suspend fun arrive(sender: String, body: String): CaptureOutcome =
        capture.captureSms(
            SmsCaptureRequest(
                senderIdentity = sender,
                body = body,
                receivedAtUtcMillis = clock,
                sessionId = null
            )
        )

    // ─── What must be kept ───────────────────────────────────────────────────

    @Test
    fun `a money message in a template we cannot read is kept for the agent`() = runTest {
        // Amount written the other way round, and no direction word this parser knows. Both
        // extractions fail, which before this change discarded the message outright.
        val outcome = arrive(
            sender = "MTN MoMo",
            body = "Transaction complete. 500.00 GHS has been applied to your agent till."
        )

        assertThat(outcome).isInstanceOf(CaptureOutcome.HeldForReview::class.java)
        assertThat(dashboard.observeHeldCount().first()).isEqualTo(1)
    }

    @Test
    fun `an ambiguous grouping is held rather than dropped`() = runTest {
        // extractAmount deliberately refuses "GHS 1 250.00" — the grouping is genuinely
        // ambiguous — and its own comment says the evidence rules will hold it for review.
        // With no direction word either, the earlier guard discarded it instead, defeating
        // exactly the safety this refusal exists to provide.
        val outcome = arrive(
            sender = "MTN MoMo",
            body = "MTN Mobile Money: GHS 1 250.00 processed on your till."
        )

        assertThat(outcome).isInstanceOf(CaptureOutcome.HeldForReview::class.java)
    }

    @Test
    fun `the held message keeps its text, because the agent has to read what we could not`() = runTest {
        val body = "Telecel Cash: 80.00 GHS handled for 0241234567."
        arrive(sender = "TelecelCash", body = body)

        val held = dashboard.observeHeld().first().single()
        assertThat(held.rawMessage).isEqualTo(body)
        assertThat(held.provider).isEqualTo("TELECEL")
    }

    // ─── What must still be discarded ────────────────────────────────────────

    @Test
    fun `a one-time code from the same shortcode is not kept`() = runTest {
        // The privacy rule. It mentions no money, so it is not a transaction we failed to
        // read — it is somebody's private text and has no business in the evidence table.
        val outcome = arrive(sender = "MTN", body = "Your MTN verification code is 481923.")

        assertThat(outcome).isInstanceOf(CaptureOutcome.Ignored::class.java)
        assertThat(dashboard.observeHeldCount().first()).isEqualTo(0)
    }

    @Test
    fun `a personal message mentioning nothing financial is not kept`() = runTest {
        val outcome = arrive(sender = "0241234567", body = "Are you at the shop? I am coming.")

        assertThat(outcome).isInstanceOf(CaptureOutcome.Ignored::class.java)
        assertThat(dashboard.observeHeldCount().first()).isEqualTo(0)
    }

    @Test
    fun `an unreadable message from no recognised provider is not kept`() = runTest {
        // A bank or a lender talking about cedis is not this agent's till, and there is no
        // provider behind it to make the message theirs.
        val outcome = arrive(
            sender = "QuickLoan",
            body = "You qualify for up to 5,000 CEDIS today. Reply YES."
        )

        assertThat(outcome).isInstanceOf(CaptureOutcome.Ignored::class.java)
    }

    // ─── Settling one ────────────────────────────────────────────────────────

    @Test
    fun `recording a held message takes it out of the queue`() = runTest {
        arrive(sender = "MTN MoMo", body = "MTN MoMo: 90.00 GHS on your till.")
        val held = dashboard.observeHeld().first().single()

        assertThat(dashboard.settleHeld(held.evidenceId, recorded = true)).isTrue()
        assertThat(dashboard.observeHeldCount().first()).isEqualTo(0)
    }

    @Test
    fun `a message cannot be settled twice`() = runTest {
        arrive(sender = "MTN MoMo", body = "MTN MoMo: 90.00 GHS on your till.")
        val held = dashboard.observeHeld().first().single()

        assertThat(dashboard.settleHeld(held.evidenceId, recorded = true)).isTrue()
        // A second tap, or a screen that was already open. The guard is in the WHERE clause.
        assertThat(dashboard.settleHeld(held.evidenceId, recorded = false)).isFalse()
    }

    @Test
    fun `dismissing a message also takes it out of the queue`() = runTest {
        arrive(sender = "MTN MoMo", body = "MTN MoMo: 90.00 GHS on your till.")
        val held = dashboard.observeHeld().first().single()

        assertThat(dashboard.settleHeld(held.evidenceId, recorded = false)).isTrue()
        assertThat(dashboard.observeHeldCount().first()).isEqualTo(0)
    }
}
