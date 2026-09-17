using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Device enrolment over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// An enrolment code is a credential that admits a handset into a tenant. These tests exist
/// because the failure modes — a reusable code, a code redeemable from another tenant, a
/// code that never expires, a code stored in clear text — each hand an attacker a device
/// inside someone's financial records.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class DeviceEnrollmentTests : IDisposable
{
    private const string CodesPath = "/api/v1/devices/enrollment-codes";
    private const string EnrolPath = "/api/v1/devices/enrol";
    private const string SelfPath = "/api/v1/devices/me";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public DeviceEnrollmentTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    // ─── Issuing ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AManagerCanIssueACodeAndThePlaintextIsReturnedOnce()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var issued = await IssueAsync(tenant);

        Assert.StartsWith("ZAZI-", issued.Code);
        Assert.Equal(tenant.BranchId, issued.BranchId);
        Assert.True(issued.ExpiresAtUtc > DateTimeOffset.UtcNow);

        // The plaintext must exist nowhere but that response.
        await using var db = _postgres.CreateContext();
        var stored = await db.DeviceEnrollmentCodes.AsNoTracking()
            .SingleAsync(x => x.Id == issued.Id);

        Assert.NotEqual(issued.Code, stored.CodeHash);
        Assert.DoesNotContain(stored.CodeHash, issued.Code, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, stored.CodeHash.Length);
    }

    [SkippableFact]
    public async Task AnAgentCannotIssueCodes()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await AgentClient(tenant).PostAsJsonAsync(CodesPath, new { branchId = tenant.BranchId });

        // Issuing is what an administrator does; redeeming is what an agent does.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task ListedCodesNeverCarrySecretMaterial()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var issued = await IssueAsync(tenant);

        var body = await ManagerClient(tenant).GetStringAsync(CodesPath);

        Assert.DoesNotContain(issued.Code, body);
        Assert.DoesNotContain("codeHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(issued.CodePrefix, body);
    }

    [SkippableFact]
    public async Task AnUnboundedLifetimeIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var response = await ManagerClient(tenant).PostAsJsonAsync(CodesPath, new
        {
            branchId = tenant.BranchId,
            lifetimeHours = 100_000
        });

        // A code that outlives the person it was issued for is a permanent way in.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ─── Redeeming ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnAgentCanEnrolTheirOwnHandsetWithoutAnAdministratorTouchingIt()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var issued = await IssueAsync(tenant);

        var enrolled = await RedeemAsync(tenant, issued.Code, "handset-0001");

        // The whole point of the feature: no device.manage required to redeem.
        Assert.Equal(tenant.BranchId, enrolled.BranchId);
        Assert.Equal(DeviceStatus.Active, enrolled.Status);
        Assert.Equal(DeviceRole.TransactionDevice, enrolled.Role);
    }

    [SkippableFact]
    public async Task ACodeCannotBeRedeemedTwice()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var issued = await IssueAsync(tenant);

        await RedeemAsync(tenant, issued.Code, "handset-first");
        var second = await RawRedeemAsync(tenant, issued.Code, "handset-second");

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);

        await using var db = _postgres.CreateContext();
        Assert.Equal(1, await EnrolledHandsetCountAsync(db, tenant));
    }

    [SkippableFact]
    public async Task AnExpiredCodeIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var issued = await IssueAsync(tenant);

        await using (var db = _postgres.CreateContext())
        {
            var code = await db.DeviceEnrollmentCodes.SingleAsync(x => x.Id == issued.Id);
            // Both moved back together: CK_DeviceEnrollmentCodes_MustExpire requires
            // ExpiresAtUtc > CreatedAt, so an expiry alone would be rejected by the database.
            code.CreatedAt = DateTimeOffset.UtcNow.AddHours(-48);
            code.ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(-24);
            await db.SaveChangesAsync();
        }

        var response = await RawRedeemAsync(tenant, issued.Code, "handset-expired");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task ARevokedCodeIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var issued = await IssueAsync(tenant);

        var revoke = await ManagerClient(tenant)
            .PostAsync($"{CodesPath}/{issued.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var response = await RawRedeemAsync(tenant, issued.Code, "handset-revoked");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task ACodeFromAnotherTenantCannotBeRedeemed()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        var alphaCode = await IssueAsync(alpha);

        // Beta's agent presents Alpha's code.
        var response = await RawRedeemAsync(beta, alphaCode.Code, "handset-cross-tenant");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var db = _postgres.CreateContext();
        Assert.Equal(0, await EnrolledHandsetCountAsync(db, beta));

        // Alpha's code must remain usable: a cross-tenant attempt must not burn it.
        var stored = await db.DeviceEnrollmentCodes.AsNoTracking().SingleAsync(x => x.Id == alphaCode.Id);
        Assert.Equal(DeviceEnrollmentCodeStatus.Active, stored.Status);
    }

    [SkippableFact]
    public async Task AnInvalidCodeIsIndistinguishableFromAnUnknownOne()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var issued = await IssueAsync(tenant);
        await RedeemAsync(tenant, issued.Code, "handset-used");

        var usedAgain = await RawRedeemAsync(tenant, issued.Code, "handset-a");
        var neverExisted = await RawRedeemAsync(tenant, "ZAZI-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ", "handset-b");

        // Distinct responses would let an attacker probe which codes exist. Bodies carry a
        // per-request correlationId, so the comparison is on the parts that could leak:
        // status, problem type and title.
        Assert.Equal(usedAgain.StatusCode, neverExisted.StatusCode);
        Assert.Equal(await ProblemSignatureAsync(usedAgain), await ProblemSignatureAsync(neverExisted));
    }

    [SkippableFact]
    public async Task ARedeemedCodeCannotReAdmitAnAlreadyRegisteredIdentifier()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();

        var first = await IssueAsync(tenant);
        await RedeemAsync(tenant, first.Code, "handset-duplicate");

        // A revoked handset must not be able to re-enrol itself with a fresh code.
        await using (var db = _postgres.CreateContext())
        {
            var device = await db.Devices.SingleAsync(x => x.DeviceIdentifier == "handset-duplicate");
            device.IsRevoked = true;
            device.Status = DeviceStatus.Revoked;
            await db.SaveChangesAsync();
        }

        var second = await IssueAsync(tenant);
        var response = await RawRedeemAsync(tenant, second.Code, "handset-duplicate");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [SkippableFact]
    public async Task ARedeemingHandsetCannotChooseItsOwnRoleOrBranch()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var otherBranchId = await CreateBranchAsync(tenant.OrganizationId);

        var issued = await IssueAsync(tenant);

        // The redeem payload has no branch or role field at all; scope comes from the code.
        var enrolled = await RedeemAsync(tenant, issued.Code, "handset-scope");

        Assert.Equal(tenant.BranchId, enrolled.BranchId);
        Assert.NotEqual(otherBranchId, enrolled.BranchId);
        Assert.Equal(DeviceRole.TransactionDevice, enrolled.Role);
    }

    [SkippableFact]
    public async Task ConcurrentRedemptionsOfOneCodeCreateExactlyOneDevice()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var issued = await IssueAsync(tenant);

        var client = AgentClient(tenant);
        using var barrier = new Barrier(10);
        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await client.PostAsJsonAsync(EnrolPath, RedeemPayload(issued.Code, $"handset-race-{i}"));
        })).ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));

        await using var db = _postgres.CreateContext();
        Assert.Equal(1, await EnrolledHandsetCountAsync(db, tenant));
    }

    // ─── Device self-state ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task ADeviceCanReadItsOwnState()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var issued = await IssueAsync(tenant);
        await RedeemAsync(tenant, issued.Code, "handset-self");

        var self = await GetSelfAsync(tenant, "handset-self");

        Assert.False(self.IsRevoked);
        Assert.Equal(DeviceStatus.Active, self.Status);
        Assert.Contains("sync.submit", self.Capabilities);
        // Server time lets the client detect its own clock drift.
        Assert.True(self.ServerTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [SkippableFact]
    public async Task ARevokedDeviceLearnsItIsRevokedWithoutHavingToFailASync()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var issued = await IssueAsync(tenant);
        await RedeemAsync(tenant, issued.Code, "handset-revoked-self");

        await using (var db = _postgres.CreateContext())
        {
            var device = await db.Devices.SingleAsync(x => x.DeviceIdentifier == "handset-revoked-self");
            device.IsRevoked = true;
            device.Status = DeviceStatus.Revoked;
            await db.SaveChangesAsync();
        }

        var self = await GetSelfAsync(tenant, "handset-revoked-self");

        // Machine-readable, so the client clears credentials deterministically rather than
        // parsing a message or inferring from a failure.
        Assert.True(self.IsRevoked);
        Assert.Equal(DeviceStatus.Revoked, self.Status);
        Assert.Empty(self.Capabilities);
    }

    [SkippableFact]
    public async Task ADeviceInAnotherTenantIsIndistinguishableFromOneThatDoesNotExist()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        var betaCode = await IssueAsync(beta);
        await RedeemAsync(beta, betaCode.Code, "handset-beta-only");

        var crossTenant = await RawSelfAsync(alpha, "handset-beta-only");
        var nonexistent = await RawSelfAsync(alpha, "handset-never-existed");

        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal(nonexistent.StatusCode, crossTenant.StatusCode);
    }

    [SkippableFact]
    public async Task AnonymousCallersCannotReachAnyEnrolmentEndpoint()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var anonymous = _factory!.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(CodesPath, new { branchId = tenant.BranchId })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(EnrolPath, RedeemPayload("ZAZI-AAAA", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(SelfPath)).StatusCode);
    }

    [SkippableFact]
    public async Task EnrolmentIsAudited()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await SeedAsync();
        var issued = await IssueAsync(tenant);
        await RedeemAsync(tenant, issued.Code, "handset-audited");

        await using var db = _postgres.CreateContext();
        var actions = await db.AuditLogs.AsNoTracking()
            .Where(x => x.OrganizationId == tenant.OrganizationId)
            .Select(x => x.Action)
            .ToListAsync();

        Assert.Contains("DEVICE_ENROLLMENT_CODE_ISSUED", actions);
        Assert.Contains("DEVICE_ENROLLED", actions);

        // The audit trail must never carry the code itself.
        var details = await db.AuditLogs.AsNoTracking()
            .Where(x => x.OrganizationId == tenant.OrganizationId)
            .Select(x => x.Details)
            .ToListAsync();

        Assert.DoesNotContain(details, d => d.Contains(issued.Code, StringComparison.OrdinalIgnoreCase));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Devices created by enrolment, excluding the one the tenant seed creates.</summary>
    private static Task<int> EnrolledHandsetCountAsync(
        Zazi.Infrastructure.ApplicationDbContext db, TenantSeed tenant) =>
        db.Devices.CountAsync(x =>
            x.OrganizationId == tenant.OrganizationId && x.DeviceIdentifier.StartsWith("handset-"));

    /// <summary>Problem type and title, with the per-request correlation id removed.</summary>
    private static async Task<string> ProblemSignatureAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var root = document.RootElement;

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        var title = root.TryGetProperty("title", out var ti) ? ti.GetString() : null;
        return $"{type}|{title}";
    }

    private static object RedeemPayload(string code, string deviceIdentifier) => new
    {
        code,
        deviceIdentifier,
        name = "Agent Handset",
        platform = "Android",
        network = "MTN",
        appVersion = "0.1.0",
        osVersion = "Android 14"
    };

    private HttpClient ManagerClient(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.BranchManager));

    private HttpClient AgentClient(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

    private async Task<EnrollmentCodeIssuedDto> IssueAsync(TenantSeed tenant)
    {
        var response = await ManagerClient(tenant)
            .PostAsJsonAsync(CodesPath, new { branchId = tenant.BranchId });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentCodeIssuedDto>())!;
    }

    private async Task<HttpResponseMessage> RawRedeemAsync(
        TenantSeed tenant, string code, string deviceIdentifier) =>
        await AgentClient(tenant).PostAsJsonAsync(EnrolPath, RedeemPayload(code, deviceIdentifier));

    private async Task<DeviceEnrolledDto> RedeemAsync(
        TenantSeed tenant, string code, string deviceIdentifier)
    {
        var response = await RawRedeemAsync(tenant, code, deviceIdentifier);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceEnrolledDto>())!;
    }

    private async Task<HttpResponseMessage> RawSelfAsync(TenantSeed tenant, string deviceIdentifier)
    {
        var client = AgentClient(tenant);
        client.DefaultRequestHeaders.Add("X-Device-Identifier", deviceIdentifier);
        return await client.GetAsync(SelfPath);
    }

    private async Task<DeviceSelfDto> GetSelfAsync(TenantSeed tenant, string deviceIdentifier)
    {
        var response = await RawSelfAsync(tenant, deviceIdentifier);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceSelfDto>())!;
    }

    private async Task<Guid> CreateBranchAsync(Guid organizationId)
    {
        await using var db = _postgres.CreateContext();
        var branch = new Branch { OrganizationId = organizationId, Name = $"Other {Guid.NewGuid():N}" };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        return branch.Id;
    }

    private Task<TenantSeed> SeedAsync() => TenantSeedFactory.CreateAsync(_postgres);
}
