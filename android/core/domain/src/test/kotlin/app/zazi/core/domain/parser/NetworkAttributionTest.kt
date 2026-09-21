package app.zazi.core.domain.parser

import app.zazi.core.domain.model.Provider
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Which network a message is attributed to.
 *
 * An agent who sees a Telecel transaction labelled MTN stops trusting the whole product, and
 * they are right to — the figures they reconcile against are per network. This is the one
 * classification that has to be correct before anything else matters.
 *
 * The existing fixtures are 18 MTN messages against one Telecel and one AirtelTigo, so the two
 * smaller networks were effectively unexercised. These cover them, and cover the cases where
 * one network's message contains another's vocabulary.
 */
class NetworkAttributionTest {

    private val registry = SmsParserRegistry()

    private fun providerOf(sender: String?, body: String): Provider =
        registry.parse(sender, body).provider

    // ─── The straightforward cases ───────────────────────────────────────────

    @Test
    fun `MTN cash in is attributed to MTN`() {
        assertThat(providerOf("MTN", "Cash In of GHS 500.00 from 0241000001 JOHN. Ref: MP240815.1201"))
            .isEqualTo(Provider.MTN)
    }

    @Test
    fun `Telecel Cash is attributed to Telecel`() {
        assertThat(providerOf("TelecelCash", "Telecel Cash: Deposit of GHS 1,250.00 from 0201000001. Transaction ID: TC98765432"))
            .isEqualTo(Provider.TELECEL)
    }

    @Test
    fun `AirtelTigo Money is attributed to AirtelTigo`() {
        assertThat(providerOf("AirtelTigo", "AirtelTigo Money: Cash Out GHS 420.00 to 0271000002. Ref: AT20240815002"))
            .isEqualTo(Provider.AIRTELTIGO)
    }

    @Test
    fun `Telecel under its former Vodafone sender is still Telecel`() {
        // Telecel took over Vodafone Ghana; handsets and shortcodes still carry the old name.
        assertThat(providerOf("VodafoneCash", "Telecel Cash: Deposit of GHS 90.00 from 0201000009"))
            .isEqualTo(Provider.TELECEL)
    }

    // ─── Where one network's message carries another's vocabulary ────────────

    @Test
    fun `a Telecel message that mentions momo is still Telecel`() {
        // "MoMo" is used generically for mobile money in Ghana, not only for MTN's product.
        // The MTN parser claims any message containing it and is consulted first, so a Telecel
        // message using the word would be filed under MTN — a wrong network on a real figure.
        assertThat(providerOf("TelecelCash", "Telecel Cash: Cash Out GHS 300.00 to 0201000003. Momo balance GHS 1,200.00"))
            .isEqualTo(Provider.TELECEL)
    }

    @Test
    fun `an AirtelTigo message that mentions momo is still AirtelTigo`() {
        assertThat(providerOf("AirtelTigo", "AirtelTigo Money: Cash In GHS 150.00 from 0271000004. Your momo wallet is now GHS 800.00"))
            .isEqualTo(Provider.AIRTELTIGO)
    }

    @Test
    fun `a bank message mentioning momo is not attributed to MTN`() {
        // Banks advertise transfers "to your MoMo wallet". That is not an MTN transaction.
        assertThat(providerOf("SomeBank", "SomeBank: GHS 100.00 sent to your MoMo wallet. Ref: SB0001"))
            .isNotEqualTo(Provider.MTN)
    }

    // ─── When the network genuinely cannot be determined ─────────────────────

    @Test
    fun `an unrecognised sender is reported as unknown rather than guessed`() {
        assertThat(providerOf("RandomCo", "You have received GHS 75.00. Ref: XY123"))
            .isEqualTo(Provider.UNKNOWN)
    }

    @Test
    fun `a message with no sender is reported as unknown`() {
        assertThat(providerOf(null, "Payment of GHS 40.00 received"))
            .isEqualTo(Provider.UNKNOWN)
    }

    // ─── What an unattributable message must not do ──────────────────────────

    @Test
    fun `an unattributable message is never usable, so it cannot post silently`() {
        // The failure mode that would be worst: a real amount, an unrecognised network, and
        // the transaction quietly landing in the ledger under a blank or wrong provider.
        // isUsable is false, so the capture path holds it for review instead of posting it.
        val parsed = registry.parse("RandomCo", "Cash In of GHS 500.00 from 0241000001. Ref: XY123")

        assertThat(parsed.provider).isEqualTo(Provider.UNKNOWN)
        assertThat(parsed.isUsable).isFalse()
    }

    @Test
    fun `an unattributable message keeps its amount so a human can still see it`() {
        // Held, not discarded. An agent can correct it; they cannot correct what was thrown away.
        val parsed = registry.parse("RandomCo", "Cash In of GHS 500.00 from 0241000001")

        assertThat(parsed.amount).isNotNull()
    }
}
