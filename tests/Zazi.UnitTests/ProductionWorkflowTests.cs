using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Services;

namespace Zazi.UnitTests;

public class ProductionWorkflowTests
{
    [Fact]
    public async Task CreateOrganizationAndBranch_WorkflowPersistsTenantData()
    {
        await using var db = CreateDbContext();
        var organizationService = new OrganizationService(db);

        var organization = await organizationService.CreateOrganizationAsync(new CreateOrganizationRequest(
            "Cedar Finance",
            "ops@cedar.finance",
            "+233200000000",
            "GH",
            Money.DefaultCurrency));

        var branch = await organizationService.CreateBranchAsync(new CreateBranchRequest(
            organization.Id,
            "Accra Central",
            "Accra"));

        Assert.NotNull(branch);
        Assert.Equal(organization.Id, branch.OrganizationId);
        Assert.Equal(1, await db.Organizations.CountAsync());
        Assert.Equal(1, await db.Branches.CountAsync());
    }

    [Fact]
    public async Task SessionAndTransactionFlow_ReconcilesExpectedCash()
    {
        await using var db = CreateDbContext();
        var org = new Organization { Name = "Mango Mobile", CurrencyCode = Money.DefaultCurrency, Country = "GH" };
        var branch = new Branch { OrganizationId = org.Id, Name = "Tema" };
        var user = new User { OrganizationId = org.Id, FullName = "Grace Agent", Email = "grace@example.com", IsActive = true };

        db.Organizations.Add(org);
        db.Branches.Add(branch);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var sessionService = new SessionService(db);
        var ledgerService = new LedgerService(db);
        var txService = new TransactionService(db, ledgerService);
        var reconciliationService = new ReconciliationService(db);

        var session = await sessionService.OpenSessionAsync(new CreateSessionRequest(
            org.Id,
            branch.Id,
            user.Id,
            null,
            500m,
            2000m));

        await txService.CreateTransactionAsync(new CreateTransactionRequest(
            org.Id,
            branch.Id,
            user.Id,
            null,
            "MTN",
            TransactionType.CashIn,
            150m,
            Money.DefaultCurrency,
            "+233201234567",
            "REF-1001",
            TransactionSource.Manual,
            "Cash deposit"));

        await txService.CreateTransactionAsync(new CreateTransactionRequest(
            org.Id,
            branch.Id,
            user.Id,
            null,
            "MTN",
            TransactionType.CashOut,
            50m,
            Money.DefaultCurrency,
            "+233208765432",
            "REF-1002",
            TransactionSource.Manual,
            "Cash withdrawal"));

        var cashBalance = await db.CashBalances.SingleAsync(x => x.OrganizationId == org.Id && x.BranchId == branch.Id);
        var floatBalance = await db.FloatBalances.SingleAsync(x => x.OrganizationId == org.Id && x.BranchId == branch.Id && x.Network == "MTN");
        var result = await reconciliationService.ReconcileSessionAsync(session.Id, cashBalance.CurrentCash, floatBalance.CurrentFloat);

        Assert.Equal("Balanced", result.Status);
        // Agent's books: a customer deposit brings cash in and sends float out; a customer
        // withdrawal pays cash out and takes float in.
        Assert.Equal(500m + 150m - 50m, cashBalance.CurrentCash);
        Assert.Equal(2000m - 150m + 50m, floatBalance.CurrentFloat);
        Assert.Equal(150m, await db.Transactions.Where(x => x.Type == TransactionType.CashIn).SumAsync(x => x.Amount));
        Assert.Equal(50m, await db.Transactions.Where(x => x.Type == TransactionType.CashOut).SumAsync(x => x.Amount));
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task RegisterAndLoginUser_ProducesAccessAndRefreshTokens()
    {
        await using var db = CreateDbContext();
        var org = new Organization { Name = "Akwaaba Care", CurrencyCode = Money.DefaultCurrency, Country = "GH" };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        var branch = new Branch { OrganizationId = org.Id, Name = "Osu" };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();

        var authService = TestServices.CreateAuthService(db);
        var registered = await authService.RegisterUserAsync(new RegisterUserRequest(
            org.Id,
            branch.Id,
            "Aisha Agent",
            "aisha.agent@carl.test",
            "Password123!",
            "+233501112233",
            [ZaziRoles.Agent]));

        var login = await authService.LoginAsync(new LoginRequest("aisha.agent@carl.test", "Password123!"));

        Assert.Equal("Aisha Agent", registered.FullName);
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));
        Assert.Equal(registered.Email, login.User.Email);
    }

    [Fact]
    public async Task OfflineQueue_TracksPendingSyncEvents()
    {
        await using var db = CreateDbContext();
        var organization = new Organization { Name = "Syncware", CurrencyCode = Money.DefaultCurrency, Country = "GH" };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync();

        var syncService = new OfflineSyncService(db);
        var queued = await syncService.QueueEventAsync(new QueueSyncEventRequest(
            organization.Id,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Transaction",
            "Captured",
            "{\"amount\":125.5,\"network\":\"MTN\"}",
            true));

        var pending = await syncService.GetPendingAsync(organization.Id);
        var processed = await syncService.ProcessPendingAsync(organization.Id);

        Assert.Equal(SyncStatus.Pending, queued.Status);
        Assert.Single(pending);
        Assert.Equal(1, processed);
    }

    [Fact]
    public async Task SmsCapture_ParsesSupportedMessageAndRejectsDuplicates()
    {
        await using var db = CreateDbContext();
        var organization = new Organization { Name = "SMS Ops", CurrencyCode = Money.DefaultCurrency, Country = "GH" };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Madina" };
        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        await db.SaveChangesAsync();

        var agentId = Guid.NewGuid();
        var ledgerService = new LedgerService(db);
        var smsService = new SmsProcessingService(db, ledgerService);
        var request = new SmsCaptureRequest(
            organization.Id,
            branch.Id,
            Guid.NewGuid(),
            "MTN",
            "MTN MOMO: Deposit of GHS 75.50. Ref: ABC12345. Customer 0241234567",
            "MTN");

        var first = await smsService.ProcessIncomingSmsAsync(request, agentId);
        var duplicate = await smsService.ProcessIncomingSmsAsync(request, agentId);

        var airtel = await smsService.ProcessIncomingSmsAsync(new SmsCaptureRequest(
            organization.Id,
            branch.Id,
            Guid.NewGuid(),
            "AIRTELTIGO",
            "AIRTELTIGO: CASH OUT of GHS 200.00. Ref: TIGO-1234. Customer 0249876543",
            "AIRTELTIGO"), agentId);

        Assert.Equal("CashIn", first.TransactionType);
        Assert.Equal(75.5m, first.Amount);
        // Asserts the property that matters rather than a magic constant: complete evidence
        // clears the auto-post bar. The exact score is SmsEvidencePolicy's business.
        Assert.True(first.ConfidenceScore >= SmsEvidencePolicy.MinimumAutoPostConfidence);
        Assert.Equal(TransactionLifecycleState.Accepted, first.State);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal("CashOut", airtel.TransactionType);
        Assert.Equal(200m, airtel.Amount);
    }

    [Fact]
    public async Task LowFloatThresholds_CreateAlertsForWeakFloats()
    {
        await using var db = CreateDbContext();
        var organization = new Organization { Name = "Float Watch", CurrencyCode = Money.DefaultCurrency, Country = "GH" };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Kumasi" };

        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        db.FloatBalances.Add(new FloatBalance
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            Network = "MTN",
            OpeningFloat = 1000m,
            CurrentFloat = 150m,
            Threshold = 500m
        });
        await db.SaveChangesAsync();

        var alertService = new AlertService(db);
        await alertService.SetThresholdAsync(new AlertThresholdRequest(
            organization.Id,
            branch.Id,
            "MTN",
            500m,
            200m));

        var alertsCreated = await alertService.EvaluateFloatAlertsAsync(organization.Id);
        var alerts = await alertService.GetAlertsAsync(organization.Id, branch.Id);

        Assert.Equal(1, alertsCreated);
        Assert.Contains(alerts, x => x.Network == "MTN" && x.Severity == "Critical");
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new ApplicationDbContext(options);
    }
}
