using Microsoft.EntityFrameworkCore;
using Zazi.Domain;
using Zazi.Infrastructure.Services;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// A branch total is a sum across its agents, not a single row.
/// </summary>
/// <remarks>
/// Cash and float moved to one row per agent — and per network on the float side — when float
/// stopped being a branch-level figure. The branch snapshot did not move with them: it still
/// fetched one row with Single…Async, which throws the moment a branch has a second agent.
/// Nothing calls it yet, which is the only reason that has not been seen in production.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class BranchLedgerSnapshotTests
{
    private readonly PostgresFixture _postgres;

    public BranchLedgerSnapshotTests(PostgresFixture postgres) => _postgres = postgres;

    [SkippableFact]
    public async Task ABranchWithSeveralAgentsReportsTheirCombinedPosition()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        await using (var seed = _postgres.CreateContext())
        {
            // Three agents at one branch, each holding their own till and their own MTN float.
            foreach (var (agent, cash, eMoney, threshold) in new[]
            {
                (tenant.UserId, 400m, 1_000m, 200m),
                (second, 250m, 750m, 500m),
                (third, 100m, 250m, 150m)
            })
            {
                seed.CashBalances.Add(new CashBalance
                {
                    OrganizationId = tenant.OrganizationId,
                    BranchId = tenant.BranchId,
                    AgentId = agent,
                    OpeningCash = cash,
                    CurrentCash = cash
                });
                seed.FloatBalances.Add(new FloatBalance
                {
                    OrganizationId = tenant.OrganizationId,
                    BranchId = tenant.BranchId,
                    AgentId = agent,
                    Network = Networks.Mtn,
                    OpeningFloat = eMoney,
                    CurrentFloat = eMoney,
                    Threshold = threshold
                });
            }

            await seed.SaveChangesAsync();
        }

        await using var db = _postgres.CreateContext();

        // Before the fix this threw "Sequence contains more than one element" rather than
        // returning a wrong number, so the failure is a crash on the first multi-agent branch.
        var snapshot = await new LedgerService(db)
            .GetBranchLedgerAsync(tenant.OrganizationId, tenant.BranchId, Networks.Mtn);

        Assert.Equal(750m, snapshot.CashOnHand);
        Assert.Equal(2_000m, snapshot.NetworkFloat);
        Assert.Equal(Networks.Mtn, snapshot.Network);

        // The largest threshold, not the sum: the branch is short when its most exposed agent
        // is short, and 850 would describe no one at this branch.
        Assert.Equal(500m, snapshot.Threshold);
    }

    [SkippableFact]
    public async Task OnlyTheRequestedNetworksFloatIsCounted()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await using (var seed = _postgres.CreateContext())
        {
            // One agent, one handset per network — the ordinary Ghanaian setup.
            foreach (var (net, amount) in new[]
            {
                (Networks.Mtn, 1_000m),
                (Networks.Telecel, 600m),
                (Networks.AirtelTigo, 300m)
            })
            {
                seed.FloatBalances.Add(new FloatBalance
                {
                    OrganizationId = tenant.OrganizationId,
                    BranchId = tenant.BranchId,
                    AgentId = tenant.UserId,
                    Network = net,
                    OpeningFloat = amount,
                    CurrentFloat = amount
                });
            }

            await seed.SaveChangesAsync();
        }

        await using var db = _postgres.CreateContext();
        var service = new LedgerService(db);

        Assert.Equal(600m, (await service.GetBranchLedgerAsync(
            tenant.OrganizationId, tenant.BranchId, Networks.Telecel)).NetworkFloat);

        // Spelled as an agent might send it. Networks are matched by name, so a snapshot that
        // did not normalise would report this agent's Telecel float as zero.
        Assert.Equal(600m, (await service.GetBranchLedgerAsync(
            tenant.OrganizationId, tenant.BranchId, " telecel ")).NetworkFloat);

        Assert.Equal(300m, (await service.GetBranchLedgerAsync(
            tenant.OrganizationId, tenant.BranchId, Networks.AirtelTigo)).NetworkFloat);
    }

    [SkippableFact]
    public async Task ASnapshotThatNamesNoNetworkIsRefusedRatherThanAnsweredAboutMtn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await using var db = _postgres.CreateContext();
        var service = new LedgerService(db);

        // It used to answer, labelling the result MTN and reporting zero float — which reads as
        // a branch holding nothing rather than as a question that was never asked properly.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetBranchLedgerAsync(tenant.OrganizationId, tenant.BranchId, ""));
    }

    [SkippableFact]
    public async Task ABranchWithNoBalancesYetReportsZeroRatherThanFailing()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await using var db = _postgres.CreateContext();
        var snapshot = await new LedgerService(db)
            .GetBranchLedgerAsync(tenant.OrganizationId, tenant.BranchId, Networks.Mtn);

        Assert.Equal(0m, snapshot.CashOnHand);
        Assert.Equal(0m, snapshot.NetworkFloat);
        Assert.Equal(0m, snapshot.Threshold);
    }
}
