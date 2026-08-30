package app.zazi.core.domain.sync

/**
 * Durable state of a locally created transaction awaiting submission.
 *
 * Strongly typed rather than strings: an outbox row is the only thing standing between an
 * agent's work and its permanent loss, and a typo in a string state silently strands it.
 */
enum class OutboxState {
    /** Written locally, not yet attempted. */
    PENDING,

    /**
     * A request is in flight. Persisted **before** the call, not after — a crash mid-request
     * must leave evidence that an attempt happened, otherwise recovery cannot tell a
     * never-sent row from a possibly-committed one.
     */
    SYNCING,

    /** The server confirmed it durably. Terminal. */
    SYNCED,

    /** Failed for a retryable reason; waiting on backoff. */
    RETRYABLE_FAILURE,

    /** The server disagrees and a person must decide. Terminal until resolved. */
    CONFLICT,

    /** Retry budget exhausted, or permanently rejected. Never deleted, always inspectable. */
    DEAD_LETTER;

    val isTerminal: Boolean get() = this == SYNCED || this == DEAD_LETTER || this == CONFLICT

    /** Whether the sync worker should pick this row up. */
    val isEligibleForSync: Boolean get() = this == PENDING || this == RETRYABLE_FAILURE
}

/**
 * Deterministic retry schedule.
 *
 * Exponential with jitter, capped. Jitter matters because a branch's devices reconnect
 * together when a network returns; without it they retry in lockstep and hammer the server.
 */
object RetryPolicy {
    const val MAX_ATTEMPTS = 10

    private const val BASE_DELAY_MILLIS = 1_000L
    private const val MAX_DELAY_MILLIS = 15 * 60 * 1_000L

    /**
     * Backoff for the given attempt number (1-based), with up to 20% jitter applied by the
     * supplied randomiser.
     */
    fun delayMillisFor(attempt: Int, jitterFraction: Double = 0.0): Long {
        require(attempt >= 1) { "Attempt numbers are 1-based." }
        require(jitterFraction in 0.0..1.0) { "Jitter fraction must be between 0 and 1." }

        // Shift caps at 2^30 to avoid overflow on an absurd attempt count.
        val exponent = (attempt - 1).coerceAtMost(30)
        val raw = BASE_DELAY_MILLIS shl exponent
        val capped = if (raw <= 0 || raw > MAX_DELAY_MILLIS) MAX_DELAY_MILLIS else raw

        val jitter = (capped * 0.2 * jitterFraction).toLong()
        return capped + jitter
    }

    fun hasBudgetRemaining(attempts: Int): Boolean = attempts < MAX_ATTEMPTS
}
