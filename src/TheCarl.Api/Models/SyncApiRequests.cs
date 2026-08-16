using System.ComponentModel.DataAnnotations;
using TheCarl.Domain;

namespace TheCarl.Api.Models;

// Validation attributes target constructor parameters, not generated properties: MVC throws
// when it finds validation metadata on a record property instead.

/// <summary>
/// One transaction captured offline.
/// </summary>
/// <remarks>
/// There is no <c>OrganizationId</c> field, by design. The tenant comes from the access
/// token, so a client has nothing to tamper with. <c>BranchId</c>, <c>DeviceId</c> and
/// <c>SessionId</c> are accepted as requests and then proved to belong to the caller's
/// organization server-side.
/// </remarks>
public sealed record SyncTransactionApiItem(
    [Required, StringLength(64, MinimumLength = 8)] string ClientTransactionId,
    [Required] TransactionType TransactionType,
    [Range(0, 99_999_999.99)] decimal Amount,
    [Required, StringLength(80, MinimumLength = 1)] string Provider,
    [Required] DateTimeOffset TransactionTimestamp,
    DateTimeOffset? DeviceReceivedAt,
    Guid? BranchId,
    Guid? DeviceId,
    Guid? SessionId,
    [StringLength(10)] string? Currency,
    [StringLength(30)] string? CustomerPhone,
    [StringLength(200)] string? TransactionReference,
    [StringLength(64)] string? EvidenceFingerprint,
    [StringLength(40)] string? ParserVersion,
    EvidenceSourceType SourceType,
    Guid? ReversesTransactionId,
    decimal? AdjustmentCashDelta,
    decimal? AdjustmentFloatDelta,
    [StringLength(1000)] string? CorrectionReason,
    [StringLength(1000)] string? Notes);

/// <summary>
/// A batch of offline transactions.
/// </summary>
/// <remarks>
/// <see cref="MaxLength"/> here is a model-binding guard that produces a 400. The
/// authoritative limit is <c>Sync:MaxBatchSize</c>, enforced in the service and surfaced as
/// a 413 with the configured maximum, so operators can tune it without a code change.
/// </remarks>
public sealed record SyncTransactionsApiRequest(
    [Required, MinLength(1), MaxLength(1000)] IReadOnlyList<SyncTransactionApiItem> Transactions);
