package app.zazi.ui.state

import app.zazi.core.data.network.RemoteTransaction
import java.math.BigDecimal
import java.math.RoundingMode
import java.time.Instant

/**
 * Transactions the server holds for this agent that this handset did not record — forwarded
 * from a keypad phone, typed on a second handset, or entered in the portal.
 *
 * <p>Pure, so the merge is tested without a network or a database. [held] is every identity,
 * client or server, this device already has; those rows are already on screen and already in
 * the totals, and showing them twice is exactly the error this app exists to prevent.</p>
 */
object RemoteActivity {

    fun notHeldHere(remote: List<RemoteTransaction>, held: Set<String>): List<RemoteTransaction> =
        remote.filter { row ->
            row.id !in held && (row.clientTransactionId == null || row.clientTransactionId !in held)
        }

    /** Cash and float movement of these rows, in pesewas, as the server recorded it. */
    fun totalsMinor(rows: List<RemoteTransaction>): Pair<Long, Long> =
        rows.sumOf { minor(it.cashDelta) } to rows.sumOf { minor(it.floatDelta) }

    fun toActivityItem(row: RemoteTransaction): ActivityItem? {
        val at = runCatching { Instant.parse(row.transactionAt).toEpochMilli() }.getOrNull()
            // The server writes an offset ("+00:00"), which older runtimes' Instant.parse
            // refuses. A time neither form reads is left out rather than shown wrongly.
            ?: runCatching { java.time.OffsetDateTime.parse(row.transactionAt).toInstant().toEpochMilli() }.getOrNull()
            ?: return null
        return ActivityItem(
            clientTransactionId = row.clientTransactionId ?: row.id,
            label = label(row.type),
            provider = row.network,
            amountMinor = minor(row.amount),
            cashDeltaMinor = minor(row.cashDelta),
            atUtcMillis = at,
            capturedAutomatically = row.source == SOURCE_SMS,
            delivery = ActivityDelivery.SENT,
            customerPhone = row.customerPhoneNumber,
            recordedElsewhere = origin(row)
        )
    }

    /**
     * Merges and orders, newest first — the order the list has always used. Capped because the
     * list is drawn in full inside a scrolling screen; the statement covers the rest.
     */
    fun merge(local: List<ActivityItem>, remote: List<ActivityItem>, limit: Int = 100): List<ActivityItem> =
        (local + remote).sortedByDescending { it.atUtcMillis }.take(limit)

    private fun origin(row: RemoteTransaction): String = when {
        row.source == SOURCE_BRIDGE -> "keypad phone"
        row.deviceId == null -> "portal"
        else -> "other phone"
    }

    private fun label(type: Int): String = when (type) {
        0 -> "Cash in"
        1 -> "Cash out"
        2 -> "Transfer"
        3 -> "Reversal"
        4 -> "Commission"
        5 -> "Adjustment"
        else -> "Transaction"
    }

    private fun minor(cedis: Double): Long =
        BigDecimal.valueOf(cedis).movePointRight(2).setScale(0, RoundingMode.HALF_UP).toLong()

    private const val SOURCE_SMS = 1
    private const val SOURCE_BRIDGE = 2
}
