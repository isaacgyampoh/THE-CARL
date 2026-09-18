package app.zazi.core.data

import app.zazi.core.data.capture.CaptureOutcome
import app.zazi.core.data.capture.ManualCaptureRequest
import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.data.network.ZaziApi
import app.zazi.core.data.network.ZaziAuthApi
import app.zazi.core.data.repository.CaptureRepository
import app.zazi.core.data.repository.OutboxRepository
import app.zazi.core.data.security.KeystoreCredentialStore
import app.zazi.core.data.session.ActivationResult
import app.zazi.core.data.session.EnrolmentResult
import app.zazi.core.data.session.LoginResult
import app.zazi.core.data.session.SessionRepository
import app.zazi.core.data.session.SessionState
import app.zazi.core.domain.model.PlatformCapability
import app.zazi.core.domain.model.Provider
import app.zazi.core.domain.model.TransactionType
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
    private lateinit var database: ZaziDatabase
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
            authApi = retrofit.create(ZaziAuthApi::class.java),
            api = retrofit.create(ZaziApi::class.java),
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

        val result = session.enrolDevice("ZAZI-ABCD-EFGH")

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
        session.enrolDevice("ZAZI-ABCD-EFGH")

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

        assertThat(session.enrolDevice("ZAZI-EXPIRED")).isEqualTo(EnrolmentResult.InvalidOrExpiredCode)
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

    // ─── Worker activation ───────────────────────────────────────────────────

    @Test
    fun `activation establishes a session with no prior sign-in`() = runTest {
        // Nothing stored. This is a handset out of the box, which is exactly the case the
        // old flow could not serve: enrolment needed a token, and a token needed an account.
        assertThat(credentialStore.read()).isNull()

        server.enqueue(activationResponse())
        server.enqueue(deviceSelfResponse())

        val result = session.activate("ZAZI-ABCD-EFGH-JKMN-PQRS")

        assertThat(result).isInstanceOf(ActivationResult.Success::class.java)
        val success = result as ActivationResult.Success
        assertThat(success.identity.workerName).isEqualTo("Ama Mensah")
        assertThat(success.identity.organizationName).isEqualTo("Mensah Mobile Money")
        assertThat(success.identity.branchName).isEqualTo("Accra Central")

        // A real stored session, not a marker.
        val stored = credentialStore.read()
        assertThat(stored).isNotNull()
        assertThat(stored!!.accessToken).isEqualTo("access-token-1")
        assertThat(stored.deviceId).isEqualTo("device-1")

        assertThat(session.state.value).isInstanceOf(SessionState.Active::class.java)
    }

    @Test
    fun `the activation request never names an organization or branch`() = runTest {
        server.enqueue(activationResponse())
        server.enqueue(deviceSelfResponse())

        session.activate("ZAZI-ABCD-EFGH-JKMN-PQRS")

        val body = server.takeRequest().body.readUtf8()

        // Scope is the server's to decide. If the handset ever started sending it, this is
        // the test that would notice.
        assertThat(body).doesNotContain("organizationId")
        assertThat(body).doesNotContain("branchId")
        assertThat(body).doesNotContain("role")
    }

    @Test
    fun `the activation request carries no authorization header`() = runTest {
        server.enqueue(activationResponse())
        server.enqueue(deviceSelfResponse())

        session.activate("ZAZI-ABCD-EFGH-JKMN-PQRS")

        // The whole point of the endpoint. If an interceptor ever started attaching a token
        // here, activation would silently start depending on having one.
        assertThat(server.takeRequest().getHeader("Authorization")).isNull()
    }

    @Test
    fun `an unusable code is reported as one thing`() = runTest {
        // The server answers 401 for invalid, expired, revoked, spent, attempt-limited and
        // worker-disabled alike. The client must not invent a distinction the server
        // deliberately refuses to make.
        // Mirrors startup: the application restores before showing anything, which is what
        // moves the repository off Initialising.
        session.restore()
        server.enqueue(MockResponse().setResponseCode(401))

        assertThat(session.activate("ZAZI-WRONG")).isEqualTo(ActivationResult.CodeNotValid)
        assertThat(credentialStore.read()).isNull()
        assertThat(session.state.value).isEqualTo(SessionState.SignedOut)
    }

    @Test
    fun `an already registered handset is reported distinctly`() = runTest {
        server.enqueue(MockResponse().setResponseCode(409))

        // Distinct because the recovery differs: this one needs the owner to reset the
        // device, not to issue another code.
        assertThat(session.activate("ZAZI-ABCD")).isEqualTo(ActivationResult.AlreadyActivated)
    }

    @Test
    fun `rate limiting is surfaced with the server's retry hint`() = runTest {
        server.enqueue(MockResponse().setResponseCode(429).setHeader("Retry-After", "45"))

        val result = session.activate("ZAZI-ABCD")

        assertThat(result).isEqualTo(ActivationResult.RateLimited(45))
    }

    @Test
    fun `activation cannot happen offline`() = runTest {
        server.shutdown()

        // Stated as a product fact, not a failure: only the server can say who this worker
        // is, so a brand-new handset genuinely cannot activate without reaching it.
        assertThat(session.activate("ZAZI-ABCD")).isEqualTo(ActivationResult.NetworkUnavailable)
        assertThat(credentialStore.read()).isNull()
    }

    @Test
    fun `a failed activation leaves no session behind`() = runTest {
        session.restore()
        server.enqueue(MockResponse().setResponseCode(500))

        session.activate("ZAZI-ABCD")

        // A half-activated handset that believes it has a session is worse than one that
        // knows it has none.
        assertThat(credentialStore.read()).isNull()
        assertThat(session.state.value).isEqualTo(SessionState.SignedOut)
    }

    @Test
    fun `captured work survives a failed activation`() = runTest {
        // The invariant this whole file exists for, checked on the new path too: a
        // transaction is an agent's financial record and does not depend on authentication.
        capture.captureManual(cashIn("40.00")) as CaptureOutcome.Queued

        server.enqueue(MockResponse().setResponseCode(401))
        session.activate("ZAZI-WRONG")

        assertThat(database.outboxDao().count()).isEqualTo(1)
    }

    @Test
    fun `an activated session remembers it has no account to return to`() = runTest {
        server.enqueue(activationResponse())
        server.enqueue(deviceSelfResponse())

        session.activate("ZAZI-ABCD-EFGH-JKMN-PQRS")

        // The handset has to know this to tell the truth about signing out: a worker
        // activated by code cannot sign back in, and getting the phone working again needs
        // their owner. Saying "sign out" unqualified offers them something that is not there.
        assertThat(credentialStore.read()!!.isActivationOnly).isTrue()

        val active = session.state.value as SessionState.Active
        assertThat(active.user.isActivationOnly).isTrue()
    }

    @Test
    fun `a password session knows it can sign back in`() = runTest {
        server.enqueue(loginResponse())
        server.enqueue(deviceSelfResponse())

        session.login("agent@carl.test", "correct-horse")

        assertThat(credentialStore.read()!!.isActivationOnly).isFalse()
    }

    @Test
    fun `the flag survives a restart`() = runTest {
        server.enqueue(activationResponse())
        server.enqueue(deviceSelfResponse())
        session.activate("ZAZI-ABCD-EFGH-JKMN-PQRS")

        // Restored from storage rather than held in memory, so the confirmation is still
        // truthful the next morning.
        server.enqueue(deviceSelfResponse())
        val restored = session.restore()

        assertThat((restored as SessionState.Active).user.isActivationOnly).isTrue()
    }

    private fun activationResponse() = MockResponse()
        .setResponseCode(200)
        .setHeader("Content-Type", "application/json")
        .setBody(
            """
            {
              "session": {
                "accessToken": "access-token-1",
                "refreshToken": "refresh-token-1",
                "expiresAtUtc": "2030-01-01T00:00:00Z",
                "user": {
                  "id": "user-1", "organizationId": "org-1", "branchId": "branch-1",
                  "email": null, "fullName": "Ama Mensah", "roles": ["AGENT"]
                }
              },
              "deviceId": "device-1",
              "deviceName": "Test Handset",
              "branchId": "branch-1",
              "branchName": "Accra Central",
              "organizationName": "Mensah Mobile Money",
              "workerName": "Ama Mensah"
            }
            """.trimIndent()
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
