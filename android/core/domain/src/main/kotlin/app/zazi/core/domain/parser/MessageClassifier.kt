package app.zazi.core.domain.parser

import app.zazi.core.domain.model.TransactionType

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

        // Prize draws and subscriptions. Taken from real messages on an agent's handset:
        // "BIG NEWS! 0533547740 your number qualifies for today's VIP spin challenge.
        // Collect points and top winners will share 30 000GHS! Reply VIP now" reached the
        // review queue because it quoted cedis and matched none of the wording below.
        "PROMO", "PRIZE", "JACKPOT", "SEND STOP TO", "TO EXIT", "SWIPE & WIN",
        "CONGRATULATIONS, YOU", "/DAY",
        "QUALIFIES FOR", "SPIN CHALLENGE", "COLLECT POINTS", "TOP WINNERS",
        "REPLY VIP", "BIG NEWS",

        // Airtime and data marketing.
        "DATA OFFER", "RECHARGE AND GET", "FREE SMS", "MEGABYTES"
    )

    /**
     * Money the agent spent on themselves, not a customer's transaction.
     *
     * <p>Buying airtime or a data bundle moves the float and is a real payment, so every test
     * above says transaction — but it is not the work a mobile money vendor is paid for, and
     * counting it as trading would put the agent's own phone bill in the day's takings.
     * Excluded until the product covers an agent's own spending properly.</p>
     */
    private val OWN_SPENDING = listOf(
        "AIRTIME", "TO MTN AIRTIME", "BUNDLE", "DATA PACKAGE", "MASHUP"
    )

    /**
     * What may reach the ledger without a person looking at it.
     *
     * <p>A vendor's whole job is a customer putting cash into a wallet or taking it out.
     * "Deposit" and "cash in" are one movement; "withdrawal" and "cash out" are the same
     * movement the other way. Commission is not a customer's transaction but it is the
     * vendor's earnings, credited by the network on its own — holding each one for review
     * would bury the queue and leave the profit figures permanently short.</p>
     *
     * <p>Everything else waits for a person. A transfer's effect on an agent's books is not
     * derivable from the message; a reversal needs the transaction it reverses; an airtime
     * top-up is the agent's own spending and is rejected before this is reached.</p>
     */
    fun postsAutomatically(type: TransactionType): Boolean = when (type) {
        TransactionType.CASH_IN, TransactionType.CASH_OUT, TransactionType.COMMISSION -> true
        else -> false
    }

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
        if (isNotATransaction(normalizedBody)) {
            return Verdict.NOT_A_TRANSACTION
        }

        val statesACompletedAction = statesACompletedAction(normalizedBody)

        return if (statesACompletedAction && hasAmount && hasReference && hasCounterparty) {
            Verdict.TRANSACTION
        } else {
            Verdict.NEEDS_REVIEW
        }
    }

    private fun statesACompletedAction(normalizedBody: String): Boolean =
        COMPLETED_ACTION.any { normalizedBody.contains(it) }

    /**
     * Whether this message is definitively not a vendor's transaction, before parsing.
     *
     * <p>Covers both marketing and the agent's own spending: an airtime top-up is real money
     * and still nothing to do with the trade the vendor is paid for.</p>
     */
    fun isNotATransaction(normalizedBody: String): Boolean {
        // The agent's own airtime or data, whatever else the message says. Real money, and
        // still not the work a vendor is paid for.
        if (OWN_SPENDING.any { normalizedBody.contains(it) }) {
            return true
        }

        // A message stating that money actually moved is a transaction however it is dressed.
        //
        // This ordering is the whole point. Networks open genuine confirmations with "Dear
        // Customer", and quote terms in the footer, so a marketing word list applied first
        // threw away real money — discarded outright, not even held, with nothing anywhere to
        // say a transaction had ever arrived. Marketing wording is only evidence in the
        // absence of a completed action; a promotion never says a payment was received.
        if (statesACompletedAction(normalizedBody)) {
            return false
        }

        if (NOT_TRANSACTIONAL.any { normalizedBody.contains(it) }) {
            return true
        }

        // A balance reminder and nothing else. Safe only here, below the completed-action
        // test, because every real confirmation also quotes a balance.
        return normalizedBody.contains("BALANCE")
    }
}
