package app.zazi.ui.state

import app.zazi.core.data.session.AuthenticatedUser
import app.zazi.core.data.session.DeviceContext
import app.zazi.core.data.session.SessionState
import app.zazi.core.domain.model.DeviceType
import app.zazi.core.domain.model.PlatformCapability
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * The way in, asserted rather than run.
 *
 * <p>Every one of these was previously observable only by installing the app and looking,
 * which is how a fully written confirmation screen shipped unreachable.</p>
 */
class EntryNavigatorTest {

    private fun destination(
        session: SessionState,
        unacknowledgedActivation: Boolean = false,
        showLogin: Boolean = false
    ) = EntryNavigator.destinationFor(session, unacknowledgedActivation, showLogin)

    @Test
    fun `a fresh install opens on activation, not a login form`() {
        // The point of the whole change: a worker on a business phone should never be asked
        // for an account they were never given.
        assertThat(destination(SessionState.SignedOut)).isEqualTo(EntryDestination.ACTIVATION)
    }

    @Test
    fun `startup shows loading before anything is decided`() {
        assertThat(destination(SessionState.Initialising)).isEqualTo(EntryDestination.LOADING)
    }

    @Test
    fun `asking for the email form reaches it`() {
        assertThat(destination(SessionState.SignedOut, showLogin = true))
            .isEqualTo(EntryDestination.LOGIN)
    }

    @Test
    fun `the confirmation screen is reachable after activation`() {
        // The regression this file exists for. Activation issues a real session, so the state
        // is already Active when the server replies. Deciding on the session alone sent the
        // worker straight to the workspace and the confirmation was never drawn.
        assertThat(destination(active(), unacknowledgedActivation = true))
            .isEqualTo(EntryDestination.ACTIVATION_CONFIRMED)
    }

    @Test
    fun `dismissing the confirmation reaches the workspace`() {
        assertThat(destination(active(), unacknowledgedActivation = false))
            .isEqualTo(EntryDestination.WORKSPACE)
    }

    @Test
    fun `an ordinary sign-in never shows the activation confirmation`() {
        // Nothing was activated, so there is nothing to confirm.
        assertThat(destination(active())).isEqualTo(EntryDestination.WORKSPACE)
    }

    @Test
    fun `a stale login flag cannot pull a working handset back to a login form`() {
        // showLogin survives configuration change. If it were consulted regardless of session
        // state, a worker mid-shift would be shown a form asking for credentials they do not
        // have.
        assertThat(destination(active(), showLogin = true)).isEqualTo(EntryDestination.WORKSPACE)
    }

    @Test
    fun `revocation is shown even when a login was requested`() {
        // Being cut off outranks a flag the worker set a moment earlier.
        assertThat(destination(SessionState.DeviceRevoked(queuedWorkCount = 3), showLogin = true))
            .isEqualTo(EntryDestination.REVOKED)
    }

    @Test
    fun `the legacy enrolment path still works`() {
        // An account holder who signed in on an unregistered handset. Untouched by activation
        // and must stay that way.
        assertThat(destination(SessionState.NeedsEnrolment(user())))
            .isEqualTo(EntryDestination.ENROLMENT)
    }

    @Test
    fun `the confirmation cannot appear before a session exists`() {
        // If it could, a failed activation that left the flag set would show someone a
        // "you're connected" screen while connected to nothing.
        assertThat(destination(SessionState.SignedOut, unacknowledgedActivation = true))
            .isEqualTo(EntryDestination.ACTIVATION)
    }

    @Test
    fun `every session state resolves to exactly one destination`() {
        // No state may fall through to the workspace by accident: reaching it means being
        // authenticated, and an unhandled state defaulting there would be an access hole.
        val states = listOf(
            SessionState.Initialising to EntryDestination.LOADING,
            SessionState.SignedOut to EntryDestination.ACTIVATION,
            SessionState.NeedsEnrolment(user()) to EntryDestination.ENROLMENT,
            SessionState.DeviceRevoked(queuedWorkCount = 0) to EntryDestination.REVOKED,
            active() to EntryDestination.WORKSPACE
        )

        states.forEach { (session, expected) ->
            assertThat(destination(session)).isEqualTo(expected)
        }
    }

    private fun user() = AuthenticatedUser(
        userId = "user-1",
        organizationId = "org-1",
        branchId = "branch-1",
        email = "",
        fullName = "Ama Mensah",
        roles = emptyList()
    )

    private fun active() = SessionState.Active(
        user = user(),
        device = DeviceContext(
            deviceId = "device-1",
            deviceIdentifier = "installation-1",
            organizationId = "org-1",
            branchId = "branch-1",
            deviceType = DeviceType.ANDROID_PHONE,
            capabilities = listOf(PlatformCapability.MANUAL_TRANSACTION_CAPTURE),
            isRevoked = false,
            maxBatchSize = 50
        )
    )
}
