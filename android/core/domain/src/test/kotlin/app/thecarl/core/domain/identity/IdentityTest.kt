package app.thecarl.core.domain.identity

import app.thecarl.core.domain.model.TransactionType
import com.google.common.truth.Truth.assertThat
import java.math.BigDecimal
import org.junit.Assert.assertThrows
import org.junit.Test

class ClientTransactionIdTest {
    private val deviceA = "device-installation-a"
    private val deviceB = "device-installation-b"

    @Test
    fun `generated ids are well formed and match the server shape`() {
        val id = ClientTransactionId.create(deviceA)

        assertThat(ClientTransactionId.isWellFormed(id)).isTrue()
        assertThat(id).hasLength(ClientTransactionId.TOTAL_LENGTH)
        assertThat(id).startsWith("CTX-")
    }

    @Test
    fun `ten thousand ids from one device are unique`() {
        val ids = (0 until 10_000).map { ClientTransactionId.create(deviceA) }.toSet()

        assertThat(ids).hasSize(10_000)
    }

    @Test
    fun `ids generated in the same millisecond are still unique`() {
        // A timestamp alone would collide here. The 80 random bits prevent it.
        val instant = System.currentTimeMillis()
        val ids = (0 until 1_000).map { ClientTransactionId.create(deviceA, instant) }.toSet()

        assertThat(ids).hasSize(1_000)
    }

    @Test
    fun `different devices in the same millisecond do not collide`() {
        val instant = System.currentTimeMillis()

        assertThat(ClientTransactionId.create(deviceA, instant))
            .isNotEqualTo(ClientTransactionId.create(deviceB, instant))
    }

    @Test
    fun `device tag is stable across restarts`() {
        // Derived from the installation id, not process state, so a reboot mid-outbox does
        // not change how a device tags its transactions.
        assertThat(ClientTransactionId.deviceTag(deviceA))
            .isEqualTo(ClientTransactionId.deviceTag(deviceA))
        assertThat(ClientTransactionId.deviceTag(deviceA))
            .isNotEqualTo(ClientTransactionId.deviceTag(deviceB))
    }

    @Test
    fun `device tag does not disclose the installation id`() {
        val tag = ClientTransactionId.deviceTag(deviceA)

        assertThat(tag).doesNotContain(deviceA)
        assertThat(tag).hasLength(ClientTransactionId.DEVICE_TAG_LENGTH)
    }

    @Test
    fun `ids sort lexicographically by creation time`() {
        val earlier = ClientTransactionId.create(deviceA, 1_700_000_000_000)
        val later = ClientTransactionId.create(deviceA, 1_800_000_000_000)

        assertThat(earlier < later).isTrue()
    }

    @Test
    fun `a clock going backwards does not produce duplicates`() {
        val now = System.currentTimeMillis()
        val a = ClientTransactionId.create(deviceA, now)
        val b = ClientTransactionId.create(deviceA, now - 3_600_000)

        assertThat(a).isNotEqualTo(b)
        assertThat(ClientTransactionId.isWellFormed(b)).isTrue()
    }

    @Test
    fun `malformed ids are rejected`() {
        listOf("", "not-an-id", "CTX-XYZ-01HQ8Z7K3M4N5P6Q7R8S9T0V1W", "CTX-9f3a1c07-SHORT")
            .forEach { assertThat(ClientTransactionId.isWellFormed(it)).isFalse() }
    }

    @Test
    fun `a blank installation id is refused`() {
        assertThrows(IllegalArgumentException::class.java) { ClientTransactionId.create("  ") }
    }
}

class EvidenceFingerprintTest {
    private val org = "11111111-1111-1111-1111-111111111111"
    private val occurred = 1_755_248_400_000L // 2025-08-15T09:00:00Z

    private fun compute(
        organizationId: String = org,
        provider: String = "MTN",
        type: TransactionType = TransactionType.CASH_IN,
        amount: BigDecimal = BigDecimal("500"),
        reference: String? = "ABC123",
        phone: String? = "0241234567",
        occurredAt: Long = occurred
    ) = EvidenceFingerprint.compute(organizationId, provider, type, amount, reference, phone, occurredAt)

    @Test
    fun `the same event produces the same fingerprint`() {
        assertThat(compute()).isEqualTo(compute())
    }

    @Test
    fun `the fingerprint is scoped to the organization`() {
        assertThat(compute()).isNotEqualTo(compute(organizationId = "22222222-2222-2222-2222-222222222222"))
    }

    @Test
    fun `msisdn formatting does not change the fingerprint`() {
        listOf("0241234567", "+233241234567", "233241234567", "024 123 4567")
            .forEach { assertThat(compute(phone = it)).isEqualTo(compute()) }
    }

    @Test
    fun `reference formatting does not change the fingerprint`() {
        listOf("ABC123", "abc123", "ABC-123", "ABC 123")
            .forEach { assertThat(compute(reference = it)).isEqualTo(compute()) }
    }

    @Test
    fun `amount scale does not change the fingerprint`() {
        listOf("500", "500.0", "500.00", "500.0000")
            .forEach { assertThat(compute(amount = BigDecimal(it))).isEqualTo(compute()) }
    }

    @Test
    fun `second level clock drift does not change the fingerprint`() {
        // Providers report seconds inconsistently between the body and delivery metadata.
        assertThat(compute(occurredAt = occurred + 45_000)).isEqualTo(compute())
    }

    @Test
    fun `a different minute produces a different fingerprint`() {
        assertThat(compute(occurredAt = occurred + 60_000)).isNotEqualTo(compute())
    }

    @Test
    fun `a different amount or type produces a different fingerprint`() {
        assertThat(compute(amount = BigDecimal("500.01"))).isNotEqualTo(compute())
        assertThat(compute(type = TransactionType.CASH_OUT)).isNotEqualTo(compute())
    }

    @Test
    fun `the raw hash ignores cosmetic whitespace`() {
        assertThat(EvidenceFingerprint.computeRawHash("MTN MOMO: Deposit of GHS 75.50"))
            .isEqualTo(EvidenceFingerprint.computeRawHash("  MTN  MOMO:\r\n Deposit of  GHS 75.50  "))
    }

    @Test
    fun `the fingerprint is lowercase hex sha256`() {
        val fingerprint = compute()

        assertThat(fingerprint).hasLength(64)
        assertThat(fingerprint.all { it.isDigit() || it in 'a'..'f' }).isTrue()
    }
}
