using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zazi.Application.Float;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// The owner gives an agent cash and float; the agent trades; both sides see the same figures.
/// </summary>
/// <remarks>
/// This is the question the product exists to answer — "how much has my agent got left?" —
/// so it is driven end to end: allocations through the float service, trading through the
/// sync API as the agent's handset does it, and the balances read back from both the owner's
/// ledger view and the agent's own endpoint.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class AgentFloatTests : IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public AgentFloatTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    private async Task<T> WithServiceAsync<T>(Func<IFloatService, Task<T>> act)
    {
        using var scope = _factory!.Services.CreateScope();
        return await act(scope.ServiceProvider.GetRequiredService<IFloatService>());
    }

    private async Task GiveAsync(TenantSeed tenant, decimal cash, decimal efloat, string network = "MTN", string? token = null)
    {
        using var scope = _factory!.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFloatService>().RecordFloatAsync(
            new RecordFloatRequest(tenant.UserId, network, cash, efloat, "Morning float", token),
            tenant.OrganizationId,
            tenant.UserId);
    }

    private HttpClient AgentApp(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

    private static object Trade(TenantSeed tenant, TransactionType type, decimal amount) => new
    {
        clientTransactionId = ClientTransactionId.Create($"device-{Guid.NewGuid():N}"),
        transactionType = type,
        amount,
        provider = "MTN",
        transactionTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
        deviceReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        branchId = tenant.BranchId,
        sourceType = EvidenceSourceType.ManualEntry,
        parserVersion = "manual-v1",
        customerPhone = "0244123456",
        transactionReference = Guid.NewGuid().ToString("N")[..12]
    };

    // ─── The whole point ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task WhatTheOwnerGivesAndWhatTheAgentTradesAgreeOnBothSides()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await GiveAsync(tenant, cash: 1000m, efloat: 2500m);

        var afterGiving = await WithServiceAsync(s => s.GetAgentLedgerAsync(tenant.OrganizationId, tenant.UserId))!;
        Assert.NotNull(afterGiving);
        Assert.Equal(1000m, afterGiving!.Cash);
        Assert.Equal(2500m, afterGiving.TotalFloat);
        Assert.Equal(3500m, afterGiving.Total);
        Assert.Equal(1000m, afterGiving.Today.CashAllocated);
        Assert.Equal(2500m, afterGiving.Today.FloatAllocated);
        // Nothing was held before today, so the day opened at nothing.
        Assert.Equal(0m, afterGiving.OpeningCash);
        Assert.Equal(0m, afterGiving.OpeningFloat);

        // A cash out: the agent hands over notes and receives e-money.
        using var app = AgentApp(tenant);
        (await app.PostAsJsonAsync("/api/v1/sync/transactions",
            new { transactions = new[] { Trade(tenant, TransactionType.CashOut, 300m) } })).EnsureSuccessStatusCode();

        var afterCashOut = await WithServiceAsync(s => s.GetAgentLedgerAsync(tenant.OrganizationId, tenant.UserId))!;
        Assert.Equal(700m, afterCashOut!.Cash);
        Assert.Equal(2800m, afterCashOut.TotalFloat);
        Assert.Equal(300m, afterCashOut.Today.CashOut);

        // A cash in: notes come back, e-money goes out.
        (await app.PostAsJsonAsync("/api/v1/sync/transactions",
            new { transactions = new[] { Trade(tenant, TransactionType.CashIn, 150m) } })).EnsureSuccessStatusCode();

        var afterCashIn = await WithServiceAsync(s => s.GetAgentLedgerAsync(tenant.OrganizationId, tenant.UserId))!;
        Assert.Equal(850m, afterCashIn!.Cash);
        Assert.Equal(2650m, afterCashIn.TotalFloat);
        Assert.Equal(150m, afterCashIn.Today.CashIn);

        // The agent's own phone sees the same holdings.
        var mine = await app.GetFromJsonAsync<JsonElement>("/api/v1/balances/mine");
        Assert.Equal(850m, mine.GetProperty("cash").GetDecimal());
        Assert.Equal(2650m, mine.GetProperty("totalFloat").GetDecimal());
        Assert.Equal(3500m, mine.GetProperty("total").GetDecimal());
        Assert.Equal(1000m, mine.GetProperty("cashGivenToday").GetDecimal());

        // And the history explains how it got there, newest first, with the running balance.
        var newest = afterCashIn.History[0];
        Assert.Equal(850m, newest.RunningCash);
        Assert.Equal(2650m, newest.RunningFloat);
        var allocation = Assert.Single(afterCashIn.History.Where(h => h.IsAllocation));
        Assert.Equal(1000m, allocation.CashDelta);
        Assert.Equal(2500m, allocation.FloatDelta);
    }

    [SkippableFact]
    public async Task PressingRecordTwiceRecordsOnce()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var token = Guid.NewGuid().ToString("N");

        await GiveAsync(tenant, 500m, 750m, token: token);
        await GiveAsync(tenant, 500m, 750m, token: token);

        var ledger = await WithServiceAsync(s => s.GetAgentLedgerAsync(tenant.OrganizationId, tenant.UserId))!;
        Assert.Equal(500m, ledger!.Cash);
        Assert.Equal(750m, ledger.TotalFloat);
        Assert.Single(ledger.History);

        // A genuinely separate allocation still goes through.
        await GiveAsync(tenant, 500m, 750m, token: Guid.NewGuid().ToString("N"));
        var after = await WithServiceAsync(s => s.GetAgentLedgerAsync(tenant.OrganizationId, tenant.UserId))!;
        Assert.Equal(1000m, after!.Cash);
        Assert.Equal(2, after.History.Count);
    }

    [SkippableFact]
    public async Task MoneyTakenBackAtTheEndOfAShiftLowersTheBalance()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await GiveAsync(tenant, 1000m, 0m);
        await GiveAsync(tenant, -400m, 0m);

        var ledger = await WithServiceAsync(s => s.GetAgentLedgerAsync(tenant.OrganizationId, tenant.UserId))!;
        Assert.Equal(600m, ledger!.Cash);
        Assert.Equal(1000m, ledger.Today.CashAllocated);
        Assert.Equal(-400m, ledger.Today.CashAdjusted);
    }

    [SkippableFact]
    public async Task AnAgentSeesOnlyTheirOwnMoney()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var mine = await TenantSeedFactory.CreateAsync(_postgres);
        var theirs = await TenantSeedFactory.CreateAsync(_postgres);

        await GiveAsync(mine, 1000m, 2000m);
        await GiveAsync(theirs, 9000m, 9000m);

        // Another business's agent is not in this business's ledger at all.
        var acrossBusinesses = await WithServiceAsync(s => s.GetAgentLedgerAsync(mine.OrganizationId, theirs.UserId));
        Assert.Null(acrossBusinesses);

        // And the handset endpoint answers for the caller, never for whoever is asked about.
        using var app = AgentApp(mine);
        var balances = await app.GetFromJsonAsync<JsonElement>("/api/v1/balances/mine");
        Assert.Equal(1000m, balances.GetProperty("cash").GetDecimal());
    }

    [SkippableFact]
    public async Task GivingToSomeoneOutsideTheBusinessIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var mine = await TenantSeedFactory.CreateAsync(_postgres);
        var theirs = await TenantSeedFactory.CreateAsync(_postgres);

        using var scope = _factory!.Services.CreateScope();
        var floats = scope.ServiceProvider.GetRequiredService<IFloatService>();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => floats.RecordFloatAsync(
            new RecordFloatRequest(theirs.UserId, "MTN", 100m, 0m),
            mine.OrganizationId,
            mine.UserId));

        await using var db = _postgres.CreateContext();
        Assert.False(await db.Transactions.AnyAsync(t => t.AgentId == theirs.UserId && t.Type == TransactionType.Adjustment));
    }

    [SkippableFact]
    public async Task NothingIsRecordedWhenBothAmountsAreZero()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        using var scope = _factory!.Services.CreateScope();
        var floats = scope.ServiceProvider.GetRequiredService<IFloatService>();

        await Assert.ThrowsAsync<ArgumentException>(() => floats.RecordFloatAsync(
            new RecordFloatRequest(tenant.UserId, "MTN", 0m, 0m), tenant.OrganizationId, tenant.UserId));

        var ledger = await WithServiceAsync(s => s.GetAgentLedgerAsync(tenant.OrganizationId, tenant.UserId))!;
        Assert.Empty(ledger!.History);
    }

    [SkippableFact]
    public async Task PesewasSurviveALongRunOfMovements()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        // Amounts that a floating-point balance would quietly get wrong.
        await GiveAsync(tenant, 100.10m, 0.35m);
        for (var i = 0; i < 9; i++)
        {
            await GiveAsync(tenant, 0.10m, 0.05m, token: Guid.NewGuid().ToString("N"));
        }

        var ledger = await WithServiceAsync(s => s.GetAgentLedgerAsync(tenant.OrganizationId, tenant.UserId))!;
        Assert.Equal(101.00m, ledger!.Cash);
        Assert.Equal(0.80m, ledger.TotalFloat);
    }

    [SkippableFact]
    public async Task ResubmittingAnAllocationLeavesOneLineInTheAuditTrail()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        // The same form submitted twice: a double tap, a back button, a retry on a slow
        // connection. The ledger already kept the money right; the audit entry did not, and
        // left two lines each saying the float had been handed over.
        const string token = "one-and-the-same-submission";
        await GiveAsync(tenant, cash: 500m, efloat: 1200m, token: token);
        await GiveAsync(tenant, cash: 500m, efloat: 1200m, token: token);

        await using var db = _postgres.CreateContext();

        var recorded = await db.AuditLogs
            .CountAsync(a => a.OrganizationId == tenant.OrganizationId && a.Action == "FLOAT_RECORDED");

        // An audit trail is what somebody reads when money is disputed. Two lines saying the
        // float was given is how an owner concludes their agent was given it twice.
        Assert.Equal(1, recorded);

        // And the money itself is still counted once.
        var holdings = await WithServiceAsync(s => s.GetHoldingsAsync(tenant.OrganizationId));
        var agent = Assert.Single(holdings, h => h.AgentId == tenant.UserId);
        Assert.Equal(500m, agent.Cash);
        Assert.Equal(1200m, agent.Floats.Sum(f => f.Amount));
    }
}
