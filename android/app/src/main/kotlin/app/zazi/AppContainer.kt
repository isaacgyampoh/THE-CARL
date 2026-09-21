package app.zazi

import android.content.Context
import android.os.Build
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.network.AndroidConnectivityObserver
import app.zazi.core.data.network.ApiTokenRefresher
import app.zazi.core.data.network.AuthInterceptor
import app.zazi.core.data.telemetry.LocalTelemetryRecorder
import app.zazi.core.data.telemetry.TelemetryInterceptor
import app.zazi.core.data.telemetry.TelemetryRecorder
import app.zazi.core.data.telemetry.TelemetryUploader
import app.zazi.core.data.network.ConnectivityObserver
import app.zazi.core.data.network.ZaziApi
import app.zazi.core.data.network.ZaziAuthApi
import app.zazi.core.data.repository.CaptureRepository
import app.zazi.core.data.repository.DashboardRepository
import app.zazi.core.data.repository.ParsingReportRepository
import app.zazi.core.data.repository.StatementRepository
import app.zazi.core.data.repository.OutboxRepository
import app.zazi.core.data.security.AndroidKeystoreCryptoBox
import app.zazi.core.data.security.KeystoreCredentialStore
import app.zazi.core.data.security.KeystoreDatabaseKeyProvider
import app.zazi.core.data.session.SessionRepository
import app.zazi.core.data.session.SessionState
import app.zazi.core.data.sync.SyncEngine
import app.zazi.core.domain.security.CredentialStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
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
 * <p>Constructed once by [ZaziApplication] and owned by it — not a global mutable singleton,
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
            cryptoBox,
            // Supplied so a database that can no longer be decrypted is moved aside rather
            // than left in place, where SQLCipher would fail to open it on every launch.
            context.getDatabasePath(ZaziDatabase.DATABASE_NAME)
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

    val database: ZaziDatabase by lazy { ZaziDatabase.encrypted(context, databaseKeyProvider) }

    /**
     * Reports, once, that local data was lost because its key could no longer be unwrapped.
     *
     * <p>Rare — a device restored from backup, or an invalidated Keystore — and the agent has
     * to be told, because what was lost is transactions they captured and never synced. They
     * would otherwise find a day's work simply absent.</p>
     */
    fun takeOrphanedDatabaseNotice(): Long? = databaseKeyProvider.takeOrphanedDatabaseNotice()

    val outboxRepository: OutboxRepository by lazy { OutboxRepository(database) }

    /**
     * Records what this handset observed. Best-effort throughout: nothing here can fail a
     * capture, a login or a sync.
     */
    val telemetryRecorder: TelemetryRecorder by lazy { LocalTelemetryRecorder(database) }

    val telemetryUploader: TelemetryUploader by lazy { TelemetryUploader(database, api) }

    /**
     * Scope for reporting. Separate from any business scope so a cancelled operation still
     * records why it was cancelled, and so a slow report never delays one.
     */
    private val telemetryScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    val dashboardRepository: DashboardRepository by lazy { DashboardRepository(database) }

    /**
     * The one path by which a provider's message leaves this handset.
     *
     * <p>Not wired into sync, and deliberately: sync carries the parsed result and never the
     * text. This sends one body, about one transaction, at the moment an agent taps to say it
     * was read wrongly.</p>
     */
    /** The agent's own statements, downloaded to a folder the share sheet can reach. */
    val statementRepository: StatementRepository by lazy {
        StatementRepository(api, context.cacheDir)
    }

    val parsingReportRepository: ParsingReportRepository by lazy {
        ParsingReportRepository(database, api, BuildConfigCompat.versionName)
    }

    // ─── Network ─────────────────────────────────────────────────────────────

    /** No auth interceptor: login is anonymous and refresh must not recurse into refresh. */
    private val authApi: ZaziAuthApi by lazy {
        retrofit(OkHttpClient.Builder().timeouts().build()).create(ZaziAuthApi::class.java)
    }

    val api: ZaziApi by lazy {
        val client = OkHttpClient.Builder()
            .timeouts()
            // Outermost, so it measures the whole exchange including authentication retries
            // and sees the correlation id every inner interceptor will send.
            .addInterceptor(TelemetryInterceptor(telemetryRecorder, telemetryScope))
            .addInterceptor(
                AuthInterceptor(
                    credentialStore = credentialStore,
                    tokenRefresher = ApiTokenRefresher(authApi),
                    onAuthFailure = { failure ->
                        // Terminal auth failure. Credentials are already cleared by the
                        // interceptor; this surfaces the state so the UI can react. Queued
                        // financial work is untouched.
                        if (failure == app.zazi.core.data.network.AuthFailure.REVOKED) {
                            runBlocking { sessionRepository.onSessionRevoked() }
                        }
                    }
                )
            )
            .build()

        retrofit(client).create(ZaziApi::class.java)
    }

    // ─── Connectivity ────────────────────────────────────────────────────────

    /**
     * Drives the connectivity indicator only. Sync scheduling stays with WorkManager's own
     * network constraint, which is the authority on when work actually runs.
     */
    val connectivityObserver: ConnectivityObserver by lazy {
        AndroidConnectivityObserver(context)
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
            deviceName = "${Build.MANUFACTURER} ${Build.MODEL}",
            telemetry = telemetryRecorder
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
        private const val INSTALLATION_PREFERENCES = "zazi.installation"
        private const val KEY_INSTALLATION_ID = "installation.id"
    }
}

/** Indirection so the container does not depend on the generated BuildConfig in tests. */
internal object BuildConfigCompat {
    val versionName: String get() = "0.1.0"
}
