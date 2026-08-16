namespace TheCarl.Domain;

/// <summary>Lifecycle of a synchronisation conflict.</summary>
public enum SyncConflictStatus
{
    /// <summary>Raised and awaiting attention.</summary>
    Open = 0,

    /// <summary>An operator has taken ownership.</summary>
    UnderReview = 1,

    /// <summary>Settled, with a recorded resolution.</summary>
    Resolved = 2,

    /// <summary>Judged not to represent a real financial event.</summary>
    Rejected = 3
}

/// <summary>What kind of disagreement occurred.</summary>
public enum SyncConflictType
{
    /// <summary>
    /// The same client transaction id arrived describing different financial facts. One of
    /// the two submissions is wrong and a person has to decide which.
    /// </summary>
    ClientIdReusedWithDifferentPayload = 0,

    /// <summary>A unique constraint fired but no existing row could be found to reconcile against.</summary>
    UnresolvableUniqueViolation = 1,

    /// <summary>A second reversal was attempted against an already-reversed transaction.</summary>
    DuplicateReversalAttempt = 2,

    /// <summary>Synchronisation failed repeatedly and exhausted its retry budget.</summary>
    RetryBudgetExhausted = 3
}

/// <summary>
/// A durable record of a synchronisation disagreement that the server could not settle
/// automatically.
/// </summary>
/// <remarks>
/// <para>
/// A conflict must never disappear. Returning <c>CONFLICT</c> to a client and forgetting it
/// server-side would leave a transaction that the device believes needs attention and the
/// business has no record of — money in limbo with nobody accountable for it.
/// </para>
/// <para>
/// This record deliberately holds identifiers and amounts, not raw evidence. It carries
/// enough for an operator to investigate without duplicating SMS bodies or customer
/// numbers into a second table with its own retention story.
/// </para>
/// </remarks>
public sealed class SyncConflict : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? DeviceId { get; set; }

    /// <summary>The submitting user, for accountability.</summary>
    public Guid? SubmittedByUserId { get; set; }

    /// <summary>Client-generated submission identity that triggered the conflict.</summary>
    public string ClientTransactionId { get; set; } = string.Empty;

    /// <summary>The existing transaction involved, when one could be identified.</summary>
    public Guid? TransactionId { get; set; }

    /// <summary>For reversal conflicts, the original the client tried to reverse.</summary>
    public Guid? RelatedTransactionId { get; set; }

    public SyncConflictType ConflictType { get; set; }

    /// <summary>Stable machine-readable code, matching the sync response.</summary>
    public string ReasonCode { get; set; } = string.Empty;

    /// <summary>Batch the conflict arose in, for correlation with logs.</summary>
    public Guid? BatchId { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>Submitted amount, so an operator can judge materiality without a join.</summary>
    public decimal SubmittedAmount { get; set; }

    public string? SubmittedCurrency { get; set; }
    public TransactionType SubmittedType { get; set; }

    public SyncConflictStatus Status { get; set; } = SyncConflictStatus.Open;

    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public Guid? ResolvedByUserId { get; set; }

    /// <summary>What was decided. Free text, written by the resolving operator.</summary>
    public string? Resolution { get; set; }

    public string? ResolutionNotes { get; set; }

    public bool IsOpen => Status is SyncConflictStatus.Open or SyncConflictStatus.UnderReview;
}
