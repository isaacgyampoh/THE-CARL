package app.zazi.core.domain.parser

import app.zazi.core.domain.model.Provider

/**
 * Selects the parser for a message.
 *
 * Selection is provider identification first, parsing second — the same two-stage approach
 * the backend uses. [GenericSmsParser] is always last and always matches, so every message
 * produces a result and nothing is silently dropped.
 */
class SmsParserRegistry(
    private val parsers: List<SmsTransactionParser> = defaultParsers()
) {
    /**
     * Picks the parser for a message, sender first.
     *
     * Two passes, deliberately. A single pass in list order let whichever parser appeared
     * earliest claim a message on a weak body match, so MTN — first in the list and matching
     * any text containing "MoMo" — took Telecel and AirtelTigo transactions. Asking every
     * parser about the sender before anyone is asked about the body means the network's own
     * shortcode decides, and body text only matters when there is no recognisable sender.
     */
    fun select(senderIdentity: String?, normalizedBody: String): SmsTransactionParser =
        parsers.firstOrNull { it.claimsSender(senderIdentity) }
            ?: parsers.firstOrNull { it.claimsBody(normalizedBody) }
            ?: parsers.first { it.canHandle(senderIdentity, normalizedBody) }

    fun parse(senderIdentity: String?, rawBody: String): ParsedSms {
        val normalized = BaseSmsParser.normalize(rawBody)
        return select(senderIdentity, normalized).parse(normalized)
    }

    fun parserFor(provider: Provider): SmsTransactionParser? =
        parsers.firstOrNull { it.provider == provider }

    companion object {
        /** Generic must remain last: it claims everything. */
        fun defaultParsers(): List<SmsTransactionParser> = listOf(
            MtnSmsParser(),
            TelecelSmsParser(),
            AirtelTigoSmsParser(),
            GenericSmsParser()
        )
    }
}
