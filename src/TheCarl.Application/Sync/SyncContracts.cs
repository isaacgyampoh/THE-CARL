using TheCarl.Domain;

namespace TheCarl.Application.Sync;

/// <summary>Outcome of one transaction inside a batch.</summary>
public enum SyncItemStatus
{
    /// <summary>Posted to the ledger for the first time.</summary>
    Accepted,

    /// <summary>
    /// Already recorded. A success, not an error: it means an earlier attempt landed.
    /// Clients must mark the row synced and stop retrying.
    /// </summary>
    Duplicate,

    /// <summary>Permanently invalid. Retrying will never succeed.</summary>
    Rejected,

    /// <summary>Server state disagrees with the submission. Needs a human.</summary>
    Conflict
}

/// <summary>
/// Stable, machine-readable reason codes.
/// </summary>
/// <remarks>
/// These are part of the public API contract: the Android client branches on them. Values
/// are never renamed or repurposed — a new situation gets a new code.
/// </remarks>
public static class SyncReasonCodes
{
    // Duplicate
    public const string DuplicateClientTransactionId = "DUPLICATE_CLIENT_TRANSACTION_ID";
    public const string DuplicateEvidenceFingerprint = "DUPLICATE_EVIDENCE_FINGERPRINT";

    // Rejected — payload
    public const string InvalidClientTransactionId = "INVALID_CLIENT_TRANSACTION_ID";
    public const string InvalidAmount = "INVALID_AMOUNT";
    public const string InvalidTransactionType = "INVALID_TRANSACTION_TYPE";
    public const string InvalidProvider = "INVALID_PROVIDER";
    public const string InvalidEvidenceFingerprint = "INVALID_EVIDENCE_FINGERPRINT";
    public const string InvalidSourceType = "INVALID_SOURCE_TYPE";
    public const string MissingParserVersion = "MISSING_PARSER_VERSION";

    // Rejected — temporal
    public const string TimestampInFuture = "TIMESTAMP_IN_FUTURE";
    public const string TimestampTooOld = "TIMESTAMP_TOO_OLD";

    // Rejected — accounting rules
    public const string UnknownTypeRequiresReview = "UNKNOWN_TYPE_REQUIRES_REVIEW";
    public const string ReversalRequiresOriginal = "REVERSAL_REQUIRES_ORIGINAL";
    public const string ReversalOriginalNotFound = "REVERSAL_ORIGINAL_NOT_FOUND";
    public const string ReversalOriginalNotEligible = "REVERSAL_ORIGINAL_NOT_ELIGIBLE";
    public const string AdjustmentRequiresExplicitDeltas = "ADJUSTMENT_REQUIRES_EXPLICIT_DELTAS";
    public const string AdjustmentRequiresReason = "ADJUSTMENT_REQUIRES_REASON";

    // Rejected — authorization and relationships
    public const string BranchNotInTenant = "BRANCH_NOT_IN_TENANT";
    public const string DeviceNotInTenant = "DEVICE_NOT_IN_TENANT";
    public const string DeviceRevoked = "DEVICE_REVOKED";
    public const string SessionNotInTenant = "SESSION_NOT_IN_TENANT";
    public const string SessionBranchMismatch = "SESSION_BRANCH_MISMATCH";
    public const string SessionNotOpenAtEventTime = "SESSION_NOT_OPEN_AT_EVENT_TIME";

    // Conflict
    public const string ClientIdReusedWithDifferentPayload = "CLIENT_ID_REUSED_WITH_DIFFERENT_PAYLOAD";

    /// <summary>
    /// The original transaction already has an effective reversal. Reversing twice would
    /// create money that never existed, so the second attempt is refused and raised as a
    /// conflict for a person to look at.
    /// </summary>
    public const string AlreadyReversed = "ALREADY_REVERSED";

    // Transient — retryable with backoff
    public const string DatabaseUnavailable = "DATABASE_UNAVAILABLE";
    public const string Timeout = "TIMEOUT";
    public const string TemporaryServerError = "TEMPORARY_SERVER_ERROR";
}

/// <summary>
/// One transaction in a sync batch.
/// </summary>
/// <remarks>
/// There is no <c>OrganizationId</c>: the tenant comes from the access token. Every field
/// here is untrusted client input and is validated server-side, regardless of any validation
/// the Android client performed for its own UX.
/// </remarks>
public sealed record SyncTransactionItem(
    string ClientTransactionId,
    TransactionType TransactionType,
    decimal Amount,
    string Provider,
    DateTimeOffset TransactionTimestamp,
    DateTimeOffset? DeviceReceivedAt = null,
    Guid? BranchId = null,
    Guid? DeviceId = null,
    Guid? SessionId = null,
    string? Currency = null,
    string? CustomerPhone = null,
    string? TransactionReference = null,
    string? EvidenceFingerprint = null,
    string? ParserVersion = null,
    EvidenceSourceType SourceType = EvidenceSourceType.ManualEntry,
    Guid? ReversesTransactionId = null,
    decimal? AdjustmentCashDelta = null,
    decimal? AdjustmentFloatDelta = null,
    string? CorrectionReason = null,
    string? Notes = null);

/// <summary>A batch of transactions captured offline.</summary>
public sealed record SyncTransactionsRequest(IReadOnlyList<SyncTransactionItem> Transactions);

/// <summary>
/// Per-item outcome. Deliberately narrow: it carries what the client needs to advance its
/// outbox and nothing more. No evidence bodies, no internal identifiers.
/// </summary>
public sealed record SyncTransactionResult(
    string ClientTransactionId,
    SyncItemStatus Status,
    Guid? TransactionId,
    string? ReasonCode,
    string? Message)
{
    /// <summary>
    /// Stable category for this outcome. Clients branch on this and <see cref="ReasonCode"/>,
    /// never on <see cref="Message"/>, which is written for people and may change.
    /// </summary>
    public SyncErrorCategory Category => SyncErrorClassification.CategoryFor(ReasonCode);

    /// <summary>Whether retrying this item could ever succeed.</summary>
    public bool IsRetryable => SyncErrorClassification.IsRetryable(Category);

    /// <summary>Conflict record raised for this item, when one was created.</summary>
    public Guid? ConflictId { get; init; }
}

/// <summary>Batch outcome with per-item results and counts.</summary>
public sealed record SyncTransactionsResponse(
    Guid BatchId,
    int Submitted,
    int Accepted,
    int Duplicate,
    int Rejected,
    int Conflict,
    DateTimeOffset ServerReceivedAtUtc,
    IReadOnlyList<SyncTransactionResult> Results);

/// <summary>
/// Trusted context for a sync batch, resolved server-side from the access token.
/// Nothing here originates from the request body.
/// </summary>
public sealed record SyncCallerContext(
    Guid OrganizationId,
    Guid UserId,
    Guid? CallerBranchId,
    bool HasOrganizationWideScope,
    string CorrelationId);

public interface ISyncTransactionService
{
    Task<SyncTransactionsResponse> SynchronizeAsync(
        SyncTransactionsRequest request,
        SyncCallerContext caller,
        CancellationToken cancellationToken = default);
}
