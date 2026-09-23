package app.zazi.core.domain.parser

/**
 * Whether a message is a mobile money transaction at all.
 *
 * <p>Zazi is for mobile money vendors, and the thing it exists to detect is a <b>transaction</b>
 * — not a message. A network's shortcode carries both: the confirmation that money moved, and a
 * steady stream of loan offers, campaign notices and prize draws that quote figures in cedis and
 * look financial to any naive test.</p>
 *
 * <p>The criteria live here, in one file, deliberately. They decide what reaches an agent's
 * money records, so they have to be readable by a person who is not going to read a parser.</p>
 *
 * <h3>The order matters</h3>
 * <ol>
 *   <li><b>Reject</b> — known non-transactional wording. Checked first, because a loan offer
 *       quoting "up to GHS 1000" passes every test for being about money.</li>
 *   <li><b>Accept</b> — an amount, a completed-action verb, a reference and a counterparty.
 *       All four, because any three of them also describe a marketing message.</li>
 *   <li><b>Review</b> — everything else. Never silently dropped and never silently posted: a
 *       filter that is too strict loses real money without anybody noticing, which is worse
 *       than showing an agent something that turns out not to be a transaction.</li>
 * </ol>
 */
object MessageClassifier {

    enum class Verdict {
        /** Marketing, an offer, or a notice. Discarded without being stored. */
        NOT_A_TRANSACTION,

        /** Everything a transaction needs was found. */
        TRANSACTION,

        /** Cannot be decided from the text. Goes to the agent, never to the ledger. */
        NEEDS_REVIEW
    }

    /**
     * Wording a real confirmation never carries.
     *
     * <p><b>Not</b> keyed on "download", "click here" or "dial" — MTN appends all three to
     * genuine payment confirmations, so matching them would discard real money. Every entry
     * here was chosen because a message that moved money cannot contain it.</p>
     */
    private val NOT_TRANSACTIONAL = listOf(
        // Credit and loan offers. "Up to" is the tell: a completed transaction states an
        // amount, it does not offer a ceiling.
        "QUALIFIED FOR", "YOU QUALIFY", "ELIGIBLE FOR", "QUALIFY FOR",
        "LOAN", "BORROW", "UP TO GHS", "UP TO GH",

        // Campaign and bulk notices.
        "DEAR VALUED CUSTOMER", "DEAR CUSTOMER", "NOTIFICATION FOR", "HEROES OF CHANGE",
        "NEWSLETTER", "TERMS AND CONDITIONS APPLY", "T&C APPLY",

        // Prize draws and subscriptions.
        "PROMO", "PRIZE", "JACKPOT", "SEND STOP TO", "TO EXIT", "SWIPE & WIN",
        "CONGRATULATIONS, YOU", "/DAY",

        // Airtime and data marketing.
        "BUNDLE", "DATA OFFER", "RECHARGE AND GET", "FREE SMS", "MEGABYTES"
    )

    /** A completed movement of money, stated in the past. An offer has none of these. */
    private val COMPLETED_ACTION = listOf(
        "PAYMENT RECEIVED", "PAYMENT MADE", "PAYMENT OF",
        "YOU HAVE RECEIVED", "YOU HAVE SENT", "HAS BEEN RECEIVED",
        "CASH IN", "CASH-IN", "CASH OUT", "CASH-OUT",
        "RECEIVED FROM", "SENT TO", "PAID TO", "WITHDRAWN", "DEPOSITED",
        "TRANSFERRED", "REVERSED"
    )

    /**
     * Classifies one already-normalised message.
     *
     * @param normalizedBody the message, whitespace-collapsed and uppercased.
     * @param hasAmount whether a currency amount was recovered.
     * @param hasReference whether a reference or transaction id was recovered.
     * @param hasCounterparty whether a name or number for the other party was recovered.
     */
    fun classify(
        normalizedBody: String,
        hasAmount: Boolean,
        hasReference: Boolean,
        hasCounterparty: Boolean
    ): Verdict {
        if (NOT_TRANSACTIONAL.any { normalizedBody.contains(it) }) {
            return Verdict.NOT_A_TRANSACTION
        }

        val statesACompletedAction = COMPLETED_ACTION.any { normalizedBody.contains(it) }

        // A balance reminder with nothing else is not a transaction. Checked after the
        // completed-action test because every real confirmation also quotes a balance.
        if (!statesACompletedAction && normalizedBody.contains("BALANCE")) {
            return Verdict.NOT_A_TRANSACTION
        }

        return if (statesACompletedAction && hasAmount && hasReference && hasCounterparty) {
            Verdict.TRANSACTION
        } else {
            Verdict.NEEDS_REVIEW
        }
    }

    /** Whether this message is definitively not a transaction, before anything is parsed. */
    fun isNotATransaction(normalizedBody: String): Boolean =
        NOT_TRANSACTIONAL.any { normalizedBody.contains(it) }
}
