using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// The one path by which a real provider message reaches whoever maintains the parser.
/// </summary>
/// <remarks>
/// <para>
/// Two parsing defects were found in a single week, both by inventing a more realistic message
/// than the corpus held. Neither was catchable by the handset's evidence-quality gate, which
/// holds messages the parser cannot read and has nothing to say about messages it reads
/// confidently and wrongly. The agent is the only party who knows.
/// </para>
/// <para>
/// These assert the properties that make the feature worth having: the body survives verbatim,
/// it is bound to the reporting tenant rather than to anything the body claims, and reporting
/// twice is not an error.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public class ParsingReportTests : IDisposable
{
    private const string Path = "/api/v1/parsing-reports";

    /// <summary>
    /// The message that caused the direction defect: a cash-out whose trailing balance
    /// reminder contains the words "cash in".
    /// </summary>
    private const string TheMessageThatCausedIt =
        "Confirmed. Cash Out of GHS 250.00 to 0241000002. "
        + "Your cash in hand is now GHS 1,750.00. Ref: MM240815003";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;

    public ParsingReportTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = postgres.IsAvailable ? new PostgresApiFactory(postgres.ConnectionString!) : null;
    }

    public void Dispose() => _factory?.Dispose();

    private HttpClient AgentFor(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

    private HttpClient OwnerFor(TenantSeed tenant) =>
        _factory!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Owner));

    [SkippableFact]
    public async Task TheMessageArrivesExactlyAsTheHandsetReceivedIt()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        var response = await AgentFor(tenant).PostAsJsonAsync(Path, new
        {
            clientTransactionId = "txn-local-0001",
            rawMessage = TheMessageThatCausedIt,
            verdict = ParsingReportVerdict.WrongDirection,
            senderIdentity = "MTN",
            observedNetwork = "MTN",
            observedType = TransactionType.CashIn,
            observedAmountMinor = 25_000L,
            note = "This was a withdrawal. The customer took the money.",
            parserVersion = "2.0.0",
            appVersion = "2.0.0"
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await using var db = _postgres.CreateContext();
        var stored = await db.ParsingReports.AsNoTracking()
            .SingleAsync(x => x.OrganizationId == tenant.OrganizationId);

        // Character for character. Normalising, trimming or re-parsing on the way in would
        // destroy the only thing this row exists to carry — and the trailing clause that
        // caused the defect sits at the very end, which is exactly what a trim takes.
        Assert.Equal(TheMessageThatCausedIt, stored.RawMessage);

        // The parser's own reading is kept beside it, because the report is a comparison.
        Assert.Equal(TransactionType.CashIn, stored.ObservedType);
        Assert.Equal(ParsingReportVerdict.WrongDirection, stored.Verdict);
        Assert.Equal(25_000L, stored.ObservedAmountMinor);
        Assert.Equal("MTN", stored.ObservedNetwork);
        Assert.Equal(tenant.UserId, stored.ReportedByUserId);
        Assert.Null(stored.ReviewedAtUtc);
    }

    [SkippableFact]
    public async Task ReportingTheSameTransactionTwiceIsNotAnError()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var agent = AgentFor(tenant);

        var body = new
        {
            clientTransactionId = "txn-local-0002",
            rawMessage = TheMessageThatCausedIt,
            verdict = ParsingReportVerdict.WrongDirection
        };

        var first = await agent.PostAsJsonAsync(Path, body);
        var second = await agent.PostAsJsonAsync(Path, body);

        // A second tap on a slow connection is the likeliest cause, and telling an agent their
        // report failed when it did not is how they learn to stop reporting.
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        await using var db = _postgres.CreateContext();
        Assert.Single(await db.ParsingReports.AsNoTracking()
            .Where(x => x.OrganizationId == tenant.OrganizationId).ToListAsync());
    }

    [SkippableFact]
    public async Task AReportIsFiledAgainstTheCallersOwnOrganisation()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var mine = await TenantSeedFactory.CreateAsync(_postgres);
        var theirs = await TenantSeedFactory.CreateAsync(_postgres);

        await AgentFor(mine).PostAsJsonAsync(Path, new
        {
            clientTransactionId = "txn-local-0003",
            rawMessage = TheMessageThatCausedIt,
            verdict = ParsingReportVerdict.WrongDirection
        });

        await using var db = _postgres.CreateContext();

        // The request body has no organisation field at all, which is the security property:
        // there is nothing for a handset to put another tenant's id into.
        Assert.Single(await db.ParsingReports.AsNoTracking()
            .Where(x => x.OrganizationId == mine.OrganizationId).ToListAsync());
        Assert.Empty(await db.ParsingReports.AsNoTracking()
            .Where(x => x.OrganizationId == theirs.OrganizationId).ToListAsync());
    }

    [SkippableFact]
    public async Task OneOrganisationCannotReadAnothersReports()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var mine = await TenantSeedFactory.CreateAsync(_postgres);
        var theirs = await TenantSeedFactory.CreateAsync(_postgres);

        await AgentFor(theirs).PostAsJsonAsync(Path, new
        {
            clientTransactionId = "txn-local-0004",
            rawMessage = TheMessageThatCausedIt,
            verdict = ParsingReportVerdict.WrongAmount
        });

        var visible = await OwnerFor(mine)
            .GetFromJsonAsync<List<ParsingReportRow>>(Path);

        // These rows are customers' financial correspondence. Leaking them across tenants
        // would be worse than the parsing defect they exist to fix.
        Assert.NotNull(visible);
        Assert.Empty(visible);
    }

    [SkippableFact]
    public async Task AnAgentCannotReadTheBacklogTheyContributeTo()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        var response = await AgentFor(tenant).GetAsync(Path);

        // Submitting is an agent's job; reading every message their colleagues reported is
        // not. Reporting is EvidenceSubmit, reading is AuditRead.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task AnOwnerSeesTheBacklogOldestFirst()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var agent = AgentFor(tenant);

        foreach (var id in new[] { "txn-a", "txn-b", "txn-c" })
        {
            await agent.PostAsJsonAsync(Path, new
            {
                clientTransactionId = id,
                rawMessage = $"Confirmed. Cash Out of GHS 10.00. Ref: {id}",
                verdict = ParsingReportVerdict.WrongDirection
            });
        }

        var backlog = await OwnerFor(tenant).GetFromJsonAsync<List<ParsingReportRow>>(Path);

        Assert.NotNull(backlog);
        Assert.Equal(3, backlog.Count);

        // A report that has waited longest is the one most likely to describe a defect still
        // shipping, so it is the one to read first.
        Assert.Equal(
            backlog.Select(x => x.ReportedAtUtc).OrderBy(x => x),
            backlog.Select(x => x.ReportedAtUtc));
    }

    [SkippableFact]
    public async Task AReportWithoutTheMessageIsRefused()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        var response = await AgentFor(tenant).PostAsJsonAsync(Path, new
        {
            clientTransactionId = "txn-local-0005",
            rawMessage = "",
            verdict = ParsingReportVerdict.WrongDirection
        });

        // A report without the message is the one thing it cannot be.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task TheEndpointIsNotAnUploadChannel()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        var response = await AgentFor(tenant).PostAsJsonAsync(Path, new
        {
            clientTransactionId = "txn-local-0006",
            rawMessage = new string('x', 40_000),
            verdict = ParsingReportVerdict.WrongDirection
        });

        // Authenticated, accepts free text, stores it forever: worth a ceiling. The longest
        // real message in the fixture corpus is under 300 characters.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task AnAnonymousCallerCannotReportAnything()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var response = await _factory!.CreateClient().PostAsJsonAsync(Path, new
        {
            clientTransactionId = "txn-local-0007",
            rawMessage = TheMessageThatCausedIt,
            verdict = ParsingReportVerdict.WrongDirection
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Only the fields these tests read, so the DTO can grow without breaking them.</summary>
    private sealed record ParsingReportRow(
        Guid Id,
        string ClientTransactionId,
        string RawMessage,
        DateTimeOffset ReportedAtUtc);
}
