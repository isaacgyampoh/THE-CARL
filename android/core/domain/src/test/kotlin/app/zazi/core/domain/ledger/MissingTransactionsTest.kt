package app.zazi.core.domain.ledger

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Finding the transaction a phone never saw.
 *
 * <p>The figures here are the real ones from a vendor's handset: a payment of GHS 295.00
 * leaving a balance of GHS 1042.16, and a payment of GHS 295.00 arriving to leave GHS 1337.16.
 * A missed message between them is invisible to the app and obvious to this arithmetic.</p>
 */
class MissingTransactionsTest {

    private fun seen(at: Long, floatDelta: Long, balance: Long?) =
        MissingTransactions.Seen(at, floatDelta, balance)

    @Test
    fun `a day whose balances agree has no gaps`() {
        val day = listOf(
            seen(at = 1, floatDelta = -29_500, balance = 104_216),
            seen(at = 2, floatDelta = +29_500, balance = 133_716)
        )

        assertThat(MissingTransactions.find(day)).isEmpty()
    }

    @Test
    fun `a missing transaction is found, with what it was worth and when`() {
        // The phone saw the first and the third. The second — a withdrawal of GHS 200.00 —
        // never arrived, so the balance drops by more than the transactions account for.
        val day = listOf(
            seen(at = 1_100, floatDelta = -29_500, balance = 104_216),
            seen(at = 1_400, floatDelta = +29_500, balance = 113_716)
        )

        val gap = MissingTransactions.find(day).single()

        assertThat(gap.amountMinor).isEqualTo(20_000)
        assertThat(gap.afterUtcMillis).isEqualTo(1_100)
        assertThat(gap.beforeUtcMillis).isEqualTo(1_400)
    }

    @Test
    fun `a pair with no stated balance proves nothing and is not reported`() {
        // Silence is not evidence of a gap. Reporting one here would send an agent looking
        // for a transaction that never existed, which is worse than saying nothing.
        val day = listOf(
            seen(at = 1, floatDelta = -29_500, balance = null),
            seen(at = 2, floatDelta = +29_500, balance = 133_716),
            seen(at = 3, floatDelta = -1_000, balance = null)
        )

        assertThat(MissingTransactions.find(day)).isEmpty()
    }

    @Test
    fun `several gaps in one day are all reported, oldest first`() {
        val day = listOf(
            seen(at = 1, floatDelta = 0, balance = 100_000),
            seen(at = 2, floatDelta = 0, balance = 95_000),
            seen(at = 3, floatDelta = 0, balance = 80_000)
        )

        val gaps = MissingTransactions.find(day)

        assertThat(gaps).hasSize(2)
        assertThat(gaps[0].amountMinor).isEqualTo(5_000)
        assertThat(gaps[1].amountMinor).isEqualTo(15_000)
        assertThat(gaps[0].afterUtcMillis).isLessThan(gaps[1].afterUtcMillis)
        assertThat(MissingTransactions.totalMinor(gaps)).isEqualTo(20_000)
    }

    @Test
    fun `a single transaction cannot be checked against anything`() {
        assertThat(MissingTransactions.find(listOf(seen(1, 0, 100_000)))).isEmpty()
        assertThat(MissingTransactions.find(emptyList())).isEmpty()
    }

    @Test
    fun `a missed deposit is found as readily as a missed withdrawal`() {
        // The balance rose by more than the phone can account for, so the missing transaction
        // was money coming in. The amount is reported without claiming a direction — the
        // agent still has the provider's own message to read.
        val day = listOf(
            seen(at = 1, floatDelta = 0, balance = 100_000),
            seen(at = 2, floatDelta = 0, balance = 107_500)
        )

        assertThat(MissingTransactions.find(day).single().amountMinor).isEqualTo(7_500)
    }
}
