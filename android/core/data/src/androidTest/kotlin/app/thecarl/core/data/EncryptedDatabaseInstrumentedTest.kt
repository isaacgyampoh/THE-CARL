package app.thecarl.core.data

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import app.thecarl.core.data.database.CarlDatabase
import app.thecarl.core.data.database.EvidenceEntity
import app.thecarl.core.data.security.AndroidKeystoreCryptoBox
import app.thecarl.core.data.security.KeystoreCredentialStore
import app.thecarl.core.data.security.KeystoreDatabaseKeyProvider
import app.thecarl.core.domain.security.StoredCredentials
import com.google.common.truth.Truth.assertThat
import java.io.File
import kotlinx.coroutines.runBlocking
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The parts that genuinely need a device or emulator.
 *
 * <p>Two things cannot run on a JVM: the Android Keystore, which Robolectric does not
 * implement, and SQLCipher, which ships native libraries for Android ABIs only. Everything
 * else is covered by host tests; these close the remaining gap.</p>
 *
 * <p>Run with: <c>./gradlew :core:data:connectedDebugAndroidTest</c></p>
 */
@RunWith(AndroidJUnit4::class)
class EncryptedDatabaseInstrumentedTest {

    private val context: Context get() = ApplicationProvider.getApplicationContext()

    // ─── Real Keystore crypto ────────────────────────────────────────────────

    @Test
    fun keystoreCryptoRoundTripsThroughRealHardwareBackedKeys() {
        val cryptoBox = AndroidKeystoreCryptoBox("thecarl.test.crypto")
        val plaintext = "refresh-token-value".toByteArray()

        val blob = cryptoBox.encrypt(plaintext)

        assertThat(blob).isNotEqualTo(plaintext)
        assertThat(cryptoBox.decrypt(blob)).isEqualTo(plaintext)
    }

    @Test
    fun tamperedCiphertextFailsAuthenticationRatherThanDecryptingToGarbage() {
        val cryptoBox = AndroidKeystoreCryptoBox("thecarl.test.tamper")
        val blob = cryptoBox.encrypt("access-token".toByteArray())

        // Flip a byte in the ciphertext. GCM's authentication tag must catch this; a mode
        // without authentication would return plausible-looking garbage the app would trust.
        val tampered = blob.copyOf().also { it[it.size - 1] = (it[it.size - 1] + 1).toByte() }

        assertThat(cryptoBox.decrypt(tampered)).isNull()
    }

    @Test
    fun twoEncryptionsOfTheSameValueDifferBecauseTheNonceDiffers() {
        val cryptoBox = AndroidKeystoreCryptoBox("thecarl.test.nonce")
        val plaintext = "same-token".toByteArray()

        // Identical ciphertext would let an observer tell that a token was unchanged.
        assertThat(cryptoBox.encrypt(plaintext)).isNotEqualTo(cryptoBox.encrypt(plaintext))
    }

    @Test
    fun credentialsSurviveARealStoreBackedByKeystore() = runBlocking {
        val preferences = context.getSharedPreferences("thecarl.test.creds", Context.MODE_PRIVATE)
        preferences.edit().clear().commit()

        val store = KeystoreCredentialStore(preferences, AndroidKeystoreCryptoBox("thecarl.test.store"))
        val credentials = StoredCredentials(
            accessToken = "real-access-token",
            refreshToken = "real-refresh-token",
            accessTokenExpiresAtUtcMillis = System.currentTimeMillis() + 3_600_000,
            userId = "user-1",
            organizationId = "org-1",
            branchId = "branch-1",
            deviceId = "device-1",
            deviceInstallationId = "install-1"
        )

        store.save(credentials)
        assertThat(store.read()).isEqualTo(credentials)

        val onDisk = preferences.all.values.joinToString(" ")
        assertThat(onDisk).doesNotContain("real-access-token")
        assertThat(onDisk).doesNotContain("real-refresh-token")

        store.clear()
        assertThat(store.read()).isNull()
    }

    // ─── Real SQLCipher ──────────────────────────────────────────────────────

    @Test
    fun theDatabaseFileIsEncryptedNotPlaintextSqlite() = runBlocking {
        context.deleteDatabase(CarlDatabase.DATABASE_NAME)

        val keyProvider = KeystoreDatabaseKeyProvider(
            context.getSharedPreferences("thecarl.test.dbkey", Context.MODE_PRIVATE),
            AndroidKeystoreCryptoBox("thecarl.test.dbkey")
        )

        val database = CarlDatabase.encrypted(context, keyProvider)
        database.evidenceDao().insert(sampleEvidence())
        database.close()

        val file = context.getDatabasePath(CarlDatabase.DATABASE_NAME)
        val header = ByteArray(16)
        File(file.absolutePath).inputStream().use { it.read(header) }

        // A plaintext SQLite file begins with "SQLite format 3". An encrypted one cannot,
        // because the header itself is encrypted. This is the assertion that proves the
        // database is genuinely protected rather than merely configured to be.
        assertThat(String(header)).doesNotContain("SQLite format 3")
    }

    @Test
    fun dataSurvivesClosingAndReopeningTheEncryptedDatabase() = runBlocking {
        context.deleteDatabase(CarlDatabase.DATABASE_NAME)

        val preferences = context.getSharedPreferences("thecarl.test.dbkey2", Context.MODE_PRIVATE)
        val cryptoBox = AndroidKeystoreCryptoBox("thecarl.test.dbkey2")

        val first = CarlDatabase.encrypted(context, KeystoreDatabaseKeyProvider(preferences, cryptoBox))
        first.evidenceDao().insert(sampleEvidence())
        first.close()

        // A second provider instance must derive the same key from wrapped storage,
        // otherwise an app restart would leave the agent's outbox unopenable.
        val second = CarlDatabase.encrypted(context, KeystoreDatabaseKeyProvider(preferences, cryptoBox))
        assertThat(second.evidenceDao().count()).isEqualTo(1)
        second.close()
    }

    @Test
    fun theDatabaseKeyIsStableAcrossProviderInstances() {
        val preferences = context.getSharedPreferences("thecarl.test.dbkey3", Context.MODE_PRIVATE)
        preferences.edit().clear().commit()
        val cryptoBox = AndroidKeystoreCryptoBox("thecarl.test.dbkey3")

        val first = KeystoreDatabaseKeyProvider(preferences, cryptoBox).databaseKey()
        val second = KeystoreDatabaseKeyProvider(preferences, cryptoBox).databaseKey()

        assertThat(second).isEqualTo(first)
        assertThat(first).hasLength(32)
    }

    private fun sampleEvidence() = EvidenceEntity(
        evidenceId = "evidence-1",
        localTransactionId = null,
        sourceType = "MANUAL_ENTRY",
        provider = "MTN",
        senderIdentity = null,
        transactionType = "CASH_IN",
        amountMinor = 50_000L,
        currency = "GHS",
        reference = "REF-1",
        customerPhoneNumber = "0241234567",
        occurredAtUtcMillis = System.currentTimeMillis(),
        observedAtUtcMillis = System.currentTimeMillis(),
        fingerprint = "a".repeat(64),
        fingerprintVersion = "v1",
        parserName = "ManualEntry",
        parserVersion = "manual-v1",
        confidence = 1.0,
        state = "ACCEPTED",
        outcomeReason = null,
        rawMessage = null,
        rawMessagePurgedAtUtcMillis = null,
        deviceId = "device-1",
        createdAtUtcMillis = System.currentTimeMillis()
    )
}
