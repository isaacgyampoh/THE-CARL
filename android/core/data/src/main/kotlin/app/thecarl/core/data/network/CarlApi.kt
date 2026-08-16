package app.thecarl.core.data.network

import app.thecarl.core.domain.sync.SyncTransactionsRequest
import app.thecarl.core.domain.sync.SyncTransactionsResponse
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import retrofit2.Response
import retrofit2.http.Body
import retrofit2.http.GET
import retrofit2.http.Header
import retrofit2.http.POST

/**
 * THE CARL's HTTP surface, as the backend actually defines it.
 *
 * <p>Request and response bodies reuse the domain wire contracts from <c>:core:domain</c>
 * rather than redeclaring them. There is one sync contract; a client-local copy would be
 * free to drift from the server's.</p>
 *
 * <p>Note what is absent: no endpoint takes an <c>organizationId</c>. Tenancy is derived
 * server-side from the access token, so there is nothing here for a client to tamper with.</p>
 */
interface CarlApi {

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
    @GET("api/v1/devices/me")
    suspend fun deviceSelf(
        @Header("X-Device-Identifier") deviceIdentifier: String
    ): Response<DeviceSelfResponse>
}

/** Authentication endpoints, kept separate so refresh can bypass the auth interceptor. */
interface CarlAuthApi {

    @POST("api/v1/auth/refresh")
    suspend fun refresh(@Body request: RefreshTokenRequest): Response<AuthTokenResponse>
}

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
