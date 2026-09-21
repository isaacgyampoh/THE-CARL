package app.zazi.core.data.repository

import app.zazi.core.data.network.FloatRequestBody
import app.zazi.core.data.network.FloatRequestInfo
import app.zazi.core.data.network.ZaziApi
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

sealed interface FloatRequestOutcome {
    data class Sent(val request: FloatRequestInfo) : FloatRequestOutcome

    /** No connection, or the server refused — with its reason, which is written for the agent. */
    data class Failed(val reason: String) : FloatRequestOutcome
}

/**
 * Asking the owner for float from the handset.
 *
 * <p>Needs a connection: the owner is told by SMS the moment it is sent, and a request queued
 * on a phone with no data would reach them hours after the customers it was for had gone.
 * Offline, an agent can text FLOAT 500 MTN from any phone instead.</p>
 */
class FloatRequestRepository(private val api: ZaziApi) {

    suspend fun request(network: String, amountMinor: Long): FloatRequestOutcome {
        val response = runCatching { api.requestFloat(FloatRequestBody(network, amountMinor / 100.0)) }
            .getOrElse { return FloatRequestOutcome.Failed("No connection. Try again with data, or text FLOAT 500 MTN to Zazi.") }

        if (!response.isSuccessful) {
            val message = runCatching {
                Json.parseToJsonElement(response.errorBody()?.string().orEmpty())
                    .jsonObject["message"]?.jsonPrimitive?.content
            }.getOrNull()
            return FloatRequestOutcome.Failed(message ?: "The request could not be sent (${response.code()}).")
        }

        return response.body()?.let(FloatRequestOutcome::Sent)
            ?: FloatRequestOutcome.Failed("The answer came back empty. Try again.")
    }

    /** Latest requests, newest first; empty offline. */
    suspend fun mine(): List<FloatRequestInfo> =
        runCatching { api.myFloatRequests() }.getOrNull()?.takeIf { it.isSuccessful }?.body() ?: emptyList()
}
