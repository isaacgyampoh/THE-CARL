using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

/// <summary>
/// Reads what the system already recorded. Writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// Every query is filtered by organization before anything else, so a caller cannot see
/// another tenant's devices, evidence or failures. That filter is applied here rather than
/// left to callers, because an observability screen is precisely where a forgotten predicate
/// would go unnoticed.
/// </para>
/// <para>
/// Results are paged and bounded. An operations dashboard that tries to render a year of
/// events is an outage of its own.
/// </para>
/// </remarks>
public sealed class ObservabilityService : IObservabilityService
{
    /// <summary>A device unheard from for longer than this is worth asking about.</summary>
    private const int StaleDeviceHours = 24;

    private const int MaxPageSize = 200;

    private readonly ApplicationDbContext _dbContext;

    public ObservabilityService(ApplicationDbContext dbContext) => _dbContext = dbContext;

    public async Task<SystemHealthDto> GetSystemHealthAsync(
        Guid organizationId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.AddHours(-StaleDeviceHours);
        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddDays(-1);

        var devices = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId)
            .Select(d => new { d.Status, d.IsRevoked, d.LastSeenAt })
            .ToListAsync(cancellationToken);

        var audits = _dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId);

        return new SystemHealthDto(
            TotalDevices: devices.Count,
            ActiveDevices: devices.Count(d => !d.IsRevoked && d.Status == DeviceStatus.Active),
            RevokedDevices: devices.Count(d => d.IsRevoked),
            // Counted only among devices still permitted to report. A revoked handset is
            // silent by design and should not read as a fault.
            StaleDevices: devices.Count(d =>
                !d.IsRevoked && (d.LastSeenAt < staleBefore)),
            HeldEvidenceCount: await _dbContext.TransactionEvidence.AsNoTracking()
                .CountAsync(e => e.OrganizationId == organizationId
                    && e.State == TransactionLifecycleState.PendingReview, cancellationToken),
            ErrorsLastHour: await audits.CountAsync(
                a => a.Severity == AuditSeverity.Error && a.CreatedAt >= hourAgo, cancellationToken),
            ErrorsLastDay: await audits.CountAsync(
                a => a.Severity == AuditSeverity.Error && a.CreatedAt >= dayAgo, cancellationToken),
            LastEventAt: await audits.OrderByDescending(a => a.CreatedAt)
                .Select(a => (DateTimeOffset?)a.CreatedAt).FirstOrDefaultAsync(cancellationToken));
    }

    public async Task<PagedResult<ObservabilityEventDto>> GetRecentEventsAsync(
        Guid organizationId, EventQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var events = _dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId);

        if (!string.IsNullOrWhiteSpace(query.Severity)
            && Enum.TryParse<AuditSeverity>(query.Severity, ignoreCase: true, out var severity))
        {
            events = events.Where(a => a.Severity == severity);
        }

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            events = events.Where(a => a.Source == query.Source);
        }

        if (query.DeviceId is { } deviceId)
        {
            events = events.Where(a => a.DeviceId == deviceId);
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            events = events.Where(a => a.CorrelationId == query.CorrelationId);
        }

        if (query.Since is { } since)
        {
            events = events.Where(a => a.CreatedAt >= since);
        }

        var total = await events.LongCountAsync(cancellationToken);

        var items = await events
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(Project)
            .ToListAsync(cancellationToken);

        return new PagedResult<ObservabilityEventDto>(items, page, pageSize, total);
    }

    public async Task<PagedResult<HeldEvidenceDto>> GetHeldEvidenceAsync(
        Guid organizationId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var held = _dbContext.TransactionEvidence.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId
                && e.State == TransactionLifecycleState.PendingReview);

        var total = await held.LongCountAsync(cancellationToken);

        // Oldest first: the point of the queue is that something has been waiting.
        var rows = await held
            .OrderBy(e => e.ServerReceivedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id, e.ServerReceivedAtUtc, e.Provider, e.ObservedType, e.Amount, e.Currency,
                e.ProviderReference, e.OutcomeReason, e.ConfidenceScore, e.SourceType,
                e.DeviceId, e.BranchId
            })
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;

        var items = rows.Select(e => new HeldEvidenceDto(
            EvidenceId: e.Id,
            ObservedAt: e.ServerReceivedAtUtc,
            Provider: e.Provider,
            TransactionType: e.ObservedType.ToString(),
            Amount: e.Amount,
            Currency: e.Currency,
            Reference: e.ProviderReference,
            OutcomeReason: e.OutcomeReason,
            Confidence: e.ConfidenceScore,
            SourceType: e.SourceType.ToString(),
            DeviceId: e.DeviceId,
            BranchId: e.BranchId,
            AgeHours: (int)Math.Max(0, (now - e.ServerReceivedAtUtc).TotalHours))).ToList();

        return new PagedResult<HeldEvidenceDto>(items, page, pageSize, total);
    }

    public async Task<IReadOnlyList<DeviceHealthDto>> GetDeviceHealthAsync(
        Guid organizationId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now.AddHours(-StaleDeviceHours);
        var dayAgo = now.AddDays(-1);

        var devices = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId)
            .Select(d => new
            {
                d.Id, d.Name, d.DeviceType, d.Status, d.IsRevoked,
                d.AppVersion, d.OsVersion, d.LastSeenAt
            })
            .ToListAsync(cancellationToken);

        // One grouped query rather than one per device: a branch with fifty handsets should
        // not cost fifty round trips to draw a table.
        var failures = await _dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId
                && a.Severity == AuditSeverity.Error
                && a.CreatedAt >= dayAgo
                && a.DeviceId != null)
            .GroupBy(a => a.DeviceId!.Value)
            .Select(g => new { DeviceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DeviceId, x => x.Count, cancellationToken);

        return devices.Select(d => new DeviceHealthDto(
            DeviceId: d.Id,
            Name: d.Name,
            DeviceType: d.DeviceType.ToString(),
            Status: d.Status.ToString(),
            IsRevoked: d.IsRevoked,
            AppVersion: d.AppVersion,
            OsVersion: d.OsVersion,
            LastSeenAtUtc: d.LastSeenAt,
            RecentFailureCount: failures.TryGetValue(d.Id, out var count) ? count : 0,
            IsStale: !d.IsRevoked && (d.LastSeenAt < staleBefore)))
            .OrderByDescending(d => d.RecentFailureCount)
            .ThenBy(d => d.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<ObservabilityEventDto>> GetTimelineAsync(
        Guid organizationId, string correlationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return Array.Empty<ObservabilityEventDto>();
        }

        // Oldest first: a timeline is read forwards.
        return await _dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.CorrelationId == correlationId)
            .OrderBy(a => a.CreatedAt)
            .Take(MaxPageSize)
            .Select(Project)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ErrorGroupDto>> GetErrorGroupsAsync(
        Guid organizationId, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        // Grouped in the database. Pulling every error back to count them in memory is how an
        // operations page becomes the slowest request in the system.
        var groups = await _dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId
                && a.Severity == AuditSeverity.Error
                && a.CreatedAt >= since
                && a.ErrorCode != null)
            .GroupBy(a => new { a.ErrorCode, a.Action, a.Source })
            .Select(g => new
            {
                g.Key.ErrorCode,
                g.Key.Action,
                g.Key.Source,
                Occurrences = g.Count(),
                FirstSeen = g.Min(a => a.CreatedAt),
                LastSeen = g.Max(a => a.CreatedAt),
                AffectedDevices = g.Select(a => a.DeviceId).Distinct().Count(),
                SampleDetails = g.Max(a => a.Details)
            })
            .OrderByDescending(g => g.Occurrences)
            .Take(50)
            .ToListAsync(cancellationToken);

        return groups.Select(g => new ErrorGroupDto(
            ErrorCode: g.ErrorCode!,
            Action: g.Action,
            Source: g.Source,
            Occurrences: g.Occurrences,
            FirstSeen: g.FirstSeen,
            LastSeen: g.LastSeen,
            AffectedDevices: g.AffectedDevices,
            SampleDetails: g.SampleDetails)).ToList();
    }

    // An expression, not a method: EF composes this into the SQL projection. Written as a
    // method it would compile and then fail at run time as untranslatable.
    // An expression, not a method: EF composes this into the SQL projection. Written as a
    // method it would compile and then fail at run time as untranslatable. Positional rather
    // than named arguments because an expression tree cannot carry named arguments.
    private static readonly Expression<Func<AuditLogEntry, ObservabilityEventDto>> Project = a =>
        new ObservabilityEventDto(
            a.Id,
            a.CreatedAt,
            a.Severity.ToString(),
            a.Action,
            a.Details,
            a.Source,
            a.Status,
            a.ErrorCode,
            a.DurationMs,
            a.CorrelationId,
            a.DeviceId,
            a.UserId,
            a.RelatedTransactionId,
            a.AppVersion,
            a.Platform);
}
