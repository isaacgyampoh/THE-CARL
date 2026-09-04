using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// The client telemetry endpoint.
/// </summary>
/// <remarks>
/// What is under test is mostly what the endpoint refuses to believe. A handset reports what
/// it saw; it does not get to say which organization, user or device the report belongs to,
/// because a device that could would be able to write into another tenant's audit trail.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class TelemetryIngestTests : IDisposable
{
    private const string Path = "/api/v1/telemetry/events";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public TelemetryIngestTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    [SkippableFact]
    public async Task AnonymousCallersCannotWriteEvents()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var response = await _factory!.CreateClient().PostAsJsonAsync(
            Path, new { events = new[] { new { eventType = "APP_START" } } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task AReportedEventBecomesAnAuditEntryForTheCallersOrganization()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await ClientFor(tenant).PostAsJsonAsync(Path, new
        {
            events = new[]
            {
                new
                {
                    eventType = "LOGIN_FAILURE",
                    severity = "Warning",
                    status = "Failed",
                    errorCode = "ANDROID_NO_NETWORK",
                    details = "POST /api/v1/auth/login",
                    durationMs = 1200,
                    correlationId = "trace-from-handset",
                    appVersion = "0.1.0",
                    platform = "Android 35"
                }
            }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var db = _postgres.CreateContext();
        var entry = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.CorrelationId == "trace-from-handset");

        Assert.Equal(tenant.OrganizationId, entry.OrganizationId);
        Assert.Equal("Android", entry.Source);
        Assert.Equal(AuditSeverity.Warning, entry.Severity);
        Assert.Equal("ANDROID_NO_NETWORK", entry.ErrorCode);
        Assert.Equal(1200, entry.DurationMs);
    }

    [SkippableFact]
    public async Task ADeviceCannotAttributeEventsToAnotherTenant()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        // The payload has no tenant field at all, and the caller's token decides. Sending a
        // correlation id that another tenant might also use must not cross the boundary.
        var response = await ClientFor(alpha).PostAsJsonAsync(Path, new
        {
            events = new[] { new { eventType = "APP_START", correlationId = "cross-tenant-probe" } }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var db = _postgres.CreateContext();
        var written = await db.AuditLogs.AsNoTracking()
            .Where(a => a.CorrelationId == "cross-tenant-probe")
            .ToListAsync();

        Assert.All(written, entry => Assert.Equal(alpha.OrganizationId, entry.OrganizationId));
        Assert.DoesNotContain(written, entry => entry.OrganizationId == beta.OrganizationId);
    }

    [SkippableFact]
    public async Task AnOversizedBatchIsRefusedRatherThanWritten()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var events = Enumerable.Range(0, 500)
            .Select(i => new { eventType = "API_REQUEST", correlationId = $"flood-{i}" })
            .ToArray();

        var response = await ClientFor(tenant).PostAsJsonAsync(Path, new { events });

        // One request must not be able to write thousands of rows.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = _postgres.CreateContext();
        Assert.Equal(0, await db.AuditLogs.CountAsync(a => a.CorrelationId!.StartsWith("flood-")));
    }

    [SkippableFact]
    public async Task AnImplausibleClientClockDoesNotMoveEventsOutOfView()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        // A handset with a wrong clock would otherwise file its events in the far future,
        // where "recent failures" would never show them.
        var response = await ClientFor(tenant).PostAsJsonAsync(Path, new
        {
            events = new[]
            {
                new
                {
                    eventType = "APP_START",
                    correlationId = "bad-clock",
                    occurredAtUtc = DateTimeOffset.UtcNow.AddYears(5)
                }
            }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var db = _postgres.CreateContext();
        var entry = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.CorrelationId == "bad-clock");

        Assert.True(entry.CreatedAt <= DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [SkippableFact]
    public async Task AnEmptyBatchIsAccepted()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await ClientFor(tenant).PostAsJsonAsync(Path, new { events = Array.Empty<object>() });

        // A handset with nothing to report should not be treated as an error.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private HttpClient ClientFor(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

    private Task<TenantSeed> SeedAsync() => TenantSeedFactory.CreateAsync(_postgres);
}
