package app.zazi.core.domain.ledger

/**
 * Finds transactions the phone never saw, using the provider's own running balance.
 *
 * <p>Every mobile money confirmation states the balance left afterwards. That figure is
 * arithmetic the app did not do, which makes it the one independent check available on a
 * handset: if the balance after one transaction plus the next transaction's effect does not
 * equal the balance after the next one, something happened in between that this phone has no
 * record of — and the difference is exactly what it was worth.</p>
 *
 * <p>This is what turns "you are short ₵200" into "a transaction of ₵200 went missing between
 * 11:27 and 11:42". An agent can do something with the second sentence.</p>
 *
 * <h3>Why a phone misses a message at all</h3>
 * <p>It is switched off, or out of signal when the network gives up retrying; the permission
 * was revoked and restored; the message arrived in parts and one part never came. None of
 * those leave a trace in the app, which is the whole problem — the agent's figures are simply
 * short, and nothing on the screen says why.</p>
 */
object MissingTransactions {

    /**
     * One transaction present in the provider's balances but absent from this phone.
     *
     * @param amountMinor how much the missing transaction moved, always positive.
     * @param afterUtcMillis the last transaction the phone did see before the gap.
     * @param beforeUtcMillis the first transaction it saw after the gap.
     */
    data class Gap(
        val amountMinor: Long,
        val afterUtcMillis: Long,
        val beforeUtcMillis: Long
    )

    /**
     * One transaction as this phone recorded it, for the purpose of checking the arithmetic.
     *
     * @param floatDeltaMinor the signed effect on the provider balance — which is the float,
     *   since a provider's balance is e-money.
     * @param balanceAfterMinor what the provider said was left, or null if it did not say.
     */
    data class Seen(
        val atUtcMillis: Long,
        val floatDeltaMinor: Long,
        val balanceAfterMinor: Long?
    )

    /**
     * The gaps in a day's transactions, oldest first.
     *
     * <p>Only consecutive pairs that both state a balance are compared; a pair with an unknown
     * balance proves nothing either way and is skipped rather than guessed at. Transactions
     * must be supplied in the order they happened.</p>
     *
     * <p>A tolerance of one pesewa absorbs nothing — the figures are integers — but it is
     * stated explicitly so that a later change to rounding cannot silently start reporting a
     * gap on every transaction.</p>
     */
    fun find(seen: List<Seen>): List<Gap> {
        val gaps = mutableListOf<Gap>()

        seen.zipWithNext { earlier, later ->
            val from = earlier.balanceAfterMinor ?: return@zipWithNext
            val to = later.balanceAfterMinor ?: return@zipWithNext

            // What the balance should be after the later transaction, given where it started.
            val expected = from + later.floatDeltaMinor
            val unexplained = to - expected

            if (unexplained != 0L) {
                gaps += Gap(
                    // Direction is not claimed. The agent is being told a transaction is
                    // missing and what it was worth; whether it was a deposit or a withdrawal
                    // is visible in their own provider's message, which they still have.
                    amountMinor = kotlin.math.abs(unexplained),
                    afterUtcMillis = earlier.atUtcMillis,
                    beforeUtcMillis = later.atUtcMillis
                )
            }
        }

        return gaps
    }

    /** What the gaps add up to — the part of a day's shortfall this can actually account for. */
    fun totalMinor(gaps: List<Gap>): Long = gaps.sumOf { it.amountMinor }
}
