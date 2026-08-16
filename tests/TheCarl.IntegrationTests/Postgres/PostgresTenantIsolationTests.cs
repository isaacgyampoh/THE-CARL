using Microsoft.EntityFrameworkCore;
using TheCarl.Application;
using TheCarl.Application.Security;
using TheCarl.Domain;
using TheCarl.Infrastructure;
using TheCarl.Infrastructure.Services;

namespace TheCarl.IntegrationTests.Postgres;

/// <summary>
/// Tenant isolation proved at the data layer against real PostgreSQL, complementing the
/// HTTP-level tests. Controller tests alone can only show that the pipeline refuses a
/// request; these show that the service and query layer refuses it too, so isolation does
/// not depend on a controller remembering to check.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresTenantIsolationTests
{
    private readonly PostgresFixture _postgres;

    public PostgresTenantIsolationTests(PostgresFixture postgres) => _postgres = postgres;

    [SkippableFact]
    public async Task TenantACannotSeeTenantBTransactions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        await CreateTransactionAsync(alpha, 111m);
        await CreateTransactionAsync(beta, 222m);

        await using var db = _postgres.CreateContext();
        var service = new TransactionService(db, new LedgerService(db));

        var page = await service.GetTransactionsAsync(
            new TransactionQuery(alpha.OrganizationId, null, 1, 50));

        Assert.All(page.Items, t => Assert.Equal(alpha.OrganizationId, t.OrganizationId));
        Assert.Contains(page.Items, t => t.Amount == 111m);
        Assert.DoesNotContain(page.Items, t => t.Amount == 222m);
    }

    [SkippableFact]
    public async Task TenantACannotCreateTransactionsForTenantB()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        await using var db = _postgres.CreateContext();
        var currentUser = new StubCurrentUser(alpha.OrganizationId, alpha.UserId, alpha.BranchId, CarlRoles.Owner);
        var guard = new TenantGuard(db, currentUser);

        // Naming another tenant's branch is refused before any row is written.
        await Assert.ThrowsAsync<TenantAccessDeniedException>(
            () => guard.EnsureBranchInTenantAsync(beta.BranchId));

        Assert.Equal(0, await db.Transactions.CountAsync(x => x.BranchId == beta.BranchId));
    }

    [SkippableFact]
    public async Task TenantACannotSynchroniseTenantBDevices()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        await using var db = _postgres.CreateContext();
        var guard = new TenantGuard(
            db, new StubCurrentUser(alpha.OrganizationId, alpha.UserId, alpha.BranchId, CarlRoles.Owner));

        await Assert.ThrowsAsync<TenantAccessDeniedException>(
            () => guard.EnsureDeviceInTenantAsync(beta.DeviceId));
    }

    [SkippableFact]
    public async Task TenantACannotModifyTenantBSessions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        var betaSessionId = await CreateSessionAsync(beta);

        await using var db = _postgres.CreateContext();
        var guard = new TenantGuard(
            db, new StubCurrentUser(alpha.OrganizationId, alpha.UserId, alpha.BranchId, CarlRoles.Owner));

        await Assert.ThrowsAsync<TenantAccessDeniedException>(
            () => guard.EnsureSessionInTenantAsync(betaSessionId));
    }

    [SkippableFact]
    public async Task DashboardAggregatesNeverCrossTenants()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var alpha = await SeedAsync();
        var beta = await SeedAsync();

        await CreateTransactionAsync(alpha, 100m);
        await CreateTransactionAsync(beta, 900m);
        await CreateTransactionAsync(beta, 900m);

        await using var db = _postgres.CreateContext();
        var dashboard = new DashboardService(db);

        var summary = await dashboard.GetOrganizationDashboardAsync(alpha.OrganizationId);

        // Beta's ₵1,800 must be invisible in Alpha's summary.
        Assert.Equal(100m, summary.TodayVolume);
        Assert.Equal(1, summary.TodayTransactions);
    }

    [SkippableFact]
    public async Task IdenticalEvidenceInTwoTenantsProducesTwoTransactions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var alpha = await SeedAsync();
        var beta = await SeedAsync();
        const string message = "MTN MOMO: Deposit of GHS 88.00. Ref: CROSSTENANT. Customer 0241234567";

        await CaptureSmsAsync(alpha, message);
        await CaptureSmsAsync(beta, message);

        await using var db = _postgres.CreateContext();

        // The unique index is (OrganizationId, EvidenceFingerprint). A global constraint
        // would have silently dropped the second tenant's real transaction.
        Assert.Equal(1, await db.Transactions.CountAsync(x => x.OrganizationId == alpha.OrganizationId));
        Assert.Equal(1, await db.Transactions.CountAsync(x => x.OrganizationId == beta.OrganizationId));
    }

    [SkippableFact]
    public async Task TheSameEvidenceTwiceInOneTenantProducesOneTransaction()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await SeedAsync();
        const string message = "MTN MOMO: Deposit of GHS 42.00. Ref: SAMETENANT. Customer 0241234567";

        await CaptureSmsAsync(tenant, message);
        await CaptureSmsAsync(tenant, message);

        await using var db = _postgres.CreateContext();

        Assert.Equal(1, await db.Transactions.CountAsync(x => x.OrganizationId == tenant.OrganizationId));

        // Both observations are still recorded: the fact that a duplicate arrived is auditable.
        Assert.Equal(2, await db.TransactionEvidence.CountAsync(x => x.OrganizationId == tenant.OrganizationId));
        Assert.Equal(1, await db.TransactionEvidence.CountAsync(
            x => x.OrganizationId == tenant.OrganizationId && x.IsDuplicate));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private sealed record TenantSeed(Guid OrganizationId, Guid BranchId, Guid UserId, Guid DeviceId);

    private async Task<TenantSeed> SeedAsync()
    {
        await using var db = _postgres.CreateContext();

        var organization = new Organization
        {
            Name = $"Isolation {Guid.NewGuid():N}",
            Country = "GH",
            CurrencyCode = Money.DefaultCurrency
        };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Main" };
        var user = new User
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            FullName = "Isolation Agent",
            Email = $"agent-{Guid.NewGuid():N}@carl.test",
            IsActive = true
        };
        var device = new Device
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            Name = "Phone",
            DeviceIdentifier = $"device-{Guid.NewGuid():N}"
        };

        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        db.Users.Add(user);
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        return new TenantSeed(organization.Id, branch.Id, user.Id, device.Id);
    }

    private async Task CreateTransactionAsync(TenantSeed tenant, decimal amount)
    {
        await using var db = _postgres.CreateContext();
        var service = new TransactionService(db, new LedgerService(db));

        await service.CreateTransactionAsync(new CreateTransactionRequest(
            tenant.OrganizationId, tenant.BranchId, tenant.UserId, null, "MTN",
            TransactionType.CashIn, amount, Money.DefaultCurrency, null, null,
            TransactionSource.Manual, null,
            ClientTransactionId: ClientTransactionId.Create($"seed-{Guid.NewGuid():N}")));
    }

    private async Task<Guid> CreateSessionAsync(TenantSeed tenant)
    {
        await using var db = _postgres.CreateContext();
        var session = new Session
        {
            OrganizationId = tenant.OrganizationId,
            BranchId = tenant.BranchId,
            UserId = tenant.UserId,
            OpeningCash = 500m,
            OpeningFloat = 2000m
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task CaptureSmsAsync(TenantSeed tenant, string message)
    {
        await using var db = _postgres.CreateContext();
        var service = new SmsProcessingService(db, new LedgerService(db));

        await service.ProcessIncomingSmsAsync(
            new SmsCaptureRequest(
                tenant.OrganizationId, tenant.BranchId, tenant.DeviceId, "MTN", message, "MTN",
                new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero)),
            tenant.UserId);
    }

    /// <summary>Minimal <see cref="ICurrentUserContext"/> for exercising the guard directly.</summary>
    private sealed class StubCurrentUser : ICurrentUserContext
    {
        private readonly string[] _roles;

        public StubCurrentUser(Guid organizationId, Guid userId, Guid? branchId, params string[] roles)
        {
            OrganizationId = organizationId;
            UserId = userId;
            BranchId = branchId;
            _roles = roles;
        }

        public bool IsAuthenticated => true;
        public Guid UserId { get; }
        public Guid OrganizationId { get; }
        public Guid? BranchId { get; }
        public IReadOnlyCollection<string> Roles => _roles;
        public string? SecurityStamp => null;
        public string CorrelationId => "test";

        public bool IsInRole(string canonicalRole) => _roles.Contains(canonicalRole, StringComparer.Ordinal);
        public bool HasOrganizationWideScope => _roles.Any(CarlRoles.IsOrganizationWide);

        public bool CanAccessBranch(Guid branchId) =>
            HasOrganizationWideScope || (BranchId.HasValue && BranchId.Value == branchId);

        public void EnsureBranchAccess(Guid branchId)
        {
            if (!CanAccessBranch(branchId))
            {
                throw new TenantAccessDeniedException("Branch access denied.");
            }
        }

        public void EnsureOrganizationMatches(Guid organizationId)
        {
            if (organizationId != OrganizationId)
            {
                throw new TenantAccessDeniedException(OrganizationId, organizationId);
            }
        }
    }
}
