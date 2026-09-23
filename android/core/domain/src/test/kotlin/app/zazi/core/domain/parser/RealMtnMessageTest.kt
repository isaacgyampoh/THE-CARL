package app.zazi.core.domain.parser

import app.zazi.core.domain.model.TransactionType
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * The messages an MTN MoMo vendor's handset actually receives.
 *
 * <p>Every message in this file is verbatim from a live handset, names and all. They are here
 * because the parser was written against templates that turned out not to be the ones MTN
 * sends: "Cash In" and "Received from" were handled, and "Payment received for GHS X from
 * NAME" — the wording on the phone — matched nothing at all.</p>
 *
 * <p>The vendor's own words for what this has to do: <i>detect transactions, not messages</i>.
 * Half of what arrives from the shortcode is a loan offer or a campaign notice quoting figures
 * in cedis, and every one of those reads as financial unless it is named and excluded.</p>
 */
class RealMtnMessageTest {

    private val registry = SmsParserRegistry()

    private fun parse(body: String) = registry.parse("MTN MoMo", body)

    private fun classify(body: String): MessageClassifier.Verdict {
        val parsed = parse(body)
        val normalized = BaseSmsParser.normalize(body)
        return MessageClassifier.classify(
            normalizedBody = normalized,
            hasAmount = parsed.amount != null,
            hasReference = !parsed.reference.isNullOrBlank(),
            hasCounterparty = !parsed.customerPhoneNumber.isNullOrBlank()
        )
    }

    // ─── Real transactions ───────────────────────────────────────────────────

    private val paymentReceived =
        "Payment received for GHS 295.00 from AARON AMPEM LARTEY  Current Balance: GHS 1337.16 " +
            ". Available Balance: GHS 1337.16. Reference: 1. Transaction ID: 90079732268. " +
            "TRANSACTION FEE: 0.00"

    private val paymentMade =
        "Payment made for GHS 295.00 to AARON AMPEM LARTEY Current Balance: GHS 1042.16 . " +
            "Available Balance: GHS 1042.16. Reference: X. Transaction ID: 90078777179. " +
            "Fee charged: GHS2.21 Tax charged: 0. Download the MoMo App for a Faster & Easier " +
            "Experience. Click here: https://mtnmymomo.onelink.me/XJOt/MoMo"

    private val paymentReceivedSmall =
        "Payment received for GHS 65.00 from SOLOMON OPARE  Current Balance: GHS 1339.37 . " +
            "Available Balance: GHS 1339.37. Reference: opare. Transaction ID: 90075281288. " +
            "TRANSACTION FEE: 0.00"

    @Test
    fun `the amount is the transaction, not the balance beside it`() {
        // Three figures in this message are GHS and only one is the transaction. Reading the
        // balance instead would overstate it by more than four times.
        assertThat(parse(paymentReceived).amount?.toPlainString()).isEqualTo("295.00")
        assertThat(parse(paymentReceivedSmall).amount?.toPlainString()).isEqualTo("65.00")
    }

    @Test
    fun `the transaction id is preferred over a reference too short to identify anything`() {
        // "Reference: 1" identifies nothing. The transaction id is what MTN's own records are
        // keyed on, so it is what a dispute is settled with.
        assertThat(parse(paymentReceived).reference).isEqualTo("90079732268")
        assertThat(parse(paymentMade).reference).isEqualTo("90078777179")
    }

    @Test
    fun `the counterparty is a name, because MTN does not put a number in these`() {
        // The parser looked only for digits, found none, and reported no counterparty — so a
        // transaction failed the "has a counterparty" test on a field that was always there.
        assertThat(parse(paymentReceived).customerPhoneNumber).isEqualTo("AARON AMPEM LARTEY")
        assertThat(parse(paymentReceivedSmall).customerPhoneNumber).isEqualTo("SOLOMON OPARE")
    }

    @Test
    fun `a real payment is not thrown away for mentioning the MoMo app`() {
        // MTN appends "Download the MoMo App ... Click here:" to genuine confirmations. A
        // marketing filter keyed on those words would discard this money.
        assertThat(classify(paymentMade)).isNotEqualTo(MessageClassifier.Verdict.NOT_A_TRANSACTION)
    }

    @Test
    fun `all three real payments survive classification`() {
        listOf(paymentReceived, paymentMade, paymentReceivedSmall).forEach { message ->
            assertThat(classify(message)).isNotEqualTo(MessageClassifier.Verdict.NOT_A_TRANSACTION)
        }
    }

    // ─── What must be thrown away ────────────────────────────────────────────

    @Test
    fun `a prize draw quoting millions is not a transaction`() {
        val promo = "Y'ello! 233533547740, GHS 1.4 MILLION in prizes including a GHS 500 000 " +
            "CASH Grand Prize is waiting in the MTN Swipe & Win Promo. Dial *5030# for FREE. " +
            "1st day free,  then GHS 1.5/day. To exit  send STOP to 5030."

        assertThat(classify(promo)).isEqualTo(MessageClassifier.Verdict.NOT_A_TRANSACTION)
    }

    @Test
    fun `a loan offer is not a transaction`() {
        val offer = "Good evening. You are qualified for up to GHS 1000. Dial *170# to check " +
            "your Qwikloan limit today."

        assertThat(classify(offer)).isEqualTo(MessageClassifier.Verdict.NOT_A_TRANSACTION)
    }

    @Test
    fun `a campaign notice is not a transaction`() {
        val notice = "Dear valued customer, this is a notification for Heroes of Change. " +
            "Vote for your favourite hero today."

        assertThat(classify(notice)).isEqualTo(MessageClassifier.Verdict.NOT_A_TRANSACTION)
    }

    @Test
    fun `a balance reply on its own is not a transaction`() {
        assertThat(classify("Your MTN MoMo balance is GHS 1339.37 as at 23 Sept."))
            .isEqualTo(MessageClassifier.Verdict.NOT_A_TRANSACTION)
    }

    @Test
    fun `a customer number is never invented from inside the transaction id`() {
        // Found by the test above. MTN's id 90079732268 contains "0079732268" — a zero and
        // nine digits — so the number pattern matched inside it and every payment was filed
        // against a customer who does not exist. An agent reads that number back to whoever
        // is disputing the payment, so a wrong one is worse than a blank.
        assertThat(parse(paymentReceived).customerPhoneNumber).doesNotContain("0079732268")

        // A real number in the same message is still found.
        val withNumber = "Payment received for GHS 40.00 from 0241234567 Current Balance: " +
            "GHS 100.00. Transaction ID: 90079732268."
        assertThat(parse(withNumber).customerPhoneNumber).isEqualTo("0241234567")
    }

    @Test
    fun `the provider's own timestamp is used when the message carries one`() {
        // Verbatim. MTN stamps completed payments, and that is when the money moved — not
        // when this handset happened to be switched on to receive the message. A phone out of
        // signal all afternoon would otherwise file the whole afternoon's work at one minute.
        val airtime = "Your payment of GHS 20.00 to MTN AIRTIME has been completed at " +
            "2026-09-22 23:21:53. Your new balance: GHS 1274.37. Fee was GHS 0.00 Tax was " +
            "GHS -. Reference: -. Financial Transaction Id: 90057627058."

        val at = parse(airtime).occurredAtUtcMillis
        assertThat(at).isNotNull()
        // Ghana keeps GMT all year, so the stated time is UTC.
        assertThat(java.time.Instant.ofEpochMilli(at!!).toString())
            .isEqualTo("2026-09-22T23:21:53Z")
    }

    @Test
    fun `a message with no stamp reports no time rather than inventing one`() {
        assertThat(parse(paymentReceived).occurredAtUtcMillis).isNull()
    }

    @Test
    fun `the financial transaction id is recovered from the airtime template`() {
        val airtime = "Your payment of GHS 20.00 to MTN AIRTIME has been completed at " +
            "2026-09-22 23:21:53. Your new balance: GHS 1274.37. Reference: -. " +
            "Financial Transaction Id: 90057627058."

        assertThat(parse(airtime).reference).isEqualTo("90057627058")
        assertThat(parse(airtime).amount?.toPlainString()).isEqualTo("20.00")
    }

    // ─── Only what a vendor is paid for ──────────────────────────────────────

    @Test
    fun `buying airtime is not the work a vendor is paid for`() {
        // Verbatim, and a genuine payment by every other test: an amount, a completed action,
        // a transaction id and a counterparty. It is the agent topping up their own phone,
        // and counting it as trading puts their phone bill in the day's takings.
        val airtime = "Your payment of GHS 20.00 to MTN AIRTIME has been completed at " +
            "2026-09-22 23:21:53. Your new balance: GHS 1274.37. Financial Transaction Id: " +
            "90057627058."

        assertThat(classify(airtime)).isEqualTo(MessageClassifier.Verdict.NOT_A_TRANSACTION)
    }

    @Test
    fun `trading and earnings post by themselves, nothing else does`() {
        // A vendor's whole job is a customer putting cash into a wallet or taking it out.
        // Deposit is cash-in; withdrawal is cash-out. Commission is not a customer's
        // transaction but it is the vendor's earnings, and holding each one for review would
        // bury the queue and leave the profit figures permanently short.
        listOf(
            TransactionType.CASH_IN,
            TransactionType.CASH_OUT,
            TransactionType.COMMISSION
        ).forEach { assertThat(MessageClassifier.postsAutomatically(it)).isTrue() }

        listOf(
            TransactionType.TRANSFER,
            TransactionType.REVERSAL,
            TransactionType.ADJUSTMENT,
            TransactionType.UNKNOWN
        ).forEach { assertThat(MessageClassifier.postsAutomatically(it)).isFalse() }
    }

    // ─── The direction is still the vendor's to state ────────────────────────

    @Test
    fun `payment wordings are not guessed into a direction`() {
        // The balances prove the float moved — 1042.16 + 295.00 = 1337.16 — but not whether
        // cash left the drawer at the same moment. Cash-in and cash-out are opposites, so a
        // guess here is wrong by twice the amount on every transaction it touches. Held for
        // the vendor to say, once, rather than assumed.
        assertThat(parse(paymentReceived).transactionType).isEqualTo(
            app.zazi.core.domain.model.TransactionType.UNKNOWN
        )
        assertThat(parse(paymentReceived).isUsable).isFalse()
    }
}
