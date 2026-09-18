package app.zazi.ui

import app.zazi.ui.state.ActivityFilter
import com.google.common.truth.Truth.assertThat
import org.junit.Test

private const val DAY = 24 * 60 * 60 * 1000L

/**
 * The exact instants where a day begins and ends.
 *
 * <p>Off-by-one here is not cosmetic: a transaction that falls in two windows is counted
 * twice by an agent checking their own day, and one that falls in neither disappears from
 * their record entirely. Every instant below is derived from a supplied now — nothing reads
 * the machine clock, so these assertions mean the same thing on any machine at any hour.</p>
 */
class ActivityBoundaryTest {

    /** Mid-afternoon, so a whole-day error cannot coincidentally still pass. */
    private val now = 1_789_000_000_000L / DAY * DAY + 15 * 60 * 60 * 1000L
    private val todayStart = now / DAY * DAY
    private val yesterdayStart = todayStart - DAY
    private val tomorrowStart = todayStart + DAY

    private val today get() = ActivityFilter.TODAY.windowUtcMillis(now)
    private val yesterday get() = ActivityFilter.YESTERDAY.windowUtcMillis(now)
    private val lastSeven get() = ActivityFilter.LAST_SEVEN_DAYS.windowUtcMillis(now)

    @Test
    fun `a transaction exactly at today's lower boundary is today, and not yesterday`() {
        assertThat(todayStart in today).isTrue()
        assertThat(todayStart in yesterday).isFalse()
    }

    @Test
    fun `a transaction one millisecond before today belongs to yesterday only`() {
        val justBefore = todayStart - 1

        assertThat(justBefore in today).isFalse()
        assertThat(justBefore in yesterday).isTrue()
    }

    @Test
    fun `a transaction exactly at yesterday's lower boundary is yesterday`() {
        assertThat(yesterdayStart in yesterday).isTrue()
        assertThat(yesterdayStart in today).isFalse()
        // One millisecond earlier is the day before, which neither window claims.
        assertThat(yesterdayStart - 1 in yesterday).isFalse()
        assertThat(yesterdayStart - 1 in today).isFalse()
    }

    @Test
    fun `the upper boundary belongs to tomorrow, not today`() {
        // Half-open: midnight opens the next day rather than closing this one twice.
        assertThat(tomorrowStart in today).isFalse()
        assertThat(tomorrowStart - 1 in today).isTrue()
    }

    @Test
    fun `today and yesterday are adjacent and never overlap`() {
        assertThat(yesterday.last + 1).isEqualTo(today.first)

        // Nothing at all can satisfy both, checked across the join rather than asserted of
        // the endpoints alone.
        val probes = listOf(
            yesterdayStart, yesterdayStart + 1, todayStart - 1,
            todayStart, todayStart + 1, now, tomorrowStart - 1
        )
        for (instant in probes) {
            assertThat(instant in today && instant in yesterday).isFalse()
        }
    }

    @Test
    fun `no transaction can appear in two date views at once`() {
        // Every millisecond across the three-day span, sampled per minute: each must belong
        // to at most one of Today and Yesterday. Last 7 days deliberately contains both and
        // is excluded from the exclusivity check — it is a span, not a day.
        var instant = yesterdayStart - DAY
        while (instant < tomorrowStart + DAY) {
            val inBoth = instant in today && instant in yesterday
            assertThat(inBoth).isFalse()
            instant += 60_000
        }
    }

    @Test
    fun `last seven days includes today and the six days before it, and stops there`() {
        assertThat(now in lastSeven).isTrue()
        assertThat(todayStart in lastSeven).isTrue()
        assertThat(tomorrowStart - 1 in lastSeven).isTrue()

        // Inclusive at the far end: the earliest included instant is the start of the sixth
        // day back, so the span is seven whole days counting today.
        assertThat(lastSeven.first).isEqualTo(todayStart - 6 * DAY)
        assertThat(todayStart - 6 * DAY in lastSeven).isTrue()
        assertThat(todayStart - 6 * DAY - 1 in lastSeven).isFalse()

        // Exclusive at the near end, like the others: tomorrow is not in it.
        assertThat(tomorrowStart in lastSeven).isFalse()
    }

    @Test
    fun `last seven days contains both day windows entirely`() {
        for (instant in listOf(yesterdayStart, todayStart, now, tomorrowStart - 1)) {
            assertThat(instant in lastSeven).isTrue()
        }
    }

    @Test
    fun `the windows do not move with the machine clock`() {
        // The same supplied instant must always produce the same window. A filter that read
        // the clock internally would silently reclassify a transaction between the list
        // being drawn and a detail being opened.
        assertThat(ActivityFilter.TODAY.windowUtcMillis(now)).isEqualTo(today)
        assertThat(ActivityFilter.TODAY.windowUtcMillis(now + 1)).isEqualTo(today)

        // And a now in a different day must produce a different window.
        val tomorrowWindow = ActivityFilter.TODAY.windowUtcMillis(now + DAY)
        assertThat(tomorrowWindow.first).isEqualTo(tomorrowStart)
        assertThat(tomorrowWindow).isNotEqualTo(today)
    }
}
