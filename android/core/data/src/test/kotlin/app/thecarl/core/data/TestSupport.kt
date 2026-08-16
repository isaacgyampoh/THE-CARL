package app.thecarl.core.data

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import app.thecarl.core.data.database.CarlDatabase
import app.thecarl.core.domain.security.CryptoBox

/**
 * In-memory database for host tests.
 *
 * <p>Uses the framework's unencrypted helper. SQLCipher ships native libraries for Android
 * ABIs and cannot load under a JVM runner, so schema, DAO and repository behaviour are
 * verified here while the encryption wiring is covered by instrumentation tests.</p>
 */
internal fun createTestDatabase(): CarlDatabase =
    Room.inMemoryDatabaseBuilder(
        ApplicationProvider.getApplicationContext<Context>(),
        CarlDatabase::class.java
    ).allowMainThreadQueries().build()

/**
 * Reversible transform standing in for Keystore-backed encryption.
 *
 * <p>Deliberately <b>not</b> a no-op: it verifies that the store round-trips through the
 * crypto boundary and that what lands on disk is not the plaintext. It is not encryption and
 * exists only so the storage logic is testable without a device.</p>
 */
internal class FakeCryptoBox(
    private val corruptOnDecrypt: Boolean = false
) : CryptoBox {
    private val mask: Byte = 0x5A

    override fun encrypt(plaintext: ByteArray): ByteArray =
        ByteArray(plaintext.size) { (plaintext[it].toInt() xor mask.toInt()).toByte() }

    override fun decrypt(blob: ByteArray): ByteArray? {
        if (corruptOnDecrypt) return null
        return ByteArray(blob.size) { (blob[it].toInt() xor mask.toInt()).toByte() }
    }
}
