using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// One agent working three phones, one per network.
/// </summary>
/// <remarks>
/// <para>
/// The ordinary setup for a Ghanaian mobile money agent: a dedicated MTN handset, a dedicated
/// Telecel handset, a dedicated AirtelTigo handset, all worked by the same person. It is not an
/// edge case, and the thing that would ruin it is subtle — the owner seeing three separate
/// agents rather than one person's combined day, or float from one network landing on another.
/// </para>
/// <para>
/// A device carries no owner of its own; the agent comes from whoever the handset is signed in
/// as. These assert that consequence rather than the assumption behind it.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class MultiPhoneAgentTests : IDisposable
{
    private const string CodesPath = "/api/v1/devices/enrollment-codes";
    private const string WorkersPath = "/api/v1/auth/workers";
    private const string ActivatePath = "/api/v1/devices/activate";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public MultiPhoneAgentTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    [SkippableFact]
    public async Task OneAgentActivatesThreeHandsetsAndRemainsOnePerson()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Kofi Three Phones");

        // One code per handset. Codes are single-use by design, so three phones need three —
        // but every one of them names the same worker.
        var handsets = new[] { "mtn-handset", "telecel-handset", "airteltigo-handset" };
        var agentIds = new List<Guid>();

        foreach (var handset in handsets)
        {
            var issued = await IssueAsync(tenant, worker.Id);
            var activation = await ActivateAsync(issued.Code, handset + "-" + Guid.NewGuid().ToString("N")[..8]);

            Assert.Equal("Kofi Three Phones", activation.WorkerName);
            agentIds.Add(worker.Id);
        }

        await using var db = _postgres.CreateContext();

        // Three handsets registered...
        var devices = await db.Devices.Where(d => d.OrganizationId == tenant.OrganizationId).ToListAsync();
        Assert.Equal(3, devices.Count);

        // ...and still exactly one worker. The failure this guards against is three phones
        // appearing as three staff, which would split one person's day three ways on every
        // report the owner reads.
        var workers = await db.Users
            .Where(u => u.OrganizationId == tenant.OrganizationId && u.CredentialType == UserCredentialType.ActivationOnly)
            .ToListAsync();
        Assert.Single(workers);
        Assert.All(agentIds, id => Assert.Equal(worker.Id, id));
    }

    [SkippableFact]
    public async Task EachHandsetGetsItsOwnSessionForTheSamePerson()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Ama Three Phones");

        foreach (var _ in Enumerable.Range(0, 3))
        {
            var issued = await IssueAsync(tenant, worker.Id);
            await ActivateAsync(issued.Code, "handset-" + Guid.NewGuid().ToString("N")[..10]);
        }

        await using var db = _postgres.CreateContext();
        var sessions = await db.AuthSessions.Where(s => s.UserId == worker.Id).ToListAsync();

        // Three sessions, three devices, one person. Revoking one handset must not sign the
        // other two out, which is why they are separate sessions rather than one shared token.
        Assert.Equal(3, sessions.Count);
        Assert.Equal(3, sessions.Select(s => s.DeviceId).Distinct().Count());
        Assert.All(sessions, s => Assert.Equal(worker.Id, s.UserId));
    }

    [SkippableFact]
    public async Task ACodeIssuedForOneHandsetCannotBeUsedBySecond()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedAsync();
        var worker = await CreateWorkerAsync(tenant, "Single Use Check");
        var issued = await IssueAsync(tenant, worker.Id);

        await ActivateAsync(issued.Code, "first-" + Guid.NewGuid().ToString("N")[..8]);

        // Otherwise one leaked code would enrol any number of handsets against a real worker.
        var second = await RawActivateAsync(issued.Code, "second-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.False(second.IsSuccessStatusCode);
    }

    // ─── helpers, mirroring WorkerActivationTests ────────────────────────────

    private sealed record TenantIds(Guid OrganizationId, Guid BranchId, Guid OwnerId);

    private async Task<TenantIds> SeedAsync()
    {
        await using var db = _postgres.CreateContext();
        var organization = new Organization { Name = $"MultiPhone {Guid.NewGuid():N}" };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Main branch" };
        var owner = new User
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            FullName = "Owner",
            Email = $"owner-{Guid.NewGuid():N}@example.com"
        };
        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        return new TenantIds(organization.Id, branch.Id, owner.Id);
    }

    private HttpClient OwnerClient(TenantIds tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.OwnerId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Owner));

    private async Task<UserDto> CreateWorkerAsync(TenantIds tenant, string name)
    {
        var response = await OwnerClient(tenant).PostAsJsonAsync(WorkersPath, new
        {
            branchId = tenant.BranchId,
            fullName = name,
            roles = new[] { ZaziRoles.Agent }
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserDto>())!;
    }

    private async Task<EnrollmentCodeIssuedDto> IssueAsync(TenantIds tenant, Guid workerId)
    {
        var response = await OwnerClient(tenant).PostAsJsonAsync(CodesPath, new
        {
            branchId = tenant.BranchId,
            intendedUserId = workerId
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentCodeIssuedDto>())!;
    }

    private async Task<HttpResponseMessage> RawActivateAsync(string code, string deviceIdentifier) =>
        await _factory!.CreateClient().PostAsJsonAsync(ActivatePath, new
        {
            code,
            deviceIdentifier,
            deviceName = deviceIdentifier
        });

    private async Task<DeviceActivationResult> ActivateAsync(string code, string deviceIdentifier)
    {
        var response = await RawActivateAsync(code, deviceIdentifier);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceActivationResult>())!;
    }
}
