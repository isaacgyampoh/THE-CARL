package app.zazi.core.domain.telemetry

/**
 * Something the handset observed, in the shape the server already records.
 *
 * <p>This is the client half of the existing audit model, not a second one: each of these
 * becomes an `AuditLogEntry` beside the entries the server writes for itself, so one timeline
 * can show a handset's attempt and the server's response to it.</p>
 *
 * <p><b>It carries no secret and no payload.</b> There is deliberately no field for a token,
 * a password, a request body, a response body, an exception message or a customer detail.
 * Failures are described by [errorCode] drawn from a fixed vocabulary, so telemetry cannot
 * become a way to move message contents off the device.</p>
 *
 * <p>Tenant, user and device are absent on purpose. The server establishes those from the
 * authenticated request; a handset that could name them could attribute its events to
 * someone else.</p>
 */
data class TelemetryEvent(
    val eventType: TelemetryEventType,
    val severity: TelemetrySeverity = TelemetrySeverity.INFORMATION,
    val status: TelemetryStatus? = null,
    val errorCode: TelemetryErrorCode? = null,

    /**
     * A short, operator-readable summary built from fixed vocabulary — never an exception
     * message, and never anything read from a transaction or a message body.
     */
    val details: String? = null,

    val durationMillis: Long? = null,

    /** Ties this to the HTTP request it describes, and to the server's own entry for it. */
    val correlationId: String? = null,

    val occurredAtUtcMillis: Long = System.currentTimeMillis()
)

/**
 * The vocabulary of things worth reporting.
 *
 * <p>A closed set rather than free strings: the dashboard groups on these, and a typo in one
 * release would otherwise look like a new kind of failure.</p>
 */
enum class TelemetryEventType {
    APP_START,
    CONTAINER_INIT_STARTED,
    CONTAINER_INIT_COMPLETED,
    DATABASE_INIT,
    NETWORK_INIT,

    LOGIN_ATTEMPT,
    LOGIN_SUCCESS,
    LOGIN_FAILURE,
    LOGOUT,
    SESSION_EXPIRED,

    ENROLMENT_STARTED,
    ENROLMENT_SUCCESS,
    ENROLMENT_FAILURE,

    API_REQUEST,

    SYNC_STARTED,
    SYNC_COMPLETED,
    SYNC_FAILED,

    CLIENT_TRANSACTION_CAPTURED,
    CLIENT_TRANSACTION_SUBMITTED,
    CLIENT_TRANSACTION_FAILED
}

enum class TelemetrySeverity { INFORMATION, WARNING, ERROR }

enum class TelemetryStatus { SUCCEEDED, FAILED, REJECTED, HELD }

/**
 * Stable failure classifications.
 *
 * <p>The dashboard fingerprints recurring problems on these, so they must not carry anything
 * that varies between occurrences — no timestamps, no identifiers, no message text. The
 * distinction that matters most in the field is the first three: "the server said no" is a
 * different problem from "the phone could not reach the server".</p>
 */
enum class TelemetryErrorCode {
    /** The request never reached a server: no route, no signal. */
    ANDROID_NO_NETWORK,

    /** A server was addressable but did not answer in time. */
    ANDROID_NETWORK_TIMEOUT,

    ANDROID_DNS_FAILURE,
    ANDROID_CONNECTION_FAILURE,
    ANDROID_TLS_FAILURE,

    /** The server answered and refused. Distinct from every code above. */
    ANDROID_API_4XX,
    ANDROID_API_5XX,

    ANDROID_INVALID_CREDENTIALS,
    ANDROID_SERVER_REJECTED,

    ANDROID_DB_INIT_FAILURE,
    ANDROID_CONTAINER_INIT_FAILURE,
    ANDROID_ENROLMENT_FAILURE,

    ANDROID_UNKNOWN
}
