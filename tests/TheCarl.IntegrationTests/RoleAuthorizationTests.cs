using System.Net;
using System.Net.Http.Json;
using TheCarl.Application.Security;
using TheCarl.Domain;

namespace TheCarl.IntegrationTests;

/// <summary>
/// Role-based access control at the API boundary: which roles may reach which capability.
/// </summary>
public class RoleAuthorizationTests : IClassFixture<CarlApiFactory>
{
    private readonly CarlApiFactory _factory;

    public RoleAuthorizationTests(CarlApiFactory factory) => _factory = factory;

    [Fact]
    public async Task AgentCannotReachTheOrganizationDashboard()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "DashOrg");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.Agent));

        var response = await client.GetAsync("/api/v1/dashboard/organization");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OwnerCanReachTheOrganizationDashboard()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "DashOwner");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.OwnerUserId, tenant.OrganizationId, null, CarlRoles.Owner));

        var response = await client.GetAsync("/api/v1/dashboard/organization");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AgentCannotEnumerateEveryOrganizationOnThePlatform()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "PlatformProbe");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.Agent));

        var response = await client.GetAsync("/api/v1/organizations");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OwnerCannotEnumerateEveryOrganizationOnThePlatform()
    {
        // Even an OWNER is confined to their own tenant; only PLATFORM_ADMIN crosses that line.
        var tenant = await TenantSeeder.SeedAsync(_factory, "OwnerProbe");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.OwnerUserId, tenant.OrganizationId, null, CarlRoles.Owner));

        var response = await client.GetAsync("/api/v1/organizations");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuditorIsReadOnly()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "AuditRO");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.OwnerUserId, tenant.OrganizationId, null, CarlRoles.Auditor));

        var read = await client.GetAsync("/api/v1/transactions");
        var write = await client.PostAsJsonAsync("/api/v1/transactions", new
        {
            branchId = tenant.BranchAId,
            network = "MTN",
            type = (int)TransactionType.CashIn,
            amount = 10m
        });

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task AgentCannotCreateStaff()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "StaffAgent");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.Agent));

        var response = await client.PostAsJsonAsync("/api/v1/auth/staff", new
        {
            branchId = tenant.BranchAId,
            fullName = "New Person",
            email = "new.person@staffagent.test",
            password = "Str0ng-Passphrase!",
            roles = new[] { CarlRoles.Agent }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BranchManagerCannotEscalateByCreatingAnOwner()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "Escalate");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.BranchManager));

        var response = await client.PostAsJsonAsync("/api/v1/auth/staff", new
        {
            branchId = tenant.BranchAId,
            fullName = "Sneaky Owner",
            email = "sneaky@escalate.test",
            password = "Str0ng-Passphrase!",
            roles = new[] { CarlRoles.Owner }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var created = await _factory.WithDbAsync(db =>
            Task.FromResult(db.Users.Any(u => u.Email == "sneaky@escalate.test")));
        Assert.False(created);
    }

    [Fact]
    public async Task BranchManagerCanCreateABranchScopedAgent()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "MgrCreates");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.BranchManager));

        var response = await client.PostAsJsonAsync("/api/v1/auth/staff", new
        {
            branchId = tenant.BranchAId,
            fullName = "Legit Agent",
            email = "legit@mgrcreates.test",
            password = "Str0ng-Passphrase!",
            roles = new[] { CarlRoles.Agent }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task AgentCannotDrainTheSyncQueue()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "SyncDrain");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.Agent));

        var response = await client.PostAsync("/api/v1/sync/process", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AgentCannotCreateBranches()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "BranchCreate");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.Agent));

        var response = await client.PostAsJsonAsync("/api/v1/branches", new { name = "Unauthorized Branch" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TokenWithNoRolesCannotReachPolicyProtectedEndpoints()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "NoRoles");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId));

        var response = await client.GetAsync("/api/v1/transactions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RoleClaimsAreMatchedIndividuallyNotAsAJoinedString()
    {
        // Regression guard: roles used to be issued as one comma-joined claim value, which
        // matched no role at all. A multi-role token must satisfy policies for each role.
        var tenant = await TenantSeeder.SeedAsync(_factory, "MultiRole");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(
                tenant.OwnerUserId,
                tenant.OrganizationId,
                null,
                CarlRoles.Auditor,
                CarlRoles.Owner));

        var dashboard = await client.GetAsync("/api/v1/dashboard/organization");
        var transactions = await client.GetAsync("/api/v1/transactions");

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal(HttpStatusCode.OK, transactions.StatusCode);
    }

    [Fact]
    public async Task ManualEntryCannotClaimVerifiedSmsProvenance()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "Provenance");
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.Agent));

        var response = await client.PostAsJsonAsync("/api/v1/transactions", new
        {
            branchId = tenant.BranchAId,
            network = "MTN",
            type = (int)TransactionType.CashIn,
            amount = 75m,
            // A client attempting to pass itself off as parsed SMS evidence.
            source = (int)TransactionSource.AutomaticSms
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var source = await _factory.WithDbAsync(db => Task.FromResult(
            db.Transactions
                .Where(t => t.OrganizationId == tenant.OrganizationId && t.Amount == 75m)
                .Select(t => t.Source)
                .Single()));

        Assert.Equal(TransactionSource.Manual, source);
    }

    [Fact]
    public async Task AgentIdentityComesFromTheTokenNotTheRequestBody()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "AgentAttrib");
        var impersonated = Guid.NewGuid();

        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.Agent));

        await client.PostAsJsonAsync("/api/v1/transactions", new
        {
            branchId = tenant.BranchAId,
            agentId = impersonated,
            network = "MTN",
            type = (int)TransactionType.CashIn,
            amount = 33m
        });

        var agentId = await _factory.WithDbAsync(db => Task.FromResult(
            db.Transactions
                .Where(t => t.OrganizationId == tenant.OrganizationId && t.Amount == 33m)
                .Select(t => t.AgentId)
                .Single()));

        Assert.Equal(tenant.AgentUserId, agentId);
        Assert.NotEqual(impersonated, agentId);
    }
}
