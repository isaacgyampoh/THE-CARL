package app.zazi.core.domain.model

/**
 * A Ghanaian mobile number, in the one spelling Zazi stores and searches by.
 *
 * <p>The customer number is what settles a complaint at the counter — "I came at 11:50 and
 * withdrew fifty cedis" — so it has to be recorded every time and found every time. Found is
 * the harder half: the same customer arrives as "0244 123 456" typed by hand, "233244123456"
 * lifted from a provider SMS and "+233 24 412 3456" pasted from a contact. Stored as typed,
 * those are three customers and a search for one misses the other two. Everything is reduced
 * to ten digits with a leading zero before it is kept.</p>
 *
 * <p>Mirrored in C# as <c>Zazi.Domain.GhanaPhoneNumber</c>. The two must agree, or a number
 * normalised on the handset would be rewritten differently by the server and searches across
 * the two would disagree.</p>
 */
object GhanaPhoneNumber {

    /**
     * The mobile prefixes in use. MTN 024 025 053 054 055 059, Telecel 020 050, AirtelTigo 026
     * 027 056 057, Glo 023.
     *
     * <p>Not used to guess the network. Numbers have been portable between networks since 2011,
     * so a 024 number may well be on Telecel; the prefix only says the number is a mobile one.</p>
     */
    val MOBILE_PREFIXES: Set<String> = setOf(
        "020", "023", "024", "025", "026", "027",
        "050", "053", "054", "055", "056", "057", "059"
    )

    /**
     * The stored form — ten digits, leading zero — or null when this is not a Ghanaian mobile
     * number.
     *
     * <p>Accepts what people actually type and paste: spaces, dashes, brackets, a +233 or 233
     * country code, and the nine digits without the leading zero.</p>
     */
    fun normalise(input: String?): String? {
        if (input.isNullOrBlank()) return null
        val digits = input.filter { it.isDigit() }

        val local = when {
            digits.length == 12 && digits.startsWith("233") -> "0" + digits.substring(3)
            // Written as +233 0244… — the zero kept after the country code, which is common.
            digits.length == 13 && digits.startsWith("2330") -> digits.substring(3)
            digits.length == 10 && digits.startsWith("0") -> digits
            digits.length == 9 && !digits.startsWith("0") -> "0$digits"
            else -> return null
        }

        return if (local.substring(0, 3) in MOBILE_PREFIXES) local else null
    }

    /** True when [input] is a Ghanaian mobile number in any of the accepted spellings. */
    fun isValid(input: String?): Boolean = normalise(input) != null

    /**
     * Grouped as people read it back: 024 412 3456. Anything that is not a valid number is
     * returned as it was, so a stored oddity is shown rather than hidden.
     */
    fun display(input: String?): String {
        val n = normalise(input) ?: return input.orEmpty()
        return "${n.substring(0, 3)} ${n.substring(3, 6)} ${n.substring(6)}"
    }

    /** What to tell someone whose number was refused. */
    const val REQUIREMENT: String =
        "Enter the customer's 10-digit mobile number, for example 024 412 3456."
}
