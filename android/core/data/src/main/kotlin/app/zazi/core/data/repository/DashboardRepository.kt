package app.zazi.core.data.repository

import app.zazi.core.data.database.RecentTransactionRow
import app.zazi.core.data.database.TransactionDetailRow
import app.zazi.core.data.database.ZaziDatabase
import kotlinx.coroutines.flow.Flow

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
        val digits = query.filter { it.isDigit() }
        if (digits.length < 3) return emptyList()
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
