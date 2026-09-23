package app.zazi.core.data.repository

import app.zazi.core.data.database.RecentTransactionRow
import app.zazi.core.data.database.TransactionDetailRow
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.domain.ledger.MissingTransactions
import app.zazi.core.domain.parser.BaseSmsParser
import app.zazi.core.domain.parser.MessageClassifier
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

/** Local movement totals for a window, in minor units. */
data class DailyTotals(val cashMinor: Long, val floatMinor: Long)

/**
 * Read-only local figures for the dashboard.
 *
 * <p>Exists so presentation code never touches a DAO. Beyond the layering, it keeps Room off
 * the <c>:app</c> compile classpath — the UI module has no business knowing the persistence
 * technology.</p>
 *
 * <p>Totals are <b>summed from stored deltas</b>, never re-derived from transaction type.
 * Direction was decided once by LedgerProjection at capture; recomputing it here would be a
 * second opinion that could disagree.</p>
 */
class DashboardRepository(private val database: ZaziDatabase) {

    /**
     * Cash and float movement within a window.
     *
     * <p>Ghana observes UTC+0 year-round, so a UTC day boundary is the business day. The
     * caller supplies the window rather than this class assuming one.</p>
     */
    suspend fun totalsBetween(fromUtcMillis: Long, toUtcMillis: Long): DailyTotals = DailyTotals(
        cashMinor = database.localTransactionDao().sumCashDeltaMinor(fromUtcMillis, toUtcMillis),
        floatMinor = database.localTransactionDao().sumFloatDeltaMinor(fromUtcMillis, toUtcMillis)
    )

    /** How many of the window's transactions are already on the server. */
    suspend fun syncedCount(fromUtcMillis: Long, toUtcMillis: Long): Int =
        database.localTransactionDao().countSyncedBetween(fromUtcMillis, toUtcMillis)

    /**
     * What this device has recorded, most recent first.
     *
     * <p>A Flow, so the list updates as sync progresses without the screen polling for it.
     * The agent sees an item move from waiting to sent on its own.</p>
     */
    fun observeRecent(limit: Int = DEFAULT_RECENT): Flow<List<RecentTransactionRow>> =
        database.localTransactionDao().observeRecentWithDelivery(limit)

    /** The same, confined to a window — a day, or the last several. */
    /**
     * Transactions for one customer, across every date on this device.
     *
     * <p>Searches by the last nine digits once nine or more are typed, so 0244123456,
     * 244123456 and 233244123456 all find the same customer; fewer digits match anywhere in
     * the number, for "the one ending 3456".</p>
     */
    suspend fun searchByCustomer(query: String, limit: Int = 200): List<RecentTransactionRow> {
        val typed = query.trim()
        val digits = typed.filter { it.isDigit() }

        // Letters mean they are looking for a person, not a number. A customer querying a
        // transaction remembers seeing their name confirmed on the agent's screen far more
        // reliably than they remember which number they used.
        if (digits.length < 3) {
            return if (typed.length >= 3) {
                database.localTransactionDao().searchByCustomer(typed, limit)
            } else {
                emptyList()
            }
        }

        val key = if (digits.length >= 9) digits.takeLast(9) else digits
        return database.localTransactionDao().searchByCustomer(key, limit)
    }

    /**
     * The identities, client or server, of these that this device already holds. What the
     * server returns for "my transactions" includes everything this handset sent; those must
     * not be shown or counted a second time.
     */
    suspend fun alreadyHeld(clientIds: List<String>, serverIds: List<String>): Set<String> {
        val dao = database.localTransactionDao()
        val held = HashSet<String>()
        // SQLite caps bound parameters; the server returns at most 200 per call.
        clientIds.chunked(500).forEach { held += dao.knownClientIds(it) }
        serverIds.chunked(500).forEach { held += dao.knownServerIds(it) }
        return held
    }

    fun observeBetween(
        fromUtcMillis: Long,
        toUtcMillis: Long,
        limit: Int = DEFAULT_RECENT
    ): Flow<List<RecentTransactionRow>> =
        database.localTransactionDao().observeBetweenWithDelivery(fromUtcMillis, toUtcMillis, limit)

    /**
     * Mobile money messages this device could not turn into a transaction on its own.
     *
     * <p>They were stored rather than discarded precisely so a person sees them. Until this
     * was observed anywhere, they sat in the table and the agent's money simply never
     * appeared — which is indistinguishable, at the counter, from the app not working.</p>
     */
    fun observeHeldCount(): Flow<Int> = database.evidenceDao().observePendingReviewCount()

    fun observeHeld(): Flow<List<HeldMessage>> =
        database.evidenceDao().observePendingReview().map { rows ->
            rows.map { row ->
                HeldMessage(
                    evidenceId = row.evidenceId,
                    provider = row.provider,
                    amountMinor = row.amountMinor,
                    customerPhoneNumber = row.customerPhoneNumber,
                    observedAtUtcMillis = row.observedAtUtcMillis,
                    reason = row.outcomeReason,
                    rawMessage = row.rawMessage
                )
            }
        }

    /** Marks a held message dealt with, once the agent has recorded it or dismissed it. */
    suspend fun settleHeld(evidenceId: String, recorded: Boolean): Boolean =
        database.evidenceDao().settlePendingReview(
            evidenceId = evidenceId,
            state = if (recorded) "ACCEPTED" else "REJECTED",
            reason = if (recorded) "Recorded by the agent." else "Dismissed by the agent."
        ) > 0

    /**
     * Re-checks messages already waiting against the current rules.
     *
     * <p>The queue was filled by older rules, so it holds loan offers and campaign notices
     * that today's classifier would never have kept. Clearing those out matters more than it
     * sounds: an agent who opens the queue and finds marketing stops opening the queue, and
     * then the real transaction sitting underneath it is never recorded either.</p>
     *
     * <p>Only ever <b>removes</b> non-transactions. Nothing is promoted into the ledger by a
     * re-scan — a message that now looks like a transaction is still the agent's to confirm,
     * because posting money on the strength of a rule change nobody watched happen is exactly
     * the kind of thing that must not be automatic.</p>
     *
     * @return how many were removed as marketing.
     */
    suspend fun rescanHeld(): Int {
        var removed = 0
        database.evidenceDao().findByState("PENDING_REVIEW").forEach { row ->
            val body = row.rawMessage ?: return@forEach
            if (MessageClassifier.isNotATransaction(BaseSmsParser.normalize(body))) {
                val settled = database.evidenceDao().settlePendingReview(
                    evidenceId = row.evidenceId,
                    state = "REJECTED",
                    reason = "Marketing, an offer or a notice — not a transaction."
                )
                if (settled > 0) removed++
            }
        }
        return removed
    }

    /**
     * Transactions the provider's balances prove happened but this phone never saw.
     *
     * <p>Answers the question an agent actually has at closing time. "You are short ₵200" is
     * not something anybody can act on; "a transaction of ₵200 is missing between 11:27 and
     * 11:42" is, because they still have the provider's own message on the phone.</p>
     */
    suspend fun missingBetween(
        fromUtcMillis: Long,
        toUtcMillis: Long
    ): List<MissingTransactions.Gap> {
        val trail = database.localTransactionDao()
            .balanceTrailBetween(fromUtcMillis, toUtcMillis)
            .map {
                MissingTransactions.Seen(
                    atUtcMillis = it.transactionAtUtcMillis,
                    floatDeltaMinor = it.floatDeltaMinor,
                    balanceAfterMinor = it.balanceAfterMinor,
                    network = it.provider
                )
            }
        return MissingTransactions.find(trail)
    }

    /** Everything known about one transaction, including why it is stuck. */
    suspend fun findDetail(clientTransactionId: String): TransactionDetailRow? =
        database.localTransactionDao().findDetail(clientTransactionId)

    /** The same detail, followed while a screen is showing it. */
    fun observeDetail(clientTransactionId: String): Flow<TransactionDetailRow?> =
        database.localTransactionDao().observeDetail(clientTransactionId)

    /**
     * Returns a dead-lettered transaction to the queue.
     *
     * <p>Returns whether anything changed. False means the row was not dead-lettered — it may
     * have been delivered by a retry between the screen being drawn and the button being
     * pressed, or it may be a conflict, which no retry can resolve. Either way the caller
     * should re-read rather than assume.</p>
     */
    suspend fun retryDeadLettered(clientTransactionId: String, nowUtcMillis: Long): Boolean =
        database.outboxDao().requeueDeadLettered(clientTransactionId, nowUtcMillis) > 0

    private companion object {
        /** Enough to cover a shift's worth of checking back, without becoming a report. */
        const val DEFAULT_RECENT = 25
    }
}

/**
 * A mobile money message that arrived but could not be recorded without a person.
 *
 * <p>Carries the raw text deliberately: the agent decides what it was by reading it, and the
 * whole point of surfacing these is that the app could not.</p>
 */
data class HeldMessage(
    val evidenceId: String,
    val provider: String,
    val amountMinor: Long?,
    val customerPhoneNumber: String?,
    val observedAtUtcMillis: Long,
    val reason: String?,
    val rawMessage: String?
)
