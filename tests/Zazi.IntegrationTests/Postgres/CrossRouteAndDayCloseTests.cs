using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zazi.Application;
using Zazi.Application.Closing;
using Zazi.Application.Keypad;
using Zazi.Application.Security;
using Zazi.Application.Sync;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// One agent, two routes into Zazi — the keypad phone and the app — and the end-of-day count
/// that has to agree with both.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CrossRouteAndDayCloseTests : IDisposable
{
    /// <summary>MTN's cash-out confirmation, as the keypad tests use it.</summary>
    private const string MtnCashOut =
        "Cash Out of GHS 250.50 to 0241000002 AMA SYNTHETIC. Ref: MP240815.1202.A00002. Your MoMo agent balance is GHS 12,089.50";

    private const string MtnReference = "MP240815.1202.A00002";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;
    private readonly WebApplicationFactory<Program>? _app;

    public CrossRouteAndDayCloseTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        if (!postgres.IsAvailable)
        {
            return;
        }

        _factory = new PostgresApiFactory(postgres.ConnectionString!);
        _app = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Sms:Provider", "Log");
            builder.UseSetting("Sms:DailySummaryEnabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISmsSender>();
                services.AddSingleton<ISmsSender, SilentSms>();
            });
        });
    }

    public void Dispose()
    {
        _app?.Dispose();
        _factory?.Dispose();
    }

    private static readonly List<(string To, string Message)> Sent = new();

    private sealed class SilentSms : ISmsSender
    {
        public Task<bool> SendAsync(string toPhoneNumber, string message, CancellationToken cancellationToken = default)
        {
            lock (Sent) Sent.Add((toPhoneNumber, message));
            return Task.FromResult(true);
        }
    }

    private static List<(string To, string Message)> SentTo(string number)
    {
        lock (Sent) return Sent.Where(s => s.To == number).ToList();
    }

    private async Task<KeypadReply> TextAsync(string from, string text)
    {
        using var scope = _app!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IKeypadSmsService>()
            .HandleAsync(new InboundSms(from, text, DateTimeOffset.UtcNow, null));
    }

    private async Task<TenantSeed> LinkedAgentAsync(string phone)
    {
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        using var scope = _app!.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentService>().IssueCodeAsync(
            new IssueEnrollmentCodeRequest(tenant.BranchId, IntendedUserId: tenant.UserId),
            tenant.OrganizationId, tenant.BranchId, tenant.UserId);
        Assert.Equal(KeypadOutcome.Linked, (await TextAsync(phone, "ZAZI " + issued.Code)).Outcome);
        return tenant;
    }

    private HttpClient AppFor(TenantSeed tenant) =>
        _app!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

    private static object SyncItem(TenantSeed tenant, string reference, decimal amount = 250.50m) => new
    {
        clientTransactionId = ClientTransactionId.Create($"device-{tenant.BranchId:N}"),
        transactionType = TransactionType.CashOut,
        amount,
        provider = "MTN",
        transactionTimestamp = DateTimeOffset.UtcNow.AddMinutes(-2),
        deviceReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        branchId = tenant.BranchId,
        sourceType = EvidenceSourceType.ManualEntry,
        parserVersion = "manual-v1",
        customerPhone = "0241000002",
        transactionReference = reference
    };

    private static async Task<SyncTransactionsResponse> SyncAsync(HttpClient client, object item)
    {
        var raw = await client.PostAsJsonAsync("/api/v1/sync/transactions", new { transactions = new[] { item } });
        raw.EnsureSuccessStatusCode();
        return (await raw.Content.ReadFromJsonAsync<SyncTransactionsResponse>())!;
    }

    private async Task<int> LedgerCountAsync(TenantSeed tenant)
    {
        await using var db = _postgres.CreateContext();
        return await db.Transactions.CountAsync(t => t.OrganizationId == tenant.OrganizationId);
    }

    // ─── One transaction, two routes ─────────────────────────────────────────

    [SkippableFact]
    public async Task AForwardedMessageThenTheAppsCopyIsCountedOnce()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await LinkedAgentAsync("0244000501");

        Assert.Equal(KeypadOutcome.Recorded, (await TextAsync("0244000501", MtnCashOut)).Outcome);
        using var app = AppFor(tenant);
        var synced = await SyncAsync(app, SyncItem(tenant, MtnReference));

        Assert.Equal(SyncItemStatus.Duplicate, synced.Results[0].Status);
        Assert.Equal(1, await LedgerCountAsync(tenant));
    }

    [SkippableFact]
    public async Task TheAppsCopyThenAForwardedMessageIsCountedOnce()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await LinkedAgentAsync("0244000502");

        using var app = AppFor(tenant);
        Assert.Equal(SyncItemStatus.Accepted, (await SyncAsync(app, SyncItem(tenant, MtnReference))).Results[0].Status);
        var forwarded = await TextAsync("0244000502", MtnCashOut);

        Assert.Equal(KeypadOutcome.AlreadyRecorded, forwarded.Outcome);
        Assert.Equal(1, await LedgerCountAsync(tenant));
    }

    [SkippableFact]
    public async Task TheSameReferenceWithADifferentAmountIsNotTreatedAsTheSameTransaction()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await LinkedAgentAsync("0244000503");

        await TextAsync("0244000503", MtnCashOut);
        using var app = AppFor(tenant);
        var synced = await SyncAsync(app, SyncItem(tenant, MtnReference, amount: 99m));

        Assert.Equal(SyncItemStatus.Accepted, synced.Results[0].Status);
        Assert.Equal(2, await LedgerCountAsync(tenant));
    }

    [SkippableFact]
    public async Task TheAppListsTheAgentsKeypadTransactions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await LinkedAgentAsync("0244000504");
        await TextAsync("0244000504", "CO 50 0244123456");

        using var app = AppFor(tenant);
        var page = await app.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/transactions/mine");

        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("0244123456", item.GetProperty("customerPhoneNumber").GetString());
        Assert.Equal((int)TransactionSource.Bridge, item.GetProperty("source").GetInt32());
    }

    // ─── Closing the day ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TheFirstCloseSetsTheBaselineAndTheNextComparesAgainstIt()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await LinkedAgentAsync("0244000511");

        var first = await TextAsync("0244000511", "CLOSE 1000 5000");
        Assert.Equal(KeypadOutcome.Closed, first.Outcome);
        Assert.Contains("starting count", first.Message, StringComparison.Ordinal);

        // A cash out: the agent hands over 50 in cash and receives 50 in float.
        await TextAsync("0244000511", "CO 50 0244123456");

        var balanced = await TextAsync("0244000511", "CLOSE 950 5050");
        Assert.Contains("All balanced", balanced.Message, StringComparison.Ordinal);
        Assert.All(balanced.Message, c => Assert.True(c < 128, $"Non-GSM character '{c}' in a reply."));

        var shortCount = await TextAsync("0244000511", "CLOSE 900 5,050");
        Assert.Contains("Cash GHS 900.00 SHORT 50.00", shortCount.Message, StringComparison.Ordinal);
        Assert.Contains("Float GHS 5,050.00 OK", shortCount.Message, StringComparison.Ordinal);

        await using var db = _postgres.CreateContext();
        Assert.Equal(3, await db.DayCloses.CountAsync(c => c.AgentId == tenant.UserId));
        Assert.True(await db.Alerts.AnyAsync(a => a.OrganizationId == tenant.OrganizationId && a.Type == "DAY_CLOSE_DIFFERENCE"));

        // The owner's view: one standing close per agent, the latest.
        using var scope = _app!.Services.CreateScope();
        var standing = await scope.ServiceProvider.GetRequiredService<IDayCloseService>()
            .ForDayAsync(tenant.OrganizationId, null, DateOnly.FromDateTime(DateTime.UtcNow));
        var mine = Assert.Single(standing);
        Assert.Equal("Short", mine.Status);
        Assert.Equal(-50m, mine.CashDifference);
    }

    [SkippableFact]
    public async Task AGarbledCloseIsExplainedNotRecorded()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await LinkedAgentAsync("0244000512");

        var reply = await TextAsync("0244000512", "CLOSE 1000");

        Assert.Equal(KeypadOutcome.NotUnderstood, reply.Outcome);
        Assert.Contains("CLOSE 1200 3500", reply.Message, StringComparison.Ordinal);
        await using var db = _postgres.CreateContext();
        Assert.False(await db.DayCloses.AnyAsync(c => c.AgentId == tenant.UserId));
    }

    [SkippableFact]
    public async Task TheAppClosesTheSignedInAgentsOwnDay()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        using var app = AppFor(tenant);

        Assert.Equal(HttpStatusCode.NoContent, (await app.GetAsync("/api/v1/day-close/latest")).StatusCode);

        var posted = await app.PostAsJsonAsync("/api/v1/day-close", new { countedCash = 800m, countedFloat = 2000m });
        posted.EnsureSuccessStatusCode();
        var result = await posted.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Baseline", result.GetProperty("status").GetString());

        var negative = await app.PostAsJsonAsync("/api/v1/day-close", new { countedCash = -1m, countedFloat = 0m });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);

        var latest = await app.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/day-close/latest");
        Assert.Equal(800m, latest.GetProperty("countedCash").GetDecimal());
    }

    // ─── Growth features for keypad agents and owners ────────────────────────

    [SkippableFact]
    public async Task AKeypadReplyWarnsWhenFloatFallsBelowTheOwnersLevel()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await LinkedAgentAsync("0244000521");

        // No level set: no warning, whatever the float — a business that does not track float
        // must not be told it is always low.
        var quiet = await TextAsync("0244000521", "CI 20 0244123456");
        Assert.DoesNotContain("float", quiet.Message, StringComparison.OrdinalIgnoreCase);

        using (var scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlertService>().SetThresholdAsync(
                new AlertThresholdRequest(tenant.OrganizationId, null, "MTN", 500m, 250m));
        }

        var warned = await TextAsync("0244000521", "CI 30 0244123456");
        Assert.Equal(KeypadOutcome.Recorded, warned.Outcome);
        Assert.Contains("Very low MTN float", warned.Message, StringComparison.Ordinal);
        Assert.All(warned.Message, c => Assert.True(c < 128, $"Non-GSM character '{c}' in a reply."));
    }

    [SkippableFact]
    public async Task AShortCloseIsTextedToTheOwner()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await LinkedAgentAsync("0244000531");
        await using (var db = _postgres.CreateContext())
        {
            var org = await db.Organizations.SingleAsync(o => o.Id == tenant.OrganizationId);
            org.PhoneNumber = "+233 20 000 0531";
            await db.SaveChangesAsync();
        }

        await TextAsync("0244000531", "CLOSE 1000 5000");
        Assert.Empty(SentTo("0200000531"));

        await TextAsync("0244000531", "CLOSE 1000 5000");
        Assert.Empty(SentTo("0200000531"));

        await TextAsync("0244000531", "CLOSE 900 5000");
        var alert = Assert.Single(SentTo("0200000531"));
        Assert.Contains("closed short", alert.Message, StringComparison.Ordinal);
        Assert.Contains("Cash SHORT 100.00", alert.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TheEveningSummaryRemindsAnAgentWhoHasNotClosed()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        await LinkedAgentAsync("0244000541");
        await LinkedAgentAsync("0244000542");
        await TextAsync("0244000541", "CO 50 0244123456");
        await TextAsync("0244000542", "CO 50 0244123456");
        await TextAsync("0244000542", "CLOSE 100 100");

        using (var scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IKeypadSmsService>().SendDailySummariesAsync();
        }

        Assert.Contains(SentTo("0244000541"), m => m.Message.Contains("CLOSE cash float", StringComparison.Ordinal));
        var closedAgent = SentTo("0244000542").Last();
        Assert.DoesNotContain("CLOSE cash float", closedAgent.Message, StringComparison.Ordinal);
    }
}
