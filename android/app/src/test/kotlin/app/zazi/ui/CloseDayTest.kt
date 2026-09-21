package app.zazi.ui

import app.zazi.core.data.network.DayCloseResponse
import app.zazi.ui.state.CloseDay
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/** What the agent is told when they close the day. */
class CloseDayTest {

    @Test
    fun `counts are read to the pesewa and anything else is refused`() {
        assertThat(CloseDay.parseMinor("1,200.50")).isEqualTo(120050)
        assertThat(CloseDay.parseMinor("0")).isEqualTo(0)
        assertThat(CloseDay.parseMinor("-5")).isNull()
        assertThat(CloseDay.parseMinor("12.345")).isNull()
        assertThat(CloseDay.parseMinor("ten")).isNull()
        assertThat(CloseDay.parseMinor("")).isNull()
    }

    @Test
    fun `differences read as ok, short or over, with a cedi of tolerance`() {
        assertThat(CloseDay.difference(-0.50)).isEqualTo("OK")
        assertThat(CloseDay.difference(-50.0)).isEqualTo("Short ₵50.00")
        assertThat(CloseDay.difference(1200.0)).isEqualTo("Over ₵1,200.00")
        assertThat(CloseDay.difference(null)).isNull()
    }

    @Test
    fun `a first count is called a starting count, never a shortage`() {
        val first = DayCloseResponse(countedCash = 800.0, countedFloat = 2000.0, isBaseline = true, status = "Baseline")
        assertThat(CloseDay.headline(first)).contains("starting count")
        assertThat(CloseDay.headline(first)).doesNotContain("short")
    }
}
