package app.zazi.core.data.security

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import app.zazi.core.domain.security.CryptoBox
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * AES-256/GCM backed by the Android Keystore.
 *
 * <p>The key is generated inside the Keystore and never leaves it — this class holds a
 * handle, not key material, so the secret cannot be extracted by reading application memory
 * or storage. On devices with a secure element or TEE the key is hardware-bound.</p>
 *
 * <p><b>GCM, not CBC.</b> GCM is authenticated: tampering with stored ciphertext fails the
 * authentication tag and surfaces as a clean null rather than as plausible-looking garbage
 * that the app would then treat as a valid token.</p>
 *
 * <p><b>Hardware backing is not asserted.</b> Whether the key is hardware-bound depends on
 * the device; the Keystore falls back to a software-isolated implementation where no secure
 * hardware exists. Both are substantially better than storing a token in clear text, and the
 * class does not claim a guarantee it cannot verify. See docs/ANDROID_SECURITY.md.</p>
 *
 * <p>This class can only run on a device or emulator: Robolectric does not implement the
 * AndroidKeyStore provider. Storage behaviour is tested on the JVM through a fake
 * [CryptoBox]; this implementation is covered by instrumentation tests.</p>
 */
class AndroidKeystoreCryptoBox(
    private val keyAlias: String = DEFAULT_KEY_ALIAS
) : CryptoBox {

    override fun encrypt(plaintext: ByteArray): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, resolveKey())

        val iv = cipher.iv
        val ciphertext = cipher.doFinal(plaintext)

        // Self-describing blob: [iv length][iv][ciphertext]. Storing the IV alongside the
        // ciphertext is correct for GCM — it is a nonce, not a secret — and keeps the blob
        // portable across key rotations.
        return ByteArray(1 + iv.size + ciphertext.size).also { blob ->
            blob[0] = iv.size.toByte()
            iv.copyInto(blob, 1)
            ciphertext.copyInto(blob, 1 + iv.size)
        }
    }

    override fun decrypt(blob: ByteArray): ByteArray? {
        // Every failure path returns null rather than throwing. Corrupt or tampered storage
        // must degrade to "please log in again", never to a crash loop the user cannot exit.
        return try {
            if (blob.isEmpty()) return null

            val ivLength = blob[0].toInt()
            if (ivLength <= 0 || blob.size <= 1 + ivLength) return null

            val iv = blob.copyOfRange(1, 1 + ivLength)
            val ciphertext = blob.copyOfRange(1 + ivLength, blob.size)

            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, resolveKey(), GCMParameterSpec(TAG_LENGTH_BITS, iv))
            cipher.doFinal(ciphertext)
        } catch (_: Exception) {
            // Includes AEADBadTagException (tampering), and the case where the Keystore key
            // was invalidated — for example after the user changed their lock screen.
            null
        }
    }

    /** Returns the existing key, generating one on first use. */
    private fun resolveKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (keyStore.getEntry(keyAlias, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                keyAlias,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(KEY_SIZE_BITS)
                // Deliberately NOT setUserAuthenticationRequired(true): an agent's outbox
                // must be able to sync in the background without the screen being unlocked.
                // Requiring authentication here would strand queued transactions.
                .build()
        )

        return generator.generateKey()
    }

    companion object {
        const val DEFAULT_KEY_ALIAS = "thecarl.credentials.v1"

        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val KEY_SIZE_BITS = 256
        private const val TAG_LENGTH_BITS = 128
    }
}
