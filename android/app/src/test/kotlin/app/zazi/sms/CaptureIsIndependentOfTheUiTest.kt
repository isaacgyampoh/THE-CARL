package app.zazi.sms

import com.google.common.truth.Truth.assertThat
import java.io.File
import org.junit.Test

/**
 * Capture must keep running while the app is closed, backgrounded, or never opened.
 *
 * <p>If anything on a screen ever came to gate capture, an agent's transactions would stop
 * being recorded whenever they put the phone down — which is most of the day — and nothing on
 * any screen would say so. The money would simply not be there at closing time.</p>
 *
 * <p>Enforced structurally rather than trusted. The receiver is registered in the manifest and
 * the system delivers to it with no activity in existence; this asserts the capture path never
 * acquires a dependency on the UI that would let someone wire the two together later.</p>
 */
class CaptureIsIndependentOfTheUiTest {

    private val captureSources: List<File> =
        File("src/main/kotlin/app/zazi/sms").walkTopDown().filter { it.extension == "kt" }.toList()

    @Test
    fun `there is capture code to check`() {
        // Guards the tests below: a path that silently matched nothing would pass forever.
        assertThat(captureSources).isNotEmpty()
    }

    @Test
    fun `nothing in the capture path depends on a screen`() {
        // Named by package rather than by class, so this keeps holding as screens come and
        // go — an earlier version listed a lock that has since been removed, and would have
        // quietly stopped guarding anything the day it was deleted.
        val offenders = captureSources.filter { file ->
            val source = file.readText()
            source.contains("app.zazi.ui.") || source.contains("MainActivity")
        }

        assertThat(offenders.map { it.name }).isEmpty()
    }

    @Test
    fun `the receiver is declared in the manifest, so it runs without the app being open`() {
        val manifest = File("src/main/AndroidManifest.xml").readText()

        // A receiver registered in code would only exist while something is running. Declared
        // here, the system starts the process to deliver to it.
        assertThat(manifest).contains("android:name=\".sms.SmsBroadcastReceiver\"")
        assertThat(manifest).contains("android.provider.Telephony.SMS_RECEIVED")
    }
}
