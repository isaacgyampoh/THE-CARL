using System.Net;
using System.Net.Http.Json;

namespace Zazi.IntegrationTests;

/// <summary>
/// Every tenant-scoped endpoint must reject unauthenticated callers.
/// <para>
/// Before Phase 10 the entire API was anonymous: any caller could read every tenant's
/// transactions and, via <c>/api/v1/sms/capture</c>, write financial records into any
/// organization. These tests exist so that regression cannot happen silently.
/// </para>
/// </summary>
public class AnonymousAccessTests : IClassFixture<ZaziApiFactory>
{
    private readonly ZaziApiFactory _factory;

    public AnonymousAccessTests(ZaziApiFactory factory) => _factory = factory;

    public static TheoryData<string> ProtectedGetEndpoints => new()
    {
        "/api/v1/transactions",
        "/api/v1/branches",
        "/api/v1/devices",
        "/api/v1/alerts",
        "/api/v1/organizations",
        "/api/v1/organizations/current",
        "/api/v1/dashboard/organization",
        "/api/v1/sync/pending",
        "/api/v1/auth/users",
        "/api/v1/auth/me"
    };

    [Theory]
    [MemberData(nameof(ProtectedGetEndpoints))]
    public async Task ProtectedGetEndpoints_RejectAnonymousCallers(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string, object> ProtectedPostEndpoints => new()
    {
        { "/api/v1/transactions", new { network = "MTN", type = 0, amount = 10m } },
        { "/api/v1/branches", new { name = "Anonymous Branch" } },
        { "/api/v1/devices", new { name = "d", deviceIdentifier = "d", platform = "Android", network = "MTN", role = 1 } },
        { "/api/v1/sms/capture", new { sourcePhoneNumber = "MTN", rawMessage = "MTN MOMO: Deposit of GHS 10.00" } },
        { "/api/v1/sessions/open", new { openingCash = 0m, openingFloat = 0m } },
        { "/api/v1/sync/queue", new { entityType = "Transaction", eventType = "Captured", payload = "{}" } },
        { "/api/v1/alerts/thresholds", new { network = "MTN", warningThreshold = 1m, criticalThreshold = 1m } },
        { "/api/v1/auth/staff", new { fullName = "x", email = "x@y.test", password = "Password123!x", roles = new[] { "AGENT" } } }
    };

    [Theory]
    [MemberData(nameof(ProtectedPostEndpoints))]
    public async Task ProtectedPostEndpoints_RejectAnonymousCallers(string path, object body)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(path, body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SmsCapture_DoesNotCreateRecordsForAnonymousCallers()
    {
        var client = _factory.CreateClient();
        var before = await _factory.WithDbAsync(db => Task.FromResult(db.TransactionEvidence.Count()));

        await client.PostAsJsonAsync("/api/v1/sms/capture", new
        {
            organizationId = Guid.NewGuid(),
            branchId = Guid.NewGuid(),
            sourcePhoneNumber = "MTN",
            rawMessage = "MTN MOMO: Deposit of GHS 5000.00. Ref: INJECT1. Customer 0241234567"
        });

        var after = await _factory.WithDbAsync(db => Task.FromResult(db.TransactionEvidence.Count()));
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/ready")]
    [InlineData("/api/v1/status")]
    public async Task OperationalEndpoints_RemainAnonymous(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StatusEndpoint_DoesNotDiscloseInfrastructureDetail()
    {
        var client = _factory.CreateClient();

        var body = await client.GetStringAsync("/api/v1/status");

        // Backing-store and version detail is reconnaissance material for an anonymous caller.
        Assert.DoesNotContain("postgres", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("in-memory", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("version", body, StringComparison.OrdinalIgnoreCase);
    }
}
