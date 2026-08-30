package app.zazi.core.data.security

import android.content.SharedPreferences
import android.util.Base64
import app.zazi.core.domain.security.CryptoBox
import app.zazi.core.domain.security.DatabaseKeyProvider
import java.security.SecureRandom

/**
 * Supplies the local database passphrase, generated once per installation.
 *
 * <p><b>The key is never in source.</b> It is 32 bytes from a cryptographic RNG, created on
 * first launch, wrapped by the Keystore-backed [CryptoBox], and stored only as ciphertext.
 * A hardcoded or derived-from-constant key would make every installation openable with one
 * secret extracted from the APK.</p>
 *
 * <p>If the wrapped key cannot be decrypted — Keystore invalidated, storage tampered with —
 * a fresh key is generated. The existing database then cannot be opened, which is the
 * correct outcome: the alternative is a database whose contents cannot be trusted. Local
 * data is a projection plus an outbox, and the outbox is the only part whose loss matters,
 * which is why unsynced work is surfaced rather than silently discarded.</p>
 */
// commit(), not apply(): the wrapped database key must be durable before it is used to open
// the database. An async write lost to process death would orphan an entire encrypted
// database, taking the agent's unsynced outbox with it.
@Suppress("ApplySharedPref")
class KeystoreDatabaseKeyProvider(
    private val preferences: SharedPreferences,
    private val cryptoBox: CryptoBox
) : DatabaseKeyProvider {

    override fun databaseKey(): ByteArray {
        preferences.getString(KEY_WRAPPED_DATABASE_KEY, null)?.let { encoded ->
            runCatching {
                val wrapped = Base64.decode(encoded, Base64.NO_WRAP)
                cryptoBox.decrypt(wrapped)
            }.getOrNull()?.let { return it }
        }

        return generateAndStoreKey()
    }

    private fun generateAndStoreKey(): ByteArray {
        val key = ByteArray(KEY_SIZE_BYTES).also { SecureRandom().nextBytes(it) }
        val wrapped = cryptoBox.encrypt(key)

        preferences.edit()
            .putString(KEY_WRAPPED_DATABASE_KEY, Base64.encodeToString(wrapped, Base64.NO_WRAP))
            .commit()

        return key
    }

    companion object {
        const val PREFERENCES_NAME = "thecarl.database"

        private const val KEY_WRAPPED_DATABASE_KEY = "database.key.v1"
        private const val KEY_SIZE_BYTES = 32
    }
}
