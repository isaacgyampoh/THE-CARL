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

    override fun canHandle(senderIdentity: String?, normalizedBody: String): Boolean {
        val sender = senderIdentity?.uppercase().orEmpty()
        return sender.contains("MTN") || sender.contains("MOMO") ||
            normalizedBody.contains("MTN MOBILE MONEY") || normalizedBody.contains("MOMO")
    }

    override fun parse(normalizedBody: String): ParsedSms {
        val type = classify(normalizedBody)
        val amount = extractAmount(normalizedBody)
        val reference = extractReference(normalizedBody)

        return ParsedSms(
            provider = provider,
            transactionType = type,
            amount = amount,
            reference = reference,
            customerPhoneNumber = extractPhone(normalizedBody),
            balanceAfter = extractBalance(normalizedBody),
            confidence = confidenceFor(type, amount, reference),
            parserName = parserName,
            parserVersion = parserVersion
        )
    }

    private fun classify(body: String): TransactionType = when {
        mentionsReversal(body) -> TransactionType.REVERSAL
        body.contains("COMMISSION") -> TransactionType.COMMISSION
        body.contains("CASH IN") || body.contains("CASH-IN") -> TransactionType.CASH_IN
        body.contains("CASH OUT") || body.contains("CASH-OUT") -> TransactionType.CASH_OUT
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

    override fun canHandle(senderIdentity: String?, normalizedBody: String): Boolean {
        val sender = senderIdentity?.uppercase().orEmpty()
        return sender.contains("TELECEL") || sender.contains("VODAFONE") ||
            normalizedBody.contains("TELECEL CASH")
    }

    override fun parse(normalizedBody: String): ParsedSms {
        val type = classify(normalizedBody)
        val amount = extractAmount(normalizedBody)
        val reference = extractReference(normalizedBody)

        return ParsedSms(
            provider = provider,
            transactionType = type,
            amount = amount,
            reference = reference,
            customerPhoneNumber = extractPhone(normalizedBody),
            balanceAfter = extractBalance(normalizedBody),
            confidence = confidenceFor(type, amount, reference),
            parserName = parserName,
            parserVersion = parserVersion
        )
    }

    private fun classify(body: String): TransactionType = when {
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

    override fun canHandle(senderIdentity: String?, normalizedBody: String): Boolean {
        val sender = senderIdentity?.uppercase().orEmpty()
        return sender.contains("AIRTELTIGO") || sender.contains("ATMONEY") || sender.contains("AT ") ||
            normalizedBody.contains("AIRTELTIGO MONEY")
    }

    override fun parse(normalizedBody: String): ParsedSms {
        val type = classify(normalizedBody)
        val amount = extractAmount(normalizedBody)
        val reference = extractReference(normalizedBody)

        return ParsedSms(
            provider = provider,
            transactionType = type,
            amount = amount,
            reference = reference,
            customerPhoneNumber = extractPhone(normalizedBody),
            balanceAfter = extractBalance(normalizedBody),
            confidence = confidenceFor(type, amount, reference),
            parserName = parserName,
            parserVersion = parserVersion
        )
    }

    private fun classify(body: String): TransactionType = when {
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

    override fun canHandle(senderIdentity: String?, normalizedBody: String): Boolean = true

    override fun parse(normalizedBody: String): ParsedSms = ParsedSms(
        provider = Provider.UNKNOWN,
        transactionType = TransactionType.UNKNOWN,
        amount = extractAmount(normalizedBody),
        reference = extractReference(normalizedBody),
        customerPhoneNumber = extractPhone(normalizedBody),
        balanceAfter = null,
        // Never high enough to auto-post: an unknown template must reach a person.
        confidence = 0.20,
        parserName = parserName,
        parserVersion = parserVersion
    )
}

/**
 * Confidence for a provider parser.
 *
 * A result only reaches auto-posting confidence when the type, a positive amount **and** a
 * provider reference were all recovered.
 *
 * Requiring the reference is what makes truncation safe. Every real provider transaction
 * message carries one, so a message without it is truncated, promotional, or an unrecognised
 * template. Without this rule a delivery truncated from `"Cash In of GHS 500.00 ... Ref: ..."`
 * to `"Cash In of GHS 5"` parses as a perfectly plausible GHS 5 cash-in and posts silently,
 * understating the agent's till by GHS 495.
 */
private fun confidenceFor(
    type: TransactionType,
    amount: java.math.BigDecimal?,
    reference: String?
): Double = when {
    type == TransactionType.UNKNOWN -> 0.30
    amount == null || amount.signum() <= 0 -> 0.40
    // Recognised provider and amount, but nothing to identify the transaction by.
    reference.isNullOrBlank() -> 0.50
    else -> 0.95
}
