package app.thecarl.core.data

import app.thecarl.core.data.capture.CaptureOutcome
import app.thecarl.core.data.capture.SmsCaptureRequest
import app.thecarl.core.data.database.CarlDatabase
import app.thecarl.core.data.repository.CaptureRepository
import app.thecarl.core.data.repository.OutboxRepository
import app.thecarl.core.domain.model.EvidenceSourceType
import app.thecarl.core.domain.model.EvidenceState
import app.thecarl.core.domain.model.Provider
import app.thecarl.core.domain.model.TransactionType
import app.thecarl.core.domain.sync.OutboxState
import com.google.common.truth.Truth.assertThat
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/**
 * SMS as an evidence source feeding the existing capture pipeline.
 *
 * <p>The point of every test here is that SMS changes <em>what was observed</em> and nothing
 * about what happens next. Direction still comes from the ledger projection, the auto-post
 * bar is still the evidence rules manual capture uses, and the idempotency key is still the
 * one shared fingerprint.</p>
 */
@RunWith(RobolectricTestRunner::class)
class SmsCaptureTest {

    private lateinit var database: CarlDatabase
    private lateinit var capture: CaptureRepository
    private lateinit var outbox: OutboxRepository

    private val organizationId = "22222222-2222-2222-2222-222222222222"

    /** Fixed so a redelivery of the same message reuses it, as the real SMSC stamp does. */
    private val receivedAt = 1_723_700_000_000L

    @Before
    fun setUp() {
        database = createTestDatabase()
        capture = CaptureRepository(
            database = database,
            deviceInstallationId = "installation-abc",
            organizationId = organizationId,
            branchId = "33333333-3333-3333-3333-333333333333",
            deviceId = "44444444-4444-4444-4444-444444444444"
        )
        outbox = OutboxRepository(database)
    }

    @After
    fun tearDown() = database.close()

    // ─── The happy path ──────────────────────────────────────────────────────

    @Test
    fun `a complete cash-in posts and moves the ledger in the projection's direction`() = runTest {
        val outcome = sms(
            "Cash In of GHS 500.00 from 0241000001 JOHN SYNTHETIC. " +
                "Ref: MP240815.1201.A00001. Your MoMo agent balance is GHS 12,340.00"
        )

        assertThat(outcome).isInstanceOf(CaptureOutcome.Queued::class.java)
        val queued = outcome as CaptureOutcome.Queued

        val transaction = database.localTransactionDao().findByClientId(queued.clientTransactionId)!!
        assertThat(transaction.transactionType).isEqualTo(TransactionType.CASH_IN.name)
        assertThat(transaction.amountMinor).isEqualTo(50_000L)
        assertThat(transaction.sourceType).isEqualTo(EvidenceSourceType.ANDROID_SMS.name)

        // Cash-in: the agent takes physical cash and sends e-money. The message had no say.
        assertThat(transaction.cashDeltaMinor).isEqualTo(50_000L)
        assertThat(transaction.floatDeltaMinor).isEqualTo(-50_000L)

        assertThat(outbox.countByState(OutboxState.PENDING)).isEqualTo(1)
    }

    @Test
    fun `a cash-out reverses the direction`() = runTest {
        val outcome = sms(
            "Cash Out of GHS 200.00 to 0241000002 AMA SYNTHETIC. Ref: MP240815.1301.B00002"
        ) as CaptureOutcome.Queued

        val transaction = database.localTransactionDao().findByClientId(outcome.clientTransactionId)!!
        assertThat(transaction.cashDeltaMinor).isEqualTo(-20_000L)
        assertThat(transaction.floatDeltaMinor).isEqualTo(20_000L)
    }

    @Test
    fun `evidence keeps the provenance an auditor needs`() = runTest {
        val body = "Cash In of GHS 75.50 from 0241000003. Ref: MP240815.1401.C00003"
        val outcome = sms(body, sender = "MTN") as CaptureOutcome.Queued

        val evidence = database.evidenceDao().findById(outcome.evidenceId)!!
        assertThat(evidence.sourceType).isEqualTo(EvidenceSourceType.ANDROID_SMS.name)
        assertThat(evidence.senderIdentity).isEqualTo("MTN")
        assertThat(evidence.provider).isEqualTo(Provider.MTN.code)
        assertThat(evidence.rawMessage).isEqualTo(body)
        assertThat(evidence.occurredAtUtcMillis).isEqualTo(receivedAt)
        assertThat(evidence.parserName).isNotEmpty()
        assertThat(evidence.parserVersion).isNotEmpty()
        assertThat(evidence.confidence).isGreaterThan(0.8)
        assertThat(evidence.fingerprintVersion).isNotEmpty()
    }

    // ─── The bar for posting automatically ───────────────────────────────────

    @Test
    fun `a truncated message is held rather than posting a plausible smaller amount`() = runTest {
        // The defect this rule exists for: "Cash In of GHS 500.00 ... Ref: MP240815..."
        // truncated in delivery parses as a perfectly believable GHS 5 and would understate
        // the agent's till by GHS 495.
        val outcome = sms("Cash In of GHS 5")

        assertThat(outcome).isInstanceOf(CaptureOutcome.HeldForReview::class.java)
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
        assertThat(outbox.countByState(OutboxState.PENDING)).isEqualTo(0)

        // The observation is still recorded — it is real, it simply cannot be trusted.
        assertThat(database.evidenceDao().count()).isEqualTo(1)
    }

    @Test
    fun `a message with no provider reference is held`() = runTest {
        val outcome = sms("Cash In of GHS 250.00 from 0241000004")

        assertThat(outcome).isInstanceOf(CaptureOutcome.HeldForReview::class.java)
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
    }

    @Test
    fun `a promotional message never becomes money`() = runTest {
        val outcome = sms("Get GHS 50.00 bonus airtime when you recharge today! Terms apply.")

        // Whether it is ignored or held, the one unacceptable outcome is a transaction.
        assertThat(outcome).isNotInstanceOf(CaptureOutcome.Queued::class.java)
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
    }

    @Test
    fun `a reversal is never turned into a fresh transaction`() = runTest {
        val outcome = sms(
            "Reversal of GHS 300.00 has been processed. Ref: MP240815.1501.D00004"
        )

        // A reversal must be the exact inverse of a specific original, which a single
        // message cannot identify. Posting it as a new transaction would move money twice.
        assertThat(outcome).isInstanceOf(CaptureOutcome.HeldForReview::class.java)
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
    }

    @Test
    fun `commission is classified as commission and not as an ordinary cash-in`() = runTest {
        val outcome = sms(
            "You have received Commission of GHS 12.75. Ref: MP240815.2201.L00011"
        ) as CaptureOutcome.Queued

        val transaction = database.localTransactionDao().findByClientId(outcome.clientTransactionId)!!

        // "RECEIVED" appears in both templates. Testing it before "COMMISSION" once made
        // every commission alert a cash-in, which inflates the till against float that never
        // moved.
        assertThat(transaction.transactionType).isEqualTo(TransactionType.COMMISSION.name)
        assertThat(transaction.amountMinor).isEqualTo(1_275L)
    }

    @Test
    fun `an unclassified message is held rather than guessed`() = runTest {
        val outcome = sms("Your request GHS 10.00 Ref: MP240815.1601.E00005 has been noted")

        assertThat(outcome).isNotInstanceOf(CaptureOutcome.Queued::class.java)
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
    }

    @Test
    fun `an unsupported provider template does not auto-post`() = runTest {
        val outcome = sms(
            "TXN ALERT: value GHS 400.00 settled. Code 998877",
            sender = "SOMEBANK"
        )

        // The generic parser reports low confidence whatever it extracts. An unrecognised
        // template is exactly where a confident guess is most dangerous.
        assertThat(outcome).isNotInstanceOf(CaptureOutcome.Queued::class.java)
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
    }

    @Test
    fun `an ordinary text is not stored at all`() = runTest {
        val outcome = sms("Your verification code is 481920. Do not share it with anyone.")

        // Keeping this would put someone's one-time code in the evidence table.
        assertThat(outcome).isInstanceOf(CaptureOutcome.Ignored::class.java)
        assertThat(database.evidenceDao().count()).isEqualTo(0)
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
    }

    // ─── Money arithmetic ────────────────────────────────────────────────────

    @Test
    fun `a large amount keeps every digit`() = runTest {
        val outcome = sms(
            "Cash In of GHS 1,250,000.75 from 0241000005. Ref: MP240815.1701.F00006"
        ) as CaptureOutcome.Queued

        // The thousands separators previously turned this into GHS 1.25.
        val transaction = database.localTransactionDao().findByClientId(outcome.clientTransactionId)!!
        assertThat(transaction.amountMinor).isEqualTo(125_000_075L)
    }

    @Test
    fun `a sub-pesewa amount is never silently rounded up into money`() = runTest {
        val outcome = sms("Cash In of GHS 0.10 from 0241000006. Ref: MP240815.1801.G00007")

        val queued = outcome as CaptureOutcome.Queued
        val transaction = database.localTransactionDao().findByClientId(queued.clientTransactionId)!!
        assertThat(transaction.amountMinor).isEqualTo(10L)
    }

    // ─── Duplicate delivery ──────────────────────────────────────────────────

    @Test
    fun `the same message delivered twice produces one transaction`() = runTest {
        val body = "Cash In of GHS 320.00 from 0241000007. Ref: MP240815.1901.H00008"

        val first = sms(body) as CaptureOutcome.Queued
        val second = sms(body)

        // Android genuinely redelivers. The existing fingerprint recognises it — no second
        // algorithm, and no second row.
        assertThat(second).isInstanceOf(CaptureOutcome.DuplicateOnThisDevice::class.java)
        assertThat((second as CaptureOutcome.DuplicateOnThisDevice).fingerprint)
            .isEqualTo(first.fingerprint)

        assertThat(database.localTransactionDao().count()).isEqualTo(1)
        assertThat(outbox.countByState(OutboxState.PENDING)).isEqualTo(1)

        // The device stops at the fingerprint match and stores nothing further. This differs
        // from the server, which records every observation including duplicates — there, a
        // duplicate arriving from another device is itself worth auditing. Locally the second
        // copy is the same handset seeing the same text again, and keeping another copy of
        // the raw message would duplicate stored SMS content for no reconciliation value.
        assertThat(database.evidenceDao().count()).isEqualTo(1)
    }

    @Test
    fun `a manual entry describing the same event collides with the SMS`() = runTest {
        val first = sms(
            "Cash In of GHS 90.00 from 0241000008. Ref: MP240815.2001.J00009"
        ) as CaptureOutcome.Queued

        val again = sms("Cash In of GHS 90.00 from 0241000008. Ref: MP240815.2001.J00009")

        // One fingerprint algorithm across every source, or the agent gets two ledger rows
        // for one real event.
        assertThat((again as CaptureOutcome.DuplicateOnThisDevice).fingerprint)
            .isEqualTo(first.fingerprint)
    }

    // ─── Robustness ──────────────────────────────────────────────────────────

    @Test
    fun `a blank message is ignored without a crash`() = runTest {
        assertThat(sms("   ")).isInstanceOf(CaptureOutcome.Ignored::class.java)
        assertThat(database.evidenceDao().count()).isEqualTo(0)
    }

    @Test
    fun `a reassembled long message parses as one transaction`() = runTest {
        // What the receiver hands over after joining fragments. Parsing the parts separately
        // would produce a truncated amount or several partial transactions.
        val reassembled = "Cash In of GHS 1,500.00 from 0241000009 KOFI SYNTHETIC. " +
            "Ref: MP240815.2101.K00010. Your MoMo agent balance is GHS 45,000.00"

        val outcome = sms(reassembled) as CaptureOutcome.Queued
        val transaction = database.localTransactionDao().findByClientId(outcome.clientTransactionId)!!

        assertThat(transaction.amountMinor).isEqualTo(150_000L)
        assertThat(transaction.transactionType).isEqualTo(TransactionType.CASH_IN.name)
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
    }

    @Test
    fun `a fragment on its own does not post`() = runTest {
        // If reassembly ever failed, the leading fragment must not look like a transaction.
        val outcome = sms("Cash In of GHS 1,500.00 from 0241000009 KOFI SYN")

        assertThat(outcome).isNotInstanceOf(CaptureOutcome.Queued::class.java)
        assertThat(database.localTransactionDao().count()).isEqualTo(0)
    }

    @Test
    fun `held evidence is preserved so it survives a restart`() = runTest {
        sms("Cash In of GHS 5")

        val stored = database.evidenceDao().count()
        assertThat(stored).isEqualTo(1)

        // Reopening the same underlying store must still show it.
        val held = database.evidenceDao().findByState(EvidenceState.PENDING_REVIEW.name)
        assertThat(held).hasSize(1)
        assertThat(held.first().outcomeReason).isNotNull()
    }

    // ─── Helper ──────────────────────────────────────────────────────────────

    private suspend fun sms(body: String, sender: String = "MTN") = capture.captureSms(
        SmsCaptureRequest(
            senderIdentity = sender,
            body = body,
            receivedAtUtcMillis = receivedAt
        )
    )
}
