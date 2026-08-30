package app.zazi.core.domain.ledger

import app.zazi.core.domain.model.TransactionType
import com.google.common.truth.Truth.assertThat
import java.math.BigDecimal
import org.junit.Assert.assertThrows
import org.junit.Test

/**
 * The direction of money on the device.
 *
 * These pin the local projection against the backend's `LedgerPolicy`. If one fails, either
 * the projection drifted from the server or the accounting model changed — both demand a
 * deliberate decision, never a quiet edit.
 */
class LedgerProjectionTest {

    @Test
    fun `cash in raises cash and lowers float`() {
        val movement = LedgerProjection.movementFor(TransactionType.CASH_IN, BigDecimal("500"))

        assertThat(movement.cashDelta).isEqualTo(BigDecimal("500"))
        assertThat(movement.floatDelta).isEqualTo(BigDecimal("-500"))
        assertThat(movement.requiresReview).isFalse()
    }

    @Test
    fun `cash out lowers cash and raises float`() {
        val movement = LedgerProjection.movementFor(TransactionType.CASH_OUT, BigDecimal("500"))

        assertThat(movement.cashDelta).isEqualTo(BigDecimal("-500"))
        assertThat(movement.floatDelta).isEqualTo(BigDecimal("500"))
    }

    @Test
    fun `cash in and cash out are exact mirrors`() {
        val cashIn = LedgerProjection.movementFor(TransactionType.CASH_IN, BigDecimal("250"))
        val cashOut = LedgerProjection.movementFor(TransactionType.CASH_OUT, BigDecimal("250"))

        assertThat(cashIn.cashDelta).isEqualTo(cashOut.cashDelta.negate())
        assertThat(cashIn.floatDelta).isEqualTo(cashOut.floatDelta.negate())
    }

    @Test
    fun `a till transaction leaves the combined position unchanged`() {
        val movement = LedgerProjection.movementFor(TransactionType.CASH_IN, BigDecimal("731.45"))

        assertThat(movement.cashDelta.add(movement.floatDelta).signum()).isEqualTo(0)
    }

    @Test
    fun `transfer moves neither balance`() {
        val movement = LedgerProjection.movementFor(TransactionType.TRANSFER, BigDecimal("100"))

        assertThat(movement.isZero).isTrue()
        assertThat(movement.requiresReview).isFalse()
    }

    @Test
    fun `commission credits float only`() {
        val movement = LedgerProjection.movementFor(TransactionType.COMMISSION, BigDecimal("12.75"))

        assertThat(movement.cashDelta.signum()).isEqualTo(0)
        assertThat(movement.floatDelta).isEqualTo(BigDecimal("12.75"))
    }

    @Test
    fun `unknown never affects balances and demands review`() {
        val movement = LedgerProjection.movementFor(TransactionType.UNKNOWN, BigDecimal("9999"))

        assertThat(movement.isZero).isTrue()
        assertThat(movement.requiresReview).isTrue()
    }

    @Test
    fun `reversal is the exact inverse of the original`() {
        listOf(TransactionType.CASH_IN, TransactionType.CASH_OUT, TransactionType.COMMISSION)
            .forEach { originalType ->
                val original = LedgerProjection.movementFor(originalType, BigDecimal("300"))
                val reversal = LedgerProjection.movementFor(
                    TransactionType.REVERSAL, BigDecimal("300"), originalType
                )

                assertThat(original.cashDelta.add(reversal.cashDelta).signum()).isEqualTo(0)
                assertThat(original.floatDelta.add(reversal.floatDelta).signum()).isEqualTo(0)
            }
    }

    @Test
    fun `reversal without an original is held for review`() {
        val movement = LedgerProjection.movementFor(TransactionType.REVERSAL, BigDecimal("300"))

        assertThat(movement.isZero).isTrue()
        assertThat(movement.requiresReview).isTrue()
    }

    @Test
    fun `adjustment requires explicit direction`() {
        assertThrows(IllegalArgumentException::class.java) {
            LedgerProjection.movementFor(TransactionType.ADJUSTMENT, BigDecimal.ZERO)
        }
    }

    @Test
    fun `adjustment uses the supplied signed deltas`() {
        val movement = LedgerProjection.movementFor(
            TransactionType.ADJUSTMENT,
            BigDecimal.ZERO,
            explicitCashDelta = BigDecimal("-25.50"),
            explicitFloatDelta = BigDecimal.ZERO
        )

        assertThat(movement.cashDelta).isEqualTo(BigDecimal("-25.50"))
    }

    @Test
    fun `a negative amount is rejected`() {
        assertThrows(IllegalArgumentException::class.java) {
            LedgerProjection.movementFor(TransactionType.CASH_IN, BigDecimal("-100"))
        }
    }

    @Test
    fun `only safe types may post automatically`() {
        assertThat(LedgerProjection.canPostAutomatically(TransactionType.CASH_IN)).isTrue()
        assertThat(LedgerProjection.canPostAutomatically(TransactionType.CASH_OUT)).isTrue()
        assertThat(LedgerProjection.canPostAutomatically(TransactionType.TRANSFER)).isTrue()
        assertThat(LedgerProjection.canPostAutomatically(TransactionType.COMMISSION)).isTrue()

        assertThat(LedgerProjection.canPostAutomatically(TransactionType.UNKNOWN)).isFalse()
        assertThat(LedgerProjection.canPostAutomatically(TransactionType.REVERSAL)).isFalse()
        assertThat(LedgerProjection.canPostAutomatically(TransactionType.ADJUSTMENT)).isFalse()
    }

    @Test
    fun `every transaction type has an explicit rule`() {
        // Guards against adding a type without deciding its accounting treatment.
        TransactionType.entries.forEach { type ->
            val movement = when (type) {
                TransactionType.ADJUSTMENT -> LedgerProjection.movementFor(
                    type, BigDecimal.ZERO, explicitCashDelta = BigDecimal.ZERO
                )
                else -> LedgerProjection.movementFor(type, BigDecimal.ONE)
            }
            assertThat(movement.rationale).isNotEmpty()
        }
    }

    @Test
    fun `repeated pesewa amounts accumulate exactly`() {
        // Why money is BigDecimal and never Double: with Double, one hundred additions of
        // 0.01 does not equal 1.00.
        var total = BigDecimal.ZERO
        repeat(100) {
            total = total.add(
                LedgerProjection.movementFor(TransactionType.CASH_IN, BigDecimal("0.01")).cashDelta
            )
        }

        assertThat(total.compareTo(BigDecimal("1.00"))).isEqualTo(0)
    }

    @Test
    fun `wire values match the backend enum exactly`() {
        // These integers travel over the API. A mismatch silently reclassifies transactions.
        assertThat(TransactionType.CASH_IN.wireValue).isEqualTo(0)
        assertThat(TransactionType.CASH_OUT.wireValue).isEqualTo(1)
        assertThat(TransactionType.TRANSFER.wireValue).isEqualTo(2)
        assertThat(TransactionType.REVERSAL.wireValue).isEqualTo(3)
        assertThat(TransactionType.COMMISSION.wireValue).isEqualTo(4)
        assertThat(TransactionType.ADJUSTMENT.wireValue).isEqualTo(5)
        assertThat(TransactionType.UNKNOWN.wireValue).isEqualTo(6)
    }

    @Test
    fun `an unrecognised wire value degrades to unknown rather than throwing`() {
        // A server that adds a type must not crash an older client into a retry loop.
        assertThat(TransactionType.fromWire(99)).isEqualTo(TransactionType.UNKNOWN)
    }
}
