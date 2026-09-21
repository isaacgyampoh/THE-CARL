package app.zazi.core.domain.parser

import app.zazi.core.domain.model.TransactionType
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Deposit versus cash-out.
 *
 * The two move an agent's books in opposite directions — a deposit raises cash and lowers
 * float, a cash-out does the reverse — so a misclassification is not a wrong label, it is a
 * balance wrong by twice the transaction value. Every float figure built on top of it inherits
 * the error, and the agent's own count is the only thing that would catch it.
 */
class DirectionClassificationTest {

    private val registry = SmsParserRegistry()

    private fun typeOf(sender: String, body: String): TransactionType =
        registry.parse(sender, body).transactionType

    // ─── The plain cases, all three networks ─────────────────────────────────

    @Test
    fun `MTN deposit and cash out`() {
        assertThat(typeOf("MTN", "Cash In of GHS 500.00 from 0241000001 JOHN. Ref: MP1"))
            .isEqualTo(TransactionType.CASH_IN)
        assertThat(typeOf("MTN", "Cash Out of GHS 250.00 to 0241000002 AMA. Ref: MP2"))
            .isEqualTo(TransactionType.CASH_OUT)
    }

    @Test
    fun `Telecel deposit and withdrawal`() {
        assertThat(typeOf("TelecelCash", "Telecel Cash: Deposit of GHS 1,250.00 from 0201000001"))
            .isEqualTo(TransactionType.CASH_IN)
        assertThat(typeOf("TelecelCash", "Telecel Cash: Withdrawal of GHS 300.00 to 0201000003"))
            .isEqualTo(TransactionType.CASH_OUT)
    }

    @Test
    fun `AirtelTigo deposit and cash out`() {
        assertThat(typeOf("AirtelTigo", "AirtelTigo Money: Cash In GHS 150.00 from 0271000004"))
            .isEqualTo(TransactionType.CASH_IN)
        assertThat(typeOf("AirtelTigo", "AirtelTigo Money: Cash Out GHS 420.00 to 0271000002"))
            .isEqualTo(TransactionType.CASH_OUT)
    }

    // ─── Where one direction's message contains the other's words ────────────

    @Test
    fun `a cash out that mentions cash in hand is still a cash out`() {
        // Provider messages routinely tell an agent what they are left holding. "Cash In" is
        // checked before "Cash Out", so the reminder decides the direction and the balance
        // moves the wrong way by twice the amount.
        assertThat(typeOf("MTN", "Cash Out of GHS 250.00 to 0241000002. Your cash in hand is now GHS 1,750.00"))
            .isEqualTo(TransactionType.CASH_OUT)
    }

    @Test
    fun `a withdrawal that mentions a deposit balance is still a withdrawal`() {
        assertThat(typeOf("TelecelCash", "Telecel Cash: Withdrawal of GHS 300.00. Deposit balance GHS 900.00"))
            .isEqualTo(TransactionType.CASH_OUT)
    }

    @Test
    fun `an AirtelTigo cash out mentioning a deposit total is still a cash out`() {
        assertThat(typeOf("AirtelTigo", "AirtelTigo Money: Cash Out GHS 80.00 to 0271000009. Today's deposits GHS 4,200.00"))
            .isEqualTo(TransactionType.CASH_OUT)
    }

    // ─── When the direction cannot be read ───────────────────────────────────

    @Test
    fun `an unreadable direction is unknown and not usable`() {
        // The worst outcome would be guessing. Unknown is held for review with the amount
        // intact, so a human decides rather than the balance moving on a coin flip.
        val parsed = registry.parse("MTN", "MTN MoMo: your balance is GHS 1,000.00")
        assertThat(parsed.transactionType).isEqualTo(TransactionType.UNKNOWN)
        assertThat(parsed.isUsable).isFalse()
    }
}
