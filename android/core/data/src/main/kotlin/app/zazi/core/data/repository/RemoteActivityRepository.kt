package app.zazi.core.data.repository

import app.zazi.core.data.network.RemoteTransaction
import app.zazi.core.data.network.ZaziApi
import java.time.Instant
import kotlinx.coroutines.withTimeoutOrNull

/**
 * The agent's transactions as the server has them, from every phone they use.
 *
 * <p>The handset's own list is local, which is what lets it work offline. But an agent who also
 * forwards MoMo messages from a keypad phone, or works a second handset, would never see those
 * on this screen. With a connection this fills the gap; without one it returns nothing and the
 * local list stands alone.</p>
 *
 * <p>Bounded by a short timeout. On a weak connection a slow answer would hold up the whole
 * dashboard, and a list that arrives a moment later is better than a screen that waits.</p>
 */
class RemoteActivityRepository(
    private val api: ZaziApi,
    private val timeoutMillis: Long = 4_000,
    private val now: () -> Long = System::currentTimeMillis
) {
    /**
     * The last answer, briefly. One dashboard refresh asks for today's rows twice — once for the
     * totals, once for the list — and a second round trip on a 2G connection buys nothing.
     */
    private var cached: Triple<String, Long, List<RemoteTransaction>>? = null

    suspend fun between(fromUtcMillis: Long, toUtcMillis: Long): List<RemoteTransaction> =
        fetch(Instant.ofEpochMilli(fromUtcMillis).toString(), Instant.ofEpochMilli(toUtcMillis).toString(), null)

    suspend fun forCustomer(query: String): List<RemoteTransaction> {
        val digits = query.filter { it.isDigit() }
        if (digits.length < 3) return emptyList()
        return fetch(null, null, digits)
    }

    private suspend fun fetch(from: String?, to: String?, customer: String?): List<RemoteTransaction> {
        val key = "$from|$to|$customer"
        cached?.let { (cachedKey, at, rows) ->
            if (cachedKey == key && now() - at < CACHE_MILLIS) return rows
        }
        val rows = withTimeoutOrNull(timeoutMillis) {
            runCatching { api.myTransactions(from, to, customer) }
                .getOrNull()
                ?.takeIf { it.isSuccessful }
                ?.body()
                ?.items
        }
        // A failure is not cached: the next refresh should try again.
        if (rows != null) cached = Triple(key, now(), rows)
        return rows ?: emptyList()
    }

    private companion object {
        const val CACHE_MILLIS = 20_000L
    }
}
