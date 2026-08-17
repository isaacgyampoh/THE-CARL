package app.thecarl.core.data

import app.thecarl.core.data.capture.CaptureOutcome
import app.thecarl.core.data.capture.ManualCaptureRequest
import app.thecarl.core.data.database.CarlDatabase
import app.thecarl.core.data.network.CarlApi
import app.thecarl.core.data.network.CarlAuthApi
import app.thecarl.core.data.repository.CaptureRepository
import app.thecarl.core.data.repository.OutboxRepository
import app.thecarl.core.data.security.KeystoreCredentialStore
import app.thecarl.core.data.session.EnrolmentResult
import app.thecarl.core.data.session.LoginResult
import app.thecarl.core.data.session.SessionRepository
import app.thecarl.core.data.session.SessionState
import app.thecarl.core.domain.model.PlatformCapability
import app.thecarl.core.domain.model.Provider
import app.thecarl.core.domain.model.TransactionType
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import com.google.common.truth.Truth.assertThat
import java.math.BigDecimal
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import retrofit2.Retrofit
import retrofit2.converter.kotlinx.serialization.asConverterFactory

/**
 * The authentication and enrolment lifecycle against a real HTTP server and real storage.
 *
 * <p>The invariant these exist to protect: <b>financial work outlives authentication</b>.
 * Logging out, being revoked, or failing to refresh must never destroy an unsynced
 * transaction.</p>
 */
@RunWith(RobolectricTestRunner::class)
class SessionRepositoryTest {

    private lateinit var server: MockWebServer
    private lateinit var database: CarlDatabase
    private lateinit var credentialStore: KeystoreCredentialStore
    private lateinit var outbox: OutboxRepository
    private lateinit var capture: CaptureRepository
    private lateinit var session: SessionRepository

    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }
    private val installationId = "installation-abc"

    @Before
    fun setUp() {
        server = MockWebServer().apply { start() }
        database = createTestDatabase()

        val context = ApplicationProvider.getApplicationContext<Context>()
        val preferences = context.getSharedPreferences("session-test", Context.MODE_PRIVATE)
        preferences.edit().clear().commit()

        credentialStore = KeystoreCredentialStore(preferences, FakeCryptoBox())
        outbox = OutboxRepository(database)
        capture = CaptureRepository(
            database, installationId,
            "22222222-2222-2222-2222-222222222222",
            "33333333-3333-3333-3333-333333333333",
            "44444444-4444-4444-4444-444444444444"
        )

        val retrofit = Retrofit.Builder()
            .baseUrl(server.url("/"))
            .client(OkHttpClient.Builder().callTimeout(2, TimeUnit.SECONDS).build())
            .addConverterFactory(json.asConverterFactory("application/json".toMediaType()))
            .build()

        session = SessionRepository(
            credentialStore = credentialStore,
            authApi = retrofit.create(CarlAuthApi::class.java),
            api = retrofit.create(CarlApi::class.java),
            outbox = outbox,
            deviceInstallationId = installationId,
            appVersion = "0.1.0",
            osVersion = "Android 14",
            deviceName = "Test Handset"
        )
    }

    @After
    fun tearDown() {
        database.close()
        server.shutdown()
    }

    // ─── Login ───────────────────────────────────────────────────────────────

    @Test
    fun `a successful login stores credentials securely`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())

        val result = session.login("agent@carl.test", "Str0ng-Passphrase!")

        assertThat(result).isInstanceOf(LoginResult.Success::class.java)

        val stored = credentialStore.read()!!
        assertThat(stored.accessToken).isEqualTo("access-token-1")
        assertThat(stored.refreshToken).isEqualTo("refresh-token-1")
        assertThat(stored.organizationId).isEqualTo("org-1")
    }

    @Test
    fun `the password is never persisted`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())

        session.login("agent@carl.test", "Str0ng-Passphrase!")

        val context = ApplicationProvider.getApplicationContext<Context>()
        val onDisk = context.getSharedPreferences("session-test", Context.MODE_PRIVATE)
            .all.values.joinToString(" ")

        // The password is used for one request and then forgotten.
        assertThat(onDisk).doesNotContain("Str0ng-Passphrase!")
    }

    @Test
    fun `invalid credentials store nothing`() = runTest {
        server.enqueue(MockResponse().setResponseCode(401))

        val result = session.login("agent@carl.test", "wrong")

        assertThat(result).isEqualTo(LoginResult.InvalidCredentials)
        assertThat(credentialStore.read()).isNull()
    }

    @Test
    fun `rate limiting is reported with retry-after`() = runTest {
        server.enqueue(MockResponse().setResponseCode(429).setHeader("Retry-After", "60"))

        val result = session.login("agent@carl.test", "whatever")

        assertThat(result).isInstanceOf(LoginResult.RateLimited::class.java)
        assertThat((result as LoginResult.RateLimited).retryAfterSeconds).isEqualTo(60)
        assertThat(credentialStore.read()).isNull()
    }

    @Test
    fun `a server error is distinguished from bad credentials`() = runTest {
        server.enqueue(MockResponse().setResponseCode(500))

        val result = session.login("agent@carl.test", "whatever")

        // The UI must not tell a user their password is wrong when the server is broken.
        assertThat(result).isInstanceOf(LoginResult.ServerError::class.java)
    }

    // ─── Enrolment ───────────────────────────────────────────────────────────

    @Test
    fun `an authenticated but unenrolled device enters the enrolment state`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(MockResponse().setResponseCode(404))

        val result = session.login("agent@carl.test", "Str0ng-Passphrase!")

        assertThat((result as LoginResult.Success).state)
            .isInstanceOf(SessionState.NeedsEnrolment::class.java)
    }

    @Test
    fun `enrolment stores the server-assigned device identity and re-queries capabilities`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(MockResponse().setResponseCode(404))
        session.login("agent@carl.test", "Str0ng-Passphrase!")

        server.enqueue(enrolResponse())
        server.enqueue(deviceSelfResponse())

        val result = session.enrolDevice("CARL-ABCD-EFGH")

        assertThat(result).isInstanceOf(EnrolmentResult.Success::class.java)
        assertThat(credentialStore.read()!!.deviceId).isEqualTo("device-1")

        val device = (result as EnrolmentResult.Success).device
        assertThat(device.branchId).isEqualTo("branch-1")
        assertThat(device.maxBatchSize).isEqualTo(100)
    }

    @Test
    fun `the client cannot assign itself a branch role or capabilities`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(MockResponse().setResponseCode(404))
        session.login("agent@carl.test", "Str0ng-Passphrase!")

        server.enqueue(enrolResponse())
        server.enqueue(deviceSelfResponse())
        session.enrolDevice("CARL-ABCD-EFGH")

        // Skip login + devices/me, inspect the enrol request body.
        server.takeRequest(); server.takeRequest()
        val enrolBody = server.takeRequest().body.readUtf8()

        // Scope comes entirely from the code the administrator issued.
        assertThat(enrolBody).doesNotContain("organizationId")
        assertThat(enrolBody).doesNotContain("branchId")
        assertThat(enrolBody).doesNotContain("role")
        assertThat(enrolBody).doesNotContain("capabilit")
    }

    @Test
    fun `an expired enrolment code is rejected without changing device identity`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(MockResponse().setResponseCode(404))
        session.login("agent@carl.test", "Str0ng-Passphrase!")

        server.enqueue(MockResponse().setResponseCode(401))

        assertThat(session.enrolDevice("CARL-EXPIRED")).isEqualTo(EnrolmentResult.InvalidOrExpiredCode)
        assertThat(credentialStore.read()!!.deviceId).isNull()
    }

    @Test
    fun `server capabilities are consumed rather than assumed`() = runTest {
        server.enqueue(loginResponse())
        // An iPhone-shaped response: no SMS capture.
        server.enqueue(deviceSelfResponse(deviceType = 3, capabilities = listOf(1, 2, 10, 11, 12)))

        val result = session.login("agent@carl.test", "Str0ng-Passphrase!")
        val active = (result as LoginResult.Success).state as SessionState.Active

        assertThat(active.device.canAttemptSmsCapture).isFalse()
        assertThat(active.device.canCaptureManually).isTrue()
        assertThat(active.device.capabilities)
            .doesNotContain(PlatformCapability.SMS_CAPTURE)
    }

    // ─── Financial work outlives authentication ──────────────────────────────

    @Test
    fun `logout clears credentials but never deletes unsynced work`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())
        session.login("agent@carl.test", "Str0ng-Passphrase!")

        val queued = capture.captureManual(cashIn("500.00")) as CaptureOutcome.Queued

        session.logout()

        assertThat(credentialStore.read()).isNull()
        assertThat(session.state.value).isEqualTo(SessionState.SignedOut)

        // The agent still owns these transactions. Deleting them would destroy a financial
        // record because someone signed out.
        assertThat(database.outboxDao().findByClientId(queued.clientTransactionId)).isNotNull()
        assertThat(database.localTransactionDao().count()).isEqualTo(1)
        assertThat(database.evidenceDao().count()).isEqualTo(1)
    }

    @Test
    fun `revocation preserves queued work and reports how much is waiting`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())
        session.login("agent@carl.test", "Str0ng-Passphrase!")

        capture.captureManual(cashIn("100.00"))
        capture.captureManual(cashIn("200.00"))

        session.onSessionRevoked()

        val state = session.state.value
        assertThat(state).isInstanceOf(SessionState.DeviceRevoked::class.java)

        // Surfaced so the agent knows work is stranded rather than lost.
        assertThat((state as SessionState.DeviceRevoked).queuedWorkCount).isEqualTo(2)
        assertThat(database.localTransactionDao().count()).isEqualTo(2)
    }

    @Test
    fun `revocation survives process death instead of degrading to a sign-in screen`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())
        session.login("agent@carl.test", "Str0ng-Passphrase!")

        capture.captureManual(cashIn("100.00"))
        capture.captureManual(cashIn("200.00"))
        session.onSessionRevoked()

        // A restart. Revocation cleared the credentials, so without a durable marker this
        // resolves to SignedOut and the agent is shown an ordinary login form with no
        // explanation and no word about their two stranded transactions.
        val afterRestart = session.restore()

        assertThat(afterRestart).isInstanceOf(SessionState.DeviceRevoked::class.java)
        assertThat((afterRestart as SessionState.DeviceRevoked).queuedWorkCount).isEqualTo(2)

        // Still no credentials: the marker explains, it does not authenticate.
        assertThat(credentialStore.read()).isNull()

        // And the work is still there to be told about.
        assertThat(database.outboxDao().count()).isEqualTo(2)
        assertThat(database.localTransactionDao().count()).isEqualTo(2)
        assertThat(database.evidenceDao().count()).isEqualTo(2)
    }

    @Test
    fun `an ordinary sign-out does not later masquerade as a revocation`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())
        session.login("agent@carl.test", "Str0ng-Passphrase!")

        session.logout()

        assertThat(session.restore()).isEqualTo(SessionState.SignedOut)
    }

    @Test
    fun `signing in again clears the revocation notice`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())
        session.login("agent@carl.test", "Str0ng-Passphrase!")
        session.onSessionRevoked()

        assertThat(session.restore()).isInstanceOf(SessionState.DeviceRevoked::class.java)

        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())
        session.login("agent@carl.test", "Str0ng-Passphrase!")

        // Whether this device may act is the server's decision, expressed through
        // /devices/me — never the mere absence of a local marker.
        assertThat(session.state.value).isInstanceOf(SessionState.Active::class.java)
    }

    @Test
    fun `a revoked device is detected at startup and stops normal syncing`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())
        session.login("agent@carl.test", "Str0ng-Passphrase!")

        server.enqueue(deviceSelfResponse(isRevoked = true))

        assertThat(session.restore()).isInstanceOf(SessionState.DeviceRevoked::class.java)

        // However revocation is discovered, no usable token is left behind for a retry loop
        // to pick up. Authorization stays with the server; the client simply has nothing to
        // present.
        assertThat(credentialStore.read()).isNull()
    }

    // ─── Startup resilience ──────────────────────────────────────────────────

    @Test
    fun `startup without credentials goes to signed out`() = runTest {
        assertThat(session.restore()).isEqualTo(SessionState.SignedOut)
    }

    @Test
    fun `startup with no network stays usable rather than crashing`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())
        session.login("agent@carl.test", "Str0ng-Passphrase!")

        server.shutdown()

        // An unreachable server at launch is normal for an offline-first client. Capture
        // must keep working; only sync waits.
        val state = session.restore()
        assertThat(state).isInstanceOf(SessionState.Active::class.java)
        assertThat((state as SessionState.Active).device.canCaptureManually).isTrue()
    }

    @Test
    fun `capture still works while signed out`() = runTest {
        // Local capture does not depend on a session. Refusing to record would lose a real
        // transaction the agent already performed.
        val outcome = capture.captureManual(cashIn("42.00"))

        assertThat(outcome).isInstanceOf(CaptureOutcome.Queued::class.java)
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private fun cashIn(amount: String) = ManualCaptureRequest(
        transactionType = TransactionType.CASH_IN,
        amount = BigDecimal(amount),
        provider = Provider.MTN,
        occurredAtUtcMillis = System.currentTimeMillis() - 60_000,
        reference = "REF-${System.nanoTime()}",
        customerPhoneNumber = "0241234567"
    )

    private fun loginResponse() = MockResponse()
        .setResponseCode(200)
        .setHeader("Content-Type", "application/json")
        .setBody(
            """
            {
              "accessToken": "access-token-1",
              "refreshToken": "refresh-token-1",
              "expiresAtUtc": "2030-01-01T00:00:00Z",
              "user": {
                "id": "user-1", "organizationId": "org-1", "branchId": "branch-1",
                "email": "agent@carl.test", "fullName": "Test Agent", "roles": ["AGENT"]
              }
            }
            """.trimIndent()
        )

    private fun deviceSelfResponse(
        isRevoked: Boolean = false,
        deviceType: Int = 1,
        capabilities: List<Int> = listOf(0, 1, 2, 3, 10, 11, 12)
    ) = MockResponse()
        .setResponseCode(200)
        .setHeader("Content-Type", "application/json")
        .setBody(
            """
            {
              "deviceId": "device-1", "organizationId": "org-1", "branchId": "branch-1",
              "deviceIdentifier": "$installationId", "status": 1, "isRevoked": $isRevoked,
              "deviceType": $deviceType,
              "platformCapabilities": ${capabilities.joinToString(prefix = "[", postfix = "]")},
              "canCaptureSms": ${capabilities.contains(0)},
              "syncConfiguration": {
                "maxBatchSize": 100, "maxClockSkewAheadSeconds": 300, "maxBacklogAgeDays": 90
              },
              "serverTimeUtc": "2026-08-16T09:31:00+00:00"
            }
            """.trimIndent()
        )

    private fun enrolResponse() = MockResponse()
        .setResponseCode(201)
        .setHeader("Content-Type", "application/json")
        .setBody(
            """
            {
              "deviceId": "device-1", "organizationId": "org-1", "branchId": "branch-1",
              "name": "Test Handset", "role": 1, "status": 1,
              "enrolledAtUtc": "2026-08-16T09:31:00+00:00"
            }
            """.trimIndent()
        )
}
