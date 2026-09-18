package app.zazi.core.data

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import app.zazi.core.data.security.KeystoreDatabaseKeyProvider
import app.zazi.core.domain.security.CryptoBox
import com.google.common.truth.Truth.assertThat
import java.io.File
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/**
 * What happens when the local database key can no longer be unwrapped.
 *
 * <p>Keystore invalidation and device restores are rare, and this is what makes them
 * survivable rather than terminal. Before, a new key was generated with the old database
 * still in place, so SQLCipher could not open it on any subsequent launch — an agent's
 * unsynced transactions unreadable on disk, no message, and no way back except clearing app
 * data. The code comment claimed the loss was surfaced; nothing surfaced it.</p>
 */
@RunWith(RobolectricTestRunner::class)
class DatabaseKeyLossTest {

    private lateinit var preferences: android.content.SharedPreferences
    private lateinit var databaseFile: File

    /** Unwraps normally until [failing] is set, standing in for an invalidated Keystore. */
    private class SwitchableCryptoBox : CryptoBox {
        var failing = false
        override fun encrypt(plaintext: ByteArray): ByteArray = plaintext.reversedArray()
        override fun decrypt(ciphertext: ByteArray): ByteArray {
            if (failing) throw IllegalStateException("Keystore key is no longer usable.")
            return ciphertext.reversedArray()
        }
    }

    private val cryptoBox = SwitchableCryptoBox()

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        preferences = context.getSharedPreferences("key-loss-test", Context.MODE_PRIVATE)
        preferences.edit().clear().commit()

        databaseFile = File(context.cacheDir, "zazi.db").apply {
            parentFile?.mkdirs()
            writeText("encrypted-bytes")
        }
        File(databaseFile.parentFile, "zazi.db-wal").writeText("journal")
    }

    private fun provider(now: () -> Long = { 1_700_000_000_000L }) =
        KeystoreDatabaseKeyProvider(preferences, cryptoBox, databaseFile, now)

    @Test
    fun `the same key comes back while the keystore still works`() {
        val first = provider().databaseKey()
        val second = provider().databaseKey()

        assertThat(second).isEqualTo(first)
        // Nothing was disturbed, so nothing to report.
        assertThat(provider().takeOrphanedDatabaseNotice()).isNull()
        assertThat(databaseFile.exists()).isTrue()
    }

    @Test
    fun `key loss yields a new key rather than a broken one`() {
        val original = provider().databaseKey()
        cryptoBox.failing = true

        val replacement = provider().databaseKey()

        assertThat(replacement).isNotEqualTo(original)
    }

    @Test
    fun `the unopenable database is moved aside so the app can start`() {
        provider().databaseKey()
        cryptoBox.failing = true

        provider().databaseKey()

        // This is what stops the crash: a fresh key and the old file still in place means
        // SQLCipher fails to open the database on every launch, for good.
        assertThat(databaseFile.exists()).isFalse()
        assertThat(File(databaseFile.parentFile, "zazi.db.orphaned-1700000000000").exists()).isTrue()
    }

    @Test
    fun `the journal moves with it`() {
        provider().databaseKey()
        cryptoBox.failing = true

        provider().databaseKey()

        // Leaving a -wal beside a fresh database would have SQLite try to replay one key's
        // journal into another key's file.
        assertThat(File(databaseFile.parentFile, "zazi.db-wal").exists()).isFalse()
        assertThat(File(databaseFile.parentFile, "zazi.db-wal.orphaned-1700000000000").exists()).isTrue()
    }

    @Test
    fun `the orphaned data is kept, not deleted`() {
        provider().databaseKey()
        cryptoBox.failing = true
        provider().databaseKey()

        // It holds transactions that never reached the server. Nothing can read it now, but
        // destroying the only copy is not a decision to take quietly on someone's behalf.
        val orphan = File(databaseFile.parentFile, "zazi.db.orphaned-1700000000000")
        assertThat(orphan.readText()).isEqualTo("encrypted-bytes")
    }

    @Test
    fun `the loss is reported once and then stops`() {
        provider().databaseKey()
        cryptoBox.failing = true
        provider().databaseKey()

        assertThat(provider().takeOrphanedDatabaseNotice()).isEqualTo(1_700_000_000_000L)
        // Told once. Repeating it on every launch would train an agent to dismiss it.
        assertThat(provider().takeOrphanedDatabaseNotice()).isNull()
    }

    @Test
    fun `a first run is not mistaken for key loss`() {
        // No wrapped key was ever stored, so there is nothing to have lost — and no reason to
        // move aside a database this installation has not written yet.
        cryptoBox.failing = true

        provider().databaseKey()

        assertThat(provider().takeOrphanedDatabaseNotice()).isNull()
        assertThat(databaseFile.exists()).isTrue()
    }
}
