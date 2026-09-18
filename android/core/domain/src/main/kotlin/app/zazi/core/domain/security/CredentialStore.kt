package app.zazi.core.domain.security

/**
 * Credentials held on a device between sessions.
 *
 * Deliberately a plain value type with no persistence concerns: the same shape is what a
 * future iOS Keychain implementation will store, so the contract is platform-neutral.
 */
data class StoredCredentials(
    val accessToken: String,
    val refreshToken: String,
    val accessTokenExpiresAtUtcMillis: Long,
    val userId: String,
    val organizationId: String,
    val branchId: String?,
    /** Server-assigned device id. An identifier, never a credential in its own right. */
    val deviceId: String?,
    /** Stable installation id used to derive ClientTransactionId device tags. */
    val deviceInstallationId: String,
    /**
     * True when this session began with an activation code rather than a password.
     *
     * <p>Not a permission — the server decides those — but the client genuinely needs it to
     * tell the truth. A worker activated by code has no account to sign back into, so an
     * unqualified "Sign out" offers them something that does not exist.</p>
     */
    val isActivationOnly: Boolean = false
) {
    /**
     * Whether the access token is past, or close to, expiry.
     *
     * The skew exists so a request is not started with a token that will expire mid-flight.
     */
    fun accessTokenNeedsRefresh(
        nowUtcMillis: Long = System.currentTimeMillis(),
        skewMillis: Long = 60_000L
    ): Boolean = nowUtcMillis + skewMillis >= accessTokenExpiresAtUtcMillis
}

/**
 * Secure storage for authentication material.
 *
 * <p>Implementations must never write a token in clear text. On Android that means
 * Keystore-backed encryption; on iOS it will mean the Keychain. The interface lives in the
 * pure-Kotlin domain module precisely so the second platform inherits the same contract
 * rather than inventing one.</p>
 *
 * <p>Tokens returned by [read] are decrypted and must never be logged, attached to analytics,
 * or included in crash reports.</p>
 */
interface CredentialStore {
    /** Replaces any existing credentials atomically. */
    suspend fun save(credentials: StoredCredentials)

    /** Null when nothing is stored, or when stored data could not be decrypted. */
    suspend fun read(): StoredCredentials?

    /** Removes stored credentials. Used on logout and on device revocation. */
    suspend fun clear()

    /** Cheap presence check that avoids decrypting. */
    suspend fun hasCredentials(): Boolean

    /**
     * Records that the server revoked this device, so the next start can say so.
     *
     * <p>Revocation clears credentials, which means a restart would otherwise find nothing
     * and show an ordinary sign-in screen — dropping the explanation at the moment the agent
     * most needs it, along with the assurance that their queued transactions still exist.</p>
     *
     * <p><b>This is a display marker, not an authorization decision.</b> It grants nothing.
     * The server remains the sole authority on what a device may do, and a device whose
     * marker were tampered with would still be refused by the backend on every call.</p>
     */
    suspend fun markDeviceRevoked()

    /** True when [markDeviceRevoked] was recorded and no successful sign-in has followed. */
    suspend fun wasDeviceRevoked(): Boolean

    /** Clears the marker. Called on sign-out and on a successful sign-in. */
    suspend fun clearDeviceRevokedMark()
}

/**
 * Authenticated encryption for locally stored secrets.
 *
 * <p>Separated from [CredentialStore] so the storage logic — overwrite, clear, corruption
 * handling — is testable on the JVM, while the platform-specific key handling stays behind
 * one narrow seam. On Android the real implementation is backed by the Android Keystore and
 * can only be exercised on a device or emulator.</p>
 */
interface CryptoBox {
    /** Encrypts, returning a self-describing blob that includes any nonce. */
    fun encrypt(plaintext: ByteArray): ByteArray

    /**
     * Decrypts a blob produced by [encrypt].
     *
     * Returns null when the blob is corrupt, truncated, or fails its authentication tag —
     * which is exactly what happens if someone tampers with it on disk. Callers treat null
     * as "no credentials" and require a fresh login rather than crashing.
     */
    fun decrypt(blob: ByteArray): ByteArray?
}

/**
 * Supplies the local database encryption key.
 *
 * <p>The key is generated once per installation, wrapped by platform-backed key material,
 * and never appears in source or configuration.</p>
 */
interface DatabaseKeyProvider {
    /** The passphrase for the encrypted local database. Generated on first use. */
    fun databaseKey(): ByteArray
}
