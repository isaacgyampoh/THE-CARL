package app.zazi.ui

import android.content.Context
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity

/**
 * Locking the app behind the phone's own screen lock.
 *
 * <p>An agent's handset holds every transaction the business has recorded, the customer numbers
 * against them and the statements built from them. A phone left on a counter, lent to someone,
 * or lost should not hand that over.</p>
 *
 * <p>The phone's existing PIN, pattern or fingerprint — never a second one of Zazi's own. An
 * agent who forgets a Zazi-only PIN is locked out of their own day's takings with nobody able
 * to help them, and a shared counter phone would end up with the PIN written on it. Reusing the
 * device credential also means the lock is as strong as whatever the owner already chose.</p>
 */
object AppLock {

    /** What the phone can actually do, which decides whether locking is offered at all. */
    enum class Availability {
        /** A biometric or a device PIN exists and can be asked for. */
        READY,

        /** The phone has no screen lock set, so there is nothing to ask for. */
        NO_SCREEN_LOCK,

        /** No suitable hardware or the platform refused; the app stays unlocked. */
        UNAVAILABLE
    }

    /**
     * Whether the app should start locked.
     *
     * <p>Only when the phone can actually be unlocked. The failure that matters here is not a
     * missing lock — it is an agent shut out of their own day's takings by one they cannot
     * answer, on a handset with no screen lock set, which is common on the cheap phones this
     * is built for.</p>
     */
    fun startsLocked(availability: Availability): Boolean = availability == Availability.READY

    private const val ALLOWED =
        BiometricManager.Authenticators.BIOMETRIC_WEAK or
            BiometricManager.Authenticators.DEVICE_CREDENTIAL

    fun availability(context: Context): Availability =
        when (BiometricManager.from(context).canAuthenticate(ALLOWED)) {
            BiometricManager.BIOMETRIC_SUCCESS -> Availability.READY
            BiometricManager.BIOMETRIC_ERROR_NONE_ENROLLED -> Availability.NO_SCREEN_LOCK
            else -> Availability.UNAVAILABLE
        }

    /**
     * Asks for the phone's credential.
     *
     * <p>[onFailed] is for a positive refusal — a wrong PIN, or the agent pressing cancel —
     * not for a transient error. The caller keeps the app locked either way; the distinction
     * only decides what the screen says next.</p>
     */
    fun prompt(
        activity: FragmentActivity,
        onUnlocked: () -> Unit,
        onFailed: (String) -> Unit
    ) {
        val prompt = BiometricPrompt(
            activity,
            ContextCompat.getMainExecutor(activity),
            object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) =
                    onUnlocked()

                override fun onAuthenticationError(code: Int, message: CharSequence) {
                    // A cancel is the agent's decision, not a fault, and saying "failed"
                    // to someone who chose to back out reads as the app being broken.
                    val cancelled = code == BiometricPrompt.ERROR_USER_CANCELED ||
                        code == BiometricPrompt.ERROR_NEGATIVE_BUTTON ||
                        code == BiometricPrompt.ERROR_CANCELED
                    onFailed(if (cancelled) "" else message.toString())
                }
            }
        )

        prompt.authenticate(
            BiometricPrompt.PromptInfo.Builder()
                .setTitle("Unlock Zazi")
                .setSubtitle("Your transactions and customer numbers are on this phone.")
                .setAllowedAuthenticators(ALLOWED)
                .build()
        )
    }
}
