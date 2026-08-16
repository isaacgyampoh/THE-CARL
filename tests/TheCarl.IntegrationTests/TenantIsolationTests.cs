using System.Net;
using System.Net.Http.Json;
using TheCarl.Application;
using TheCarl.Application.Security;
using TheCarl.Domain;

namespace TheCarl.IntegrationTests;

/// <summary>
/// Tenant isolation must hold server-side. These tests assert that a valid token for one
/// organization cannot reach another organization's data by any request-shaped means.
/// </summary>
public class TenantIsolationTests : IClassFixture<CarlApiFactory>
{
    private readonly CarlApiFactory _factory;

    public TenantIsolationTests(CarlApiFactory factory) => _factory = factory;

    [Fact]
    public async Task TransactionList_ReturnsOnlyTheCallersOrganization()
    {
        var alpha = await TenantSeeder.SeedAsync(_factory, "Alpha");
        var beta = await TenantSeeder.SeedAsync(_factory, "Beta");

        await SeedTransactionAsync(alpha, 111m);
        await SeedTransactionAsync(beta, 222m);

        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(alpha.OwnerUserId, alpha.OrganizationId, null, CarlRoles.Owner));

        var page = await client.GetFromJsonAsync<PagedResult<TransactionDto>>("/api/v1/transactions");

        Assert.NotNull(page);
        Assert.All(page!.Items, t => Assert.Equal(alpha.OrganizationId, t.OrganizationId));
        Assert.Contains(page.Items, t => t.Amount == 111m);
        Assert.DoesNotContain(page.Items, t => t.Amount == 222m);
    }

    [Fact]
    public async Task BranchList_ReturnsOnlyTheCallersOrganization()
    {
        var alpha = await TenantSeeder.SeedAsync(_factory, "Gamma");
        await TenantSeeder.SeedAsync(_factory, "Delta");

        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(alpha.OwnerUserId, alpha.OrganizationId, null, CarlRoles.Owner));

        var branches = await client.GetFromJsonAsync<List<BranchDto>>("/api/v1/branches");

        Assert.NotNull(branches);
        Assert.All(branches!, b => Assert.Equal(alpha.OrganizationId, b.OrganizationId));
    }

    [Fact]
    public async Task ReadingAnotherTenantsSession_IsForbidden()
    {
        var alpha = await TenantSeeder.SeedAsync(_factory, "Epsilon");
        var beta = await TenantSeeder.SeedAsync(_factory, "Zeta");

        var betaSessionId = await _factory.WithDbAsync(async db =>
        {
            var session = new Session
            {
                OrganizationId = beta.OrganizationId,
                BranchId = beta.BranchAId,
                UserId = beta.AgentUserId,
                OpeningCash = 900m,
                OpeningFloat = 4000m
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
            return session.Id;
        });

        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(alpha.OwnerUserId, alpha.OrganizationId, null, CarlRoles.Owner));

        var response = await client.GetAsync($"/api/v1/sessions/{betaSessionId}");

        // 403 rather than 404: the caller learns nothing about whether the id exists.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WritingToAnotherTenantsBranch_IsForbidden()
    {
        var alpha = await TenantSeeder.SeedAsync(_factory, "Eta");
        var beta = await TenantSeeder.SeedAsync(_factory, "Theta");

        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(alpha.OwnerUserId, alpha.OrganizationId, null, CarlRoles.Owner));

        var response = await client.PostAsJsonAsync("/api/v1/transactions", new
        {
            branchId = beta.BranchAId,
            network = "MTN",
            type = (int)TransactionType.CashIn,
            amount = 50m
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var leaked = await _factory.WithDbAsync(db =>
            Task.FromResult(db.Transactions.Any(t => t.BranchId == beta.BranchAId)));
        Assert.False(leaked);
    }

    [Fact]
    public async Task AgentCannotReachAnotherBranchWithinTheirOwnOrganization()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "Iota");

        // Agent is assigned to branch A and asks to write into branch B.
        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.Agent));

        var response = await client.PostAsJsonAsync("/api/v1/transactions", new
        {
            branchId = tenant.BranchBId,
            network = "MTN",
            type = (int)TransactionType.CashIn,
            amount = 25m
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AgentTransactionListIsPinnedToTheirOwnBranch()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "Kappa");
        await SeedTransactionAsync(tenant, 10m, tenant.BranchAId);
        await SeedTransactionAsync(tenant, 20m, tenant.BranchBId);

        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(tenant.AgentUserId, tenant.OrganizationId, tenant.BranchAId, CarlRoles.Agent));

        // Even when the agent explicitly asks for branch B, they get branch A.
        var page = await client.GetFromJsonAsync<PagedResult<TransactionDto>>(
            $"/api/v1/transactions?branchId={tenant.BranchBId}");

        Assert.NotNull(page);
        Assert.All(page!.Items, t => Assert.Equal(tenant.BranchAId, t.BranchId));
        Assert.DoesNotContain(page.Items, t => t.Amount == 20m);
    }

    [Fact]
    public async Task TokenSignedWithAnotherKey_IsRejected()
    {
        var tenant = await TenantSeeder.SeedAsync(_factory, "Lambda");

        var client = _factory.CreateClient().Authenticated(
            TestTokens.CreateWithForeignKey(tenant.OwnerUserId, tenant.OrganizationId, null, CarlRoles.Owner));

        var response = await client.GetAsync("/api/v1/transactions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SmsEvidenceIsRecordedAgainstTheCallersOrganizationOnly()
    {
        var alpha = await TenantSeeder.SeedAsync(_factory, "Mu");
        var beta = await TenantSeeder.SeedAsync(_factory, "Nu");

        var client = _factory.CreateClient().Authenticated(
            TestTokens.Create(alpha.AgentUserId, alpha.OrganizationId, alpha.BranchAId, CarlRoles.Agent));

        // The body carries Beta's identifiers; they must be ignored entirely.
        var response = await client.PostAsJsonAsync("/api/v1/sms/capture", new
        {
            organizationId = beta.OrganizationId,
            branchId = (Guid?)null,
            sourcePhoneNumber = "MTN",
            rawMessage = "MTN MOMO: Deposit of GHS 42.00. Ref: ISOLATE1. Customer 0241234567"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var recordedForBeta = await _factory.WithDbAsync(db =>
            Task.FromResult(db.TransactionEvidence.Any(m => m.OrganizationId == beta.OrganizationId)));
        var recordedForAlpha = await _factory.WithDbAsync(db =>
            Task.FromResult(db.TransactionEvidence.Any(m =>
                m.OrganizationId == alpha.OrganizationId && m.BranchId == alpha.BranchAId)));

        Assert.False(recordedForBeta);
        Assert.True(recordedForAlpha);
    }

    [Fact]
    public async Task IdenticalSmsInTwoTenantsBothProduceEvidence()
    {
        var alpha = await TenantSeeder.SeedAsync(_factory, "Xi");
        var beta = await TenantSeeder.SeedAsync(_factory, "Omicron");

        const string message = "MTN MOMO: Deposit of GHS 88.00. Ref: SHARED9. Customer 0241234567";

        var alphaClient = _factory.CreateClient().Authenticated(
            TestTokens.Create(alpha.AgentUserId, alpha.OrganizationId, alpha.BranchAId, CarlRoles.Agent));
        var betaClient = _factory.CreateClient().Authenticated(
            TestTokens.Create(beta.AgentUserId, beta.OrganizationId, beta.BranchAId, CarlRoles.Agent));

        var first = await alphaClient.PostAsJsonAsync("/api/v1/sms/capture", new
        {
            sourcePhoneNumber = "MTN",
            rawMessage = message
        });
        var second = await betaClient.PostAsJsonAsync("/api/v1/sms/capture", new
        {
            sourcePhoneNumber = "MTN",
            rawMessage = message
        });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var betaResult = await second.Content.ReadFromJsonAsync<SmsParseResultDto>();

        // Duplicate detection is per-tenant: Alpha's capture must not suppress Beta's.
        Assert.NotNull(betaResult);
        Assert.False(betaResult!.IsDuplicate);
    }

    private async Task SeedTransactionAsync(TestTenant tenant, decimal amount, Guid? branchId = null)
    {
        await _factory.WithDbAsync(async db =>
        {
            db.Transactions.Add(new FinancialTransaction
            {
                OrganizationId = tenant.OrganizationId,
                BranchId = branchId ?? tenant.BranchAId,
                AgentId = tenant.AgentUserId,
                Network = "MTN",
                Type = TransactionType.CashIn,
                Amount = amount,
                State = TransactionLifecycleState.Accepted
            });
            await db.SaveChangesAsync();
        });
    }
}
