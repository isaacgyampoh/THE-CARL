package app.zazi.ui.state

import app.zazi.core.data.session.SessionState

/**
 * Which screen the app shows before the worker reaches their workspace.
 *
 * <p>Named rather than inferred at the call site, so the decision can be asserted without a
 * device, an emulator or a running server.</p>
 */
enum class EntryDestination {
    /** Startup has not finished reading stored credentials. */
    LOADING,

    /** First run, or after a reset: the worker types the code their owner gave them. */
    ACTIVATION,

    /** An owner or manager with an existing account asked for the email form. */
    LOGIN,

    /** Just activated: who they are, which business, which branch. */
    ACTIVATION_CONFIRMED,

    /** Authenticated but this handset is not a registered device. */
    ENROLMENT,

    /** The server no longer trusts this handset. */
    REVOKED,

    /** Normal operation. */
    WORKSPACE
}

/**
 * The rules for what the app shows on the way in.
 *
 * <p>Pure, and for a reason that has already cost something. The confirmation screen after
 * activation was written, wired and unreachable: activation issues a real session, so the
 * session state had already moved to active by the time the server replied, and the branch
 * that drew it no longer applied. Nothing failed, nothing logged, and no test could have
 * noticed — the logic only existed inside a composable, where the only way to observe it was
 * to run the app and look.</p>
 *
 * <p>It lives here now so that class of defect fails a test instead.</p>
 */
object EntryNavigator {

    fun destinationFor(
        session: SessionState,
        /** True once activation has succeeded and the worker has not dismissed the result. */
        hasUnacknowledgedActivation: Boolean,
        /** True when the worker asked for the email form instead of the code field. */
        showLogin: Boolean
    ): EntryDestination = when {
        // Checked before the session state, not after. This is the whole fix: activation
        // produces a live session, so deciding on the session alone would skip straight past
        // the confirmation to the workspace.
        session is SessionState.Active && hasUnacknowledgedActivation ->
            EntryDestination.ACTIVATION_CONFIRMED

        session is SessionState.Initialising -> EntryDestination.LOADING

        // Only meaningful while signed out. A worker already in their workspace must not be
        // shown a login form because a flag was left set.
        session is SessionState.SignedOut && showLogin -> EntryDestination.LOGIN

        session is SessionState.SignedOut -> EntryDestination.ACTIVATION

        session is SessionState.NeedsEnrolment -> EntryDestination.ENROLMENT

        session is SessionState.DeviceRevoked -> EntryDestination.REVOKED

        else -> EntryDestination.WORKSPACE
    }
}
