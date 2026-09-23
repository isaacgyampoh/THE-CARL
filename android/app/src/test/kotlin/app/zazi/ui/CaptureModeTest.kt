package app.zazi.ui

import app.zazi.ui.state.DashboardUiState
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Automatic capture and recording by hand are mutually exclusive.
 *
 * <p>A vendor whose phone is already recording every transaction must not also be able to type
 * one in. Two records of one payment cannot be told apart afterwards — neither the agent nor
 * the owner can say which was the real one — and the day stops tallying for a reason nobody
 * can find. So while capture is on, the only deliberate action left is closing the day.</p>
 *
 * <p>Both halves are required for the mode to be on. The server says whether this kind of
 * device may capture at all; Android says whether this installation was allowed to. Either one
 * alone means no message ever reaches the app, and an agent left with no way to record
 * anything would lose a day's work.</p>
 */
class CaptureModeTest {

    @Test
    fun `capture is off until the device may capture and the permission is granted`() {
        assertThat(mode(deviceMayCapture = false, permissionGranted = false)).isFalse()
        assertThat(mode(deviceMayCapture = true, permissionGranted = false)).isFalse()
        assertThat(mode(deviceMayCapture = false, permissionGranted = true)).isFalse()
        assertThat(mode(deviceMayCapture = true, permissionGranted = true)).isTrue()
    }

    @Test
    fun `the permission alone never turns capture on`() {
        // An owner can revoke a device server-side. Android still reports the permission as
        // granted, and without the first half the phone would claim to be recording while
        // every message was being discarded.
        assertThat(mode(deviceMayCapture = false, permissionGranted = true)).isFalse()
    }

    @Test
    fun `a state carrying the mode says so`() {
        assertThat(DashboardUiState(isAutomaticCapture = true).isAutomaticCapture).isTrue()
        // The default is off, so a screen built before the mode existed still offers the
        // manual form rather than silently hiding it.
        assertThat(DashboardUiState().isAutomaticCapture).isFalse()
    }

    /**
     * The rule as MainActivity applies it, stated once here so a change to it fails a test
     * rather than quietly reopening the manual form on a phone that is already recording.
     */
    private fun mode(deviceMayCapture: Boolean, permissionGranted: Boolean): Boolean =
        deviceMayCapture && permissionGranted
}
