using Zazi.Application;
using Zazi.Domain;
using Zazi.Infrastructure.Services;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// The observability queries, against real PostgreSQL.
/// </summary>
/// <remarks>
/// These run against a real database rather than a fake because what is being tested is
/// mostly the queries themselves — grouping, paging, ordering and, above all, the tenant
/// filter. An in-memory provider would happily answer a query that PostgreSQL rejects, and
/// would not prove the isolation that matters most here: observability data names devices,
/// operators and amounts.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class ObservabilityTests
{
    private readonly PostgresFixture _postgres;

    public ObservabilityTests(PostgresFixture postgres) => _postgres = postgres;

    [SkippableFact]
    public async Task HeldEvidenceIsVisibleAndOldestFirst()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var older = await AddHeldEvidenceAsync(tenant, 500m, DateTimeOffset.UtcNow.AddHours(-5));
        var newer = await AddHeldEvidenceAsync(tenant, 250m, DateTimeOffset.UtcNow.AddHours(-1));

        var page = await Service().GetHeldEvidenceAsync(tenant.OrganizationId, 1, 25);

        // The queue exists because something has been waiting; the longest wait leads.
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(older, page.Items[0].EvidenceId);
        Assert.Equal(newer, page.Items[1].EvidenceId);
        Assert.True(page.Items[0].AgeHours >= 4);
    }

    [SkippableFact]
    public async Task HeldEvidenceExcludesAcceptedTransactions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        await AddHeldEvidenceAsync(tenant, 100m, DateTimeOffset.UtcNow);
        await AddEvidenceAsync(tenant, 900m, DateTimeOffset.UtcNow, TransactionLifecycleState.Accepted);

        var page = await Service().GetHeldEvidenceAsync(tenant.OrganizationId, 1, 25);

        // A posted transaction is not waiting for anyone. Showing it here would bury the
        // items that genuinely need a decision.
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(100m, page.Items[0].Amount);
    }

    [SkippableFact]
    public async Task OneTenantNeverSeesAnothersHeldEvidence()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        await AddHeldEvidenceAsync(beta, 750m, DateTimeOffset.UtcNow);

        var page = await Service().GetHeldEvidenceAsync(alpha.OrganizationId, 1, 25);

        Assert.Equal(0, page.TotalCount);
    }

    [SkippableFact]
    public async Task OneTenantNeverSeesAnothersEventsOrTimeline()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        await AddEventAsync(beta, AuditSeverity.Error, "beta.failed", correlationId: "shared-trace");

        var service = Service();

        var events = await service.GetRecentEventsAsync(alpha.OrganizationId, new EventQuery());
        Assert.Equal(0, events.TotalCount);

        // Guessing another tenant's correlation id must not open their incident.
        var timeline = await service.GetTimelineAsync(alpha.OrganizationId, "shared-trace");
        Assert.Empty(timeline);
    }

    [SkippableFact]
    public async Task ATimelineReadsForwardsAndCarriesOnlyItsOwnTrace()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        await AddEventAsync(tenant, AuditSeverity.Information, "login", correlationId: "trace-1",
            at: DateTimeOffset.UtcNow.AddSeconds(-30));
        await AddEventAsync(tenant, AuditSeverity.Error, "sync.failed", correlationId: "trace-1",
            at: DateTimeOffset.UtcNow.AddSeconds(-10));
        await AddEventAsync(tenant, AuditSeverity.Information, "unrelated", correlationId: "trace-2");

        var timeline = await Service().GetTimelineAsync(tenant.OrganizationId, "trace-1");

        Assert.Equal(2, timeline.Count);
        Assert.Equal("login", timeline[0].Action);
        Assert.Equal("sync.failed", timeline[1].Action);
    }

    [SkippableFact]
    public async Task RecurringFailuresCollapseIntoOneGroup()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        for (var i = 0; i < 5; i++)
        {
            await AddEventAsync(tenant, AuditSeverity.Error, "POST /sync",
                errorCode: "sync.batch_too_large", details: $"attempt {i}");
        }

        await AddEventAsync(tenant, AuditSeverity.Error, "POST /login", errorCode: "auth.failed");

        var groups = await Service().GetErrorGroupsAsync(
            tenant.OrganizationId, DateTimeOffset.UtcNow.AddDays(-1));

        // Five occurrences of one problem is one row, not five. The message differed each
        // time; the code did not, which is why grouping keys on the code.
        Assert.Equal(2, groups.Count);
        var largest = groups.First();
        Assert.Equal("sync.batch_too_large", largest.ErrorCode);
        Assert.Equal(5, largest.Occurrences);
        Assert.True(largest.LastSeen >= largest.FirstSeen);
    }

    [SkippableFact]
    public async Task InformationalEventsAreNotReportedAsFailures()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        await AddEventAsync(tenant, AuditSeverity.Information, "login", errorCode: "not.an.error");

        var groups = await Service().GetErrorGroupsAsync(
            tenant.OrganizationId, DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Empty(groups);
    }

    [SkippableFact]
    public async Task EventsCanBeFilteredAndPaged()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        for (var i = 0; i < 7; i++)
        {
            await AddEventAsync(tenant, AuditSeverity.Error, $"failure {i}", source: "Api");
        }

        await AddEventAsync(tenant, AuditSeverity.Information, "routine", source: "SyncWorker");

        var service = Service();

        var errors = await service.GetRecentEventsAsync(
            tenant.OrganizationId, new EventQuery(Page: 1, PageSize: 5, Severity: "Error"));

        Assert.Equal(7, errors.TotalCount);
        Assert.Equal(5, errors.Items.Count);
        Assert.True(errors.HasMore);

        var bySource = await service.GetRecentEventsAsync(
            tenant.OrganizationId, new EventQuery(Source: "SyncWorker"));

        Assert.Equal(1, bySource.TotalCount);
    }

    [SkippableFact]
    public async Task APageSizeCannotBeUsedToPullTheWholeTable()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        for (var i = 0; i < 3; i++)
        {
            await AddEventAsync(tenant, AuditSeverity.Information, $"event {i}");
        }

        // An unbounded page size turns one dashboard request into a table scan.
        var page = await Service().GetRecentEventsAsync(
            tenant.OrganizationId, new EventQuery(PageSize: 100_000));

        Assert.True(page.PageSize <= 200);
    }

    [SkippableFact]
    public async Task SystemHealthCountsHeldEvidenceAndRecentErrors()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        await AddHeldEvidenceAsync(tenant, 300m, DateTimeOffset.UtcNow);
        await AddEventAsync(tenant, AuditSeverity.Error, "recent failure");
        await AddEventAsync(tenant, AuditSeverity.Error, "old failure",
            at: DateTimeOffset.UtcNow.AddDays(-3));

        var health = await Service().GetSystemHealthAsync(tenant.OrganizationId);

        Assert.Equal(1, health.HeldEvidenceCount);
        Assert.Equal(1, health.ErrorsLastHour);
        // The three-day-old failure is outside the day window and outside the hour window.
        Assert.Equal(1, health.ErrorsLastDay);
    }

    [SkippableFact]
    public async Task DeviceHealthReportsFailuresAndDoesNotFlagRevokedDevicesAsSilent()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var revoked = await AddDeviceAsync(tenant, "Revoked handset",
            lastSeen: DateTimeOffset.UtcNow.AddDays(-9), isRevoked: true);
        var quiet = await AddDeviceAsync(tenant, "Quiet handset",
            lastSeen: DateTimeOffset.UtcNow.AddDays(-9));
        var busy = await AddDeviceAsync(tenant, "Busy handset", lastSeen: DateTimeOffset.UtcNow);

        await AddEventAsync(tenant, AuditSeverity.Error, "sync failed", deviceId: busy);
        await AddEventAsync(tenant, AuditSeverity.Error, "sync failed", deviceId: busy);

        var devices = await Service().GetDeviceHealthAsync(tenant.OrganizationId);

        // A revoked handset is silent because it was told to be. Reporting it as "not
        // reporting" would put a permanent false alarm on the dashboard.
        Assert.False(devices.Single(d => d.DeviceId == revoked).IsStale);
        Assert.True(devices.Single(d => d.DeviceId == quiet).IsStale);
        Assert.Equal(2, devices.Single(d => d.DeviceId == busy).RecentFailureCount);

        // Noisiest first: the dashboard should open on whatever is going wrong.
        Assert.Equal(busy, devices[0].DeviceId);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private ObservabilityService Service() => new(_postgres.CreateContext());

    private async Task<Guid> AddHeldEvidenceAsync(TenantSeed tenant, decimal amount, DateTimeOffset at) =>
        await AddEvidenceAsync(tenant, amount, at, TransactionLifecycleState.PendingReview);

    private async Task<Guid> AddEvidenceAsync(
        TenantSeed tenant, decimal amount, DateTimeOffset at, TransactionLifecycleState state)
    {
        await using var db = _postgres.CreateContext();

        var evidence = new TransactionEvidence
        {
            OrganizationId = tenant.OrganizationId,
            BranchId = tenant.BranchId,
            SubmittedByUserId = tenant.UserId,
            Provider = "MTN",
            ObservedType = TransactionType.CashIn,
            Amount = amount,
            ServerReceivedAtUtc = at,
            State = state,
            OutcomeReason = state == TransactionLifecycleState.PendingReview
                ? "Evidence is incomplete — a provider reference is required before posting."
                : null,
            ConfidenceScore = 0.5m,
            SourceType = EvidenceSourceType.AndroidSms,
            Fingerprint = Guid.NewGuid().ToString("N"),
            ParserName = "MtnSmsParser",
            ParserVersion = "mtn-v1"
        };

        db.TransactionEvidence.Add(evidence);
        await db.SaveChangesAsync();
        return evidence.Id;
    }

    private async Task<Guid> AddDeviceAsync(
        TenantSeed tenant, string name, DateTimeOffset lastSeen, bool isRevoked = false)
    {
        await using var db = _postgres.CreateContext();

        var device = new Device
        {
            OrganizationId = tenant.OrganizationId,
            BranchId = tenant.BranchId,
            Name = name,
            DeviceIdentifier = Guid.NewGuid().ToString(),
            DeviceType = DeviceType.AndroidPhone,
            Status = isRevoked ? DeviceStatus.Revoked : DeviceStatus.Active,
            IsRevoked = isRevoked,
            LastSeenAt = lastSeen
        };

        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task AddEventAsync(
        TenantSeed tenant,
        AuditSeverity severity,
        string action,
        string? errorCode = null,
        string? correlationId = null,
        string? source = "Api",
        string? details = null,
        Guid? deviceId = null,
        DateTimeOffset? at = null)
    {
        await using var db = _postgres.CreateContext();

        db.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = tenant.OrganizationId,
            UserId = tenant.UserId,
            DeviceId = deviceId,
            Action = action,
            Details = details ?? action,
            ActorType = "Api",
            Severity = severity,
            ErrorCode = errorCode,
            CorrelationId = correlationId,
            Source = source,
            CreatedAt = at ?? DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }

    private Task<TenantSeed> SeedAsync() => TenantSeedFactory.CreateAsync(_postgres);
}
