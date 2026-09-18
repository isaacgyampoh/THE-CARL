package app.zazi.ui

import app.zazi.ui.state.ActivityDelivery
import app.zazi.ui.state.ActivityFilter
import app.zazi.ui.state.TransactionDetail
import com.google.common.truth.Truth.assertThat
import org.junit.Test

private const val DAY = 24 * 60 * 60 * 1000L

/** Window boundaries, and what an agent is told to do about a stopped transaction. */
class ActivityFilterTest {

    // 18 Sep 2026, 08:30 UTC — mid-morning, so a boundary error shows up as a whole day.
    private val now = 1789000000000L / DAY * DAY + 8 * 60 * 60 * 1000L + 30 * 60 * 1000L
    private val dayStart = now / DAY * DAY

    @Test
    fun `today starts at midnight and excludes tomorrow`() {
        val window = ActivityFilter.TODAY.windowUtcMillis(now)

        assertThat(window.first).isEqualTo(dayStart)
        // Half-open: the last included millisecond is one before midnight, so a transaction
        // stamped exactly at midnight belongs to the next day and not to both.
        assertThat(window.last).isEqualTo(dayStart + DAY - 1)
        assertThat(now in window).isTrue()
    }

    @Test
    fun `yesterday is the day before, and does not overlap today`() {
        val yesterday = ActivityFilter.YESTERDAY.windowUtcMillis(now)
        val today = ActivityFilter.TODAY.windowUtcMillis(now)

        assertThat(yesterday.first).isEqualTo(dayStart - DAY)
        assertThat(yesterday.last).isEqualTo(dayStart - 1)
        // The boundary is the thing worth asserting: an overlap would double-count a
        // transaction across two views of the same history.
        assertThat(yesterday.last + 1).isEqualTo(today.first)
        assertThat(now in yesterday).isFalse()
    }

    @Test
    fun `last seven days includes today`() {
        val window = ActivityFilter.LAST_SEVEN_DAYS.windowUtcMillis(now)

        // Six days back plus today. A window that excluded what just happened would be the
        // most confusing possible reading of "last 7 days".
        assertThat(window.first).isEqualTo(dayStart - 6 * DAY)
        assertThat(now in window).isTrue()
        assertThat(dayStart - 6 * DAY in window).isTrue()
        assertThat(dayStart - 7 * DAY in window).isFalse()
    }

    @Test
    fun `a stopped transaction offers a retry, a rejected one explains instead`() {
        val deadLettered = detail(ActivityDelivery.NEEDS_REVIEW, retryable = true)
        val conflicted = detail(ActivityDelivery.NEEDS_REVIEW, retryable = false)

        assertThat(deadLettered.guidance).contains("try again")

        // A conflict must never suggest retrying. Re-sending the same payload would be
        // refused identically and leave a second audit entry, and the agent would be left
        // pressing a button that cannot work.
        assertThat(conflicted.guidance).doesNotContain("try again")
        assertThat(conflicted.guidance).contains("manager")
    }

    @Test
    fun `the attempt count reads naturally at one and at many`() {
        val once = detail(ActivityDelivery.NEEDS_REVIEW, retryable = true, attempts = 1)
        val several = detail(ActivityDelivery.NEEDS_REVIEW, retryable = true, attempts = 10)

        // "after 1 attempts" reads as a bug to the person holding the phone, which
        // undermines the rest of what this screen is telling them.
        assertThat(once.guidance).contains("after 1 attempt.")
        assertThat(several.guidance).contains("after 10 attempts.")
    }

    @Test
    fun `a delivered or in-flight transaction asks nothing of the agent`() {
        assertThat(detail(ActivityDelivery.SENT, retryable = false).guidance).isNull()
        assertThat(detail(ActivityDelivery.SENDING, retryable = false).guidance).isNull()
    }

    private fun detail(
        delivery: ActivityDelivery,
        retryable: Boolean,
        attempts: Int = 5
    ) = TransactionDetail(
        clientTransactionId = "client-transaction-0001",
        label = "Cash in",
        provider = "MTN",
        amountMinor = 25_000,
        cashDeltaMinor = 25_000,
        atUtcMillis = now,
        customerPhone = null,
        reference = null,
        capturedAutomatically = false,
        delivery = delivery,
        attemptCount = attempts,
        lastReasonCode = null,
        isRetryable = retryable
    )
}
