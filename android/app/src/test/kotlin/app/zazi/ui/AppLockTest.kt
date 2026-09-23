package app.zazi.ui

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * When the app locks, and — more importantly — when it does not.
 *
 * <p>The failure that matters is not a missing lock. It is an agent shut out of their own
 * day's takings by a prompt they cannot answer, on a handset with no screen lock set, which is
 * common on the cheap phones this is built for. So locking is imposed only where the phone can
 * actually be unlocked, and every other answer leaves the app reachable.</p>
 */
class AppLockTest {

    @Test
    fun `a phone that can be unlocked starts locked`() {
        assertThat(AppLock.startsLocked(AppLock.Availability.READY)).isTrue()
    }

    @Test
    fun `a phone with no screen lock is never locked out of its own records`() {
        assertThat(AppLock.startsLocked(AppLock.Availability.NO_SCREEN_LOCK)).isFalse()
    }

    @Test
    fun `hardware that cannot answer a prompt does not produce one`() {
        assertThat(AppLock.startsLocked(AppLock.Availability.UNAVAILABLE)).isFalse()
    }

    @Test
    fun `exactly one state locks, so a new one added later fails this rather than stranding someone`() {
        val locking = AppLock.Availability.entries.filter { AppLock.startsLocked(it) }
        assertThat(locking).containsExactly(AppLock.Availability.READY)
    }
}
