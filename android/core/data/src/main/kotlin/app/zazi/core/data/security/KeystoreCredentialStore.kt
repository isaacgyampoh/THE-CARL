package app.zazi.core.data.security

import android.content.SharedPreferences
import app.zazi.core.domain.security.CredentialStore
import app.zazi.core.domain.security.CryptoBox
import app.zazi.core.domain.security.StoredCredentials
import android.util.Base64
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * Credential storage encrypted by a [CryptoBox].
 *
 * <p><b>What is on disk.</b> Only a Base64 blob of AES-GCM ciphertext. The preference file
 * holds no token, no user id, and no readable field — an attacker with the file and without
 * the Keystore key gets nothing. This is why credentials are not stored in Room: the
 * database is for operational data, not secrets.</p>
 *
 * <p>Encryption is delegated rather than performed here, which keeps this logic — overwrite,
 * clear, corruption handling — testable on the JVM while the Keystore work stays in one
 * device-only class.</p>
 */
@Suppress("ApplySharedPref")
class KeystoreCredentialStore(
    private val preferences: SharedPreferences,
    private val cryptoBox: CryptoBox,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO
) : CredentialStore {

    override suspend fun save(credentials: StoredCredentials): Unit = withContext(ioDispatcher) {
        val plaintext = json.encodeToString(CredentialPayload.serializer(), credentials.toPayload())
        val blob = cryptoBox.encrypt(plaintext.toByteArray(Charsets.UTF_8))

        // commit(), not apply(): a credential write must be durable before the caller
        // proceeds. An async write lost to process death would silently sign the user out.
        preferences.edit()
            .putString(KEY_CREDENTIAL_BLOB, Base64.encodeToString(blob, Base64.NO_WRAP))
            .commit()
    }

    override suspend fun read(): StoredCredentials? = withContext(ioDispatcher) {
        val encoded = preferences.getString(KEY_CREDENTIAL_BLOB, null) ?: return@withContext null

        try {
            val blob = Base64.decode(encoded, Base64.NO_WRAP)
            val plaintext = cryptoBox.decrypt(blob) ?: return@withContext null

            json.decodeFromString(
                CredentialPayload.serializer(),
                plaintext.toString(Charsets.UTF_8)
            ).toCredentials()
        } catch (_: Exception) {
            // Corrupt Base64, a payload written by an older schema, or a failed
            // authentication tag. All resolve to "no usable credentials" — the user logs in
            // again, which is recoverable. Throwing here would be a crash loop.
            null
        }
    }

    override suspend fun clear(): Unit = withContext(ioDispatcher) {
        preferences.edit().remove(KEY_CREDENTIAL_BLOB).commit()
    }

    override suspend fun hasCredentials(): Boolean = withContext(ioDispatcher) {
        preferences.contains(KEY_CREDENTIAL_BLOB)
    }

    // The marker is not a secret and is deliberately not encrypted: it carries no token, no
    // identity and no permission. It is stored here rather than in a second component so
    // there remains exactly one place that knows whether this installation has a session.

    override suspend fun markDeviceRevoked(): Unit = withContext(ioDispatcher) {
        preferences.edit().putBoolean(KEY_DEVICE_REVOKED, true).commit()
    }

    override suspend fun wasDeviceRevoked(): Boolean = withContext(ioDispatcher) {
        preferences.getBoolean(KEY_DEVICE_REVOKED, false)
    }

    override suspend fun clearDeviceRevokedMark(): Unit = withContext(ioDispatcher) {
        preferences.edit().remove(KEY_DEVICE_REVOKED).commit()
    }

    /**
     * On-disk shape, kept separate from the domain type so a domain refactor cannot silently
     * change the persisted format and orphan every installed device's credentials.
     */
    @Serializable
    private data class CredentialPayload(
        val accessToken: String,
        val refreshToken: String,
        val accessTokenExpiresAtUtcMillis: Long,
        val userId: String,
        val organizationId: String,
        val branchId: String? = null,
        val deviceId: String? = null,
        val deviceInstallationId: String
    )

    private fun StoredCredentials.toPayload() = CredentialPayload(
        accessToken, refreshToken, accessTokenExpiresAtUtcMillis,
        userId, organizationId, branchId, deviceId, deviceInstallationId
    )

    private fun CredentialPayload.toCredentials() = StoredCredentials(
        accessToken, refreshToken, accessTokenExpiresAtUtcMillis,
        userId, organizationId, branchId, deviceId, deviceInstallationId
    )

    companion object {
        /** Preference file name. Contains ciphertext only. */
        const val PREFERENCES_NAME = "thecarl.credentials"

        private const val KEY_CREDENTIAL_BLOB = "credentials.v1"
        private const val KEY_DEVICE_REVOKED = "device.revoked.v1"

        private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
    }
}
