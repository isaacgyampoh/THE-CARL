namespace Zazi.Application;

/// <summary>
/// Read-only views over what the system has already recorded.
/// </summary>
/// <remarks>
/// <para>
/// Every method here queries existing tables — <c>AuditLogEntries</c>, <c>Devices</c>,
/// <c>TransactionEvidence</c>. Nothing in this service writes, and nothing collects data
/// that was not already being recorded. It exists because the information was present and
/// unreachable: held evidence in particular has been written by two services since the
/// evidence model was introduced and has never been visible anywhere.
/// </para>
/// <para>
/// Every query is scoped to one organization. Observability data names devices, operators and
/// amounts, so it is exactly as tenant-sensitive as the ledger itself.
/// </para>
/// </remarks>
public interface IObservabilityService
{
    Task<SystemHealthDto> GetSystemHealthAsync(
        Guid organizationId, CancellationToken cancellationToken = default);

    Task<PagedResult<ObservabilityEventDto>> GetRecentEventsAsync(
        Guid organizationId, EventQuery query, CancellationToken cancellationToken = default);

    Task<PagedResult<HeldEvidenceDto>> GetHeldEvidenceAsync(
        Guid organizationId, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceHealthDto>> GetDeviceHealthAsync(
        Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Everything recorded under one correlation id, oldest first.</summary>
    Task<IReadOnlyList<ObservabilityEventDto>> GetTimelineAsync(
        Guid organizationId, string correlationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ErrorGroupDto>> GetErrorGroupsAsync(
        Guid organizationId, DateTimeOffset since, CancellationToken cancellationToken = default);
}

// PagedResult already exists in Contracts.cs and is reused here rather than redefined.

/// <summary>Filter for the event list. Every field is optional.</summary>
public record EventQuery(
    int Page = 1,
    int PageSize = 50,
    string? Severity = null,
    string? Source = null,
    Guid? DeviceId = null,
    string? CorrelationId = null,
    DateTimeOffset? Since = null);

public record ObservabilityEventDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Severity,
    string Action,
    string Details,
    string? Source,
    string? Status,
    string? ErrorCode,
    int? DurationMs,
    string? CorrelationId,
    Guid? DeviceId,
    Guid? UserId,
    Guid? RelatedTransactionId,
    string? AppVersion,
    string? Platform);

/// <summary>
/// A transaction that was observed but deliberately not posted.
/// </summary>
/// <remarks>
/// Held evidence is the system working as intended — a message it could not read confidently
/// is kept for a person rather than guessed at. It is only a problem when nobody can see it,
/// which until now was the case.
/// </remarks>
public record HeldEvidenceDto(
    Guid EvidenceId,
    DateTimeOffset ObservedAt,
    string Provider,
    string TransactionType,
    decimal Amount,
    string Currency,
    string? Reference,
    string? OutcomeReason,
    decimal Confidence,
    string SourceType,
    Guid? DeviceId,
    Guid BranchId,
    int AgeHours);

public record DeviceHealthDto(
    Guid DeviceId,
    string Name,
    string DeviceType,
    string Status,
    bool IsRevoked,
    string? AppVersion,
    string? OsVersion,
    DateTimeOffset LastSeenAtUtc,
    int RecentFailureCount,
    bool IsStale);

public record SystemHealthDto(
    int TotalDevices,
    int ActiveDevices,
    int RevokedDevices,
    int StaleDevices,
    int HeldEvidenceCount,
    int ErrorsLastHour,
    int ErrorsLastDay,
    DateTimeOffset? LastEventAt);

/// <summary>
/// Recurring failures collapsed into one row.
/// </summary>
/// <remarks>
/// Grouped on the error code rather than the message, so rewording an error does not split a
/// long-running problem into two apparently new ones.
/// </remarks>
public record ErrorGroupDto(
    string ErrorCode,
    string Action,
    string? Source,
    int Occurrences,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int AffectedDevices,
    string SampleDetails);
