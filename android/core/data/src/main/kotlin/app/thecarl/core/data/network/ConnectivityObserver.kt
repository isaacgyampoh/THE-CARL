package app.thecarl.core.data.network

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import kotlinx.coroutines.flow.distinctUntilChanged

/**
 * Whether this device currently has usable internet access.
 *
 * <p>The dashboard's connectivity indicator and the capture confirmation both describe the
 * agent's situation to them. Reporting "Online" while a phone is in a dead spot tells an agent
 * their queued work is moving when it is not, so this reads the platform rather than
 * assuming.</p>
 *
 * <p><b>This is an indicator, not a gate.</b> Nothing here decides whether a transaction is
 * saved, queued, or synced — capture always writes to the outbox and {@code SyncEngine} remains
 * the single authority on transmission. A wrong answer here can only ever mislabel a screen; it
 * can never drop, duplicate, or hold financial work.</p>
 */
interface ConnectivityObserver {
    /** Emits on every change, starting with the current value. */
    val isOnline: Flow<Boolean>
}

/**
 * Platform implementation backed by {@link ConnectivityManager}.
 *
 * <p>Requires {@code ACCESS_NETWORK_STATE}, which the application already declares.</p>
 */
class AndroidConnectivityObserver(context: Context) : ConnectivityObserver {

    private val connectivityManager =
        context.applicationContext.getSystemService(ConnectivityManager::class.java)

    override val isOnline: Flow<Boolean> = callbackFlow {
        val manager = connectivityManager
        if (manager == null) {
            // No connectivity service at all. Claiming "offline" would be the safer-sounding
            // answer, but it is equally unverified, and it would permanently label a working
            // device as offline. Report optimistically and let the outbox tell the truth.
            trySend(true)
            awaitClose { }
            return@callbackFlow
        }

        fun publish() = trySend(manager.hasValidatedInternet())

        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = publish().let { }
            override fun onLost(network: Network) = publish().let { }
            override fun onUnavailable() = publish().let { }

            override fun onCapabilitiesChanged(
                network: Network,
                capabilities: NetworkCapabilities
            ) = publish().let { }
        }

        val request = NetworkRequest.Builder()
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .build()

        manager.registerNetworkCallback(request, callback)
        publish()

        awaitClose { manager.unregisterNetworkCallback(callback) }
    }.distinctUntilChanged()
}

/**
 * A network that is present *and* has been validated as actually reaching the internet.
 *
 * <p>{@code NET_CAPABILITY_INTERNET} alone is not enough: a captive portal or a connected-but-
 * dead Wi-Fi network advertises it while no request can succeed.</p>
 */
private fun ConnectivityManager.hasValidatedInternet(): Boolean {
    val capabilities = getNetworkCapabilities(activeNetwork) ?: return false
    return capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) &&
        capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
}
