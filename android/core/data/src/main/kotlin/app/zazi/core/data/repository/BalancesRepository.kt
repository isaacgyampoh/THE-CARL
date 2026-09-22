package app.zazi.core.data.repository

import app.zazi.core.data.network.MyBalances
import app.zazi.core.data.network.ZaziApi
import kotlinx.coroutines.withTimeoutOrNull

/**
 * What the owner has given this agent, as the server's ledger has it.
 *
 * <p>Held by the business, not by the handset: cash and float are given by the owner and moved
 * by every transaction the agent records anywhere, so the server is the only place that knows
 * the total. Offline the app shows what it last saw rather than a figure it invented.</p>
 */
class BalancesRepository(
    private val api: ZaziApi,
    private val timeoutMillis: Long = 4_000
) {
    @Volatile
    private var lastSeen: MyBalances? = null

    /** The latest balances, or the last ones seen when there is no connection. */
    suspend fun mine(): MyBalances? {
        val fetched = withTimeoutOrNull(timeoutMillis) {
            runCatching { api.myBalances() }.getOrNull()?.takeIf { it.isSuccessful }?.body()
        }
        if (fetched != null) {
            lastSeen = fetched
        }
        return fetched ?: lastSeen
    }
}
