package app.zazi.core.data.repository

import app.zazi.core.data.database.ZaziDatabase
import app.zazi.core.domain.parser.Masking
import app.zazi.core.data.network.SubmitParsingReportRequest
import app.zazi.core.data.network.ZaziApi

/**
 * What an agent is telling us was wrong, in the terms they would use at a counter.
 *
 * <p>Deliberately coarse. Someone reporting a problem mid-shift picks the first option that is
 * roughly right, so finer distinctions would buy precision that is not there. The message
 * itself carries the detail.</p>
 */
enum class ParsingVerdict(val wireName: String, val label: String) {
    WRONG_DIRECTION("WrongDirection", "Wrong way round"),
    WRONG_AMOUNT("WrongAmount", "Wrong amount"),
    WRONG_NETWORK("WrongNetwork", "Wrong network"),
    NOT_A_TRANSACTION("NotATransaction", "Not a transaction")
}

/** Why a transaction cannot be reported, when it cannot. */
enum class ReportUnavailable {
    /** The transaction is not on this device. */
    NO_SUCH_TRANSACTION,

    /** It was typed in by hand, so there is no provider message to send. */
    NOT_FROM_A_MESSAGE,

    /** The message was cleared by the retention purge. */
    MESSAGE_PURGED
}

sealed interface ReportOutcome {
    /** Received. [alreadyReported] means this agent had already sent it — still a success. */
    data class Sent(val alreadyReported: Boolean) : ReportOutcome

    /** Nothing to send. */
    data class Unavailable(val reason: ReportUnavailable) : ReportOutcome

    /** The server could not be reached or refused it. Nothing was stored anywhere. */
    data class Failed(val statusCode: Int?) : ReportOutcome
}

/**
 * Sends one provider message an agent says Zazi read wrongly.
 *
 * <p>This is the only path by which a message body leaves this handset. The sync payload
 * carries the parsed result — type, amount, network, reference — and never the text, which is
 * the right default for software sitting on other people's financial correspondence. The
 * consequence is that when a transaction comes out wrong, nobody who could fix the parser can
 * see the message that caused it.</p>
 *
 * <p>So the exception is made as narrowly as it can be: one body, about one transaction, sent
 * at the moment an agent taps to say it is wrong. There is no background send, no batch, and
 * no setting that turns it on for everything.</p>
 *
 * <p>Deliberately not routed through the outbox. The outbox retries indefinitely, and a report
 * that keeps re-sending a customer's message while the network flaps is not what the agent
 * agreed to. A failed report is dropped and can be sent again by hand.</p>
 */
class ParsingReportRepository(
    private val database: ZaziDatabase,
    private val api: ZaziApi,
    private val appVersion: String
) {

    private companion object {
        /** EvidenceSourceType.ANDROID_SMS.name — the only source with a body to send. */
        const val SMS_SOURCE = "ANDROID_SMS"
    }

    /**
     * Whether this transaction has a message that could be reported.
     *
     * <p>Asked before the report form is offered, so an agent is never shown a button that
     * cannot do anything.</p>
     */
    suspend fun canReport(clientTransactionId: String): ReportUnavailable? =
        when (val row = database.localTransactionDao().findReportableMessage(clientTransactionId)) {
            null -> ReportUnavailable.NO_SUCH_TRANSACTION
            else -> row.unavailableReason()
        }

    /**
     * Why this row cannot be reported, or null when it can.
     *
     * <p>Decided on the source rather than on whether the body is null, because a manual
     * capture writes an evidence row of its own with no body. Reading only the null would tell
     * an agent who typed a transaction in that their message had been deleted, when there was
     * never a message.</p>
     */
    private fun app.zazi.core.data.database.ReportableMessageRow.unavailableReason() = when {
        sourceType != SMS_SOURCE -> ReportUnavailable.NOT_FROM_A_MESSAGE
        rawMessage.isNullOrBlank() -> ReportUnavailable.MESSAGE_PURGED
        else -> null
    }

    /**
     * Sends the wordings the phone could not read, so they can be fixed without it.
     *
     * <p>Until this existed the only way to learn why an agent's takings were not being
     * recorded was to hold their handset: the messages sat in the phone's own review queue
     * and never left it. A network changes a template, every agent on it stops being
     * recorded, and nobody can see the wording that did it.</p>
     *
     * <p>Sent once each, best effort, and never in place of showing the agent: the message
     * stays in their queue until they deal with it. Customer names and numbers are removed
     * first — what fixes a parser is the shape of a message, never who was in it.</p>
     *
     * @return how many were sent.
     */
    suspend fun reportUnreadable(limit: Int = 25, deviceId: String? = null): Int {
        var sent = 0

        database.evidenceDao().findByState("PENDING_REVIEW")
            .filter { it.sourceType == SMS_SOURCE && it.rawMessage != null && it.reportedAtUtcMillis == null }
            .take(limit)
            .forEach { row ->
                val body = row.rawMessage ?: return@forEach
                val response = runCatching {
                    api.reportParsing(
                        SubmitParsingReportRequest(
                            clientTransactionId = row.evidenceId,
                            rawMessage = Masking.maskPeople(body),
                            verdict = ParsingVerdict.NOT_A_TRANSACTION.wireName,
                            deviceId = deviceId,
                            senderIdentity = row.senderIdentity,
                            observedNetwork = row.provider,
                            observedType = row.transactionType,
                            observedAmountMinor = row.amountMinor ?: 0L,
                            note = "Held on the handset: " + (row.outcomeReason ?: "could not be read"),
                            parserVersion = row.parserVersion,
                            appVersion = appVersion
                        )
                    )
                }.getOrNull()

                if (response?.isSuccessful == true) {
                    database.evidenceDao().markReported(row.evidenceId, System.currentTimeMillis())
                    sent++
                }
            }

        return sent
    }

    suspend fun report(
        clientTransactionId: String,
        verdict: ParsingVerdict,
        note: String? = null,
        deviceId: String? = null
    ): ReportOutcome {
        val row = database.localTransactionDao().findReportableMessage(clientTransactionId)
            ?: return ReportOutcome.Unavailable(ReportUnavailable.NO_SUCH_TRANSACTION)

        // The transaction may still be here while the message that produced it is not, or it
        // may never have come from a message at all. Either way there is nothing to send, and
        // an empty report would spend the agent's goodwill and tell nobody anything.
        row.unavailableReason()?.let { return ReportOutcome.Unavailable(it) }

        val body = row.rawMessage!!

        val response = runCatching {
            api.reportParsing(
                SubmitParsingReportRequest(
                    clientTransactionId = clientTransactionId,
                    // Verbatim. Trimming or normalising here would destroy the evidence — and
                    // the clause that caused the direction defect sat at the very end of the
                    // message, which is exactly what a trim takes.
                    rawMessage = body,
                    verdict = verdict.wireName,
                    deviceId = deviceId,
                    senderIdentity = row.senderIdentity,
                    observedNetwork = row.provider,
                    observedType = row.transactionType,
                    observedAmountMinor = row.amountMinor,
                    note = note?.takeIf { it.isNotBlank() },
                    parserVersion = row.parserVersion,
                    appVersion = appVersion
                )
            )
        }.getOrElse { return ReportOutcome.Failed(statusCode = null) }

        return when {
            response.isSuccessful ->
                ReportOutcome.Sent(alreadyReported = response.body()?.alreadyReported ?: false)

            else -> ReportOutcome.Failed(statusCode = response.code())
        }
    }
}
