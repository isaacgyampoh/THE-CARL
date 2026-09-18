package app.zazi.core.data.security

import android.content.SharedPreferences
import android.util.Base64
import app.zazi.core.domain.security.CryptoBox
import app.zazi.core.domain.security.DatabaseKeyProvider
import java.io.File
import java.security.SecureRandom

/**
 * Supplies the local database passphrase, generated once per installation.
 *
 * <p><b>The key is never in source.</b> It is 32 bytes from a cryptographic RNG, created on
 * first launch, wrapped by the Keystore-backed [CryptoBox], and stored only as ciphertext.
 * A hardcoded or derived-from-constant key would make every installation openable with one
 * secret extracted from the APK.</p>
 *
 * <p>If the wrapped key cannot be decrypted — Keystore invalidated, a device restored from
 * backup, storage tampered with — a fresh key is generated. The existing database then
 * cannot be opened, which is the correct outcome: the alternative is a database whose
 * contents cannot be trusted.</p>
 *
 * <p><b>What that costs, and what is done about it.</b> This comment used to claim unsynced
 * work was "surfaced rather than silently discarded", and nothing surfaced it. Worse than
 * silent: a new key with the old file still in place means SQLCipher cannot open the
 * database at all, so the app failed on first use with an agent's unsynced transactions
 * sitting unreadable on disk and no way back except clearing app data.</p>
 *
 * <p>So the orphaned file is now moved aside — which is also what lets the app start again —
 * and the fact is recorded durably. [takeOrphanedDatabaseNotice] reports it exactly once, so
 * the agent can be told their queued work could not be recovered instead of discovering a
 * day's captures missing. The file is kept rather than deleted: it is the only remaining
 * copy of that work, and deleting evidence to tidy up is not this component's call.</p>
 */
// commit(), not apply(): the wrapped database key must be durable before it is used to open
// the database. An async write lost to process death would orphan an entire encrypted
// database, taking the agent's unsynced outbox with it.
@Suppress("ApplySharedPref")
class KeystoreDatabaseKeyProvider(
    private val preferences: SharedPreferences,
    private val cryptoBox: CryptoBox,
    /**
     * The encrypted database, so an unopenable one can be moved aside. Null only in tests
     * that are not exercising key loss.
     */
    private val databaseFile: File? = null,
    private val now: () -> Long = System::currentTimeMillis
) : DatabaseKeyProvider {

    override fun databaseKey(): ByteArray {
        val encoded = preferences.getString(KEY_WRAPPED_DATABASE_KEY, null)

        if (encoded != null) {
            runCatching {
                cryptoBox.decrypt(Base64.decode(encoded, Base64.NO_WRAP))
            }.getOrNull()?.let { return it }

            // A wrapped key was stored and can no longer be unwrapped. That is key loss, not
            // a first run, and the database on disk belongs to a key nobody has any more.
            orphanUnopenableDatabase()
        }

        return generateAndStoreKey()
    }

    /**
     * Reports, once, that a database was orphaned by key loss.
     *
     * <p>Returns the time it happened and clears the notice, so the app can tell the agent
     * their queued work could not be recovered without repeating it on every launch.</p>
     */
    fun takeOrphanedDatabaseNotice(): Long? {
        val at = preferences.getLong(KEY_ORPHANED_AT, 0L)
        if (at == 0L) return null

        preferences.edit().remove(KEY_ORPHANED_AT).commit()
        return at
    }

    private fun orphanUnopenableDatabase() {
        val database = databaseFile ?: return
        if (!database.exists()) return

        // Moved rather than deleted. It holds transactions that never reached the server, and
        // although nothing can decrypt it now, destroying the only copy is not a decision to
        // make quietly on a user's behalf.
        //
        // The journal files go with it: leaving a -wal beside a fresh database would have
        // SQLite try to replay one key's journal into another's file.
        val stamp = now()
        listOf("", "-wal", "-shm").forEach { suffix ->
            val part = File(database.parentFile, database.name + suffix)
            if (part.exists()) {
                part.renameTo(File(database.parentFile, "${database.name}$suffix.orphaned-$stamp"))
            }
        }

        preferences.edit().putLong(KEY_ORPHANED_AT, stamp).commit()
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
        const val PREFERENCES_NAME = "zazi.database"

        private const val KEY_WRAPPED_DATABASE_KEY = "database.key.v1"
        private const val KEY_ORPHANED_AT = "database.orphaned.at.v1"
        private const val KEY_SIZE_BYTES = 32
    }
}
