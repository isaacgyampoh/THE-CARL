package app.zazi.sms

import app.zazi.core.domain.parser.SmsParserRegistry
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Fragment joining, tested directly.
 *
 * <p>PDU decoding belongs to the platform. Joining the pieces belongs to us, and getting it
 * wrong is not a crash — it is a shorter message that still parses. "GHS 1,500.00" arriving
 * as "GHS 1,5" is a believable transaction for a fraction of the money.</p>
 */
class SmsReassemblyTest {

    private val parsers = SmsParserRegistry()

    @Test
    fun `fragments are joined in delivery order`() {
        val message = SmsBroadcastReceiver.reassemble(
            bodies = listOf(
                "Cash In of GHS 1,500.00 from 0241000009 KOFI SYNTHETIC. ",
                "Ref: MP240815.2101.K00010. Your MoMo agent balance is GHS 45,000.00"
            ),
            sender = "MTN",
            receivedAtUtcMillis = 1_723_700_000_000L
        )!!

        // Reversing or dropping a fragment changes the amount, so the whole string matters.
        assertThat(message.body).isEqualTo(
            "Cash In of GHS 1,500.00 from 0241000009 KOFI SYNTHETIC. " +
                "Ref: MP240815.2101.K00010. Your MoMo agent balance is GHS 45,000.00"
        )
    }

    @Test
    fun `a joined message parses as the whole transaction, not the first fragment`() {
        val message = SmsBroadcastReceiver.reassemble(
            bodies = listOf(
                "Cash In of GHS 1,500.00 from 0241000009 KOFI SYNTHETIC. ",
                "Ref: MP240815.2101.K00010"
            ),
            sender = "MTN",
            receivedAtUtcMillis = 1L
        )!!

        val parsed = parsers.parse("MTN", message.body)

        assertThat(parsed.amount!!.toPlainString()).isEqualTo("1500.00")
        assertThat(parsed.reference).isEqualTo("MP240815.2101.K00010")
        assertThat(parsed.isUsable).isTrue()
    }

    @Test
    fun `the leading fragment alone never reaches auto-post confidence`() {
        // What would happen if the join were skipped. The amount is plausible and the type is
        // right; only the missing reference stops it, which is exactly the truncation defence.
        val parsed = parsers.parse("MTN", "Cash In of GHS 1,500.00 from 0241000009 KOFI SYN")

        assertThat(parsed.isUsable).isFalse()
    }

    @Test
    fun `a lost middle fragment does not abort the message`() {
        val message = SmsBroadcastReceiver.reassemble(
            bodies = listOf("Cash In of GHS 1,500.00 ", null, "from 0241000009"),
            sender = "MTN",
            receivedAtUtcMillis = 1L
        )!!

        // Preserved as evidence rather than discarded — but without its reference it cannot
        // clear the bar, so it waits for a person instead of posting a partial figure.
        assertThat(message.body).isEqualTo("Cash In of GHS 1,500.00 from 0241000009")
        assertThat(parsers.parse("MTN", message.body).isUsable).isFalse()
    }

    @Test
    fun `a single-part message is unchanged`() {
        val message = SmsBroadcastReceiver.reassemble(
            bodies = listOf("Cash Out of GHS 200.00. Ref: MP240815.1301.B00002"),
            sender = "MTN",
            receivedAtUtcMillis = 42L
        )!!

        assertThat(message.body).isEqualTo("Cash Out of GHS 200.00. Ref: MP240815.1301.B00002")
        assertThat(message.receivedAtUtcMillis).isEqualTo(42L)
        assertThat(message.sender).isEqualTo("MTN")
    }

    @Test
    fun `nothing usable yields nothing at all`() {
        assertThat(SmsBroadcastReceiver.reassemble(emptyList(), "MTN", 1L)).isNull()
        assertThat(SmsBroadcastReceiver.reassemble(listOf(null, null), "MTN", 1L)).isNull()
        assertThat(SmsBroadcastReceiver.reassemble(listOf("   ", ""), "MTN", 1L)).isNull()
    }

    @Test
    fun `the timestamp is carried through unchanged`() {
        // The fingerprint is computed from this. Substituting the clock would give a
        // redelivered message a different identity and post it twice.
        val stamp = 1_723_700_123_456L
        val message = SmsBroadcastReceiver.reassemble(listOf("Cash In of GHS 1.00"), null, stamp)!!

        assertThat(message.receivedAtUtcMillis).isEqualTo(stamp)
    }
}
