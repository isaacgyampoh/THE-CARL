package app.zazi.ui.state

import app.zazi.core.data.network.DayCloseResponse
import java.math.BigDecimal
import java.math.RoundingMode

/** What the close-the-day screen needs to say, kept out of Compose so it is tested plainly. */
object CloseDay {

    /** Differences under a cedi are coins in a drawer. The server uses the same line. */
    private const val TOLERANCE_MINOR = 100L

    /** Pesewas, or null for anything that is not a plain non-negative amount. */
    fun parseMinor(input: String): Long? {
        val value = runCatching { BigDecimal(input.trim().replace(",", "")) }.getOrNull() ?: return null
        if (value.signum() < 0 || value.scale() > 2) return null
        return value.movePointRight(2).setScale(0, RoundingMode.UNNECESSARY).longValueExact()
    }

    fun headline(result: DayCloseResponse): String = when (result.status) {
        "Baseline" -> "Day closed — starting count saved"
        "Balanced" -> "Day closed — all balanced"
        "Short" -> "Day closed — you are short"
        "Over" -> "Day closed — you are over"
        else -> "Day closed"
    }

    fun advice(result: DayCloseResponse): String = when (result.status) {
        "Baseline" -> "This is your first count, so there is nothing to compare it with yet. From your next close, Zazi will tell you if anything is short."
        "Balanced" -> "Your cash and float agree with everything you recorded. Well done."
        "Short" -> "Look for a transaction you did not record. If you find one, record it, then close again. Your owner can see this close."
        "Over" -> "Look for a transaction recorded twice, or recorded but not done. Your owner can see this close."
        else -> ""
    }

    /** "OK", "Short ₵50.00", "Over ₵20.00" — or null on a first count, which has no comparison. */
    fun difference(cedis: Double?): String? {
        cedis ?: return null
        val minor = BigDecimal.valueOf(cedis).movePointRight(2).setScale(0, RoundingMode.HALF_UP).toLong()
        return when {
            kotlin.math.abs(minor) < TOLERANCE_MINOR -> "OK"
            minor < 0 -> "Short ${MoneyFormat.format(-minor)}"
            else -> "Over ${MoneyFormat.format(minor)}"
        }
    }

    fun minor(cedis: Double): Long =
        BigDecimal.valueOf(cedis).movePointRight(2).setScale(0, RoundingMode.HALF_UP).toLong()
}
