package app.thecarl.core.domain.contract

import app.thecarl.core.domain.identity.EvidenceFingerprint
import app.thecarl.core.domain.ledger.LedgerProjection
import app.thecarl.core.domain.model.TransactionType
import app.thecarl.core.domain.parser.ParsedSms
import app.thecarl.core.domain.parser.SmsParserRegistry
import com.google.common.truth.Truth.assertThat
import java.io.File
import java.math.BigDecimal
import java.time.Instant
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Test

/**
 * Drives the Android parser from `contracts/sms-contract-fixtures.json` — the same file the
 * .NET suite reads.
 *
 * The two parsers cannot share code, so this corpus is what stops them diverging. They
 * already had, three separate ways: the server did not require a provider reference before
 * auto-posting, parsed "GHS 1,250.00" as 1.25, and classified "received Commission" as a
 * cash-in. Each would have moved real money incorrectly.
 */
class SmsContractFixtureTest {

    private val registry = SmsParserRegistry()
    private val corpus = Corpus.load()

    @Test
    fun `the corpus this platform loads is the whole corpus`() {
        // Guards against a vacuous pass. Every assertion below iterates the fixtures, so a
        // corpus that silently failed to load, or that quietly lost entries, would leave
        // this suite green while testing nothing.
        assertThat(corpus.fixtures).hasSize(21)

        // The cases that exist because they once moved the wrong money.
        assertThat(corpus.fixtures.map { it.id }).containsAtLeast(
            "mtn-amount-split-by-space",
            "mtn-balance-stated-before-amount",
            "mtn-reversal-word-in-footer",
            "truncated-loses-reference"
        )
    }

    @Test
    fun `android parser matches the shared contract`() {
        corpus.fixtures.forEach { fixture ->
            val parsed = registry.parse(fixture.sender, fixture.body)

            fixture.expectedType?.let {
                assertThat(parsed.transactionType.name).isEqualTo(it)
            }

            if (fixture.amountSpecified) {
                val actual = parsed.amount
                if (fixture.expectedAmount == null) {
                    assertThat(actual).isNull()
                } else {
                    assertThat(actual).isNotNull()
                    assertThat(actual!!.compareTo(fixture.expectedAmount)).isEqualTo(0)
                }
            }

            if (fixture.referenceSpecified) {
                assertThat(parsed.reference).isEqualTo(fixture.expectedReference)
            }

            fixture.expectedCustomerPhone?.let {
                assertThat(parsed.customerPhoneNumber).isEqualTo(it)
            }
        }
    }

    @Test
    fun `auto-post decision matches the shared contract`() {
        corpus.fixtures.forEach { fixture ->
            val parsed = registry.parse(fixture.sender, fixture.body)

            // Both gates, as the pipeline applies them: evidence quality, then the
            // type-based rule that keeps reversals and adjustments off the automatic path.
            val autoPost = parsed.isUsable && LedgerProjection.canPostAutomatically(parsed.transactionType)

            assertThat(autoPost).isEqualTo(fixture.autoPostAllowed)
        }
    }

    @Test
    fun `truncated evidence never posts`() {
        // Named separately so the regression is visible in a test list and cannot be lost
        // by an edit to the corpus.
        val fixture = corpus.fixture("truncated-loses-reference")
        val parsed = registry.parse(fixture.sender, fixture.body)

        assertThat(fixture.autoPostAllowed).isFalse()
        assertThat(parsed.isUsable).isFalse()
        assertThat(parsed.reference).isNull()
        assertThat(parsed.confidence).isLessThan(ParsedSms.MINIMUM_CONFIDENCE)
    }

    @Test
    fun `every fixture that may not post is provably blocked`() {
        corpus.fixtures.filterNot { it.autoPostAllowed }.forEach { fixture ->
            val parsed = registry.parse(fixture.sender, fixture.body)

            val blockedByConfidence = parsed.confidence < ParsedSms.MINIMUM_CONFIDENCE
            val blockedByAmount = parsed.amount == null ||
                parsed.amount!! < LedgerProjection.MINIMUM_AMOUNT
            val blockedByType = !LedgerProjection.canPostAutomatically(parsed.transactionType)

            assertThat(blockedByConfidence || blockedByAmount || blockedByType).isTrue()
        }
    }

    @Test
    fun `fingerprints agree with the shared contract`() {
        val canonical = fingerprintOf(corpus.fingerprintCase("canonical"))

        corpus.fingerprintCases.forEach { case ->
            val fingerprint = fingerprintOf(case)

            if (case.sameFingerprintAs != null) {
                assertThat(fingerprint).isEqualTo(canonical)
            }
            if (case.differentFingerprintFrom != null) {
                assertThat(fingerprint).isNotEqualTo(canonical)
            }
        }
    }

    private fun fingerprintOf(case: Corpus.FingerprintCase): String =
        EvidenceFingerprint.compute(
            organizationId = corpus.organizationId,
            provider = case.provider,
            transactionType = TransactionType.valueOf(case.transactionType),
            amount = BigDecimal(case.amount),
            providerReference = case.reference,
            customerPhone = case.customerPhone,
            occurredAtUtcMillis = Instant.parse(case.occurredAtUtc).toEpochMilli()
        )

    /** Reader for the shared corpus. */
    private class Corpus(
        val organizationId: String,
        val fixtures: List<Fixture>,
        val fingerprintCases: List<FingerprintCase>
    ) {
        fun fixture(id: String) = fixtures.first { it.id == id }
        fun fingerprintCase(id: String) = fingerprintCases.first { it.id == id }

        class Fixture(
            val id: String,
            val sender: String?,
            val body: String,
            val expectedType: String?,
            val amountSpecified: Boolean,
            val expectedAmount: BigDecimal?,
            val referenceSpecified: Boolean,
            val expectedReference: String?,
            val expectedCustomerPhone: String?,
            val autoPostAllowed: Boolean
        )

        class FingerprintCase(
            val id: String,
            val provider: String,
            val transactionType: String,
            val amount: String,
            val reference: String?,
            val customerPhone: String?,
            val occurredAtUtc: String,
            val sameFingerprintAs: String?,
            val differentFingerprintFrom: String?
        )

        companion object {
            fun load(): Corpus {
                val root = Json.parseToJsonElement(locate().readText()).jsonObject

                val fixtures = root["fixtures"]!!.jsonArray.map { element ->
                    val obj = element.jsonObject
                    val expect = obj["expect"]!!.jsonObject
                    Fixture(
                        id = obj.str("id")!!,
                        sender = obj.str("sender"),
                        body = obj.str("body")!!,
                        expectedType = expect.str("transactionType"),
                        amountSpecified = expect.containsKey("amount"),
                        expectedAmount = expect.str("amount")?.let { BigDecimal(it) },
                        referenceSpecified = expect.containsKey("reference"),
                        expectedReference = expect.str("reference"),
                        expectedCustomerPhone = expect.str("customerPhone"),
                        autoPostAllowed = expect["autoPostAllowed"]!!.jsonPrimitive.content.toBoolean()
                    )
                }

                val fingerprintCases = root["fingerprintCases"]!!.jsonArray.map { element ->
                    val obj = element.jsonObject
                    FingerprintCase(
                        id = obj.str("id")!!,
                        provider = obj.str("provider")!!,
                        transactionType = obj.str("transactionType")!!,
                        amount = obj.str("amount")!!,
                        reference = obj.str("reference"),
                        customerPhone = obj.str("customerPhone"),
                        occurredAtUtc = obj.str("occurredAtUtc")!!,
                        sameFingerprintAs = obj.str("sameFingerprintAs"),
                        differentFingerprintFrom = obj.str("differentFingerprintFrom")
                    )
                }

                return Corpus(root.str("organizationId")!!, fixtures, fingerprintCases)
            }

            /** Null-aware string read: JSON null becomes Kotlin null, not the text "null". */
            private fun JsonObject.str(key: String): String? {
                val element = this[key] ?: return null
                val primitive = element.jsonPrimitive
                return if (primitive.content == "null" && !primitive.isString) null else primitive.content
            }

            private fun locate(): File {
                System.getProperty("thecarl.contracts.dir")?.let { configured ->
                    val file = File(configured, "sms-contract-fixtures.json")
                    if (file.exists()) return file
                }

                var directory: File? = File(System.getProperty("user.dir"))
                while (directory != null) {
                    val candidate = File(directory, "contracts/sms-contract-fixtures.json")
                    if (candidate.exists()) return candidate
                    directory = directory.parentFile
                }

                error("contracts/sms-contract-fixtures.json was not found.")
            }
        }
    }
}
