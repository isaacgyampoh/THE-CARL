package app.thecarl.core.domain.parser

/**
 * Representative provider messages.
 *
 * All numbers, references and names are synthetic. No real customer data appears here, and
 * none ever should: test fixtures are committed, searchable and copied into bug reports.
 */
object SmsFixtures {
    // ─── MTN ─────────────────────────────────────────────────────────────────
    const val MTN_CASH_IN =
        "Cash In of GHS 500.00 from 0241000001 JOHN SYNTHETIC. " +
            "Ref: MP240815.1201.A00001. Your MoMo agent balance is GHS 12,340.00"

    const val MTN_CASH_OUT =
        "Cash Out of GHS 250.50 to 0241000002 AMA SYNTHETIC. " +
            "Ref: MP240815.1202.A00002. Your MoMo agent balance is GHS 12,089.50"

    const val MTN_COMMISSION =
        "You have received Commission of GHS 12.75 for MoMo transactions. " +
            "Ref: MP240815.1203.C00003. Balance: GHS 12,102.25"

    const val MTN_REVERSAL =
        "Transaction Reversal of GHS 500.00 has been processed. " +
            "Ref: MP240815.1204.R00004. Original Ref: MP240815.1201.A00001"

    const val MTN_TRANSFER =
        "Transfer of GHS 80.00 sent to 0241000003. Ref: MP240815.1205.T00005"

    // ─── Telecel ─────────────────────────────────────────────────────────────
    const val TELECEL_CASH_IN =
        "Telecel Cash: Deposit of GHS 1,250.00 from 0201000001. " +
            "Transaction ID: TC98765432. Balance GHS 8,400.00"

    const val TELECEL_CASH_OUT =
        "Telecel Cash: Withdrawal of GHS 300.00 by 0201000002. Transaction ID: TC98765433"

    // ─── AirtelTigo ──────────────────────────────────────────────────────────
    const val AIRTELTIGO_CASH_IN =
        "AirtelTigo Money: Cash In GHS 75.50 from 0271000001. Ref: AT20240815001"

    const val AIRTELTIGO_CASH_OUT =
        "AirtelTigo Money: Cash Out GHS 420.00 to 0271000002. Ref: AT20240815002"

    // ─── Awkward but real-world shapes ───────────────────────────────────────

    /** No currency marker at all — the amount must not be invented. */
    const val MTN_NO_CURRENCY_MARKER =
        "MTN MoMo: Cash In 500 from 0241000001. Ref: MP240815.1206.A00006"

    /** Thousands separators must not truncate the amount to 1. */
    const val MTN_THOUSANDS_SEPARATOR =
        "Cash In of GHS 1,250,000.75 from 0241000001. Ref: MP240815.1207.A00007"

    /** Alternative cedi glyph. */
    const val MTN_CEDI_SYMBOL =
        "MTN MoMo Cash In of ₵340.25 from 0241000004. Ref: MP240815.1208.A00008"

    /** Recognised provider, unrecognised wording. Must not be guessed at. */
    const val MTN_UNRECOGNISED_WORDING =
        "MTN MoMo: Your service bundle has been updated. Ref: MP240815.1209.X00009"

    /** Not a transaction at all. */
    const val PROMOTIONAL =
        "Dear customer, buy MTN data bundles and get 50% extra. Dial *138# now!"

    /** Truncated mid-sentence, as a real delivery failure produces. */
    const val MALFORMED_TRUNCATED = "Cash In of GHS 5"

    /** Empty body. */
    const val EMPTY = ""

    /** Unknown provider entirely. */
    const val UNKNOWN_PROVIDER =
        "SomeBank: You have received GHS 100.00 from ACCOUNT 123456. Ref: SB0001"
}
