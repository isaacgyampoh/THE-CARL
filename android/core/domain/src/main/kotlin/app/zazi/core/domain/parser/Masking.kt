package app.zazi.core.domain.parser

/**
 * Removes the people from a message, leaving its shape.
 *
 * <p>An unreadable wording has to leave the handset for anyone to fix it, and what fixes a
 * parser is the shape of a message — where the amount sits, how the reference is labelled,
 * which words state the direction. Never who was in it. A customer's number and name are the
 * two things Zazi would be least forgiven for sending somewhere, and they are the two things
 * a parser author does not need.</p>
 *
 * <p>Amounts and references are left alone on purpose: an amount's punctuation is exactly what
 * a parser reads, and a reference is what the network's own record is keyed on.</p>
 */
object Masking {

    private val GHANA_NUMBER = Regex("""(?<![0-9])(?:\+?233|0)\d{9}(?![0-9])""")

    /**
     * A run of capitalised words, which is how the networks write a person.
     *
     * <p>Bounded to four words so it cannot swallow a sentence, and requires at least two so
     * that "GHS", "MTN" and "Ref" survive — those carry meaning a parser depends on.</p>
     */
    private val PERSON_NAME = Regex("""\b[A-Z][a-z]+(?:\s+[A-Z][a-z']+){1,3}\b|\b[A-Z]{2,}(?:\s+[A-Z]{2,}){1,3}\b""")

    private val KEEP = setOf(
        "GHS", "GHC", "MTN", "AT", "SMS", "ID", "REF", "VIP", "TAX", "MOMO",
        "CASH IN", "CASH OUT", "TRANSACTION ID", "CURRENT BALANCE", "AVAILABLE BALANCE",
        "NEW BALANCE", "FEE CHARGED", "TAX CHARGED", "TRANSACTION FEE", "BIG NEWS"
    )

    fun maskPeople(message: String): String =
        GHANA_NUMBER.replace(message) { "[number]" }
            .let { withoutNumbers ->
                PERSON_NAME.replace(withoutNumbers) { match ->
                    if (KEEP.any { match.value.uppercase().contains(it) }) match.value else "[name]"
                }
            }
}
