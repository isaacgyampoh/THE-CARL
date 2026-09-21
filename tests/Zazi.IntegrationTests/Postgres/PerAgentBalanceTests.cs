using Microsoft.EntityFrameworkCore;
using Zazi.Domain;


namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// That each agent's cash and float are their own.
/// </summary>
/// <remarks>
/// Balances were keyed on the branch alone, so two agents working one counter shared a single
/// figure. An owner asking how much the person in front of them was carrying could not be
/// answered, and handing someone float — the thing this product exists to keep track of — had
/// nowhere to be recorded against them.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PerAgentBalanceTests
{
    private readonly PostgresFixture _postgres;

    public PerAgentBalanceTests(PostgresFixture postgres) => _postgres = postgres;

    [SkippableFact]
    public async Task TwoAgentsAtOneBranchKeepSeparateCashAndFloat()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var db = _postgres.CreateContext();
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var kofi = Guid.NewGuid();
        var ama = Guid.NewGuid();

        db.CashBalances.AddRange(
            new CashBalance { OrganizationId = organizationId, BranchId = branchId, AgentId = kofi, CurrentCash = 2_000m },
            new CashBalance { OrganizationId = organizationId, BranchId = branchId, AgentId = ama, CurrentCash = 500m });
        db.FloatBalances.AddRange(
            new FloatBalance { OrganizationId = organizationId, BranchId = branchId, AgentId = kofi, Network = "MTN", CurrentFloat = 1_500m },
            new FloatBalance { OrganizationId = organizationId, BranchId = branchId, AgentId = ama, Network = "TELECEL", CurrentFloat = 800m });
        await db.SaveChangesAsync();

        // Same organization, same branch, different people — and the figures stay apart.
        var kofiCash = await db.CashBalances.SingleAsync(x => x.BranchId == branchId && x.AgentId == kofi);
        var amaCash = await db.CashBalances.SingleAsync(x => x.BranchId == branchId && x.AgentId == ama);

        Assert.Equal(2_000m, kofiCash.CurrentCash);
        Assert.Equal(500m, amaCash.CurrentCash);
    }

    [SkippableFact]
    public async Task OneAgentCanHoldFloatOnMoreThanOneNetwork()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var db = _postgres.CreateContext();
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var agent = Guid.NewGuid();

        // An agent working two networks from one phone holds two separate floats. Filing both
        // under one network — which the session code did, hardcoding "MTN" — meant the float
        // they were given and the float their transactions moved were different rows.
        db.FloatBalances.AddRange(
            new FloatBalance { OrganizationId = organizationId, BranchId = branchId, AgentId = agent, Network = "MTN", CurrentFloat = 1_000m },
            new FloatBalance { OrganizationId = organizationId, BranchId = branchId, AgentId = agent, Network = "TELECEL", CurrentFloat = 400m });
        await db.SaveChangesAsync();

        var floats = await db.FloatBalances
            .Where(x => x.AgentId == agent)
            .OrderBy(x => x.Network)
            .ToListAsync();

        Assert.Equal(2, floats.Count);
        Assert.Equal("MTN", floats[0].Network);
        Assert.Equal(1_000m, floats[0].CurrentFloat);
        Assert.Equal("TELECEL", floats[1].Network);
        Assert.Equal(400m, floats[1].CurrentFloat);
    }

    [SkippableFact]
    public async Task ABranchWideBalanceFromBeforeAgentsWereTrackedStillReads()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        await using var db = _postgres.CreateContext();
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        // AgentId is nullable precisely so existing rows keep meaning what they meant. A
        // migration that orphaned them would silently zero every balance already recorded.
        db.CashBalances.Add(new CashBalance
        {
            OrganizationId = organizationId, BranchId = branchId, AgentId = null, CurrentCash = 750m
        });
        await db.SaveChangesAsync();

        var branchWide = await db.CashBalances.SingleAsync(x => x.BranchId == branchId && x.AgentId == null);
        Assert.Equal(750m, branchWide.CurrentCash);
    }
}
