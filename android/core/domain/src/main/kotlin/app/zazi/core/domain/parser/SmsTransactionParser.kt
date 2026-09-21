package app.zazi.core.domain.parser

import app.zazi.core.domain.model.Provider
import app.zazi.core.domain.model.TransactionType
import java.math.BigDecimal

/**
 * Structured result of interpreting one message. A parser produces a *claim*, never a
 * financial record — [confidence] and [transactionType] decide what happens next.
 */
data class ParsedSms(
    val provider: Provider,
    val transactionType: TransactionType,
    val amount: BigDecimal?,
    val reference: String?,
    val customerPhoneNumber: String?,
    val balanceAfter: BigDecimal? = null,
    val confidence: Double,
    val parserName: String,
    val parserVersion: String
) {
    /**
     * A parser result is usable only with a known type, a positive amount, and enough
     * confidence. Anything else becomes evidence for review rather than a transaction.
     */
    val isUsable: Boolean
        get() = transactionType != TransactionType.UNKNOWN &&
            amount != null &&
            amount.signum() > 0 &&
            confidence >= MINIMUM_CONFIDENCE

    companion object {
        /**
         * Below this, a result is held for human classification. Set deliberately high: a
         * wrong auto-posted transaction costs an agent real money, while a held one costs
         * them a few seconds of review.
         */
        const val MINIMUM_CONFIDENCE = 0.80

        fun unparseable(provider: Provider, parserName: String, parserVersion: String) = ParsedSms(
            provider = provider,
            transactionType = TransactionType.UNKNOWN,
            amount = null,
            reference = null,
            customerPhoneNumber = null,
            confidence = 0.0,
            parserName = parserName,
            parserVersion = parserVersion
        )
    }
}

/**
 * Provider-independent SMS parsing.
 *
 * Mirrors the backend's parser architecture rather than inventing a second one. Each
 * implementation owns one provider's templates; selection happens by [canHandle], never by a
 * single accumulating regex.
 *
 * **Versioning is mandatory.** Provider templates change, and a transaction parsed last
 * month must remain traceable to the parser that interpreted it. [parserVersion] is
 * recorded on every piece of evidence and is never reused across behaviour changes.
 */
interface SmsTransactionParser {
    val parserName: String

    /** Bumped whenever parsing behaviour changes. Never reused. */
    val parserVersion: String

    val provider: Provider

    /**
     * Whether the sender identity names this provider.
     *
     * The authoritative signal, and the reason the two questions are separate. A shortcode is
     * assigned by the network and cannot be borrowed by another; message text can say anything.
     * The registry asks every parser this first, and only falls back to the body when no
     * sender is recognised.
     */
    fun claimsSender(senderIdentity: String?): Boolean

    /**
     * Whether the message body names this provider, for when the sender does not.
     *
     * Must key on wording only this provider uses. "MoMo" is not such a wording: in Ghana it
     * is used generically for mobile money, and matching it here attributed Telecel and
     * AirtelTigo transactions — and bank alerts — to MTN.
     */
    fun claimsBody(normalizedBody: String): Boolean

    /** Whether this parser recognises the message at all, by either signal. */
    fun canHandle(senderIdentity: String?, normalizedBody: String): Boolean =
        claimsSender(senderIdentity) || claimsBody(normalizedBody)

    fun parse(normalizedBody: String): ParsedSms
}

/** Shared normalisation and extraction helpers. */
abstract class BaseSmsParser : SmsTransactionParser {

    protected fun extractAmount(body: String): BigDecimal? {
        // Currency markers vary: GHS, GH¢, GHC, or nothing at all. Thousands separators are
        // stripped before parsing so "1,250.00" does not become 1.
        for (match in AMOUNT_PATTERN.findAll(body)) {
            val start = match.range.first
            val preceding = body.substring(maxOf(0, start - BALANCE_LOOKBEHIND), start)

            // A closing balance is not the transaction amount. Taking the first currency
            // figure in the message posts the balance instead — "balance is GHS 12,340.00
            // after Cash In of GHS 500.00" would move twenty-four times the real money.
            if (preceding.contains("BALANCE")) {
                continue
            }

            val captured = match.groupValues[1]

            // "GHS 1 250.00" matches only "1". Reading that as one cedi understates the
            // transaction by three orders of magnitude, and it carries a real reference so
            // nothing else would stop it posting. The grouping is genuinely ambiguous —
            // a space-separated thousands group and two adjacent numbers look identical —
            // so this refuses to choose and lets the evidence rules hold it for review.
            val tail = body.substring(match.range.last + 1)
            if (!captured.contains('.') && SPLIT_NUMBER_TAIL.containsMatchIn(tail)) {
                return null
            }

            return captured.replace(",", "").toBigDecimalOrNull()
        }

        return null
    }

    /** Whether the message reports a reversal, rather than merely mentioning the word. */
    protected fun mentionsReversal(body: String): Boolean =
        body.contains("REVERSAL OF") || body.contains("REVERSED")

    protected fun extractReference(body: String): String? {
        // References legitimately contain dots (MP240815.1201.A00001), so the pattern admits
        // them — which also swallows the sentence-ending period. Trimming trailing
        // punctuation keeps "MP240815.1201.A00001." from differing from the server's value.
        val captured = REFERENCE_PATTERN.find(body)?.groupValues?.get(1) ?: return null
        return captured.trimEnd('.', '-', ',').uppercase().ifBlank { null }
    }

    protected fun extractPhone(body: String): String? =
        PHONE_PATTERN.find(body)?.value?.replace(" ", "")

    protected fun extractBalance(body: String): BigDecimal? {
        val match = BALANCE_PATTERN.find(body) ?: return null
        return match.groupValues[1].replace(",", "").toBigDecimalOrNull()
    }

    companion object {
        /** Collapses whitespace and uppercases. Applied before any parser sees a message. */
        fun normalize(body: String): String =
            body.trim().replace(Regex("\\s+"), " ").uppercase()

        private val AMOUNT_PATTERN =
            Regex("""(?:GHS|GH¢|GHC|₵|CEDIS)\s*([0-9][0-9,]*(?:\.[0-9]{1,2})?)""")

        private val BALANCE_PATTERN =
            Regex("""BALANCE[^0-9]{0,20}?([0-9][0-9,]*(?:\.[0-9]{1,2})?)""")

        private val REFERENCE_PATTERN =
            Regex("""(?:REF|REFERENCE|TRANSACTION ID|TXN ID|TRANS\. ID)[:.\s]*([A-Z0-9][A-Z0-9.-]{3,39}?)(?=[\s,]|${'$'})""")

        private val PHONE_PATTERN =
            Regex("""(?:\+?233|0)\d{9}""")

        /** How far back to look for a balance label before a currency figure. */
        private const val BALANCE_LOOKBEHIND = 24

        /** A further number immediately after the match, i.e. the grouping is ambiguous. */
        private val SPLIT_NUMBER_TAIL = Regex("""^\s+[0-9]""")
    }
}
