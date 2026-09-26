package app.zazi.core.data

import app.zazi.core.data.capture.CaptureOutcome
import app.zazi.core.data.capture.SmsCaptureRequest
import app.zazi.core.data.database.EvidenceEntity
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

    @Test
    fun `a promotion quoting millions is not a transaction`() = runTest {
        // Verbatim from a real handset. It quotes three figures in cedis, so every currency
        // test says financial; the parser even read "GHS 1.4 MILLION" as ₵1.40. A review queue
        // filling with these is a queue the agent stops opening.
        val outcome = arrive(
            sender = "MTN",
            body = "Y'ello! 233533547740, GHS 1.4 MILLION in prizes including a GHS 500 000 " +
                "CASH Grand Prize is waiting in the MTN Swipe & Win Promo. Dial *5030# for " +
                "FREE. 1st day free,  then GHS 1.5/day. To exit  send STOP to 5030."
        )

        assertThat(outcome).isInstanceOf(CaptureOutcome.Ignored::class.java)
        assertThat(dashboard.observeHeldCount().first()).isEqualTo(0)
    }

    @Test
    fun `a real payment that mentions downloading the app is still a transaction`() = runTest {
        // Also verbatim. MTN appends "Download the MoMo App ... Click here:" to genuine
        // confirmations, so a promotion filter keyed on those words would discard real money.
        val outcome = arrive(
            sender = "MTN MoMo",
            body = "Payment made for GHS 295.00 to AARON AMPEM LARTEY Current Balance: GHS " +
                "1042.16 . Available Balance: GHS 1042.16. Reference: X. Transaction ID: " +
                "90078777179. Fee charged: GHS2.21 Tax charged: 0. Download the MoMo App " +
                "for a Faster & Easier Experience. Click here: https://mtnmymomo.onelink.me/X"
        )

        assertThat(outcome).isNotInstanceOf(CaptureOutcome.Ignored::class.java)
    }

    @Test
    fun `a rescan clears marketing the old rules had kept`() = runTest {
        // Seeded the way the old rules would have: straight into the queue, no classifier.
        val offer = "Good evening. You are qualified for up to GHS 1000. Dial *170# today."
        database.evidenceDao().insert(
            heldEvidence(evidenceId = "offer-1", body = offer)
        )
        database.evidenceDao().insert(
            heldEvidence(
                evidenceId = "real-1",
                body = "Payment received for GHS 65.00 from SOLOMON OPARE Current Balance: " +
                    "GHS 1339.37. Transaction ID: 90075281288."
            )
        )
        assertThat(dashboard.observeHeldCount().first()).isEqualTo(2)

        assertThat(dashboard.rescanHeld()).isEqualTo(1)

        // The offer is gone; the payment is untouched and still the agent's to confirm.
        val remaining = dashboard.observeHeld().first().single()
        assertThat(remaining.evidenceId).isEqualTo("real-1")
    }

    @Test
    fun `a rescan never promotes anything into the ledger`() = runTest {
        database.evidenceDao().insert(
            heldEvidence(
                evidenceId = "real-2",
                body = "Payment made for GHS 295.00 to AARON AMPEM LARTEY Current Balance: " +
                    "GHS 1042.16. Transaction ID: 90078777179."
            )
        )

        dashboard.rescanHeld()

        // Still waiting for a person. A rule change is not a reason to post money.
        assertThat(dashboard.observeHeldCount().first()).isEqualTo(1)
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
    }

    @Test
    fun `replaying a message the phone already recorded records nothing new`() = runTest {
        // What the catch-up after an update does: hands the same alerts to capture a second
        // time. The fingerprint is built from the message and the moment it arrived, so a
        // replay carrying the same arrival time is the same evidence and is refused. Without
        // that, recovering a missed day would double every transaction that was not missed.
        val body = "Cash In of GHS 500.00 from 0241234567. New balance GHS 1,250.00. Ref: MP1."

        val first = arrive(sender = "MTN MoMo", body = body)
        val replay = arrive(sender = "MTN MoMo", body = body)

        assertThat(first).isInstanceOf(CaptureOutcome.Queued::class.java)
        assertThat(replay).isInstanceOf(CaptureOutcome.DuplicateOnThisDevice::class.java)
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
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

    /** An evidence row in the state the old rules left behind: held, with its text. */
    private fun heldEvidence(evidenceId: String, body: String) = EvidenceEntity(
        evidenceId = evidenceId,
        localTransactionId = null,
        sourceType = "ANDROID_SMS",
        provider = "MTN",
        senderIdentity = "MTN MoMo",
        transactionType = "UNKNOWN",
        amountMinor = null,
        currency = "GHS",
        reference = null,
        customerPhoneNumber = null,
        occurredAtUtcMillis = clock,
        observedAtUtcMillis = clock,
        fingerprint = "fp-$evidenceId",
        fingerprintVersion = "v1",
        parserName = "MtnSmsParser",
        parserVersion = "mtn-v1",
        confidence = 0.3,
        state = "PENDING_REVIEW",
        outcomeReason = "'UNKNOWN' requires review before it can be posted.",
        rawMessage = body,
        rawMessagePurgedAtUtcMillis = null,
        deviceId = "44444444-4444-4444-4444-444444444444",
        createdAtUtcMillis = clock
    )
}
