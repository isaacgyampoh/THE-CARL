package app.zazi.core.domain.identity

import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Identity a device assigns to a transaction it creates while completely offline.
 *
 * **This must produce values the server accepts.** The format mirrors the backend's
 * `ClientTransactionId` exactly: `CTX-{deviceTag}-{ulid}`, 39 characters, where deviceTag is
 * 8 lowercase hex characters of SHA-256 over the device installation id, and ulid is 26
 * Crockford base32 characters encoding a 48-bit millisecond timestamp plus 80 random bits.
 *
 * Properties that matter offline:
 * - generated with no server contact
 * - survives process death and reboot: nothing derives from a counter or in-memory state
 * - survives clock changes: uniqueness rests on the random bits, not the timestamp
 * - distinct across devices: the device tag differs before randomness is considered
 * - lexicographically sortable by creation time, so the outbox drains in submission order
 *
 * A new id is **never** generated on retry. Regenerating after a timeout is exactly how a
 * lost response turns into a double posting.
 */
object ClientTransactionId {
    const val PREFIX = "CTX"
    const val DEVICE_TAG_LENGTH = 8
    const val ULID_LENGTH = 26
    const val TOTAL_LENGTH = 39

    /** Crockford base32: no I, L, O or U, so transcription is unambiguous. */
    private const val ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

    private val random = SecureRandom()

    /**
     * Derives the stable 8-character tag for a device installation id. The id itself is never
     * embedded, so a transaction id does not disclose device identity.
     */
    fun deviceTag(deviceInstallationId: String): String {
        require(deviceInstallationId.isNotBlank()) { "A device installation id is required." }
        val digest = MessageDigest.getInstance("SHA-256")
            .digest(deviceInstallationId.trim().toByteArray(Charsets.UTF_8))
        return digest.take(4).joinToString("") { "%02x".format(it) }
    }

    fun create(deviceInstallationId: String, nowUtcMillis: Long = System.currentTimeMillis()): String {
        val timestamp = if (nowUtcMillis < 0) 0 else nowUtcMillis
        return "$PREFIX-${deviceTag(deviceInstallationId)}-${createUlid(timestamp)}"
    }

    /**
     * Validates shape only. The server enforces uniqueness with a database constraint; this
     * catches malformed values before they cost a round trip.
     */
    fun isWellFormed(value: String?): Boolean {
        if (value == null || value.length != TOTAL_LENGTH) return false
        val parts = value.split('-')
        if (parts.size != 3 || parts[0] != PREFIX) return false
        if (parts[1].length != DEVICE_TAG_LENGTH) return false
        if (!parts[1].all { it.isDigit() || it in 'a'..'f' }) return false
        return parts[2].length == ULID_LENGTH && parts[2].all { it in ALPHABET }
    }

    private fun createUlid(timestampMillis: Long): String {
        val bytes = ByteArray(16)
        bytes[0] = (timestampMillis ushr 40).toByte()
        bytes[1] = (timestampMillis ushr 32).toByte()
        bytes[2] = (timestampMillis ushr 24).toByte()
        bytes[3] = (timestampMillis ushr 16).toByte()
        bytes[4] = (timestampMillis ushr 8).toByte()
        bytes[5] = timestampMillis.toByte()

        val entropy = ByteArray(10)
        random.nextBytes(entropy)
        entropy.copyInto(bytes, 6)

        return encodeBase32(bytes)
    }

    /**
     * Encodes 128 bits as 26 base32 characters. Three bits for the leading character plus
     * five for each of the remaining 25 covers exactly 128, so the leading character is
     * always '0'-'7'.
     */
    private fun encodeBase32(bytes: ByteArray): String {
        val result = CharArray(ULID_LENGTH)
        var bitPosition = 0

        for (index in 0 until ULID_LENGTH) {
            val bitsToTake = if (index == 0) 3 else 5
            var value = 0
            for (bit in 0 until bitsToTake) {
                val absoluteBit = bitPosition + bit
                val byteIndex = absoluteBit / 8
                val bitInByte = 7 - (absoluteBit % 8)
                val bitValue = if (byteIndex < bytes.size) {
                    (bytes[byteIndex].toInt() shr bitInByte) and 1
                } else {
                    0
                }
                value = (value shl 1) or bitValue
            }
            result[index] = ALPHABET[value]
            bitPosition += bitsToTake
        }

        return String(result)
    }
}
