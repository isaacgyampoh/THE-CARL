package app.zazi.core.domain.parser

import app.zazi.core.domain.model.Provider
import app.zazi.core.domain.model.TransactionType

/**
 * MTN MoMo agent messages.
 *
 * Wording is matched on the *agent's* perspective. "Cash In" on an agent handset means the
 * agent took cash and sent e-money — cash up, float down — which is the opposite of what the
 * same phrase means on a customer handset. Getting this backwards inverts every balance.
 */
class MtnSmsParser : BaseSmsParser() {
    override val parserName = "MtnSmsParser"
    override val parserVersion = "mtn-v1"
    override val provider = Provider.MTN

    /**
     * <p>"MOBILEMONEY" is the one that matters. It is what MTN Ghana actually sends from, and
     * it contains neither "MTN" nor "MOMO" — so every genuine alert from it fell past this
     * parser to the generic one, scored too low to post, and queued for review. That was never
     * a regression; it has been the case since the first commit and only shows against real
     * traffic, which is why it survived every test written from imagined messages.</p>
     */
    override fun claimsSender(senderIdentity: String?): Boolean {
        val sender = BaseSmsParser.normalizeSender(senderIdentity)
        return sender.contains("MTN") || sender.contains("MOMO") ||
            sender.contains("MOBILEMONEY")
    }

    // "MTN" qualified, never a bare "MOMO". The word is generic for mobile money in Ghana, so
    // matching it alone claimed Telecel and AirtelTigo messages — and bank alerts offering to
    // send "to your MoMo wallet" — for MTN.
    override fun claimsBody(normalizedBody: String): Boolean =
        normalizedBody.contains("MTN MOBILE MONEY") || normalizedBody.contains("MTN MOMO")

    override fun parse(normalizedBody: String): ParsedSms {
        val type = classify(normalizedBody)
        val amount = extractAmount(normalizedBody)
        val reference = extractReference(normalizedBody)

        return ParsedSms(
            provider = provider,
            transactionType = type,
            amount = amount,
            reference = reference,
            customerPhoneNumber = extractCounterparty(normalizedBody),
            customerName = extractName(normalizedBody),
            balanceAfter = extractBalance(normalizedBody),
            confidence = confidenceFor(type, amount, reference, extractBalance(normalizedBody)),
            parserName = parserName,
            parserVersion = parserVersion,
            occurredAtUtcMillis = extractTimestamp(normalizedBody)
        )
    }

    private fun classify(body: String): TransactionType {
        val stated = classifyIn(BaseSmsParser.directionClause(body))
        // Falling back to the whole body keeps messages that lead with the amount working;
        // the clause is consulted first so a balance reminder cannot outvote the transaction.
        return if (stated != TransactionType.UNKNOWN) stated else classifyIn(body)
    }

    /**
     * <p>The "payment" wordings are read from the agent's float, because that is the only
     * side of the transaction the message describes and the balance it quotes proves the
     * direction beyond argument:</p>
     *
     * <ul>
     *   <li><b>Payment received</b> — e-money arrives, so the float rises. A customer sends
     *     the agent e-money and takes notes away: a withdrawal, which in this ledger is a
     *     cash-out (cash falls, float rises).</li>
     *   <li><b>Payment made</b> — e-money leaves, so the float falls. The agent sends
     *     e-money and keeps the customer's notes: a deposit, which is a cash-in.</li>
     * </ul>
     *
     * <p>Measured, not assumed: two real messages fifteen minutes apart on one handset read
     * GHS 1042.16 after a payment made and GHS 1337.16 after a payment received of GHS
     * 295.00, and 1042.16 + 295.00 is 1337.16 exactly.</p>
     *
     * <p><b>If a vendor tells you this is backwards, this is the block to change</b> — and
     * it is worth checking with one, because getting it the wrong way round is wrong by
     * twice the amount on every transaction it touches. The older "received from" line below
     * reads the cash side rather than the float and is deliberately left alone: it is a
     * different wording on a different template.</p>
     */
    private fun classifyIn(body: String): TransactionType = when {
        mentionsReversal(body) -> TransactionType.REVERSAL
        body.contains("COMMISSION") -> TransactionType.COMMISSION
        body.contains("CASH IN") || body.contains("CASH-IN") -> TransactionType.CASH_IN
        body.contains("CASH OUT") || body.contains("CASH-OUT") -> TransactionType.CASH_OUT

        // Float rises: the customer sent e-money and walked away with notes.
        body.contains("PAYMENT RECEIVED") -> TransactionType.CASH_OUT

        // Float falls: the agent sent e-money and kept the notes.
        body.contains("PAYMENT MADE") || body.contains("PAYMENT OF") -> TransactionType.CASH_IN

        // "Received from" on an agent line is a customer depositing cash with the agent.
        body.contains("RECEIVED FROM") -> TransactionType.CASH_IN
        body.contains("PAID TO") || body.contains("WITHDRAWN") -> TransactionType.CASH_OUT
        body.contains("TRANSFER") || body.contains("SENT TO") -> TransactionType.TRANSFER
        else -> TransactionType.UNKNOWN
    }
}

/** Telecel Cash agent messages. */
class TelecelSmsParser : BaseSmsParser() {
    override val parserName = "TelecelSmsParser"
    override val parserVersion = "telecel-v1"
    override val provider = Provider.TELECEL

    // Vodafone Ghana became Telecel; handsets and shortcodes still carry the old name.
    override fun claimsSender(senderIdentity: String?): Boolean {
        val sender = BaseSmsParser.normalizeSender(senderIdentity)
        // The network was Vodafone Cash and the old sender identities are still in use, so
        // both names and both short forms have to be recognised.
        return sender.contains("TELECEL") || sender.contains("VODAFONE") ||
            sender.contains("VODACASH") || sender.contains("VFCASH") ||
            sender.contains("TCASH")
    }

    override fun claimsBody(normalizedBody: String): Boolean =
        normalizedBody.contains("TELECEL CASH") || normalizedBody.contains("VODAFONE CASH")

    override fun parse(normalizedBody: String): ParsedSms {
        val type = classify(normalizedBody)
        val amount = extractAmount(normalizedBody)
        val reference = extractReference(normalizedBody)

        return ParsedSms(
            provider = provider,
            transactionType = type,
            amount = amount,
            reference = reference,
            customerPhoneNumber = extractCounterparty(normalizedBody),
            customerName = extractName(normalizedBody),
            balanceAfter = extractBalance(normalizedBody),
            confidence = confidenceFor(type, amount, reference, extractBalance(normalizedBody)),
            parserName = parserName,
            parserVersion = parserVersion,
            occurredAtUtcMillis = extractTimestamp(normalizedBody)
        )
    }

    private fun classify(body: String): TransactionType {
        val stated = classifyIn(BaseSmsParser.directionClause(body))
        // Falling back to the whole body keeps messages that lead with the amount working;
        // the clause is consulted first so a balance reminder cannot outvote the transaction.
        return if (stated != TransactionType.UNKNOWN) stated else classifyIn(body)
    }

    private fun classifyIn(body: String): TransactionType = when {
        mentionsReversal(body) -> TransactionType.REVERSAL
        body.contains("COMMISSION") -> TransactionType.COMMISSION
        body.contains("DEPOSIT") || body.contains("CASH IN") -> TransactionType.CASH_IN
        body.contains("WITHDRAW") || body.contains("CASH OUT") -> TransactionType.CASH_OUT
        body.contains("TRANSFER") -> TransactionType.TRANSFER
        else -> TransactionType.UNKNOWN
    }
}

/** AirtelTigo Money agent messages. */
class AirtelTigoSmsParser : BaseSmsParser() {
    override val parserName = "AirtelTigoSmsParser"
    override val parserVersion = "airteltigo-v1"
    override val provider = Provider.AIRTELTIGO

    override fun claimsSender(senderIdentity: String?): Boolean {
        val raw = senderIdentity?.uppercase().orEmpty().trim()
        val sender = BaseSmsParser.normalizeSender(senderIdentity)
        // Airtel and Tigo merged and both heritages still send. A bare "AT" stays exact or
        // prefixed: as a substring it matched any sender with those two letters in it.
        return sender.contains("AIRTELTIGO") || sender.contains("ATMONEY") ||
            sender.contains("AIRTELMONEY") || sender.contains("TIGOCASH") ||
            raw == "AT" || raw.startsWith("AT-") || raw.startsWith("AT.")
    }

    override fun claimsBody(normalizedBody: String): Boolean =
        normalizedBody.contains("AIRTELTIGO MONEY") || normalizedBody.contains("AIRTELTIGO")

    override fun parse(normalizedBody: String): ParsedSms {
        val type = classify(normalizedBody)
        val amount = extractAmount(normalizedBody)
        val reference = extractReference(normalizedBody)

        return ParsedSms(
            provider = provider,
            transactionType = type,
            amount = amount,
            reference = reference,
            customerPhoneNumber = extractCounterparty(normalizedBody),
            customerName = extractName(normalizedBody),
            balanceAfter = extractBalance(normalizedBody),
            confidence = confidenceFor(type, amount, reference, extractBalance(normalizedBody)),
            parserName = parserName,
            parserVersion = parserVersion,
            occurredAtUtcMillis = extractTimestamp(normalizedBody)
        )
    }

    private fun classify(body: String): TransactionType {
        val stated = classifyIn(BaseSmsParser.directionClause(body))
        // Falling back to the whole body keeps messages that lead with the amount working;
        // the clause is consulted first so a balance reminder cannot outvote the transaction.
        return if (stated != TransactionType.UNKNOWN) stated else classifyIn(body)
    }

    private fun classifyIn(body: String): TransactionType = when {
        mentionsReversal(body) -> TransactionType.REVERSAL
        body.contains("COMMISSION") -> TransactionType.COMMISSION
        body.contains("CASH IN") || body.contains("DEPOSIT") -> TransactionType.CASH_IN
        body.contains("CASH OUT") || body.contains("WITHDRAW") -> TransactionType.CASH_OUT
        body.contains("TRANSFER") || body.contains("SENT") -> TransactionType.TRANSFER
        else -> TransactionType.UNKNOWN
    }
}

/**
 * Last-resort parser for messages no provider parser claims.
 *
 * It deliberately reports low confidence even when it extracts an amount. An unrecognised
 * template is exactly the case where a confident guess is most dangerous, so its output
 * routes to review rather than to the ledger.
 */
class GenericSmsParser : BaseSmsParser() {
    override val parserName = "GenericSmsParser"
    override val parserVersion = "generic-v1"
    override val provider = Provider.UNKNOWN

    // The fallback. Claims nothing on its own; the registry uses it only when no provider
    // recognised the message, so an unattributable message is reported as UNKNOWN rather than
    // guessed into somebody's figures.
    override fun claimsSender(senderIdentity: String?): Boolean = false

    override fun claimsBody(normalizedBody: String): Boolean = false

    override fun canHandle(senderIdentity: String?, normalizedBody: String): Boolean = true

    override fun parse(normalizedBody: String): ParsedSms = ParsedSms(
        provider = Provider.UNKNOWN,
        transactionType = TransactionType.UNKNOWN,
        amount = extractAmount(normalizedBody),
        reference = extractReference(normalizedBody),
        customerPhoneNumber = extractCounterparty(normalizedBody),
            customerName = extractName(normalizedBody),
        balanceAfter = null,
        // Never high enough to auto-post: an unknown template must reach a person.
        confidence = 0.20,
        parserName = parserName,
        parserVersion = parserVersion,
        occurredAtUtcMillis = extractTimestamp(normalizedBody)
    )
}

/**
 * Confidence for a provider parser.
 *
 * A result reaches auto-posting confidence with a known type, a positive amount, and evidence
 * that the message arrived whole.
 *
 * That last part is what makes truncation safe. A delivery cut from
 * `"Cash In of GHS 500.00 ... Ref: ..."` to `"Cash In of GHS 5"` parses as a perfectly
 * plausible GHS 5 cash-in and would post silently, understating the agent's till by GHS 495.
 *
 * A reference proves wholeness, because it sits at the end of the message. So does a stated
 * closing balance, for exactly the same reason — anything that survives to quote the balance
 * left afterwards was not cut short. Accepting either is what stopped a whole network's
 * template being held for review forever because its wording puts no "Ref:" in the text: real
 * transactions queued up behind a prompt while an agent watched their takings not appear.
 */
private fun confidenceFor(
    type: TransactionType,
    amount: java.math.BigDecimal?,
    reference: String?,
    balanceAfter: java.math.BigDecimal? = null
): Double = when {
    type == TransactionType.UNKNOWN -> 0.30
    amount == null || amount.signum() <= 0 -> 0.40
    // Nothing from the tail of the message survived, so it may not have all arrived.
    reference.isNullOrBlank() && balanceAfter == null -> 0.50
    else -> 0.95
}
