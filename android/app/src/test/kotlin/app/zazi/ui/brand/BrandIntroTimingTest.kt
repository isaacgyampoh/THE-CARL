package app.zazi.ui.brand

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * The introduction's length is a product decision, so it is pinned by a test.
 *
 * <p>Long enough to be read, short enough that nobody recording a transaction resents it.
 * Without this, a later tweak to one phase drifts the total past the point where it stops
 * being brand and starts being delay, and nothing would catch it.</p>
 */
class BrandIntroTimingTest {

    @Test
    fun `runs within the intended window`() {
        val total = BrandIntroTiming.totalMillis(reducedMotion = false)

        assertThat(total).isAtLeast(800)
        assertThat(total).isAtMost(1500)
    }

    @Test
    fun `reduced motion is shorter, never longer`() {
        val reduced = BrandIntroTiming.totalMillis(reducedMotion = true)
        val full = BrandIntroTiming.totalMillis(reducedMotion = false)

        assertThat(reduced).isLessThan(full)
    }

    @Test
    fun `reduced motion still shows the brand`() {
        // Collapsing to zero would mean somebody who asked for less movement never sees the
        // product they are signing in to. Less movement is not no brand.
        assertThat(BrandIntroTiming.totalMillis(reducedMotion = true)).isGreaterThan(0)
    }

    @Test
    fun `the exit is quick enough to read as one transition`() {
        assertThat(BrandIntroTiming.EXIT_MILLIS).isAtMost(300)
    }

    @Test
    fun `every letter of the wordmark is accounted for`() {
        assertThat(BrandIntroTiming.LETTERS).isEqualTo("Zazi".length)
    }
}
