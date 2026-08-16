namespace TheCarl.Application.Sync;

/// <summary>
/// Stable category for a sync outcome, so a client can decide what to do without parsing
/// human-readable messages.
/// </summary>
/// <remarks>
/// Messages are for people and change freely; categories and reason codes are API surface
/// and do not. An Android client branches on these, never on <c>Message</c>.
/// </remarks>
public enum SyncErrorCategory
{
    /// <summary>Succeeded. No error.</summary>
    None = 0,

    /// <summary>The payload is malformed or breaks an accounting rule. Permanent.</summary>
    Validation = 1,

    /// <summary>The caller, device, branch or session is not permitted. Permanent.</summary>
    Authorization = 2,

    /// <summary>Already recorded. A success from the client's point of view.</summary>
    Duplicate = 3,

    /// <summary>Server state disagrees; a person must decide. Permanent until resolved.</summary>
    Conflict = 4,

    /// <summary>Temporary infrastructure failure. Retryable with backoff.</summary>
    Transient = 5,

    /// <summary>Unexpected server fault. Retryable, but escalate if it persists.</summary>
    System = 6
}

/// <summary>
/// Maps reason codes to categories and retry behaviour.
/// </summary>
/// <remarks>
/// The retry decision lives here, on the server, rather than in each client's head. A client
/// that retries a permanent validation failure forever burns battery and rate limit and never
/// succeeds; one that gives up on a transient failure loses a real transaction.
/// </remarks>
public static class SyncErrorClassification
{
    private static readonly Dictionary<string, SyncErrorCategory> Categories = new(StringComparer.Ordinal)
    {
        // ─── Duplicate: already recorded, treat as success ───────────────────
        [SyncReasonCodes.DuplicateClientTransactionId] = SyncErrorCategory.Duplicate,
        [SyncReasonCodes.DuplicateEvidenceFingerprint] = SyncErrorCategory.Duplicate,

        // ─── Validation: permanent, never retry ──────────────────────────────
        [SyncReasonCodes.InvalidClientTransactionId] = SyncErrorCategory.Validation,
        [SyncReasonCodes.InvalidAmount] = SyncErrorCategory.Validation,
        [SyncReasonCodes.InvalidTransactionType] = SyncErrorCategory.Validation,
        [SyncReasonCodes.InvalidProvider] = SyncErrorCategory.Validation,
        [SyncReasonCodes.InvalidEvidenceFingerprint] = SyncErrorCategory.Validation,
        [SyncReasonCodes.InvalidSourceType] = SyncErrorCategory.Validation,
        [SyncReasonCodes.MissingParserVersion] = SyncErrorCategory.Validation,
        [SyncReasonCodes.TimestampInFuture] = SyncErrorCategory.Validation,
        [SyncReasonCodes.TimestampTooOld] = SyncErrorCategory.Validation,
        [SyncReasonCodes.UnknownTypeRequiresReview] = SyncErrorCategory.Validation,
        [SyncReasonCodes.ReversalRequiresOriginal] = SyncErrorCategory.Validation,
        [SyncReasonCodes.ReversalOriginalNotFound] = SyncErrorCategory.Validation,
        [SyncReasonCodes.ReversalOriginalNotEligible] = SyncErrorCategory.Validation,
        [SyncReasonCodes.AdjustmentRequiresExplicitDeltas] = SyncErrorCategory.Validation,
        [SyncReasonCodes.AdjustmentRequiresReason] = SyncErrorCategory.Validation,

        // ─── Authorization: permanent for this caller ────────────────────────
        [SyncReasonCodes.BranchNotInTenant] = SyncErrorCategory.Authorization,
        [SyncReasonCodes.DeviceNotInTenant] = SyncErrorCategory.Authorization,
        [SyncReasonCodes.DeviceRevoked] = SyncErrorCategory.Authorization,
        [SyncReasonCodes.SessionNotInTenant] = SyncErrorCategory.Authorization,
        [SyncReasonCodes.SessionBranchMismatch] = SyncErrorCategory.Authorization,
        [SyncReasonCodes.SessionNotOpenAtEventTime] = SyncErrorCategory.Authorization,

        // ─── Conflict: needs a person ────────────────────────────────────────
        [SyncReasonCodes.ClientIdReusedWithDifferentPayload] = SyncErrorCategory.Conflict,
        [SyncReasonCodes.AlreadyReversed] = SyncErrorCategory.Conflict,

        // ─── Transient: retry with backoff ───────────────────────────────────
        [SyncReasonCodes.DatabaseUnavailable] = SyncErrorCategory.Transient,
        [SyncReasonCodes.Timeout] = SyncErrorCategory.Transient,
        [SyncReasonCodes.TemporaryServerError] = SyncErrorCategory.Transient
    };

    public static SyncErrorCategory CategoryFor(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return SyncErrorCategory.None;
        }

        // An unrecognised code is treated as a system fault rather than silently as
        // validation: an unmapped code means the server changed and the mapping did not.
        return Categories.TryGetValue(reasonCode, out var category)
            ? category
            : SyncErrorCategory.System;
    }

    /// <summary>
    /// Whether a client should retry. Only transient and system faults are retryable —
    /// retrying a validation or authorization failure can never succeed.
    /// </summary>
    public static bool IsRetryable(SyncErrorCategory category) =>
        category is SyncErrorCategory.Transient or SyncErrorCategory.System;

    public static bool IsRetryable(string? reasonCode) => IsRetryable(CategoryFor(reasonCode));
}
