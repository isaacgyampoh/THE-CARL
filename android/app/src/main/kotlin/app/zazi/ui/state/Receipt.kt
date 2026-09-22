package app.zazi.ui.state

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * A receipt the agent hands to the customer — sent from their own WhatsApp or SMS, so it costs
 * the business nothing and arrives from a number the customer already knows.
 *
 * <p>A customer with a receipt can settle a dispute without the agent having to remember
 * anything, which is the whole argument for recording transactions in the first place.</p>
 */
object Receipt {

    private val stamp = DateTimeFormatter.ofPattern("d MMM yyyy 'at' HH:mm", Locale.UK)

    fun forCustomer(detail: TransactionDetail, businessName: String?): String {
        val what = when {
            detail.label.equals("Cash out", ignoreCase = true) -> "You withdrew"
            detail.label.equals("Cash in", ignoreCase = true) -> "You deposited"
            else -> detail.label + " of"
        }
        val at = stamp.format(Instant.ofEpochMilli(detail.atUtcMillis).atZone(ZoneId.systemDefault()))
        val from = businessName?.takeIf { it.isNotBlank() } ?: "your agent"

        return buildString {
            append(what).append(' ').append(MoneyFormat.format(detail.amountMinor))
            append(" at ").append(from)
            append(" (").append(detail.provider).append(") on ").append(at).append('.')
            detail.reference?.takeIf { it.isNotBlank() }?.let { append(" Ref ").append(it).append('.') }
            append(" Zazi reference ").append(detail.shortReference).append('.')
            append(" Keep this message.")
        }
    }
}
