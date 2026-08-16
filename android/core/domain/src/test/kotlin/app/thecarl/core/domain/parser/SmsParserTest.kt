package app.thecarl.core.domain.parser

import app.thecarl.core.domain.model.Provider
import app.thecarl.core.domain.model.TransactionType
import com.google.common.truth.Truth.assertThat
import java.math.BigDecimal
import org.junit.Test

class SmsParserRegistryTest {
    private val registry = SmsParserRegistry()

    // ─── Provider selection ──────────────────────────────────────────────────

    @Test
    fun `each provider message routes to its own parser`() {
        assertThat(registry.parse("MTN", SmsFixtures.MTN_CASH_IN).parserName).isEqualTo("MtnSmsParser")
        assertThat(registry.parse("TelecelCash", SmsFixtures.TELECEL_CASH_IN).parserName)
            .isEqualTo("TelecelSmsParser")
        assertThat(registry.parse("AirtelTigo", SmsFixtures.AIRTELTIGO_CASH_IN).parserName)
            .isEqualTo("AirtelTigoSmsParser")
    }

    @Test
    fun `an unknown sender falls through to the generic parser`() {
        val parsed = registry.parse("SomeBank", SmsFixtures.UNKNOWN_PROVIDER)

        assertThat(parsed.parserName).isEqualTo("GenericSmsParser")
        assertThat(parsed.provider).isEqualTo(Provider.UNKNOWN)
    }

    @Test
    fun `every message produces a result rather than being dropped`() {
        listOf(
            SmsFixtures.EMPTY,
            SmsFixtures.PROMOTIONAL,
            SmsFixtures.MALFORMED_TRUNCATED,
            SmsFixtures.UNKNOWN_PROVIDER
        ).forEach { body ->
            // Nothing is silently discarded: unusable input still becomes reviewable evidence.
            assertThat(registry.parse(null, body)).isNotNull()
        }
    }

    // ─── Classification and amounts ──────────────────────────────────────────

    @Test
    fun `mtn cash in is classified with its amount and counterparty`() {
        val parsed = registry.parse("MTN", SmsFixtures.MTN_CASH_IN)

        assertThat(parsed.transactionType).isEqualTo(TransactionType.CASH_IN)
        assertThat(parsed.amount).isEqualTo(BigDecimal("500.00"))
        assertThat(parsed.customerPhoneNumber).isEqualTo("0241000001")
        assertThat(parsed.isUsable).isTrue()
    }

    @Test
    fun `mtn cash out is classified`() {
        val parsed = registry.parse("MTN", SmsFixtures.MTN_CASH_OUT)

        assertThat(parsed.transactionType).isEqualTo(TransactionType.CASH_OUT)
        assertThat(parsed.amount).isEqualTo(BigDecimal("250.50"))
    }

    @Test
    fun `commission and reversal are distinguished from ordinary movements`() {
        assertThat(registry.parse("MTN", SmsFixtures.MTN_COMMISSION).transactionType)
            .isEqualTo(TransactionType.COMMISSION)
        assertThat(registry.parse("MTN", SmsFixtures.MTN_REVERSAL).transactionType)
            .isEqualTo(TransactionType.REVERSAL)
        assertThat(registry.parse("MTN", SmsFixtures.MTN_TRANSFER).transactionType)
            .isEqualTo(TransactionType.TRANSFER)
    }

    @Test
    fun `telecel and airteltigo movements are classified`() {
        assertThat(registry.parse("TelecelCash", SmsFixtures.TELECEL_CASH_IN).transactionType)
            .isEqualTo(TransactionType.CASH_IN)
        assertThat(registry.parse("TelecelCash", SmsFixtures.TELECEL_CASH_OUT).transactionType)
            .isEqualTo(TransactionType.CASH_OUT)
        assertThat(registry.parse("AirtelTigo", SmsFixtures.AIRTELTIGO_CASH_IN).transactionType)
            .isEqualTo(TransactionType.CASH_IN)
        assertThat(registry.parse("AirtelTigo", SmsFixtures.AIRTELTIGO_CASH_OUT).transactionType)
            .isEqualTo(TransactionType.CASH_OUT)
    }

    @Test
    fun `thousands separators do not truncate the amount`() {
        // "1,250,000.75" naively parsed becomes 1. That would understate a transaction by
        // six orders of magnitude.
        val parsed = registry.parse("MTN", SmsFixtures.MTN_THOUSANDS_SEPARATOR)

        assertThat(parsed.amount).isEqualTo(BigDecimal("1250000.75"))
    }

    @Test
    fun `the cedi symbol is recognised as a currency marker`() {
        val parsed = registry.parse("MTN", SmsFixtures.MTN_CEDI_SYMBOL)

        assertThat(parsed.amount).isEqualTo(BigDecimal("340.25"))
    }

    @Test
    fun `telecel amounts with separators parse correctly`() {
        assertThat(registry.parse("TelecelCash", SmsFixtures.TELECEL_CASH_IN).amount)
            .isEqualTo(BigDecimal("1250.00"))
    }

    // ─── Refusing to guess ───────────────────────────────────────────────────

    @Test
    fun `an amount with no currency marker is not invented`() {
        // Extracting a bare number risks reading a reference or a balance as the amount.
        val parsed = registry.parse("MTN", SmsFixtures.MTN_NO_CURRENCY_MARKER)

        assertThat(parsed.amount).isNull()
        assertThat(parsed.isUsable).isFalse()
    }

    @Test
    fun `recognised provider with unrecognised wording is not classified`() {
        val parsed = registry.parse("MTN", SmsFixtures.MTN_UNRECOGNISED_WORDING)

        assertThat(parsed.transactionType).isEqualTo(TransactionType.UNKNOWN)
        assertThat(parsed.isUsable).isFalse()
    }

    @Test
    fun `promotional and malformed messages are never usable`() {
        listOf(SmsFixtures.PROMOTIONAL, SmsFixtures.MALFORMED_TRUNCATED, SmsFixtures.EMPTY)
            .forEach { body ->
                assertThat(registry.parse("MTN", body).isUsable).isFalse()
            }
    }

    @Test
    fun `the generic parser never reports postable confidence`() {
        // An unrecognised template is exactly where a confident guess is most dangerous.
        val parsed = registry.parse("SomeBank", SmsFixtures.UNKNOWN_PROVIDER)

        assertThat(parsed.confidence).isLessThan(ParsedSms.MINIMUM_CONFIDENCE)
        assertThat(parsed.isUsable).isFalse()
    }

    @Test
    fun `a usable result requires a known type a positive amount and confidence`() {
        val usable = registry.parse("MTN", SmsFixtures.MTN_CASH_IN)

        assertThat(usable.isUsable).isTrue()
        assertThat(usable.confidence).isAtLeast(ParsedSms.MINIMUM_CONFIDENCE)
        assertThat(usable.transactionType).isNotEqualTo(TransactionType.UNKNOWN)
        assertThat(usable.amount!!.signum()).isGreaterThan(0)
    }

    // ─── Provenance ──────────────────────────────────────────────────────────

    @Test
    fun `every result records the parser that produced it`() {
        // When a provider changes its template, historical evidence must remain traceable to
        // the parser that interpreted it.
        listOf(
            "MTN" to SmsFixtures.MTN_CASH_IN,
            "TelecelCash" to SmsFixtures.TELECEL_CASH_IN,
            "AirtelTigo" to SmsFixtures.AIRTELTIGO_CASH_IN,
            "SomeBank" to SmsFixtures.UNKNOWN_PROVIDER
        ).forEach { (sender, body) ->
            val parsed = registry.parse(sender, body)
            assertThat(parsed.parserName).isNotEmpty()
            assertThat(parsed.parserVersion).isNotEmpty()
        }
    }

    @Test
    fun `normalisation makes parsing insensitive to case and whitespace`() {
        val spaced = "  cash   in  of  ghs 500.00 from 0241000001. ref: MP240815.1201.A00001  "

        assertThat(registry.parse("MTN", spaced).transactionType).isEqualTo(TransactionType.CASH_IN)
    }

    @Test
    fun `references are extracted and uppercased`() {
        val parsed = registry.parse("TelecelCash", SmsFixtures.TELECEL_CASH_IN)

        assertThat(parsed.reference).isEqualTo("TC98765432")
    }
}
