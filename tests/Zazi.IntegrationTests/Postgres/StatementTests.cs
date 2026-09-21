using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Application.Statements;
using Zazi.Domain;
using Zazi.Infrastructure.Services;
using Zazi.Infrastructure.Statements;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Statements, the customer-number search, and records that cannot be rewritten.
/// </summary>
/// <remarks>
/// A tester using a rival app named what they depend on: downloading a statement for any day,
/// week, month or year, and finding every transaction a customer's number made — "I came at
/// 11:50 and withdrew fifty cedis" — with the exact time. A ₵50 cash-out recorded in testing
/// carried no number at all. These assert the number is kept, found and printed, and that the
/// record it is printed from cannot have been changed since.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class StatementTests : IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public StatementTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    private static DateTimeOffset Today(int hour, int minute) =>
        new(DateTime.UtcNow.Date.AddHours(hour).AddMinutes(minute), TimeSpan.Zero);

    private async Task<Guid> RecordAsync(
        TenantSeed tenant,
        TransactionType type,
        decimal amount,
        string? customer,
        DateTimeOffset at,
        Guid? agentId = null,
        string network = "MTN")
    {
        var (cash, eMoney) = type switch
        {
            TransactionType.CashIn => (amount, -amount),
            TransactionType.CashOut => (-amount, amount),
            _ => (0m, 0m)
        };

        var transaction = new FinancialTransaction
        {
            OrganizationId = tenant.OrganizationId,
            BranchId = tenant.BranchId,
            AgentId = agentId ?? tenant.UserId,
            Network = network,
            Type = type,
            Amount = amount,
            CashDelta = cash,
            FloatDelta = eMoney,
            CustomerPhoneNumber = customer,
            TransactionAtUtc = at,
            ClientTransactionId = Guid.NewGuid().ToString("N")
        };

        await using var db = _postgres.CreateContext();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return transaction.Id;
    }

    // ─── Statements ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AStatementCarriesEveryNumberAndTimeAndTheTotalsAtTheTop()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await RecordAsync(tenant, TransactionType.CashIn, 250m, "0244123456", Today(8, 5));
        await RecordAsync(tenant, TransactionType.CashOut, 50m, "0201234567", Today(11, 50));
        await RecordAsync(tenant, TransactionType.CashOut, 120m, "0271234567", Today(9, 30));
        await RecordAsync(tenant, TransactionType.Commission, 12.5m, null, Today(12, 0));

        await using var db = _postgres.CreateContext();
        var statement = await new StatementService(db).BuildAsync(
            new StatementRequest(tenant.OrganizationId, StatementPeriod.Today));

        // Oldest first — the order a statement is read in.
        Assert.Equal(
            new[] { Today(8, 5), Today(9, 30), Today(11, 50), Today(12, 0) },
            statement.Lines.Select(l => l.At));

        // The ₵50 cash-out at 11:50, with its number: the line a complaint is settled by.
        var complaint = Assert.Single(statement.Lines, l => l.At == Today(11, 50));
        Assert.Equal(TransactionType.CashOut, complaint.Type);
        Assert.Equal("0201234567", complaint.CustomerPhone);
        Assert.Equal(50m, complaint.Amount);

        var t = statement.Totals;
        Assert.Equal(4, t.TransactionCount);
        Assert.Equal(1, t.DepositCount);
        Assert.Equal(250m, t.Deposits);
        Assert.Equal(2, t.WithdrawalCount);
        Assert.Equal(170m, t.Withdrawals);
        Assert.Equal(12.5m, t.Commission);
        Assert.Equal(250m - 170m, t.NetCash);
    }

    [SkippableFact]
    public async Task AnAgentsStatementHoldsOnlyTheirOwnTransactions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var colleague = Guid.NewGuid();

        await RecordAsync(tenant, TransactionType.CashIn, 100m, "0244000001", Today(8, 0));
        await RecordAsync(tenant, TransactionType.CashIn, 900m, "0244000002", Today(8, 1), agentId: colleague);

        await using var db = _postgres.CreateContext();
        var statement = await new StatementService(db).BuildAsync(
            new StatementRequest(tenant.OrganizationId, StatementPeriod.Today, AgentId: tenant.UserId));

        var only = Assert.Single(statement.Lines);
        Assert.Equal(100m, only.Amount);
    }

    [SkippableFact]
    public async Task ACustomersNumberFindsThemHoweverItWasStored()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        // One customer, stored two ways: normalised, and as an older SMS capture wrote it.
        await RecordAsync(tenant, TransactionType.CashOut, 50m, "0244123456", Today(11, 50));
        await RecordAsync(tenant, TransactionType.CashIn, 80m, "233244123456", Today(10, 0));
        await RecordAsync(tenant, TransactionType.CashIn, 999m, "0209999999", Today(10, 5));

        await using var db = _postgres.CreateContext();
        var page = await new TransactionService(db, new LedgerService(db)).GetTransactionsAsync(
            new TransactionQuery(tenant.OrganizationId, null, 1, 50, CustomerPhone: "+233 24 412 3456"));

        Assert.Equal(2, page.TotalCount);
        Assert.DoesNotContain(page.Items, t => t.Amount == 999m);
    }

    [SkippableFact]
    public async Task TheCsvKeepsTheLeadingZeroAndCannotRunFormulas()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        await RecordAsync(tenant, TransactionType.CashOut, 50m, "0244123456", Today(11, 50));

        await using var db = _postgres.CreateContext();
        var file = await new StatementService(db).RenderAsync(
            new StatementRequest(tenant.OrganizationId, StatementPeriod.Today), StatementFormat.Csv);

        // A byte-order mark, or Excel reads the file as the wrong encoding.
        Assert.Equal(Encoding.UTF8.GetPreamble(), file.Content[..3]);

        var text = Encoding.UTF8.GetString(file.Content[3..]);
        // Grouped, so Excel keeps it as text rather than dropping the leading zero.
        Assert.Contains("024 412 3456", text, StringComparison.Ordinal);
        Assert.DoesNotContain(",244123456,", text, StringComparison.Ordinal);
        Assert.Contains("Withdrawal (cash out)", text, StringComparison.Ordinal);
        Assert.EndsWith(".csv", file.FileName, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ThePdfIsAWellFormedDocumentWithEveryPageNumbered()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        // Enough to run onto a second page.
        for (var i = 0; i < 70; i++)
        {
            await RecordAsync(tenant, TransactionType.CashIn, 10m + i, $"02440000{i:D2}", Today(7, 0).AddMinutes(i));
        }

        await using var db = _postgres.CreateContext();
        var file = await new StatementService(db).RenderAsync(
            new StatementRequest(tenant.OrganizationId, StatementPeriod.Today), StatementFormat.Pdf);

        var pdf = Encoding.Latin1.GetString(file.Content);
        Assert.StartsWith("%PDF-1.4", pdf, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", pdf, StringComparison.Ordinal);

        // The cross-reference table is where a reader starts; if its offset is wrong the file
        // opens as damaged or not at all.
        var startxref = long.Parse(pdf[(pdf.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.Equal("xref", pdf.Substring((int)startxref, 4));

        Assert.Contains("/Count 2", pdf, StringComparison.Ordinal);
        Assert.Contains("(Page 2 of 2)", pdf, StringComparison.Ordinal);
        Assert.Contains("(024 400 0042)", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void AWeekStartsOnMondayAndACustomRangeIsChecked()
    {
        // Thursday 18 Sep 2025.
        var thursday = new DateTimeOffset(2025, 9, 18, 15, 0, 0, TimeSpan.Zero);

        var (from, to, _) = StatementPeriods.Resolve(StatementPeriod.ThisWeek, null, null, thursday);
        Assert.Equal(new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2025, 9, 19, 0, 0, 0, TimeSpan.Zero), to);

        Assert.Throws<ArgumentException>(() => StatementPeriods.Resolve(
            StatementPeriod.Custom, new DateOnly(2025, 9, 10), new DateOnly(2025, 9, 1), thursday));
        Assert.Throws<ArgumentException>(() => StatementPeriods.Resolve(
            StatementPeriod.Custom, null, null, thursday));
    }

    [SkippableFact]
    public async Task AnAgentAskingForAColleaguesStatementGetsTheirOwn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var colleague = Guid.NewGuid();

        await RecordAsync(tenant, TransactionType.CashIn, 100m, "0244000001", Today(8, 0));
        await RecordAsync(tenant, TransactionType.CashIn, 900m, "0244000002", Today(8, 1), agentId: colleague);

        var agent = _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

        var response = await agent.GetAsync($"/api/v1/statements?period=Today&format=Csv&agentId={colleague}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync();
        // The query named a colleague; the statement is still the caller's own.
        Assert.Contains("024 400 0001", text, StringComparison.Ordinal);
        Assert.DoesNotContain("024 400 0002", text, StringComparison.Ordinal);
    }

    // ─── Records that cannot be rewritten ────────────────────────────────────

    [SkippableFact]
    public async Task ARecordedTransactionCannotBeEditedOrDeleted()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var id = await RecordAsync(tenant, TransactionType.CashOut, 50m, "0244123456", Today(11, 50));

        await using var db = _postgres.CreateContext();

        // The amount a customer disputes, the number that made it, the time it happened:
        // changed by anyone, through anything, the database refuses.
        foreach (var sql in new[]
        {
            $"UPDATE \"Transactions\" SET \"Amount\" = 5 WHERE \"Id\" = '{id}'",
            $"UPDATE \"Transactions\" SET \"CustomerPhoneNumber\" = '0209999999' WHERE \"Id\" = '{id}'",
            $"UPDATE \"Transactions\" SET \"TransactionAtUtc\" = now() WHERE \"Id\" = '{id}'",
            $"DELETE FROM \"Transactions\" WHERE \"Id\" = '{id}'"
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(PostgresErrorCodes.RestrictViolation, refused.SqlState);
        }

        var unchanged = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Equal(50m, unchanged.Amount);
        Assert.Equal("0244123456", unchanged.CustomerPhoneNumber);
    }

    [SkippableFact]
    public async Task TheLifecycleCanStillMove()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var id = await RecordAsync(tenant, TransactionType.CashOut, 50m, "0244123456", Today(11, 50));

        await using var db = _postgres.CreateContext();

        // Only the facts are frozen; bookkeeping columns move as they always have.
        await db.Database.ExecuteSqlRawAsync(
            $"UPDATE \"Transactions\" SET \"UpdatedAt\" = now() WHERE \"Id\" = '{id}'");
    }
}
