using Microsoft.EntityFrameworkCore;
using Zazi.Application.Float;
using Zazi.Domain;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Float;
using Zazi.Infrastructure.Services;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Recording what an owner hands an agent, and reading back what they hold.
/// </summary>
/// <remarks>
/// Allocations are ordinary Adjustment transactions rather than rows in a table of their own,
/// so these assert the thing that actually matters: that the money lands in the same balances
/// every other movement uses, against the right person.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class FloatServiceTests
{
    private readonly PostgresFixture _postgres;

    public FloatServiceTests(PostgresFixture postgres) => _postgres = postgres;

    private sealed record Seed(Guid OrganizationId, Guid BranchId, Guid AgentId, Guid OwnerId);

    // Names are made unique per seed: a fixed name collided with another suite that looks
    // its worker up by name with Single(), and the two only failed when run together.
    private static async Task<Seed> SeedAsync(ApplicationDbContext db, string agentName = "Agent")
    {
        var organization = new Organization { Name = $"Float {Guid.NewGuid():N}" };
        var branch = new Branch { OrganizationId = organization.Id, Name = "Main branch" };
        var owner = new User { OrganizationId = organization.Id, FullName = "Owner", Email = $"o{Guid.NewGuid():N}@example.com" };
        var agent = new User
        {
            OrganizationId = organization.Id,
            BranchId = branch.Id,
            FullName = $"{agentName} {Guid.NewGuid():N}",
            CredentialType = UserCredentialType.ActivationOnly
        };
        db.Organizations.Add(organization);
        db.Branches.Add(branch);
        db.Users.AddRange(owner, agent);
        await db.SaveChangesAsync();
        return new Seed(organization.Id, branch.Id, agent.Id, owner.Id);
    }

    private static FloatService ServiceFor(ApplicationDbContext db) =>
        new(db, new TransactionService(db, new LedgerService(db)));

    [SkippableFact]
    public async Task CashAndFloatGivenToAnAgentShowUpAsTheirHoldings()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        await using var db = _postgres.CreateContext();
        var seed = await SeedAsync(db);
        var service = ServiceFor(db);

        await service.RecordFloatAsync(
            new RecordFloatRequest(seed.AgentId, "MTN", CashAmount: 2_000m, FloatAmount: 1_500m),
            seed.OrganizationId, seed.OwnerId);

        var holdings = await service.GetHoldingsAsync(seed.OrganizationId);
        var agent = holdings.Single(h => h.AgentId == seed.AgentId);

        Assert.Equal(2_000m, agent.Cash);
        Assert.Equal(1_500m, Assert.Single(agent.Floats).Amount);
        Assert.Equal("MTN", agent.Floats[0].Network);
    }

    [SkippableFact]
    public async Task MoneyTakenBackIsRecordedAsANegativeAndReducesTheBalance()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        await using var db = _postgres.CreateContext();
        var seed = await SeedAsync(db);
        var service = ServiceFor(db);

        await service.RecordFloatAsync(
            new RecordFloatRequest(seed.AgentId, "MTN", 1_000m, 0m), seed.OrganizationId, seed.OwnerId);
        // End of shift: the owner takes some of the cash back.
        await service.RecordFloatAsync(
            new RecordFloatRequest(seed.AgentId, "MTN", -400m, 0m), seed.OrganizationId, seed.OwnerId);

        var agent = (await service.GetHoldingsAsync(seed.OrganizationId)).Single(h => h.AgentId == seed.AgentId);
        Assert.Equal(600m, agent.Cash);
    }

    [SkippableFact]
    public async Task FloatOnDifferentNetworksIsKeptApart()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        await using var db = _postgres.CreateContext();
        var seed = await SeedAsync(db);
        var service = ServiceFor(db);

        await service.RecordFloatAsync(
            new RecordFloatRequest(seed.AgentId, "MTN", 0m, 900m), seed.OrganizationId, seed.OwnerId);
        await service.RecordFloatAsync(
            new RecordFloatRequest(seed.AgentId, "TELECEL", 0m, 250m), seed.OrganizationId, seed.OwnerId);

        var agent = (await service.GetHoldingsAsync(seed.OrganizationId)).Single(h => h.AgentId == seed.AgentId);

        Assert.Equal(2, agent.Floats.Count);
        Assert.Equal(900m, agent.Floats.Single(f => f.Network == "MTN").Amount);
        Assert.Equal(250m, agent.Floats.Single(f => f.Network == "TELECEL").Amount);
    }

    [SkippableFact]
    public async Task TwoAgentsHoldingsDoNotMix()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        await using var db = _postgres.CreateContext();
        var seed = await SeedAsync(db, "Kofi");

        var ama = new User
        {
            OrganizationId = seed.OrganizationId,
            BranchId = seed.BranchId,
            FullName = $"Ama {Guid.NewGuid():N}",
            CredentialType = UserCredentialType.ActivationOnly
        };
        db.Users.Add(ama);
        await db.SaveChangesAsync();

        var service = ServiceFor(db);
        await service.RecordFloatAsync(new RecordFloatRequest(seed.AgentId, "MTN", 2_000m, 0m), seed.OrganizationId, seed.OwnerId);
        await service.RecordFloatAsync(new RecordFloatRequest(ama.Id, "MTN", 500m, 0m), seed.OrganizationId, seed.OwnerId);

        var holdings = await service.GetHoldingsAsync(seed.OrganizationId);

        // The whole point of tracking per agent: one counter, two people, two figures.
        Assert.Equal(2_000m, holdings.Single(h => h.AgentId == seed.AgentId).Cash);
        Assert.Equal(500m, holdings.Single(h => h.AgentId == ama.Id).Cash);
    }

    [SkippableFact]
    public async Task EveryAllocationAppearsInTheAgentsMovements()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        await using var db = _postgres.CreateContext();
        var seed = await SeedAsync(db);
        var service = ServiceFor(db);

        await service.RecordFloatAsync(
            new RecordFloatRequest(seed.AgentId, "MTN", 1_200m, 800m, "Morning shift"),
            seed.OrganizationId, seed.OwnerId);

        var movements = await service.GetMovementsAsync(seed.OrganizationId, seed.AgentId);
        var entry = Assert.Single(movements);

        // The owner's own note is the label, because "Adjustment" tells them nothing.
        Assert.Equal("Morning shift", entry.Description);
        Assert.Equal(1_200m, entry.CashDelta);
        Assert.Equal(800m, entry.FloatDelta);
    }

    [SkippableFact]
    public async Task RecordingNothingIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        await using var db = _postgres.CreateContext();
        var seed = await SeedAsync(db);
        var service = ServiceFor(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordFloatAsync(
            new RecordFloatRequest(seed.AgentId, "MTN", 0m, 0m), seed.OrganizationId, seed.OwnerId));
    }

    [SkippableFact]
    public async Task MoneyCannotBeRecordedAgainstSomeoneInAnotherBusiness()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        await using var db = _postgres.CreateContext();
        var mine = await SeedAsync(db);
        var theirs = await SeedAsync(db);
        var service = ServiceFor(db);

        // Tenant isolation at the point it matters most: moving money into someone else's books.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RecordFloatAsync(
            new RecordFloatRequest(theirs.AgentId, "MTN", 100m, 0m), mine.OrganizationId, mine.OwnerId));
    }
}
