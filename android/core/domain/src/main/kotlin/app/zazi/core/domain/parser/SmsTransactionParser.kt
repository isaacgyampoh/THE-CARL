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
    /**
     * The counterparty's registered name, as the network confirmed it.
     *
     * <p>The name that appears on an agent's screen when they dial a number to send money,
     * and the thing a customer coming back to dispute a transaction actually remembers —
     * they saw their own name confirmed, and often cannot recall which number was used.
     * Separate from the number so either can be searched.</p>
     */
    val customerName: String? = null,
    val balanceAfter: BigDecimal? = null,
    val confidence: Double,
    val parserName: String,
    val parserVersion: String,
    /**
     * When the provider says it happened, if the message states it.
     *
     * <p>Null for the templates that do not. An SMS otherwise carries no trustworthy send
     * time, so the handset's arrival time is the fallback — but where the provider does stamp
     * the message, that is the time the transaction actually took place, and it is what a
     * customer disputing it will quote.</p>
     */
    val occurredAtUtcMillis: Long? = null
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

            // Whichever side the currency was written on.
            val captured = match.groupValues[1].ifEmpty { match.groupValues[2] }

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

    /**
     * Who the money moved to or from.
     *
     * <p>MTN's payment confirmations name a person rather than giving a number — "from AARON
     * AMPEM LARTEY" — so a parser that only looks for digits finds no counterparty at all and
     * the message fails the transaction test on a field that was there all along.</p>
     *
     * <p>The number is preferred when the message carries one, because it is what a customer
     * at the counter will quote back. The name is the fallback, never a fabricated number.</p>
     */
    protected fun extractCounterparty(body: String): String? = extractPhone(body)

    /**
     * The counterparty's registered name, where the network states one.
     *
     * <p>Never a substitute for the number — both are recorded when both are there, and a
     * message carrying only a name still produces a usable transaction rather than being
     * rejected for a field the network did not send.</p>
     */
    protected fun extractName(body: String): String? {
        val name = COUNTERPARTY_NAME.find(body)?.groupValues?.get(1)?.trim() ?: return null
        // Two characters is not a name; it is the tail of a word the pattern over-reached into.
        return name.takeIf { it.length >= 3 }
    }

    /**
     * The time the provider stamped on the message, in epoch milliseconds.
     *
     * <p>Ghana keeps GMT all year, so the stated local time is UTC and no zone conversion is
     * involved. Returns null rather than guessing when the message carries no stamp.</p>
     */
    protected fun extractTimestamp(body: String): Long? {
        val match = TIMESTAMP_PATTERN.find(body) ?: return null
        return runCatching {
            java.time.LocalDateTime.of(
                match.groupValues[1].toInt(),
                match.groupValues[2].toInt(),
                match.groupValues[3].toInt(),
                match.groupValues[4].toInt(),
                match.groupValues[5].toInt(),
                match.groupValues[6].toInt()
            ).toInstant(java.time.ZoneOffset.UTC).toEpochMilli()
        }.getOrNull()
    }

    protected fun extractBalance(body: String): BigDecimal? {
        val match = BALANCE_PATTERN.find(body) ?: return null
        return match.groupValues[1].replace(",", "").toBigDecimalOrNull()
    }

    companion object {
        /**
         * A sender identity reduced to letters and digits, uppercased.
         *
         * <p>Networks write the same sender half a dozen ways — "MTN MoMo", "MTNMobileMoney",
         * "AirtelTigo Money", "AT-Money", "T-Cash" — and which one arrives depends on the
         * aggregator, the handset and sometimes the SIM. Comparing the raw string meant a
         * space or a hyphen decided whether an agent's transaction was recognised.</p>
         */
        fun normalizeSender(senderIdentity: String?): String =
            senderIdentity?.uppercase()?.filter { it.isLetterOrDigit() }.orEmpty()

        /** Collapses whitespace and uppercases. Applied before any parser sees a message. */
        fun normalize(body: String): String =
            body.trim().replace(Regex("\\s+"), " ").uppercase()

        /**
         * The part of a message that states what this transaction was.
         *
         * A provider message routinely tells the agent what they are left holding — "Cash Out
         * of GHS 250.00 to 0241000002. Your cash in hand is now GHS 1,750.00". Searching the
         * whole body for "CASH IN" finds that reminder, and because it is checked before
         * "CASH OUT" the transaction is recorded as a deposit. A deposit and a cash-out move
         * cash and float in opposite directions, so the balance ends up wrong by twice the
         * amount, and every figure built on it inherits the error.
         *
         * The direction is stated before the amount it applies to, so everything up to the
         * first amount is the clause that describes this transaction. Anything after it is
         * commentary about balances.
         */
        fun directionClause(normalizedBody: String): String {
            val amount = AMOUNT_PATTERN.find(normalizedBody) ?: return normalizedBody
            val prefix = normalizedBody.substring(0, amount.range.first)
            // A message that leads with the amount — "GHS 500.00 has been deposited" — has no
            // prefix to read, so the whole body is still the best available evidence.
            return if (prefix.isBlank()) normalizedBody else prefix
        }

        /**
         * An amount with its currency, written either way round.
         *
         * <p>"GHS 500.00" is the common form and was the only one matched. Templates that
         * write "500.00 GHS" — and some do — yielded no amount at all, which took the parse
         * below the confidence needed to post and queued a real transaction for review. The
         * currency marker is still required in one position or the other: a bare number in a
         * message is as likely to be a date, a balance or part of a reference.</p>
         */
        private val AMOUNT_PATTERN = Regex(
            """(?:(?:GHS|GH¢|GHC|₵|CEDIS)\s*([0-9][0-9,]*(?:\.[0-9]{1,2})?)""" +
                """|([0-9][0-9,]*(?:\.[0-9]{1,2})?)\s*(?:GHS|GH¢|GHC|₵|CEDIS))"""
        )

        private val BALANCE_PATTERN =
            Regex("""BALANCE[^0-9]{0,20}?([0-9][0-9,]*(?:\.[0-9]{1,2})?)""")

        /**
         * A person named as the other side of the transaction.
         *
         * <p>Bounded by the labels MTN puts after the name, so it cannot run on into the rest
         * of the message. Letters, spaces, apostrophes and hyphens only — a name never
         * contains a digit, and admitting digits would swallow the balance that follows.</p>
         */
        private val COUNTERPARTY_NAME = Regex(
            """(?:\bFROM|\bTO)\s+([A-Z][A-Z'\- ]{2,40}?)\s*""" +
                """(?=CURRENT BALANCE|AVAILABLE BALANCE|REFERENCE|TRANSACTION ID|TXN|\.|,|${'$'})"""
        )

        private val REFERENCE_PATTERN =
            Regex("""(?:REF|REFERENCE|TRANSACTION ID|TXN ID|TRANS\. ID)[:.\s]*([A-Z0-9][A-Z0-9.-]{3,39}?)(?=[\s,]|${'$'})""")

        /**
         * A Ghanaian number, and only when the whole run of digits is one.
         *
         * <p>The boundaries are the point. Without them this matched inside any long number:
         * MTN's transaction id 90079732268 contains "0079732268", which is a 0 followed by
         * nine digits, so every payment confirmation was recorded against a customer number
         * invented from the middle of its own reference. A wrong number on a transaction is
         * worse than no number — it is what an agent reads back to a customer disputing a
         * payment, and it would have matched nothing and blamed nobody.</p>
         */
        /** "completed at 2026-09-22 23:21:53", as MTN stamps a completed payment. */
        private val TIMESTAMP_PATTERN =
            Regex("""(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})""")

        private val PHONE_PATTERN =
            Regex("""(?<![0-9])(?:\+?233|0)\d{9}(?![0-9])""")

        /** How far back to look for a balance label before a currency figure. */
        private const val BALANCE_LOOKBEHIND = 24

        /** A further number immediately after the match, i.e. the grouping is ambiguous. */
        private val SPLIT_NUMBER_TAIL = Regex("""^\s+[0-9]""")

        /**
         * Whether a message is about money at all, however badly it reads.
         *
         * <p>Deliberately weaker than [AMOUNT_PATTERN], which needs a currency marker
         * immediately followed by a figure it can trust. This asks only whether money is
         * mentioned, so a template we cannot yet read — an amount written the other way round,
         * a grouping too ambiguous to resolve — is still recognisably financial and can be put
         * in front of the agent instead of being discarded as somebody's private text.</p>
         *
         * <p>A one-time code or a marketing blast from the same shortcode mentions no currency,
         * which is what keeps those out.</p>
         */
        fun looksFinancial(normalizedBody: String): Boolean =
            CURRENCY_MENTION.containsMatchIn(normalizedBody)

        private val CURRENCY_MENTION = Regex("""GHS|GH¢|GHC|₵|CEDI""")

        /**
         * Whether a message is advertising rather than a transaction.
         *
         * <p>A network's own shortcode sends promotions, and they quote figures — "GHS 1.4
         * MILLION in prizes" reads as financial to any currency test. Without this, every
         * promotional blast lands in the agent's review queue quoting an amount, and a queue
         * full of things that are not transactions is one nobody reads.</p>
         *
         * <p>Keyed only on wording that a real transaction message never carries. Notably
         * <i>not</i> "download" or "click here": MTN appends both to genuine payment
         * confirmations, so matching those would discard real money.</p>
         */
        fun looksPromotional(normalizedBody: String): Boolean =
            PROMOTIONAL.containsMatchIn(normalizedBody)

        private val PROMOTIONAL =
            Regex("""PROMO|PRIZE|GRAND PRIZE|SEND STOP TO|TO EXIT|JACKPOT|CONGRATULATIONS, YOU""")
    }
}
