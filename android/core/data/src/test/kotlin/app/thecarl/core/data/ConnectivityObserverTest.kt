package app.thecarl.core.data

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import androidx.test.core.app.ApplicationProvider
import app.thecarl.core.data.network.AndroidConnectivityObserver
import com.google.common.truth.Truth.assertThat
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.shadows.ShadowNetworkCapabilities

/**
 * The connectivity indicator reads the platform.
 *
 * <p>This exists because of a defect found on a real emulator: the dashboard reported "Online"
 * while the device was in aeroplane mode, and the capture screen's "Saved offline" wording was
 * unreachable as a result. The view-model tests could not catch it — they pass {@code isOnline}
 * in directly, so they stayed green while nothing in the application ever supplied a real
 * value.</p>
 */
@RunWith(RobolectricTestRunner::class)
class ConnectivityObserverTest {

    private val context: Context = ApplicationProvider.getApplicationContext()

    private val connectivityManager =
        context.getSystemService(ConnectivityManager::class.java)!!

    @Test
    fun `reports offline when there is no active network`() = runTest {
        shadowOf(connectivityManager).setDefaultNetworkActive(false)
        shadowOf(connectivityManager).setNetworkCapabilities(
            connectivityManager.activeNetwork,
            null
        )

        assertThat(AndroidConnectivityObserver(context).isOnline.first()).isFalse()
    }

    @Test
    fun `reports online only when the network is validated`() = runTest {
        val capabilities = ShadowNetworkCapabilities.newInstance()
        shadowOf(capabilities).addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
        shadowOf(capabilities).addCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
        shadowOf(connectivityManager)
            .setNetworkCapabilities(connectivityManager.activeNetwork, capabilities)

        assertThat(AndroidConnectivityObserver(context).isOnline.first()).isTrue()
    }

    @Test
    fun `a connected but unvalidated network is not treated as online`() = runTest {
        // A captive portal advertises INTERNET while no request can actually succeed.
        // Believing it would tell an agent their queued work is draining when it is not.
        val capabilities = ShadowNetworkCapabilities.newInstance()
        shadowOf(capabilities).addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
        shadowOf(connectivityManager)
            .setNetworkCapabilities(connectivityManager.activeNetwork, capabilities)

        assertThat(AndroidConnectivityObserver(context).isOnline.first()).isFalse()
    }
}
