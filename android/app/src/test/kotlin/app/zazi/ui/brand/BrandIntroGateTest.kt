package app.zazi.ui.brand

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * The launch sequence, asserted rather than watched.
 *
 * <p>A defect here is close to unreportable in the field — it reads as "it sometimes sticks on
 * the logo" — and it is over before anyone can attach a debugger. So the rules are pure and
 * the pathological cases are named: the introduction must always end, must never come back,
 * and must never be the thing the user is waiting on.</p>
 */
class BrandIntroGateTest {

    @Test
    fun `is visible at the very start`() {
        assertThat(BrandIntroGate.isVisible(LaunchState())).isTrue()
    }

    @Test
    fun `waits for the session even when the animation has finished`() {
        val state = BrandIntroGate.onAnimationFinished(LaunchState())

        assertThat(state.introCompleted).isFalse()
        assertThat(BrandIntroGate.isVisible(state)).isTrue()
    }

    @Test
    fun `waits for the animation even when the session resolved first`() {
        // The common case on a warm start: restore finishes almost immediately. The brand
        // still gets its moment, because the alternative is a flash the user cannot read.
        val state = BrandIntroGate.onSessionResolved(LaunchState(), resolved = true)

        assertThat(state.introCompleted).isFalse()
        assertThat(BrandIntroGate.isVisible(state)).isTrue()
    }

    @Test
    fun `completes when the animation finishes last`() {
        var state = BrandIntroGate.onSessionResolved(LaunchState(), resolved = true)
        state = BrandIntroGate.onAnimationFinished(state)

        assertThat(BrandIntroGate.isVisible(state)).isFalse()
    }

    @Test
    fun `completes when the session resolves last`() {
        // The cold-start case: a slow restore outlasts the animation. Order must not matter.
        var state = BrandIntroGate.onAnimationFinished(LaunchState())
        state = BrandIntroGate.onSessionResolved(state, resolved = true)

        assertThat(BrandIntroGate.isVisible(state)).isFalse()
    }

    @Test
    fun `an unresolved session does not complete the launch`() {
        var state = BrandIntroGate.onAnimationFinished(LaunchState())
        state = BrandIntroGate.onSessionResolved(state, resolved = false)

        assertThat(BrandIntroGate.isVisible(state)).isTrue()
    }

    @Test
    fun `never replays once complete`() {
        // Signing out republishes the session state. Without the one-way door that would put
        // a brand animation in front of somebody mid-shift, which is the single worst place
        // for it.
        var state = BrandIntroGate.onAnimationFinished(LaunchState())
        state = BrandIntroGate.onSessionResolved(state, resolved = true)
        val completed = state

        state = BrandIntroGate.onSessionResolved(state, resolved = false)
        state = BrandIntroGate.onSessionResolved(state, resolved = true)
        state = BrandIntroGate.onAnimationFinished(state)

        assertThat(state).isEqualTo(completed)
        assertThat(BrandIntroGate.isVisible(state)).isFalse()
    }

    @Test
    fun `repeated callbacks are idempotent`() {
        // Compose can re-run an effect. Advancing twice must not undo or double anything.
        var state = LaunchState()
        repeat(5) { state = BrandIntroGate.onAnimationFinished(state) }
        repeat(5) { state = BrandIntroGate.onSessionResolved(state, resolved = true) }

        assertThat(state.introCompleted).isTrue()
        assertThat(BrandIntroGate.advance(state)).isEqualTo(state)
    }

    @Test
    fun `a restored completed state does not show the introduction again`() {
        // What a configuration change hands back mid-session. Rotating the phone on the
        // dashboard must not put the launch animation back on screen.
        val restored = LaunchState(
            animationFinished = true,
            sessionResolved = true,
            introCompleted = true
        )

        assertThat(BrandIntroGate.isVisible(restored)).isFalse()
    }

    @Test
    fun `a restored mid-launch state resumes rather than restarts`() {
        // Rotated while still waiting on the session: the animation flag survives, so when
        // the session lands the launch completes immediately instead of demanding a second
        // viewing.
        val restored = LaunchState(animationFinished = true, sessionResolved = false)

        val state = BrandIntroGate.onSessionResolved(restored, resolved = true)

        assertThat(BrandIntroGate.isVisible(state)).isFalse()
    }
}
