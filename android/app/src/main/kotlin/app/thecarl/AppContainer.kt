package app.thecarl

import android.content.Context
import android.os.Build
import app.thecarl.core.data.database.CarlDatabase
import app.thecarl.core.data.network.ApiTokenRefresher
import app.thecarl.core.data.network.AuthInterceptor
import app.thecarl.core.data.network.CarlApi
import app.thecarl.core.data.network.CarlAuthApi
import app.thecarl.core.data.repository.CaptureRepository
import app.thecarl.core.data.repository.DashboardRepository
import app.thecarl.core.data.repository.OutboxRepository
import app.thecarl.core.data.security.AndroidKeystoreCryptoBox
import app.thecarl.core.data.security.KeystoreCredentialStore
import app.thecarl.core.data.security.KeystoreDatabaseKeyProvider
import app.thecarl.core.data.session.SessionRepository
import app.thecarl.core.data.session.SessionState
import app.thecarl.core.data.sync.SyncEngine
import app.thecarl.core.domain.security.CredentialStore
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory

/**
 * The application's object graph.
 *
 * <p>Constructed once by [CarlApplication] and owned by it — not a global mutable singleton,
 * and not instantiated inside Activities or Composables. A full DI framework was considered
 * and rejected for this phase: Hilt is declared in the version catalogue but applied
 * nowhere, and adding annotation processing to wire nine objects would be more machinery
 * than the problem warrants. This is small enough to read in one sitting and trivially
 * substitutable in tests.</p>
 *
 * <p>Everything is lazy so an unreachable network or an unopenable database at construction
 * cannot prevent the process from starting.</p>
 */
class AppContainer(private val context: Context, private val baseUrl: String) {

    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }

    // ─── Security ────────────────────────────────────────────────────────────

    private val cryptoBox by lazy { AndroidKeystoreCryptoBox() }

    val credentialStore: CredentialStore by lazy {
        KeystoreCredentialStore(
            context.getSharedPreferences(KeystoreCredentialStore.PREFERENCES_NAME, Context.MODE_PRIVATE),
            cryptoBox
        )
    }

    private val databaseKeyProvider by lazy {
        KeystoreDatabaseKeyProvider(
            context.getSharedPreferences(KeystoreDatabaseKeyProvider.PREFERENCES_NAME, Context.MODE_PRIVATE),
            cryptoBox
        )
    }

    /**
     * Stable per-installation identifier.
     *
     * <p>Generated locally and used only to derive ClientTransactionId device tags and to
     * name this handset during enrolment. Deliberately <b>not</b> a hardware identifier:
     * IMEI and serial are privacy-sensitive, restricted on modern Android, and would be a
     * poor security identity anyway — the server issues the real device identity.</p>
     */
    val deviceInstallationId: String by lazy {
        val preferences = context.getSharedPreferences(INSTALLATION_PREFERENCES, Context.MODE_PRIVATE)
        preferences.getString(KEY_INSTALLATION_ID, null) ?: UUID.randomUUID().toString().also {
            preferences.edit().putString(KEY_INSTALLATION_ID, it).commit()
        }
    }

    // ─── Persistence ─────────────────────────────────────────────────────────

    val database: CarlDatabase by lazy { CarlDatabase.encrypted(context, databaseKeyProvider) }

    val outboxRepository: OutboxRepository by lazy { OutboxRepository(database) }

    val dashboardRepository: DashboardRepository by lazy { DashboardRepository(database) }

    // ─── Network ─────────────────────────────────────────────────────────────

    /** No auth interceptor: login is anonymous and refresh must not recurse into refresh. */
    private val authApi: CarlAuthApi by lazy {
        retrofit(OkHttpClient.Builder().timeouts().build()).create(CarlAuthApi::class.java)
    }

    val api: CarlApi by lazy {
        val client = OkHttpClient.Builder()
            .timeouts()
            .addInterceptor(
                AuthInterceptor(
                    credentialStore = credentialStore,
                    tokenRefresher = ApiTokenRefresher(authApi),
                    onAuthFailure = { failure ->
                        // Terminal auth failure. Credentials are already cleared by the
                        // interceptor; this surfaces the state so the UI can react. Queued
                        // financial work is untouched.
                        if (failure == app.thecarl.core.data.network.AuthFailure.REVOKED) {
                            runBlocking { sessionRepository.onSessionRevoked() }
                        }
                    }
                )
            )
            .build()

        retrofit(client).create(CarlApi::class.java)
    }

    // ─── Session ─────────────────────────────────────────────────────────────

    val sessionRepository: SessionRepository by lazy {
        SessionRepository(
            credentialStore = credentialStore,
            authApi = authApi,
            api = api,
            outbox = outboxRepository,
            deviceInstallationId = deviceInstallationId,
            appVersion = BuildConfigCompat.versionName,
            osVersion = "Android ${Build.VERSION.RELEASE}",
            deviceName = "${Build.MANUFACTURER} ${Build.MODEL}"
        )
    }

    /**
     * Capture repository for the current session.
     *
     * <p>Built per call because organization, branch and device come from the active session
     * rather than being fixed at construction. Returns null when there is no enrolled device
     * — capture needs somewhere to attribute the transaction.</p>
     */
    fun captureRepository(): CaptureRepository? {
        val state = sessionRepository.state.value as? SessionState.Active ?: return null

        return CaptureRepository(
            database = database,
            deviceInstallationId = deviceInstallationId,
            organizationId = state.device.organizationId,
            branchId = state.device.branchId,
            deviceId = state.device.deviceId
        )
    }

    /** Sync engine honouring the server's reported batch size rather than a local constant. */
    val syncEngine: SyncEngine by lazy {
        SyncEngine(
            database = database,
            outbox = outboxRepository,
            api = api,
            batchSizeProvider = {
                (sessionRepository.state.value as? SessionState.Active)
                    ?.device?.maxBatchSize ?: SyncEngine.DEFAULT_BATCH_SIZE
            }
        )
    }

    private fun retrofit(client: OkHttpClient) = Retrofit.Builder()
        .baseUrl(baseUrl)
        .client(client)
        .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
        .build()

    private fun OkHttpClient.Builder.timeouts() = apply {
        connectTimeout(15, TimeUnit.SECONDS)
        readTimeout(30, TimeUnit.SECONDS)
        // Generous: a full batch on a poor connection is normal, and an aborted request
        // becomes an ambiguous outcome the server has to deduplicate.
        callTimeout(60, TimeUnit.SECONDS)
    }

    companion object {
        private const val INSTALLATION_PREFERENCES = "thecarl.installation"
        private const val KEY_INSTALLATION_ID = "installation.id"
    }
}

/** Indirection so the container does not depend on the generated BuildConfig in tests. */
internal object BuildConfigCompat {
    val versionName: String get() = "0.1.0"
}
