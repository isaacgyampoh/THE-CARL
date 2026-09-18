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
    val email: String,
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
