package app.zazi.ui

import app.zazi.ui.state.ActivityDelivery
import app.zazi.ui.state.Receipt
import app.zazi.ui.state.TransactionDetail
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/** The message a customer keeps. */
class ReceiptTest {

    private fun detail(label: String, reference: String? = "MP240815.1202") = TransactionDetail(
        clientTransactionId = "CTX-5eed0001-0123456789ABCDEFGHJKMNPQRS",
        label = label,
        provider = "MTN",
        amountMinor = 5_000,
        cashDeltaMinor = -5_000,
        atUtcMillis = 1_789_000_000_000,
        customerPhone = "0244123456",
        reference = reference,
        capturedAutomatically = true,
        delivery = ActivityDelivery.SENT,
        attemptCount = 1,
        lastReasonCode = null,
        isRetryable = false
    )

    @Test
    fun `a cash out reads from the customer's side`() {
        val text = Receipt.forCustomer(detail("Cash out"), "Asante Mobile Money")

        assertThat(text).contains("You withdrew ₵50.00")
        assertThat(text).contains("at Asante Mobile Money")
        assertThat(text).contains("(MTN)")
        assertThat(text).contains("Ref MP240815.1202")
        // The Zazi reference is what support asks for.
        assertThat(text).contains("NPQRS")
    }

    @Test
    fun `a cash in reads as a deposit, and a missing business name is not left blank`() {
        val text = Receipt.forCustomer(detail("Cash in", reference = null), null)

        assertThat(text).contains("You deposited ₵50.00")
        assertThat(text).contains("at your agent")
        assertThat(text).doesNotContain("Ref .")
        assertThat(text).doesNotContain("null")
    }
}
