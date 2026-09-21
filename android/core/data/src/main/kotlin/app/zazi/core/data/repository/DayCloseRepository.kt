package app.zazi.core.data.repository

import app.zazi.core.data.network.DayCloseRequest
import app.zazi.core.data.network.DayCloseResponse
import app.zazi.core.data.network.ZaziApi

sealed interface DayCloseOutcome {
    data class Closed(val result: DayCloseResponse) : DayCloseOutcome

    /** No connection, or the server refused. Nothing was recorded. */
    data class Failed(val reason: String) : DayCloseOutcome
}

/**
 * Sends the agent's end-of-day count.
 *
 * <p>Needs a connection, deliberately: the comparison is made on the server against every
 * phone the agent used today, and an answer worked out on this handset alone would call a
 * keypad-phone cash out a shortage. Without data the agent can text CLOSE instead.</p>
 */
class DayCloseRepository(private val api: ZaziApi) {

    suspend fun close(cashMinor: Long, floatMinor: Long): DayCloseOutcome {
        val response = runCatching {
            api.closeDay(DayCloseRequest(countedCash = cashMinor / 100.0, countedFloat = floatMinor / 100.0))
        }.getOrElse {
            return DayCloseOutcome.Failed("No connection. Closing needs data or Wi-Fi — or text CLOSE cash float to Zazi.")
        }

        if (!response.isSuccessful) {
            return DayCloseOutcome.Failed(
                if (response.code() == 400) "Check the figures and try again."
                else "The close could not be recorded (${response.code()}). Try again."
            )
        }

        return response.body()?.let(DayCloseOutcome::Closed)
            ?: DayCloseOutcome.Failed("The answer came back empty. Try again.")
    }
}
