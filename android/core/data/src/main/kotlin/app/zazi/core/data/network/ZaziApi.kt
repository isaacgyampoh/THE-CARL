package app.zazi.core.data.network

import app.zazi.core.domain.sync.SyncTransactionsRequest
import app.zazi.core.domain.sync.SyncTransactionsResponse
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import retrofit2.Response
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.Header
import retrofit2.http.POST

/**
 * Zazi's HTTP surface, as the backend actually defines it.
 *
 * <p>Request and response bodies reuse the domain wire contracts from <c>:core:domain</c>
 * rather than redeclaring them. There is one sync contract; a client-local copy would be
 * free to drift from the server's.</p>
 *
 * <p>Note what is absent: no endpoint takes an <c>organizationId</c>. Tenancy is derived
 * server-side from the access token, so there is nothing here for a client to tamper with.</p>
 */
interface ZaziApi {

    /**
     * Submits a batch of offline-captured transactions.
     *
     * <p>Returns HTTP 200 even when individual items fail — the outcome is per item, never
     * inferred from the status code. Callers must read every result.</p>
     */
    @POST("api/v1/sync/transactions")
    suspend fun syncTransactions(
        @Body request: SyncTransactionsRequest
    ): Response<SyncTransactionsResponse>

    /**
     * Reports this device's own live state, including the capabilities and sync limits the
     * client must honour rather than hardcode.
     */
    /**
     * Reports what the handset observed. Accepted-and-forgotten by design: the client never
     * waits on it and never retries it as though it mattered.
     */
    @POST("api/v1/telemetry/events")
    suspend fun recordTelemetry(@Body request: TelemetryBatchRequest): Response<Unit>

    /**
     * Sends one provider message an agent says was read wrongly.
     *
     * <p>The only call that carries a message body off this handset. Everything else — sync
     * included — sends the parsed result and leaves the text here. This one is sent because
     * an agent tapped a button about a specific transaction, and never in the background.</p>
     */
    @POST("api/v1/parsing-reports")
    suspend fun reportParsing(
        @Body request: SubmitParsingReportRequest
    ): Response<ParsingReportReceipt>

    @GET("api/v1/devices/me")
    suspend fun deviceSelf(
        @Header("X-Device-Identifier") deviceIdentifier: String
    ): Response<DeviceSelfResponse>

    /**
     * Redeems an enrolment code, creating this device.
     *
     * <p>Authenticated but not gated on <c>device.manage</c> — enrolment is precisely the
     * case where an agent does not hold that capability.</p>
     */
    @POST("api/v1/devices/enrol")
    suspend fun enrolDevice(@Body request: EnrolDeviceRequest): Response<DeviceEnrolledResponse>

    /**
     * Activates this handset from an owner-issued code, with no prior authentication.
     *
     * <p>The only call in this interface that carries no token. The code is the credential,
     * and the response is a real session — the same one login returns.</p>
     */
    @POST("api/v1/devices/activate")
    suspend fun activateDevice(@Body request: ActivateDeviceRequest): Response<DeviceActivationResponse>
}

/**
 * Authentication endpoints.
 *
 * <p>Kept on a separate interface, served by a client <b>without</b> the auth interceptor:
 * login is anonymous, and a 401 on refresh must not recurse back into refresh.</p>
 */
interface ZaziAuthApi {

    @POST("api/v1/auth/login")
    suspend fun login(@Body request: LoginRequest): Response<AuthTokenResponse>

    @POST("api/v1/auth/refresh")
    suspend fun refresh(@Body request: RefreshTokenRequest): Response<AuthTokenResponse>
}

/**
 * Login credentials.
 *
 * <p>[deviceIdentifier] binds the resulting session to a registered device, so revoking the
 * device immediately stops both refresh and sync. Omitted before enrolment, when no device
 * identity exists yet.</p>
 */
@Serializable
data class LoginRequest(
    val email: String,
    val password: String,
    val deviceIdentifier: String? = null
)

/**
 * Redeems an enrolment code.
 *
 * <p>Note what is absent: no organization, branch, role or device type. All of those come
 * from the code the administrator issued. A handset that could name its own scope could
 * enrol itself into another branch or grant itself SMS capture.</p>
 */
@Serializable
data class EnrolDeviceRequest(
    val code: String,
    val deviceIdentifier: String,
    val name: String,
    val platform: String,
    val network: String,
    val appVersion: String,
    val osVersion: String
)

@Serializable
data class DeviceEnrolledResponse(
    val deviceId: String,
    val organizationId: String,
    val branchId: String,
    val name: String,
    val role: Int,
    val status: Int,
    val enrolledAtUtc: String
)

/**
 * Activation request.
 *
 * <p>Deliberately identical in shape to [EnrolDeviceRequest] minus nothing — and carrying no
 * organization, branch or role, because the server reads those from the code. A handset that
 * could name its own scope could join another business.</p>
 */
@Serializable
data class ActivateDeviceRequest(
    val code: String,
    val deviceIdentifier: String,
    val name: String,
    val platform: String,
    val network: String,
    val appVersion: String,
    val osVersion: String
)

/** What the server tells a freshly activated handset: a session, and who it belongs to. */
@Serializable
data class DeviceActivationResponse(
    val session: AuthTokenResponse,
    val deviceId: String,
    val deviceName: String,
    val branchId: String,
    val branchName: String,
    val organizationName: String,
    val workerName: String
)

@Serializable
data class RefreshTokenRequest(val refreshToken: String)

@Serializable
data class AuthTokenResponse(
    val accessToken: String,
    val refreshToken: String,
    val expiresAtUtc: String,
    val user: AuthUserResponse
)

@Serializable
data class AuthUserResponse(
    val id: String,
    val organizationId: String,
    val branchId: String? = null,
    /**
     * Null for a worker activated by code, who has no account of their own.
     *
     * <p>This was non-nullable, and activation failed on it: the server legitimately sends
     * null for such a worker, deserialization threw, and the catch-all turned it into an
     * opaque ServerError(0). A required field is a contract, and the contract changed.</p>
     */
    val email: String? = null,
    val fullName: String,
    val roles: List<String> = emptyList()
)

/**
 * Device self-state.
 *
 * <p>[syncConfiguration] is the reason a client never hardcodes a batch size: the server's
 * limit is configurable, and a mismatched constant is discovered only by being rejected
 * with 413.</p>
 */
@Serializable
data class DeviceSelfResponse(
    val deviceId: String,
    val organizationId: String,
    val branchId: String,
    // Null-tolerant: a server older than this field simply omits it, and the handset
    // shows the branch reference instead of a name rather than failing to deserialise.
    val branchName: String? = null,
    val deviceIdentifier: String,
    val status: Int,
    val isRevoked: Boolean,
    val deviceType: Int,
    val platformCapabilities: List<Int> = emptyList(),
    val canCaptureSms: Boolean = false,
    val syncConfiguration: SyncConfigurationResponse? = null,
    @SerialName("serverTimeUtc") val serverTimeUtc: String
)

@Serializable
data class SyncConfigurationResponse(
    val maxBatchSize: Int,
    val maxClockSkewAheadSeconds: Int,
    val maxBacklogAgeDays: Int
)

/** RFC 7807 problem response. Parsed so failures carry a correlation id into local logs. */
@Serializable
data class ProblemDetails(
    val status: Int? = null,
    val title: String? = null,
    val type: String? = null,
    val correlationId: String? = null,
    val maxBatchSize: Int? = null
)

/**
 * One upload of client-observed events.
 *
 * <p>Carries no tenant, user or device: the server establishes those from the authenticated
 * request, so a handset cannot attribute its events to anyone else.</p>
 */
@Serializable
data class TelemetryBatchRequest(
    val events: List<ClientTelemetryEvent>
)

/**
 * One observed event.
 *
 * <p>There is deliberately no field for a token, a password, a request or response body, or
 * an exception message. Failures travel as [errorCode], a value from a closed vocabulary, so
 * this cannot become a way to move message contents off the device.</p>
 */
@Serializable
data class ClientTelemetryEvent(
    val eventType: String,
    val severity: String? = null,
    val status: String? = null,
    val errorCode: String? = null,
    val details: String? = null,
    val durationMs: Int? = null,
    val correlationId: String? = null,
    val appVersion: String? = null,
    val platform: String? = null,
    val occurredAtUtc: String? = null
)

/**
 * An agent's report that Zazi read one of their messages wrongly.
 *
 * <p>No organisation, branch or user field: all three come from the access token, so a handset
 * cannot file a report against another tenant's transaction.</p>
 */
@Serializable
data class SubmitParsingReportRequest(
    val clientTransactionId: String,
    val rawMessage: String,
    /** ParsingReportVerdict as its server-side name. */
    val verdict: String,
    val deviceId: String? = null,
    val senderIdentity: String? = null,
    val observedNetwork: String? = null,
    /** TransactionType as its server-side name. */
    val observedType: String? = null,
    val observedAmountMinor: Long = 0,
    val note: String? = null,
    val parserVersion: String? = null,
    val appVersion: String? = null
)

@Serializable
data class ParsingReportReceipt(
    val reportId: String,
    /** True when this agent had already reported this transaction. Not an error. */
    val alreadyReported: Boolean = false
)
