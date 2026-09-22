using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zazi.Application;
using Zazi.Application.Email;
using Zazi.Application.Growth;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// What the business kept, and the evening email that tells the owner about it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProfitAndDigestTests : IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;
    private readonly WebApplicationFactory<Program>? _app;
    private readonly Outbox _outbox = new();

    public ProfitAndDigestTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        if (!postgres.IsAvailable)
        {
            return;
        }

        _factory = new PostgresApiFactory(postgres.ConnectionString!);
        _app = _factory.WithWebHostBuilder(builder =>
        {
            // The evening job must not fire while a test is running; the tests call it.
            builder.UseSetting("Digest:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(_outbox);
            });
        });
    }

    public void Dispose()
    {
        _app?.Dispose();
        _factory?.Dispose();
    }

    private sealed class Outbox : IEmailSender
    {
        private readonly List<EmailMessage> _sent = new();

        public IReadOnlyList<EmailMessage> Sent { get { lock (_sent) return _sent.ToList(); } }

        public Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            lock (_sent) _sent.Add(message);
            return Task.FromResult(EmailResult.Success());
        }
    }

    private async Task<T> ServiceAsync<T>(Func<T, Task> act) where T : notnull
    {
        using var scope = _app!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<T>();
        await act(service);
        return service;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task<TenantSeed> BusinessAsync(string email = "owner@carl.test")
    {
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        await using var db = _postgres.CreateContext();
        var org = await db.Organizations.SingleAsync(o => o.Id == tenant.OrganizationId);
        org.Email = $"{Guid.NewGuid():N}@carl.test";
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task CommissionAsync(TenantSeed tenant, decimal amount)
    {
        using var scope = _app!.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITransactionService>().CreateTransactionAsync(
            new CreateTransactionRequest(tenant.OrganizationId, tenant.BranchId, tenant.UserId, null, "MTN",
                TransactionType.Commission, amount, "GHS", null, Guid.NewGuid().ToString("N")[..10],
                TransactionSource.Manual, "Commission"));
    }

    // ─── Profit ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ProfitIsCommissionLessWhatTheBusinessSpent()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await BusinessAsync();

        await CommissionAsync(tenant, 300m);
        await ServiceAsync<IExpenseService>(async s =>
        {
            await s.RecordAsync(tenant.OrganizationId, null, null, ExpenseCategory.Rent, 120m, Today, "Shop", tenant.UserId, null);
            await s.RecordAsync(tenant.OrganizationId, null, tenant.UserId, ExpenseCategory.Airtime, 30.50m, Today, null, tenant.UserId, null);
        });

        await ServiceAsync<IExpenseService>(async s =>
        {
            var profit = await s.ProfitAsync(tenant.OrganizationId, null, Today.AddDays(-1), Today);
            Assert.Equal(300m, profit.Commission);
            Assert.Equal(150.50m, profit.Expenses);
            Assert.Equal(149.50m, profit.Profit);
            // Largest first, so the owner sees what to look at.
            Assert.Equal(ExpenseCategory.Rent, profit.ByCategory[0].Category);
            Assert.Equal(49.8m, profit.Margin);
        });
    }

    [SkippableFact]
    public async Task ACostRecordedTwiceIsCountedOnce()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await BusinessAsync();
        var token = Guid.NewGuid().ToString("N");

        await ServiceAsync<IExpenseService>(async s =>
        {
            await s.RecordAsync(tenant.OrganizationId, null, null, ExpenseCategory.Transport, 40m, Today, null, tenant.UserId, token);
            await s.RecordAsync(tenant.OrganizationId, null, null, ExpenseCategory.Transport, 40m, Today, null, tenant.UserId, token);

            var profit = await s.ProfitAsync(tenant.OrganizationId, null, Today, Today);
            Assert.Equal(40m, profit.Expenses);
        });
    }

    [SkippableFact]
    public async Task ACostIsRefusedWhenItCannotBeTrue()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await BusinessAsync();

        await ServiceAsync<IExpenseService>(async s =>
        {
            await Assert.ThrowsAsync<ExpenseRejectedException>(() => s.RecordAsync(
                tenant.OrganizationId, null, null, ExpenseCategory.Rent, 0m, Today, null, tenant.UserId, null));
            await Assert.ThrowsAsync<ExpenseRejectedException>(() => s.RecordAsync(
                tenant.OrganizationId, null, null, ExpenseCategory.Rent, 50m, Today.AddDays(2), null, tenant.UserId, null));
            // Somebody else's agent is not this business's cost.
            var stranger = await TenantSeedFactory.CreateAsync(_postgres);
            await Assert.ThrowsAsync<ExpenseRejectedException>(() => s.RecordAsync(
                tenant.OrganizationId, null, stranger.UserId, ExpenseCategory.Wages, 50m, Today, null, tenant.UserId, null));
        });
    }

    [SkippableFact]
    public async Task ACorrectionIsRecordedRatherThanTheCostBeingChanged()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await BusinessAsync();

        await ServiceAsync<IExpenseService>(async s =>
        {
            await s.RecordAsync(tenant.OrganizationId, null, null, ExpenseCategory.Other, 200m, Today, "Typed wrong", tenant.UserId, null);
            await s.RecordAsync(tenant.OrganizationId, null, null, ExpenseCategory.Other, -150m, Today, "Correction", tenant.UserId, null);

            var profit = await s.ProfitAsync(tenant.OrganizationId, null, Today, Today);
            Assert.Equal(50m, profit.Expenses);
            var list = await s.ListAsync(tenant.OrganizationId, null, Today, Today);
            Assert.Equal(2, list.Count);
        });
    }

    // ─── The evening email ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheOwnerIsEmailedTheDayWithWhatIsWaitingOnThem()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await BusinessAsync();

        await CommissionAsync(tenant, 42.50m);
        using (var scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITransactionService>().CreateTransactionAsync(
                new CreateTransactionRequest(tenant.OrganizationId, tenant.BranchId, tenant.UserId, null, "MTN",
                    TransactionType.CashOut, 250m, "GHS", "0244123456", null, TransactionSource.Manual, null));
            await scope.ServiceProvider.GetRequiredService<IExpenseService>().RecordAsync(
                tenant.OrganizationId, null, null, ExpenseCategory.Airtime, 12.50m, Today, null, tenant.UserId, null);
        }

        DailyDigest? digest = null;
        await ServiceAsync<IDailyDigestService>(async s => digest = await s.BuildAsync(tenant.OrganizationId, Today));

        Assert.NotNull(digest);
        Assert.Equal(1, digest!.Transactions);
        Assert.Equal(250m, digest.Volume);
        Assert.Equal(42.50m, digest.Commission);
        Assert.Equal(30m, digest.MonthProfit);
        Assert.True(digest.WorthSending);

        // Other businesses seeded by other tests share this database, so what matters is that
        // this one was written to — not how many were.
        await ServiceAsync<IDailyDigestService>(async s => Assert.True(await s.SendAllAsync(Today) >= 1));

        var email = Assert.Single(_outbox.Sent, m => m.ToAddress == digest.ToAddress);
        Assert.Contains("1 transactions", email.Subject, StringComparison.Ordinal);
        Assert.Contains("GHS 30.00", email.HtmlBody, StringComparison.Ordinal);
        // They traded and did not close, so the email says so rather than claiming all is well.
        Assert.Contains("Not closed", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Test Agent", email.HtmlBody, StringComparison.Ordinal);
        // One summary per business per day, whatever the connection does.
        Assert.Equal($"digest-{tenant.OrganizationId:N}-{Today:yyyy-MM-dd}", email.IdempotencyKey);
    }

    [SkippableFact]
    public async Task ABusinessThatDidNothingIsNotEmailed()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var quiet = await BusinessAsync();

        DailyDigest? digest = null;
        await ServiceAsync<IDailyDigestService>(async s => digest = await s.BuildAsync(quiet.OrganizationId, Today));

        Assert.NotNull(digest);
        Assert.False(digest!.WorthSending);
        Assert.DoesNotContain(_outbox.Sent, m => m.ToAddress == digest.ToAddress);
    }

    [SkippableFact]
    public async Task AnOwnerWhoSwitchedItOffIsNotEmailed()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await BusinessAsync();
        await CommissionAsync(tenant, 10m);

        await ServiceAsync<IBusinessSettingsService>(s =>
            s.SaveAsync(tenant.OrganizationId, null, false, sendDailyDigest: false, tenant.UserId));

        await ServiceAsync<IDailyDigestService>(async s => await s.SendAllAsync(Today));

        await using var db = _postgres.CreateContext();
        var address = (await db.Organizations.SingleAsync(o => o.Id == tenant.OrganizationId)).Email;
        Assert.DoesNotContain(_outbox.Sent, m => m.ToAddress == address);
    }
}
