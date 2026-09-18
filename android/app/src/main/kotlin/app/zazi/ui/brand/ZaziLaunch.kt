package app.zazi.ui.brand

/**
 * Whether the brand introduction is still on screen, and what it is waiting for.
 *
 * <p>Two independent things have to finish before the user is let through: the animation, and
 * whatever the application was doing anyway — restoring the session. Tracking them separately
 * is what keeps the introduction honest. It never shortens real startup work, and it never
 * extends it: whichever finishes last decides.</p>
 */
data class LaunchState(
    /** The animation has played to the end. */
    val animationFinished: Boolean = false,
    /** Session restore has produced an answer, so there is a screen to show. */
    val sessionResolved: Boolean = false,
    /** The introduction is done and must never play again in this process. */
    val introCompleted: Boolean = false
)

/**
 * The rules for getting past the brand introduction.
 *
 * <p>Pure, and deliberately so. A launch sequence is the one part of an application that is
 * hardest to observe — it is over before anyone can attach to it, and a defect here shows up
 * as "it sometimes hangs on the logo", which is close to unreportable. Keeping the decision in
 * functions with no Compose, no clock and no Android in them means it can be asserted.</p>
 */
object BrandIntroGate {

    /**
     * Whether the introduction should be drawn.
     *
     * <p>Keyed only on completion, which is why this cannot loop: [introCompleted] is a
     * one-way door. Nothing sets it back to false, so a session that resolves twice, a retry
     * or a sign-out cannot bring the animation back mid-use.</p>
     */
    fun isVisible(state: LaunchState): Boolean = !state.introCompleted

    /**
     * Advances the state, completing the introduction once both halves are in.
     *
     * <p>Idempotent: calling it again after completion changes nothing, so a duplicate
     * callback — Compose can and does re-run effects — cannot restart anything.</p>
     */
    fun advance(state: LaunchState): LaunchState =
        if (!state.introCompleted && state.animationFinished && state.sessionResolved) {
            state.copy(introCompleted = true)
        } else {
            state
        }

    /** The animation has ended. */
    fun onAnimationFinished(state: LaunchState): LaunchState =
        advance(state.copy(animationFinished = true))

    /**
     * Session restore has settled.
     *
     * <p>Called with false while the answer is still [app.zazi.core.data.session.SessionState]
     * .Initialising. Passing false after it was true cannot un-resolve the launch, because
     * [advance] only ever moves forward — and once the introduction is complete the flag is
     * not consulted again at all.</p>
     */
    fun onSessionResolved(state: LaunchState, resolved: Boolean): LaunchState =
        if (state.introCompleted) state else advance(state.copy(sessionResolved = resolved))
}
