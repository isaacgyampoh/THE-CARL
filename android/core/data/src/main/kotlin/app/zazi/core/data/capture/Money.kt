package app.zazi.core.data.capture

import java.math.BigDecimal
import java.math.RoundingMode

/**
 * Conversion between decimal amounts and the minor units stored locally.
 *
 * <p>SQLite has no decimal type and REAL is binary floating point, which cannot represent
 * GHS 0.10 exactly. Amounts are therefore stored as Long pesewas and converted only at the
 * boundary. Nothing in between ever holds a Double.</p>
 */
object MinorUnits {
    private const val MINOR_UNITS_PER_MAJOR = 100L

    /** GHS carries two decimal places. */
    private const val CURRENCY_SCALE = 2

    private val SCALE = BigDecimal(MINOR_UNITS_PER_MAJOR)

    /**
     * @throws IllegalArgumentException if the amount carries sub-pesewa precision, rather
     * than rounding it away silently — a rounded amount is a wrong amount.
     */
    fun fromDecimal(amount: BigDecimal): Long {
        val scaled = amount.multiply(SCALE)
        require(scaled.stripTrailingZeros().scale() <= 0) {
            "Amount $amount has sub-pesewa precision and cannot be stored exactly."
        }
        return scaled.setScale(0, RoundingMode.UNNECESSARY).toLong()
    }

    /**
     * Converts back to a decimal amount at currency scale.
     *
     * The scale is set explicitly so 10 pesewas renders as "0.10" rather than "0.1".
     * BigDecimal equality is scale-sensitive, so an unset scale makes two equal amounts
     * compare unequal — a subtle way for reconciliation to disagree with itself.
     */
    fun toDecimal(minor: Long): BigDecimal =
        BigDecimal(minor).divide(SCALE).setScale(CURRENCY_SCALE, RoundingMode.UNNECESSARY)
}
