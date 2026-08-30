package app.zazi.core.data

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import app.zazi.core.data.security.KeystoreCredentialStore
import app.zazi.core.domain.security.StoredCredentials
import com.google.common.truth.Truth.assertThat
import kotlinx.coroutines.test.runTest
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/**
 * Credential storage behaviour.
 *
 * <p>The Keystore crypto itself needs a device — Robolectric does not implement the
 * AndroidKeyStore provider — so these tests drive the storage logic through a fake
 * [app.zazi.core.domain.security.CryptoBox]. What they prove is that the store round-trips
 * correctly, overwrites cleanly, clears on logout, survives recreation, and never writes a
 * readable token. The real crypto is covered by instrumentation tests.</p>
 */
@RunWith(RobolectricTestRunner::class)
class CredentialStoreTest {

    private lateinit var context: Context

    private val credentials = StoredCredentials(
        accessToken = "access-token-value",
        refreshToken = "refresh-token-value",
        accessTokenExpiresAtUtcMillis = System.currentTimeMillis() + 3_600_000,
        userId = "11111111-1111-1111-1111-111111111111",
        organizationId = "22222222-2222-2222-2222-222222222222",
        branchId = "33333333-3333-3333-3333-333333333333",
        deviceId = "44444444-4444-4444-4444-444444444444",
        deviceInstallationId = "installation-abc"
    )

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        preferences().edit().clear().commit()
    }

    @Test
    fun `save then read round-trips every field`() = runTest {
        val store = newStore()
        store.save(credentials)

        assertThat(store.read()).isEqualTo(credentials)
    }

    @Test
    fun `read returns null when nothing is stored`() = runTest {
        assertThat(newStore().read()).isNull()
        assertThat(newStore().hasCredentials()).isFalse()
    }

    @Test
    fun `save overwrites previous credentials`() = runTest {
        val store = newStore()
        store.save(credentials)

        val rotated = credentials.copy(
            accessToken = "rotated-access",
            refreshToken = "rotated-refresh"
        )
        store.save(rotated)

        // Token rotation must leave exactly one credential set, not accumulate.
        assertThat(store.read()).isEqualTo(rotated)
    }

    @Test
    fun `clear removes credentials on logout`() = runTest {
        val store = newStore()
        store.save(credentials)

        store.clear()

        assertThat(store.read()).isNull()
        assertThat(store.hasCredentials()).isFalse()
    }

    @Test
    fun `credentials survive a recreated store instance`() = runTest {
        newStore().save(credentials)

        // A new instance stands in for an app restart: the same preference file is read by a
        // freshly constructed store.
        assertThat(newStore().read()).isEqualTo(credentials)
    }

    @Test
    fun `no plaintext token is written to storage`() = runTest {
        newStore().save(credentials)

        val onDisk = preferences().all.values.joinToString(" ")

        // The single most important assertion here. Anyone reading the preference file must
        // find nothing usable.
        assertThat(onDisk).doesNotContain("access-token-value")
        assertThat(onDisk).doesNotContain("refresh-token-value")
        assertThat(onDisk).doesNotContain("installation-abc")
    }

    @Test
    fun `corrupted ciphertext degrades to no credentials rather than crashing`() = runTest {
        newStore().save(credentials)

        // Simulates tampered storage or a Keystore key invalidated by a lock-screen change.
        val store = KeystoreCredentialStore(preferences(), FakeCryptoBox(corruptOnDecrypt = true))

        // Must be recoverable — the user logs in again. Throwing would be a crash loop with
        // no way out short of reinstalling.
        assertThat(store.read()).isNull()
    }

    @Test
    fun `garbage in the preference value is handled safely`() = runTest {
        preferences().edit().putString("credentials.v1", "!!!not-base64!!!").commit()

        assertThat(newStore().read()).isNull()
    }

    @Test
    fun `an expiring access token is reported as needing refresh`() {
        val expiringSoon = credentials.copy(
            accessTokenExpiresAtUtcMillis = System.currentTimeMillis() + 10_000
        )

        // The skew stops a request being started with a token that expires mid-flight.
        assertThat(expiringSoon.accessTokenNeedsRefresh()).isTrue()
        assertThat(credentials.accessTokenNeedsRefresh()).isFalse()
    }

    private fun newStore() = KeystoreCredentialStore(preferences(), FakeCryptoBox())

    private fun preferences() =
        context.getSharedPreferences(KeystoreCredentialStore.PREFERENCES_NAME, Context.MODE_PRIVATE)
}
