using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// <c>GET /devices/me</c> after the Phase 1 extension.
/// </summary>
/// <remarks>
/// The endpoint is how a client learns what it may do. If it lied — claiming SMS capture on
/// an iPhone, or omitting the legacy field an existing build reads — the client would either
/// offer an impossible feature or break outright.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class DeviceCapabilityEndpointTests : IDisposable
{
    private const string SelfPath = "/api/v1/devices/me";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public DeviceCapabilityEndpointTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    [SkippableFact]
    public async Task ExistingResponseFieldsAreUnchanged()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        await RegisterDeviceAsync(tenant, "compat-device", "Android", DeviceType.AndroidPhone);

        var body = await GetSelfJsonAsync(tenant, "compat-device");

        // Every field an existing client already reads must still be present. Removing one
        // would break a shipped build in the field.
        foreach (var field in new[]
                 {
                     "deviceId", "organizationId", "branchId", "name", "deviceIdentifier",
                     "role", "status", "isRevoked", "platform", "network", "appVersion",
                     "osVersion", "lastSeenAtUtc", "capabilities", "serverTimeUtc"
                 })
        {
            Assert.True(body.TryGetProperty(field, out _), $"'{field}' disappeared from the response.");
        }

        // The legacy string list is retained verbatim alongside the structured form.
        Assert.Equal(JsonValueKind.Array, body.GetProperty("capabilities").ValueKind);
    }

    [SkippableFact]
    public async Task TheResponseCarriesDeviceTypeAndStructuredCapabilities()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        await RegisterDeviceAsync(tenant, "android-device", "Android", DeviceType.AndroidPhone);

        var body = await GetSelfJsonAsync(tenant, "android-device");

        Assert.Equal((int)DeviceType.AndroidPhone, body.GetProperty("deviceType").GetInt32());
        Assert.True(body.GetProperty("canCaptureSms").GetBoolean());

        var capabilities = body.GetProperty("platformCapabilities")
            .EnumerateArray().Select(x => x.GetInt32()).ToList();

        Assert.Contains((int)PlatformCapability.SmsCapture, capabilities);
        Assert.Contains((int)PlatformCapability.ManualTransactionCapture, capabilities);
    }

    [SkippableFact]
    public async Task AniPhoneIsToldItCannotCaptureSmsButCanCaptureManually()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        await RegisterDeviceAsync(tenant, "iphone-device", "iPhone", DeviceType.iPhone);

        var body = await GetSelfJsonAsync(tenant, "iphone-device");

        Assert.Equal((int)DeviceType.iPhone, body.GetProperty("deviceType").GetInt32());

        // The product-level point of this whole phase: iOS is told authoritatively that
        // automatic capture is unavailable, so the app offers manual entry rather than a
        // feature that cannot work.
        Assert.False(body.GetProperty("canCaptureSms").GetBoolean());

        var capabilities = body.GetProperty("platformCapabilities")
            .EnumerateArray().Select(x => x.GetInt32()).ToList();

        Assert.DoesNotContain((int)PlatformCapability.SmsCapture, capabilities);
        Assert.Contains((int)PlatformCapability.ManualTransactionCapture, capabilities);
        Assert.Contains((int)PlatformCapability.OfflineStorage, capabilities);
        Assert.Contains((int)PlatformCapability.DashboardAccess, capabilities);
    }

    [SkippableFact]
    public async Task TheDeviceIsToldTheServersSyncLimitsRatherThanGuessing()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        await RegisterDeviceAsync(tenant, "sync-config-device", "Android", DeviceType.AndroidPhone);

        var body = await GetSelfJsonAsync(tenant, "sync-config-device");
        var config = body.GetProperty("syncConfiguration");

        // Without this a client discovers the batch limit only by being rejected with 413.
        Assert.Equal(100, config.GetProperty("maxBatchSize").GetInt32());
        Assert.True(config.GetProperty("maxBacklogAgeDays").GetInt32() > 0);
    }

    [SkippableFact]
    public async Task ARevokedDeviceIsGrantedNothingAndToldNothingAboutSyncing()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        await RegisterDeviceAsync(tenant, "revoked-device", "Android", DeviceType.AndroidPhone);

        await using (var db = _postgres.CreateContext())
        {
            var device = await db.Devices.SingleAsync(x => x.DeviceIdentifier == "revoked-device");
            device.IsRevoked = true;
            device.Status = DeviceStatus.Revoked;
            await db.SaveChangesAsync();
        }

        var body = await GetSelfJsonAsync(tenant, "revoked-device");

        Assert.True(body.GetProperty("isRevoked").GetBoolean());
        Assert.Empty(body.GetProperty("platformCapabilities").EnumerateArray());
        Assert.Empty(body.GetProperty("capabilities").EnumerateArray());
        Assert.False(body.GetProperty("canCaptureSms").GetBoolean());

        // A revoked device has no authority to sync, so it is told nothing about how to.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("syncConfiguration").ValueKind);
    }

    [SkippableFact]
    public async Task TheResponseNeverCarriesSecrets()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        await RegisterDeviceAsync(tenant, "leak-probe-device", "Android", DeviceType.AndroidPhone);

        var raw = await GetSelfRawAsync(tenant, "leak-probe-device");

        // The device identifier is deliberately free of these words so the assertion cannot
        // match its own fixture data.
        foreach (var forbidden in new[]
                 {
                     "password", "passwordHash", "refreshToken", "tokenHash", "securityStamp",
                     "codeHash", "enrollmentCode", "privateKey", "secret", "rawMessage"
                 })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task ADeviceInAnotherTenantStaysIndistinguishableFromOneThatDoesNotExist()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();
        await RegisterDeviceAsync(beta, "beta-only-device", "Android", DeviceType.AndroidPhone);

        var crossTenant = await RawSelfAsync(alpha, "beta-only-device");
        var nonexistent = await RawSelfAsync(alpha, "never-existed-device");

        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal(nonexistent.StatusCode, crossTenant.StatusCode);
    }

    [SkippableFact]
    public async Task AnAnonymousCallerIsRejected()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Identifier", "anything");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(SelfPath)).StatusCode);
    }

    [SkippableFact]
    public async Task EnrolmentDerivesDeviceTypeRatherThanAcceptingItFromTheHandset()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var manager = _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.BranchManager));

        var issue = await manager.PostAsJsonAsync(
            "/api/v1/devices/enrollment-codes", new { branchId = tenant.BranchId });
        issue.EnsureSuccessStatusCode();
        var code = (await issue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

        var agent = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

        // The handset claims to be an iPhone but there is no deviceType field to send —
        // the server derives it. A device that could name its own type could grant itself
        // SMS capture.
        var enrol = await agent.PostAsJsonAsync("/api/v1/devices/enrol", new
        {
            code,
            deviceIdentifier = "derived-type-device",
            name = "Agent Handset",
            platform = "iPhone",
            network = "MTN",
            appVersion = "0.1.0",
            osVersion = "iOS 17"
        });
        enrol.EnsureSuccessStatusCode();

        await using var db = _postgres.CreateContext();
        var device = await db.Devices.AsNoTracking()
            .SingleAsync(x => x.DeviceIdentifier == "derived-type-device");

        Assert.Equal(DeviceType.iPhone, device.DeviceType);
        Assert.False(PlatformCapabilityPolicy.CanCaptureSms(device.DeviceType));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task RegisterDeviceAsync(
        TenantSeed tenant, string identifier, string platform, DeviceType expectedType)
    {
        await using var db = _postgres.CreateContext();
        db.Devices.Add(new Device
        {
            OrganizationId = tenant.OrganizationId,
            BranchId = tenant.BranchId,
            Name = identifier,
            DeviceIdentifier = identifier,
            Platform = platform,
            DeviceType = expectedType,
            Status = DeviceStatus.Active
        });
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> RawSelfAsync(TenantSeed tenant, string identifier)
    {
        var client = _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));
        client.DefaultRequestHeaders.Add("X-Device-Identifier", identifier);
        return await client.GetAsync(SelfPath);
    }

    private async Task<string> GetSelfRawAsync(TenantSeed tenant, string identifier)
    {
        var response = await RawSelfAsync(tenant, identifier);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<JsonElement> GetSelfJsonAsync(TenantSeed tenant, string identifier) =>
        JsonDocument.Parse(await GetSelfRawAsync(tenant, identifier)).RootElement.Clone();

    private Task<TenantSeed> SeedAsync() => TenantSeedFactory.CreateAsync(_postgres);
}
