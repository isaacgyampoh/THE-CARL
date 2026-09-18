package app.zazi.core.data.repository

import app.zazi.core.data.database.RecentTransactionRow
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.domain.sync.OutboxState
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

    suspend fun syncedCount(): Int =
        database.outboxDao().countByState(OutboxState.SYNCED.name)

    /**
     * What this device has recorded, most recent first.
     *
     * <p>A Flow, so the list updates as sync progresses without the screen polling for it.
     * The agent sees an item move from waiting to sent on its own.</p>
     */
    fun observeRecent(limit: Int = DEFAULT_RECENT): Flow<List<RecentTransactionRow>> =
        database.localTransactionDao().observeRecentWithDelivery(limit)

    private companion object {
        /** Enough to cover a shift's worth of checking back, without becoming a report. */
        const val DEFAULT_RECENT = 25
    }
}
