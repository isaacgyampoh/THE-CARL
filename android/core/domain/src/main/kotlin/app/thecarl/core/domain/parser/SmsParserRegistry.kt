package app.thecarl.core.domain.parser

import app.thecarl.core.domain.model.Provider

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
    fun select(senderIdentity: String?, normalizedBody: String): SmsTransactionParser =
        parsers.first { it.canHandle(senderIdentity, normalizedBody) }

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
