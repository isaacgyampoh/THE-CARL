package app.thecarl.core.domain.identity

import app.thecarl.core.domain.model.TransactionType
import java.math.BigDecimal
import java.math.RoundingMode
import java.security.MessageDigest
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/**
 * Deterministic fingerprint of an observed transaction event.
 *
 * **Must match the backend byte for byte.** The server recomputes the fingerprint and uses
 * its own value for duplicate detection, so a client that canonicalises differently simply
 * fails to deduplicate locally — it cannot corrupt the server. Matching the algorithm lets
 * the device suppress obvious local duplicates before spending a round trip.
 *
 * Algorithm: SHA-256 over a version-prefixed, newline-delimited canonical field list,
 * rendered lowercase hex.
 *
 * Canonicalisation exists because providers change templates without changing the event:
 * amounts are fixed-scale, references stripped to alphanumerics, MSISDNs reduced to the
 * national significant number, and timestamps truncated to the minute because providers
 * report seconds inconsistently.
 *
 * Note the organization id is **not** available on the device before enrolment completes;
 * callers pass the enrolled organization id, and the server recomputes with the authoritative
 * one regardless.
 */
object EvidenceFingerprint {
    const val VERSION = "v1"

    private val minuteFormatter: DateTimeFormatter =
        DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm").withZone(ZoneOffset.UTC)

    fun compute(
        organizationId: String,
        provider: String,
        transactionType: TransactionType,
        amount: BigDecimal,
        providerReference: String?,
        customerPhone: String?,
        occurredAtUtcMillis: Long
    ): String {
        val canonical = listOf(
            VERSION,
            organizationId,
            normalizeToken(provider),
            transactionType.name.replace("_", ""),
            normalizeAmount(amount),
            normalizeReference(providerReference),
            normalizeMsisdn(customerPhone),
            minuteFormatter.format(Instant.ofEpochMilli(occurredAtUtcMillis))
        ).joinToString("\n")

        return sha256Hex(canonical)
    }

    /**
     * Hash of the raw message, kept separate from the canonical fingerprint so an exact
     * redelivery is detectable even before parsing succeeds.
     */
    fun computeRawHash(rawMessage: String): String = sha256Hex(collapseWhitespace(rawMessage))

    private fun sha256Hex(value: String): String =
        MessageDigest.getInstance("SHA-256")
            .digest(value.toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }

    private fun normalizeToken(value: String?): String =
        collapseWhitespace(value.orEmpty()).uppercase()

    /** Fixed scale so "50", "50.0" and "50.00" fingerprint identically. */
    private fun normalizeAmount(amount: BigDecimal): String =
        amount.setScale(4, RoundingMode.HALF_UP).toPlainString()

    /** Uppercase alphanumerics only: providers vary separators between templates. */
    private fun normalizeReference(reference: String?): String =
        reference.orEmpty().filter { it.isLetterOrDigit() }.uppercase()

    /** Ghanaian MSISDN reduced to its national significant number. */
    private fun normalizeMsisdn(phone: String?): String {
        var digits = phone.orEmpty().filter { it.isDigit() }
        if (digits.startsWith("233") && digits.length >= 12) {
            digits = digits.substring(3)
        } else if (digits.startsWith("0") && digits.length >= 10) {
            digits = digits.substring(1)
        }
        return digits
    }

    private fun collapseWhitespace(value: String): String =
        value.trim().replace(Regex("\\s+"), " ")
}
