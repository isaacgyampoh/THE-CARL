using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Domain;
using Zazi.Infrastructure.Services;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// That money is stored at one precision, everywhere.
/// </summary>
/// <remarks>
/// <para>
/// The schema comment claims "money is numeric(18,4) everywhere", and it was not: opening
/// balances were unconstrained <c>numeric</c> while the current balances derived from them
/// were <c>numeric(18,4)</c>. Both are written from the same request value, so a client
/// sending more than four decimal places made a session's opening cash and its own cash
/// balance disagree the instant it was created.
/// </para>
/// <para>
/// In a reconciliation product that is not a rounding curiosity. An agent whose till is
/// flagged short by a fraction of a pesewa they cannot see has no way to resolve it.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class MoneyPrecisionTests
{
    private readonly PostgresFixture _postgres;

    public MoneyPrecisionTests(PostgresFixture postgres) => _postgres = postgres;

    [SkippableFact]
    public async Task AnOpeningBalanceIsStoredAtCurrencyScale()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        // More precision than a currency has. A client can send this today.
        const decimal overPrecise = 100.00005m;

        await using var db = _postgres.CreateContext();
        var service = new SessionService(db);

        var opened = await service.OpenSessionAsync(new CreateSessionRequest(
            tenant.OrganizationId, tenant.BranchId, tenant.UserId, tenant.DeviceId,
            OpeningCash: overPrecise, OpeningFloat: overPrecise));

        await using var check = _postgres.CreateContext();
        var session = await check.Sessions.AsNoTracking().SingleAsync(x => x.Id == opened.Id);

        // Stored at currency scale rather than exactly as sent.
        Assert.Equal(LedgerPolicy.RoundToCurrency(overPrecise), session.OpeningCash);
        Assert.Equal(LedgerPolicy.RoundToCurrency(overPrecise), session.OpeningFloat);
    }

    [SkippableFact]
    public async Task ASessionAndItsBalancesAgreeOnTheOpeningFigure()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        const decimal overPrecise = 250.00007m;

        await using var db = _postgres.CreateContext();
        var opened = await new SessionService(db).OpenSessionAsync(new CreateSessionRequest(
            tenant.OrganizationId, tenant.BranchId, tenant.UserId, tenant.DeviceId,
            OpeningCash: overPrecise, OpeningFloat: overPrecise));

        await using var check = _postgres.CreateContext();
        var session = await check.Sessions.AsNoTracking().SingleAsync(x => x.Id == opened.Id);
        var cash = await check.CashBalances.AsNoTracking()
            .SingleAsync(x => x.BranchId == tenant.BranchId);
        var eMoney = await check.FloatBalances.AsNoTracking()
            .SingleAsync(x => x.BranchId == tenant.BranchId);

        // The defect this catches: the session kept the exact value and the balance rounded
        // it, so the two disagreed about the same number from the moment they were written.
        Assert.Equal(session.OpeningCash, cash.OpeningCash);
        Assert.Equal(session.OpeningCash, cash.CurrentCash);
        Assert.Equal(session.OpeningFloat, eMoney.OpeningFloat);
        Assert.Equal(session.OpeningFloat, eMoney.CurrentFloat);
    }

    [SkippableFact]
    public void EveryMoneyColumnDeclaresTheSamePrecision()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        using var db = _postgres.CreateContext();

        // Walks the model rather than naming columns, so a money column added later is
        // covered without anyone remembering to extend this list.
        var unconstrained = db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?))
                .Where(p => !p.Name.Contains("Confidence", StringComparison.Ordinal))
                .Where(p => p.GetColumnType() is null or "numeric")
                .Select(p => $"{entity.ShortName()}.{p.Name}"))
            .ToList();

        Assert.True(
            unconstrained.Count == 0,
            "Money columns without an explicit precision: " + string.Join(", ", unconstrained));
    }
}
