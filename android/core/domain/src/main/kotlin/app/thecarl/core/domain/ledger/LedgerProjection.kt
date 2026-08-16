package app.thecarl.core.domain.ledger

import app.thecarl.core.domain.model.TransactionType
import java.math.BigDecimal

/**
 * Signed effect a transaction has on the agent's two balances. Positive means increase.
 */
data class LedgerMovement(
    val cashDelta: BigDecimal,
    val floatDelta: BigDecimal,
    val requiresReview: Boolean,
    val rationale: String
) {
    val isZero: Boolean get() = cashDelta.signum() == 0 && floatDelta.signum() == 0

    companion object {
        fun none(rationale: String) = LedgerMovement(BigDecimal.ZERO, BigDecimal.ZERO, false, rationale)
        fun review(rationale: String) = LedgerMovement(BigDecimal.ZERO, BigDecimal.ZERO, true, rationale)
    }
}

/**
 * Local balance projection for offline usability.
 *
 * **The server is the financial authority.** This exists so an agent can see a plausible
 * cash and float position while offline; it is not a second ledger. Where the local
 * projection disagrees with the server, the server wins and the difference is surfaced for
 * reconciliation rather than silently overwritten.
 *
 * The direction rules are duplicated from the backend deliberately and narrowly — an agent
 * offline for hours needs to see the effect of their own work. The duplication is confined
 * to this one object so it cannot drift across the codebase, and
 * `LedgerProjectionTest` pins each rule against the backend's `LedgerPolicy`.
 *
 * Direction, from the agent's books:
 * - **Cash-in**: customer hands over cash, agent sends e-money. Cash up, float down.
 * - **Cash-out**: customer sends e-money, agent pays out cash. Cash down, float up.
 */
object LedgerProjection {
    /** Smallest representable amount: one pesewa. */
    val MINIMUM_AMOUNT: BigDecimal = BigDecimal("0.01")

    /** Storage scale, matching the backend's numeric(18,4). */
    const val STORAGE_SCALE = 4

    fun movementFor(
        type: TransactionType,
        amount: BigDecimal,
        originalType: TransactionType? = null,
        explicitCashDelta: BigDecimal? = null,
        explicitFloatDelta: BigDecimal? = null
    ): LedgerMovement {
        require(type == TransactionType.ADJUSTMENT || amount.signum() >= 0) {
            "Amount must be a positive magnitude; direction comes from the transaction type."
        }

        return when (type) {
            TransactionType.CASH_IN -> LedgerMovement(
                cashDelta = amount,
                floatDelta = amount.negate(),
                requiresReview = false,
                rationale = "Cash-in: agent receives physical cash and sends e-money."
            )

            TransactionType.CASH_OUT -> LedgerMovement(
                cashDelta = amount.negate(),
                floatDelta = amount,
                requiresReview = false,
                rationale = "Cash-out: agent pays out physical cash and receives e-money."
            )

            // Which side of the agent's books a transfer touches, if either, cannot be
            // inferred from a message, so nothing is projected rather than guessing.
            TransactionType.TRANSFER -> LedgerMovement.none(
                "Transfer: no balance movement; source and destination are not derivable."
            )

            TransactionType.COMMISSION -> LedgerMovement(
                cashDelta = BigDecimal.ZERO,
                floatDelta = amount,
                requiresReview = false,
                rationale = "Commission: provider credits the agent's e-money float."
            )

            TransactionType.REVERSAL -> reversalMovement(amount, originalType)

            TransactionType.ADJUSTMENT -> adjustmentMovement(explicitCashDelta, explicitFloatDelta)

            // Never project an unclassified transaction. Doing so would let a parser failure
            // silently corrupt the agent's view of their own position.
            TransactionType.UNKNOWN -> LedgerMovement.review(
                "Unknown type: excluded from all balance projections until classified."
            )
        }
    }

    /**
     * Whether a type may post automatically from parsed evidence without human review.
     * Reversals and adjustments are excluded: they need a reference or explicit deltas that
     * a parser cannot supply.
     */
    fun canPostAutomatically(type: TransactionType): Boolean = when (type) {
        TransactionType.CASH_IN,
        TransactionType.CASH_OUT,
        TransactionType.TRANSFER,
        TransactionType.COMMISSION -> true
        else -> false
    }

    private fun reversalMovement(amount: BigDecimal, originalType: TransactionType?): LedgerMovement {
        if (originalType == null) {
            return LedgerMovement.review("Reversal without a referenced original: held for review.")
        }
        if (originalType == TransactionType.REVERSAL) {
            return LedgerMovement.review("Reversal of a reversal: held for review.")
        }

        val original = movementFor(originalType, amount)
        return LedgerMovement(
            cashDelta = original.cashDelta.negate(),
            floatDelta = original.floatDelta.negate(),
            requiresReview = original.requiresReview,
            rationale = "Reversal of $originalType: exact inverse of the original movement."
        )
    }

    private fun adjustmentMovement(
        cashDelta: BigDecimal?,
        floatDelta: BigDecimal?
    ): LedgerMovement {
        require(cashDelta != null || floatDelta != null) {
            "An adjustment must carry an explicit cash delta, float delta, or both."
        }
        return LedgerMovement(
            cashDelta = cashDelta ?: BigDecimal.ZERO,
            floatDelta = floatDelta ?: BigDecimal.ZERO,
            requiresReview = false,
            rationale = "Adjustment: explicit correction with signed deltas."
        )
    }
}
