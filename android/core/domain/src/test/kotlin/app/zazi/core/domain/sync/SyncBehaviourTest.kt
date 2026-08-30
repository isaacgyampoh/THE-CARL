package app.zazi.core.domain.sync

import com.google.common.truth.Truth.assertThat
import kotlinx.serialization.json.Json
import org.junit.Test

/**
 * How the client reacts to server responses and transport failures.
 *
 * The rule these all serve: never lose a transaction, never post one twice.
 */
class SyncResultInterpreterTest {

    private fun result(
        status: String,
        transactionId: String? = null,
        reasonCode: String? = null,
        retryable: Boolean = false
    ) = SyncTransactionResult(
        clientTransactionId = "CTX-9f3a1c07-01HQ8Z7K3M4N5P6Q7R8S9T0V1W",
        status = status,
        transactionId = transactionId,
        reasonCode = reasonCode,
        isRetryable = retryable
    )

    @Test
    fun `an accepted transaction becomes synced`() {
        val next = SyncResultInterpreter.nextState(result(SyncItemStatus.ACCEPTED, "srv-1"), 1)

        assertThat(next).isEqualTo(OutboxState.SYNCED)
    }

    @Test
    fun `a duplicate becomes synced rather than failed`() {
        // The lost-response case: the server committed and the reply never arrived. Treating
        // this as a failure is what turns one transaction into an endless retry.
        val next = SyncResultInterpreter.nextState(
            result(SyncItemStatus.DUPLICATE, "srv-1", SyncReasonCodes.DUPLICATE_CLIENT_TRANSACTION_ID), 3
        )

        assertThat(next).isEqualTo(OutboxState.SYNCED)
    }

    @Test
    fun `a duplicate carries the original server transaction id`() {
        // Without this the client cannot finish reconciling the row it already sent.
        val duplicate = result(SyncItemStatus.DUPLICATE, "srv-original")

        assertThat(SyncResultInterpreter.serverTransactionId(duplicate)).isEqualTo("srv-original")
    }

    @Test
    fun `a conflict is never silently retried`() {
        val next = SyncResultInterpreter.nextState(
            result(SyncItemStatus.CONFLICT, reasonCode = SyncReasonCodes.ALREADY_REVERSED), 1
        )

        assertThat(next).isEqualTo(OutboxState.CONFLICT)
        assertThat(OutboxState.CONFLICT.isTerminal).isTrue()
        assertThat(OutboxState.CONFLICT.isEligibleForSync).isFalse()
    }

    @Test
    fun `a permanent rejection dead-letters instead of retrying forever`() {
        val next = SyncResultInterpreter.nextState(
            result(SyncItemStatus.REJECTED, reasonCode = "INVALID_AMOUNT", retryable = false), 1
        )

        assertThat(next).isEqualTo(OutboxState.DEAD_LETTER)
    }

    @Test
    fun `a retryable rejection retries while budget remains`() {
        val next = SyncResultInterpreter.nextState(
            result(SyncItemStatus.REJECTED, reasonCode = "TEMPORARY_SERVER_ERROR", retryable = true), 2
        )

        assertThat(next).isEqualTo(OutboxState.RETRYABLE_FAILURE)
    }

    @Test
    fun `a retryable rejection dead-letters once the budget is spent`() {
        val next = SyncResultInterpreter.nextState(
            result(SyncItemStatus.REJECTED, retryable = true), RetryPolicy.MAX_ATTEMPTS
        )

        assertThat(next).isEqualTo(OutboxState.DEAD_LETTER)
    }

    @Test
    fun `an unrecognised status retries rather than being discarded`() {
        // A newer server may return a status this build does not know. Discarding a possibly
        // valid transaction is worse than retrying one that will never succeed.
        val next = SyncResultInterpreter.nextState(result("SomeFutureStatus"), 1)

        assertThat(next).isEqualTo(OutboxState.RETRYABLE_FAILURE)
    }

    @Test
    fun `a dead-lettered row is never eligible for automatic sync but is still inspectable`() {
        assertThat(OutboxState.DEAD_LETTER.isEligibleForSync).isFalse()
        assertThat(OutboxState.DEAD_LETTER.isTerminal).isTrue()
    }

    @Test
    fun `only pending and retryable rows are picked up by the worker`() {
        val eligible = OutboxState.entries.filter { it.isEligibleForSync }

        assertThat(eligible).containsExactly(OutboxState.PENDING, OutboxState.RETRYABLE_FAILURE)
    }
}

class TransportFailurePolicyTest {

    @Test
    fun `no response at all is retryable, never a discard`() {
        // The server may have committed. Assuming failure and dropping the row loses money.
        assertThat(TransportFailurePolicy.nextState(httpStatus = null, attemptsSoFar = 1))
            .isEqualTo(OutboxState.RETRYABLE_FAILURE)
    }

    @Test
    fun `server faults are retryable`() {
        listOf(500, 502, 503, 504).forEach { status ->
            assertThat(TransportFailurePolicy.nextState(status, 1))
                .isEqualTo(OutboxState.RETRYABLE_FAILURE)
        }
    }

    @Test
    fun `rate limiting is always retryable regardless of budget`() {
        // A 429 says "later", not "never". Dead-lettering on it would discard valid work
        // simply because the agent came back online during a busy period.
        assertThat(TransportFailurePolicy.nextState(429, RetryPolicy.MAX_ATTEMPTS + 5))
            .isEqualTo(OutboxState.RETRYABLE_FAILURE)
    }

    @Test
    fun `authentication failures stay retryable so work survives a token refresh`() {
        assertThat(TransportFailurePolicy.nextState(401, 1)).isEqualTo(OutboxState.RETRYABLE_FAILURE)
        assertThat(TransportFailurePolicy.requiresTokenRefresh(401)).isTrue()
    }

    @Test
    fun `a malformed or oversized batch is not retried unchanged`() {
        assertThat(TransportFailurePolicy.nextState(400, 1)).isEqualTo(OutboxState.DEAD_LETTER)
        assertThat(TransportFailurePolicy.nextState(413, 1)).isEqualTo(OutboxState.DEAD_LETTER)
    }

    @Test
    fun `revocation is detected from either the status or the reason code`() {
        assertThat(TransportFailurePolicy.indicatesRevocation(403, null)).isTrue()
        assertThat(TransportFailurePolicy.indicatesRevocation(200, SyncReasonCodes.DEVICE_REVOKED)).isTrue()
        assertThat(TransportFailurePolicy.indicatesRevocation(500, null)).isFalse()
    }
}

class RetryPolicyTest {

    @Test
    fun `backoff grows exponentially`() {
        val first = RetryPolicy.delayMillisFor(1)
        val second = RetryPolicy.delayMillisFor(2)
        val third = RetryPolicy.delayMillisFor(3)

        assertThat(first).isEqualTo(1_000L)
        assertThat(second).isEqualTo(2_000L)
        assertThat(third).isEqualTo(4_000L)
    }

    @Test
    fun `backoff is capped at fifteen minutes`() {
        // Without a cap, attempt 20 would schedule a retry years away.
        assertThat(RetryPolicy.delayMillisFor(30)).isAtMost(15 * 60 * 1_000L)
        assertThat(RetryPolicy.delayMillisFor(1_000)).isAtMost(15 * 60 * 1_000L)
    }

    @Test
    fun `jitter spreads retries without shortening them`() {
        // A branch's devices reconnect together when a network returns; identical backoff
        // makes them hammer the server in lockstep.
        val base = RetryPolicy.delayMillisFor(5, jitterFraction = 0.0)
        val jittered = RetryPolicy.delayMillisFor(5, jitterFraction = 1.0)

        assertThat(jittered).isGreaterThan(base)
        assertThat(jittered).isAtMost((base * 1.2).toLong())
    }

    @Test
    fun `budget runs out after the maximum attempts`() {
        assertThat(RetryPolicy.hasBudgetRemaining(RetryPolicy.MAX_ATTEMPTS - 1)).isTrue()
        assertThat(RetryPolicy.hasBudgetRemaining(RetryPolicy.MAX_ATTEMPTS)).isFalse()
    }
}

class SyncWireContractTest {
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }

    @Test
    fun `a request serialises without any organization identifier`() {
        // Tenancy comes from the access token. A client that could name an organization
        // would be a tenant-isolation hole.
        val request = SyncTransactionsRequest(
            listOf(
                SyncTransactionRequestItem(
                    clientTransactionId = "CTX-9f3a1c07-01HQ8Z7K3M4N5P6Q7R8S9T0V1W",
                    transactionType = 0,
                    amount = "500.00",
                    provider = "MTN",
                    transactionTimestamp = "2026-08-15T09:30:00Z",
                    sourceType = 0
                )
            )
        )

        val encoded = json.encodeToString(SyncTransactionsRequest.serializer(), request)

        assertThat(encoded).doesNotContain("organizationId")
        assertThat(encoded).contains("clientTransactionId")
        assertThat(encoded).contains("\"transactionType\":0")
    }

    @Test
    fun `a real server response shape deserialises`() {
        // Captured from the .NET SyncTransactionsResponse contract.
        val payload = """
            {
              "batchId": "0f9c2f0e-1f3e-4a1b-9a2c-2f0e1f3e4a1b",
              "submitted": 2, "accepted": 1, "duplicate": 1, "rejected": 0, "conflict": 0,
              "serverReceivedAtUtc": "2026-08-15T09:31:00+00:00",
              "results": [
                { "clientTransactionId": "CTX-a", "status": "Accepted",
                  "transactionId": "11111111-1111-1111-1111-111111111111",
                  "reasonCode": null, "message": null,
                  "category": "None", "isRetryable": false, "conflictId": null },
                { "clientTransactionId": "CTX-b", "status": "Duplicate",
                  "transactionId": "22222222-2222-2222-2222-222222222222",
                  "reasonCode": "DUPLICATE_CLIENT_TRANSACTION_ID",
                  "message": "This submission was already recorded.",
                  "category": "Duplicate", "isRetryable": false, "conflictId": null }
              ]
            }
        """.trimIndent()

        val response = json.decodeFromString(SyncTransactionsResponse.serializer(), payload)

        assertThat(response.submitted).isEqualTo(2)
        assertThat(response.results).hasSize(2)
        assertThat(SyncResultInterpreter.nextState(response.results[0], 1)).isEqualTo(OutboxState.SYNCED)
        assertThat(SyncResultInterpreter.nextState(response.results[1], 1)).isEqualTo(OutboxState.SYNCED)
    }

    @Test
    fun `unknown response fields do not break an older client`() {
        // The server will gain fields. An older build must keep working rather than failing
        // to parse and stranding its outbox.
        val payload = """
            {
              "batchId": "0f9c2f0e-1f3e-4a1b-9a2c-2f0e1f3e4a1b",
              "submitted": 1, "accepted": 1, "duplicate": 0, "rejected": 0, "conflict": 0,
              "serverReceivedAtUtc": "2026-08-15T09:31:00+00:00",
              "someFutureField": { "nested": true },
              "results": [
                { "clientTransactionId": "CTX-a", "status": "Accepted",
                  "transactionId": "11111111-1111-1111-1111-111111111111",
                  "anotherFutureField": 42 }
              ]
            }
        """.trimIndent()

        val response = json.decodeFromString(SyncTransactionsResponse.serializer(), payload)

        assertThat(response.accepted).isEqualTo(1)
    }

    @Test
    fun `amounts travel as strings to avoid floating point rounding`() {
        // Serialising money as a JSON number invites a Double somewhere in the chain.
        val item = SyncTransactionRequestItem(
            clientTransactionId = "CTX-a",
            transactionType = 0,
            amount = "1250000.75",
            provider = "MTN",
            transactionTimestamp = "2026-08-15T09:30:00Z",
            sourceType = 1
        )

        val encoded = json.encodeToString(SyncTransactionRequestItem.serializer(), item)

        assertThat(encoded).contains("\"amount\":\"1250000.75\"")
    }
}
