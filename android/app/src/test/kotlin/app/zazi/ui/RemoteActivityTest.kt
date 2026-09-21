package app.zazi.ui

import app.zazi.core.data.network.RemoteTransaction
import app.zazi.ui.state.ActivityDelivery
import app.zazi.ui.state.ActivityItem
import app.zazi.ui.state.RemoteActivity
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/** The agent's transactions from other phones: shown once, counted once, labelled honestly. */
class RemoteActivityTest {

    private fun remote(
        id: String,
        clientId: String? = "CTX-$id",
        source: Int = 2,
        deviceId: String? = "device",
        cash: Double = 50.0,
        float: Double = -50.0,
        at: String = "2026-09-21T10:15:00+00:00"
    ) = RemoteTransaction(
        id = id,
        deviceId = deviceId,
        clientTransactionId = clientId,
        network = "MTN",
        type = 1,
        amount = 50.0,
        cashDelta = cash,
        floatDelta = float,
        customerPhoneNumber = "0244123456",
        transactionAt = at,
        source = source
    )

    @Test
    fun `a row this handset already holds is left out, by either identity`() {
        val rows = listOf(remote("a"), remote("b"), remote("c", clientId = null))
        val kept = RemoteActivity.notHeldHere(rows, held = setOf("CTX-a", "c"))
        assertThat(kept.map { it.id }).containsExactly("b")
    }

    @Test
    fun `totals are in pesewas and keep direction`() {
        val (cash, float) = RemoteActivity.totalsMinor(
            listOf(remote("a", cash = 50.10, float = -50.10), remote("b", cash = -20.0, float = 20.0))
        )
        assertThat(cash).isEqualTo(3010)
        assertThat(float).isEqualTo(-3010)
    }

    @Test
    fun `a keypad row says where it came from and is already sent`() {
        val item = RemoteActivity.toActivityItem(remote("a"))!!
        assertThat(item.label).isEqualTo("Cash out")
        assertThat(item.recordedElsewhere).isEqualTo("keypad phone")
        assertThat(item.delivery).isEqualTo(ActivityDelivery.SENT)
        assertThat(item.amountMinor).isEqualTo(5000)
        assertThat(item.customerPhone).isEqualTo("0244123456")
    }

    @Test
    fun `origin distinguishes the portal from another phone`() {
        assertThat(RemoteActivity.toActivityItem(remote("a", source = 0, deviceId = null))!!.recordedElsewhere)
            .isEqualTo("portal")
        assertThat(RemoteActivity.toActivityItem(remote("a", source = 1))!!.recordedElsewhere)
            .isEqualTo("other phone")
    }

    @Test
    fun `both time forms the server may write are read, and nonsense is dropped`() {
        val offset = RemoteActivity.toActivityItem(remote("a", at = "2026-09-21T10:15:00+00:00"))!!
        val zulu = RemoteActivity.toActivityItem(remote("a", at = "2026-09-21T10:15:00Z"))!!
        assertThat(offset.atUtcMillis).isEqualTo(zulu.atUtcMillis)
        assertThat(RemoteActivity.toActivityItem(remote("a", at = "yesterday"))).isNull()
    }

    @Test
    fun `merged list is newest first`() {
        fun local(at: Long) = ActivityItem("L$at", "Cash in", "MTN", 100, 100, at, false, ActivityDelivery.SENT)
        val elsewhere = RemoteActivity.toActivityItem(remote("a", at = "2026-09-21T10:15:00Z"))!!
        val merged = RemoteActivity.merge(listOf(local(elsewhere.atUtcMillis + 1), local(elsewhere.atUtcMillis - 1)), listOf(elsewhere))
        assertThat(merged.map { it.recordedElsewhere }).containsExactly(null, "keypad phone", null).inOrder()
    }
}
