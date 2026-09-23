package app.zazi.core.domain.sync

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Wire contract for `POST /api/v1/sync/transactions`.
 *
 * **Mirrors the backend exactly.** These types were written against the .NET
 * `SyncTransactionApiItem` / `SyncTransactionsResponse` records, not invented. Field names
 * match the server's camelCase JSON.
 *
 * Note there is deliberately **no `organizationId`**. The server derives tenancy from the
 * access token; a client cannot supply or override it. See docs/ANDROID_API_CONTRACT.md.
 */
@Serializable
data class SyncTransactionRequestItem(
    val clientTransactionId: String,
    val transactionType: Int,
    val amount: String,
    val provider: String,
    val transactionTimestamp: String,
    val deviceReceivedAt: String? = null,
    val branchId: String? = null,
    val deviceId: String? = null,
    val sessionId: String? = null,
    val currency: String? = null,
    val customerPhone: String? = null,
    val transactionReference: String? = null,
    val evidenceFingerprint: String? = null,
    val parserVersion: String? = null,
    val sourceType: Int,
    val reversesTransactionId: String? = null,
    val adjustmentCashDelta: String? = null,
    val adjustmentFloatDelta: String? = null,
    val correctionReason: String? = null,
    val notes: String? = null,
    /**
     * The counterparty's registered name, where the network stated one.
     *
     * <p>Last in the list to match the server's contract, which appends rather than inserts:
     * a parameter added to the middle of a positional record shifts every call site, and the
     * compiler only notices where the types differ.</p>
     */
    val customerName: String? = null
)

@Serializable
data class SyncTransactionsRequest(
    val transactions: List<SyncTransactionRequestItem>
)

@Serializable
data class SyncTransactionResult(
    val clientTransactionId: String,
    val status: String,
    val transactionId: String? = null,
    val reasonCode: String? = null,
    val message: String? = null,
    val category: String? = null,
    val isRetryable: Boolean = false,
    val conflictId: String? = null
)

@Serializable
data class SyncTransactionsResponse(
    val batchId: String,
    val submitted: Int,
    val accepted: Int,
    val duplicate: Int,
    val rejected: Int,
    val conflict: Int,
    @SerialName("serverReceivedAtUtc") val serverReceivedAtUtc: String,
    val results: List<SyncTransactionResult>
)

/** Server-side per-item outcomes, as returned in [SyncTransactionResult.status]. */
object SyncItemStatus {
    const val ACCEPTED = "Accepted"

    /**
     * Already recorded. **A success, not an error.** It means an earlier attempt landed —
     * typically one whose response was lost — and the client must mark the row synced using
     * the returned transaction id rather than retrying.
     */
    const val DUPLICATE = "Duplicate"

    const val REJECTED = "Rejected"
    const val CONFLICT = "Conflict"
}

/** Stable server reason codes the client branches on. */
object SyncReasonCodes {
    const val DUPLICATE_CLIENT_TRANSACTION_ID = "DUPLICATE_CLIENT_TRANSACTION_ID"
    const val DUPLICATE_EVIDENCE_FINGERPRINT = "DUPLICATE_EVIDENCE_FINGERPRINT"
    const val ALREADY_REVERSED = "ALREADY_REVERSED"
    const val DEVICE_REVOKED = "DEVICE_REVOKED"
    const val BRANCH_NOT_IN_TENANT = "BRANCH_NOT_IN_TENANT"
    const val DEVICE_NOT_IN_TENANT = "DEVICE_NOT_IN_TENANT"
}

/**
 * Maps a server result onto the local outbox state.
 *
 * This is the single place the client decides what a response means, so the lost-response
 * rule cannot be implemented differently in two call sites.
 */
object SyncResultInterpreter {

    /**
     * @param attemptsSoFar attempts already made, including the one that produced [result].
     */
    fun nextState(result: SyncTransactionResult, attemptsSoFar: Int): OutboxState = when (result.status) {
        SyncItemStatus.ACCEPTED -> OutboxState.SYNCED

        // The lost-response rule. The server already holds this transaction; retrying would
        // never produce anything different, and treating it as failure is what turns one
        // transaction into a retry storm.
        SyncItemStatus.DUPLICATE -> OutboxState.SYNCED

        SyncItemStatus.CONFLICT -> OutboxState.CONFLICT

        SyncItemStatus.REJECTED ->
            if (result.isRetryable && RetryPolicy.hasBudgetRemaining(attemptsSoFar)) {
                OutboxState.RETRYABLE_FAILURE
            } else {
                // Permanently invalid, or out of budget. Never deleted — an agent's record of
                // work must remain inspectable even when it cannot be posted.
                OutboxState.DEAD_LETTER
            }

        // An unrecognised status means the server moved ahead of this client. Retry rather
        // than dead-letter: guessing "permanent" would discard a possibly-valid transaction.
        else ->
            if (RetryPolicy.hasBudgetRemaining(attemptsSoFar)) OutboxState.RETRYABLE_FAILURE
            else OutboxState.DEAD_LETTER
    }

    /**
     * The server transaction id to record locally, if any. Present for both acceptances and
     * duplicates, which is what lets a client reconcile after losing a response.
     */
    fun serverTransactionId(result: SyncTransactionResult): String? = result.transactionId
}

/**
 * How a transport-level failure — no response at all — maps onto the outbox.
 *
 * **A timeout is not a failure to record.** The server may have committed. The row returns
 * to a retryable state carrying the same ClientTransactionId, and the server's idempotency
 * constraint resolves it as a duplicate on the next attempt.
 */
object TransportFailurePolicy {

    fun nextState(httpStatus: Int?, attemptsSoFar: Int): OutboxState {
        val budget = RetryPolicy.hasBudgetRemaining(attemptsSoFar)

        return when (httpStatus) {
            // No response: connection reset, timeout, airplane mode. Always retryable.
            null -> if (budget) OutboxState.RETRYABLE_FAILURE else OutboxState.DEAD_LETTER

            // Authentication and authorization: retrying the batch will not help until the
            // session is repaired, but the transaction itself is still valid work.
            401, 403 -> OutboxState.RETRYABLE_FAILURE

            // Rate limited. Always retryable; the worker honours Retry-After.
            429 -> OutboxState.RETRYABLE_FAILURE

            // Server-side faults are transient by definition.
            in 500..599 -> if (budget) OutboxState.RETRYABLE_FAILURE else OutboxState.DEAD_LETTER

            // The batch itself was malformed or too large. Retrying it unchanged cannot help.
            400, 413 -> OutboxState.DEAD_LETTER

            else -> if (budget) OutboxState.RETRYABLE_FAILURE else OutboxState.DEAD_LETTER
        }
    }

    /** Whether a 401 should trigger a token refresh before the next attempt. */
    fun requiresTokenRefresh(httpStatus: Int?): Boolean = httpStatus == 401

    /**
     * Whether the local authentication state should be cleared outright. A 403 after a
     * successful refresh means the device or session was revoked server-side, and continuing
     * to retry would be pointless.
     */
    fun indicatesRevocation(httpStatus: Int?, reasonCode: String?): Boolean =
        httpStatus == 403 || reasonCode == SyncReasonCodes.DEVICE_REVOKED
}
