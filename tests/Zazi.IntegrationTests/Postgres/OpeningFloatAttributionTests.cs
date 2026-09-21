using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Infrastructure.Services;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// An opening float belongs to a network, and the system refuses to guess which.
/// </summary>
/// <remarks>
/// <para>
/// Float balances are held per network, because an agent working MTN, Telecel and AirtelTigo
/// carries three separate wallets. An opening figure with no network therefore has to land
/// somewhere, and until now it landed on MTN — silently, and in the HTTP path unavoidably,
/// because the request model had no field to say otherwise. A Telecel agent's opening float
/// was added to an MTN balance their own transactions never touched.
/// </para>
/// <para>
/// UNKNOWN as a default was tried and is worse: no transaction posts to it, so the money
/// leaves the books altogether rather than sitting under the wrong heading. Since no default is
/// safe, the only correct behaviour is to refuse — which is what these assert.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class OpeningFloatAttributionTests : IDisposable
{
    private const string OpenPath = "/api/v1/sessions/open";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public OpeningFloatAttributionTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    private HttpClient ClientFor(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

    [SkippableTheory]
    [InlineData("MTN")]
    [InlineData("Telecel")]
    [InlineData("AirtelTigo")]
    public async Task AnOpeningFloatLandsOnTheNetworkItWasDeclaredOn(string network)
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await using var db = _postgres.CreateContext();
        await new SessionService(db).OpenSessionAsync(new CreateSessionRequest(
            tenant.OrganizationId, tenant.BranchId, tenant.UserId, tenant.DeviceId,
            OpeningCash: 500m, OpeningFloat: 1_200m, Network: network));

        await using var check = _postgres.CreateContext();
        var balances = await check.FloatBalances.AsNoTracking()
            .Where(x => x.BranchId == tenant.BranchId)
            .ToListAsync();

        // Exactly one row: the declared network, and nothing on any other. The bug this fails
        // on for Telecel and AirtelTigo produced a row named MTN holding their money.
        var only = Assert.Single(balances);
        Assert.Equal(network.ToUpperInvariant(), only.Network);
        Assert.Equal(1_200m, only.OpeningFloat);
        Assert.Equal(1_200m, only.CurrentFloat);
    }

    [SkippableFact]
    public async Task AnOpeningFloatWithNoNetworkIsRefusedRatherThanAssignedToOne()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await using var db = _postgres.CreateContext();
        var service = new SessionService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.OpenSessionAsync(
            new CreateSessionRequest(
                tenant.OrganizationId, tenant.BranchId, tenant.UserId, tenant.DeviceId,
                OpeningCash: 500m, OpeningFloat: 1_200m)));

        // And nothing was written on the way to refusing. A session recorded without the
        // balance it implies is a worse state than no session at all.
        await using var check = _postgres.CreateContext();
        Assert.Empty(await check.Sessions.AsNoTracking()
            .Where(x => x.BranchId == tenant.BranchId).ToListAsync());
        Assert.Empty(await check.FloatBalances.AsNoTracking()
            .Where(x => x.BranchId == tenant.BranchId).ToListAsync());
    }

    [SkippableFact]
    public async Task NoOpeningFloatNeedsNoNetworkAndCreatesNoFloatRow()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await using var db = _postgres.CreateContext();
        await new SessionService(db).OpenSessionAsync(new CreateSessionRequest(
            tenant.OrganizationId, tenant.BranchId, tenant.UserId, tenant.DeviceId,
            OpeningCash: 300m, OpeningFloat: 0m));

        await using var check = _postgres.CreateContext();

        // An agent starting the day with an empty wallet is the ordinary case, and it must not
        // be made to name a network it holds nothing on. The ledger creates a float balance per
        // network on demand, so the absent row costs nothing; pre-creating one here is what
        // made MTN look like every agent's default.
        Assert.Empty(await check.FloatBalances.AsNoTracking()
            .Where(x => x.BranchId == tenant.BranchId).ToListAsync());

        // The cash side is unaffected: it has no network, so it is written either way.
        var cash = await check.CashBalances.AsNoTracking()
            .SingleAsync(x => x.BranchId == tenant.BranchId);
        Assert.Equal(300m, cash.OpeningCash);
    }

    [SkippableFact]
    public async Task TheNetworkIsStoredInOneCaseHoweverItIsSpelled()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await using var db = _postgres.CreateContext();
        await new SessionService(db).OpenSessionAsync(new CreateSessionRequest(
            tenant.OrganizationId, tenant.BranchId, tenant.UserId, tenant.DeviceId,
            OpeningCash: 0m, OpeningFloat: 400m, Network: "  telecel  "));

        await using var check = _postgres.CreateContext();
        var only = Assert.Single(await check.FloatBalances.AsNoTracking()
            .Where(x => x.BranchId == tenant.BranchId).ToListAsync());

        // Balances are matched by network name, so "Telecel" and "TELECEL" must not become two
        // wallets holding half an agent's money each.
        Assert.Equal("TELECEL", only.Network);
    }

    [SkippableFact]
    public async Task TheHttpApiCanSayWhichNetworkTheOpeningFloatIsOn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var client = ClientFor(tenant);

        var response = await client.PostAsJsonAsync(OpenPath, new
        {
            branchId = tenant.BranchId,
            openingCash = 250m,
            openingFloat = 900m,
            network = "AirtelTigo"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var check = _postgres.CreateContext();
        var balance = await check.FloatBalances.AsNoTracking()
            .SingleAsync(x => x.BranchId == tenant.BranchId);

        // The defect this covers is not the fallback but the missing field: OpenSessionApiRequest
        // had nowhere to put a network, so every session opened over HTTP went to MTN no matter
        // what the agent was holding.
        Assert.Equal("AIRTELTIGO", balance.Network);
    }

    [SkippableFact]
    public async Task TheHttpApiRefusesAnOpeningFloatWithNoNetworkAndSaysWhy()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var client = ClientFor(tenant);

        var response = await client.PostAsJsonAsync(OpenPath, new
        {
            branchId = tenant.BranchId,
            openingCash = 250m,
            openingFloat = 900m
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Checked in the controller rather than only in the service so the response names the
        // field. The exception middleware returns titles rather than messages, so a throw from
        // the service would reach the handset as "The request is not valid." and the agent
        // would have no idea which part of it.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("etwork", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheHttpApiStillAcceptsASessionOpenedWithNothingInTheWallet()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var client = ClientFor(tenant);

        var response = await client.PostAsJsonAsync(OpenPath, new
        {
            branchId = tenant.BranchId,
            openingCash = 0m,
            openingFloat = 0m
        });

        // Requiring a network for a zero float would have broken every caller that opens a
        // session before the day's float arrives, which is the common one.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
