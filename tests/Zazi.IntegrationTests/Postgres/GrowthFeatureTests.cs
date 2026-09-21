using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zazi.Application;
using Zazi.Application.Growth;
using Zazi.Application.Keypad;
using Zazi.Application.Security;
using Zazi.Application.Sync;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Customer receipts, float requests, languages, commission and the trading record — end to
/// end over SMS and HTTP against a real database, with the SMS gateway replaced by a recorder.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GrowthFeatureTests : IDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;
    private readonly WebApplicationFactory<Program>? _app;
    private static readonly List<(string To, string Message)> Sent = new();

    public GrowthFeatureTests(PostgresFixture postgres)
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
            // Twi switched on here, as it would be after a speaker has checked it.
            builder.UseSetting("Sms:Languages", "EN,TWI");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISmsSender>();
                services.AddSingleton<ISmsSender, Recorder>();
            });
        });
    }

    public void Dispose()
    {
        _app?.Dispose();
        _factory?.Dispose();
    }

    private sealed class Recorder : ISmsSender
    {
        public Task<bool> SendAsync(string toPhoneNumber, string message, CancellationToken cancellationToken = default)
        {
            lock (Sent) Sent.Add((toPhoneNumber, message));
            return Task.FromResult(true);
        }
    }

    private static List<string> SentTo(string number)
    {
        lock (Sent) return Sent.Where(s => s.To == number).Select(s => s.Message).ToList();
    }

    private async Task<KeypadReply> TextAsync(string from, string text)
    {
        using var scope = _app!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IKeypadSmsService>()
            .HandleAsync(new InboundSms(from, text, DateTimeOffset.UtcNow, null));
    }

    private async Task<T> ServiceAsync<T>(Func<T, Task> act) where T : notnull
    {
        using var scope = _app!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<T>();
        await act(service);
        return service;
    }

    /// <summary>A business with an owner (who has the OWNER role and an SMS number) and a keypad agent.</summary>
    private async Task<(TenantSeed Tenant, Guid OwnerId)> BusinessAsync(string agentPhone, string ownerPhone, bool receipts = false)
    {
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        Guid ownerId;
        await using (var db = _postgres.CreateContext())
        {
            var role = new Role { OrganizationId = tenant.OrganizationId, Name = ZaziRoles.Owner };
            var owner = new User
            {
                OrganizationId = tenant.OrganizationId,
                BranchId = tenant.BranchId,
                FullName = "Ama Owner",
                Email = $"owner-{Guid.NewGuid():N}@carl.test",
                IsActive = true,
                Roles = [role]
            };
            db.Roles.Add(role);
            db.Users.Add(owner);
            var org = await db.Organizations.SingleAsync(o => o.Id == tenant.OrganizationId);
            org.PhoneNumber = ownerPhone;
            org.SendCustomerReceipts = receipts;
            await db.SaveChangesAsync();
            ownerId = owner.Id;
        }

        using var scope = _app!.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentService>().IssueCodeAsync(
            new IssueEnrollmentCodeRequest(tenant.BranchId, IntendedUserId: tenant.UserId),
            tenant.OrganizationId, tenant.BranchId, tenant.UserId);
        Assert.Equal(KeypadOutcome.Linked, (await TextAsync(agentPhone, "ZAZI " + issued.Code)).Outcome);
        return (tenant, ownerId);
    }

    private HttpClient AppFor(TenantSeed tenant) =>
        _app!.CreateClient().Authenticated(
            TestTokens.Create(tenant.UserId, tenant.OrganizationId, tenant.BranchId, ZaziRoles.Agent));

    // ─── Customer receipts ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task ACustomerGetsAReceiptOnlyWhenTheBusinessHasSwitchedThemOn()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (off, _) = await BusinessAsync("0244000601", "0200000601");
        await TextAsync("0244000601", "CO 50 0244600601");
        Assert.Empty(SentTo("0244600601"));

        var (on, _) = await BusinessAsync("0244000602", "0200000602", receipts: true);
        await TextAsync("0244000602", "CO 75 0244600602");
        var receipt = Assert.Single(SentTo("0244600602"));
        Assert.Contains("You withdrew GHS 75.00 (MTN)", receipt, StringComparison.Ordinal);
        Assert.Contains("Questions: 020 000 0602", receipt, StringComparison.Ordinal);
        Assert.True(receipt.Length <= 160, receipt);
        Assert.All(receipt, c => Assert.True(c < 128, $"Non-GSM character '{c}' in a receipt."));
    }

    [SkippableFact]
    public async Task AnAppSyncedTransactionIsReceiptedOnceEvenWhenTheBatchIsRetried()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, _) = await BusinessAsync("0244000611", "0200000611", receipts: true);
        using var client = AppFor(tenant);
        var item = new
        {
            clientTransactionId = ClientTransactionId.Create($"device-{tenant.BranchId:N}"),
            transactionType = TransactionType.CashIn,
            amount = 120m,
            provider = "TELECEL",
            transactionTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
            deviceReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            branchId = tenant.BranchId,
            sourceType = EvidenceSourceType.ManualEntry,
            parserVersion = "manual-v1",
            customerPhone = "0204600611",
            transactionReference = Guid.NewGuid().ToString("N")[..12]
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            (await client.PostAsJsonAsync("/api/v1/sync/transactions", new { transactions = new[] { item } })).EnsureSuccessStatusCode();
        }

        var receipt = Assert.Single(SentTo("0204600611"));
        Assert.Contains("You deposited GHS 120.00 (Telecel)", receipt, StringComparison.Ordinal);
    }

    // ─── Float requests ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AKeypadAgentAsksForFloatAndTheOwnerGivesItByReplyingOk()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, _) = await BusinessAsync("0244000621", "0200000621");

        var asked = await TextAsync("0244000621", "FLOAT 500 MTN");
        Assert.Equal(KeypadOutcome.Recorded, asked.Outcome);

        var toOwner = Assert.Single(SentTo("0200000621"));
        var code = System.Text.RegularExpressions.Regex.Match(toOwner, @"OK (\d{4})").Groups[1].Value;
        Assert.Contains("asks for GHS 500.00 MTN float", toOwner, StringComparison.Ordinal);

        var answer = await TextAsync("0200000621", $"OK {code}");
        Assert.Contains("float recorded for Test Agent", answer.Message, StringComparison.Ordinal);
        Assert.Contains(SentTo("0244000621"), m => m.Contains("was approved and recorded", StringComparison.Ordinal));

        await using var db = _postgres.CreateContext();
        var request = await db.FloatRequests.SingleAsync(r => r.OrganizationId == tenant.OrganizationId);
        Assert.Equal(FloatRequestStatus.Approved, request.Status);
        Assert.Equal("SMS", request.DecidedVia);
        var mtn = await db.FloatBalances.Where(b => b.OrganizationId == tenant.OrganizationId && b.AgentId == tenant.UserId && b.Network == "MTN")
            .SumAsync(b => b.CurrentFloat);
        Assert.Equal(500m, mtn);

        // Answered once: the same code again finds nothing waiting.
        Assert.Contains("no waiting float request", (await TextAsync("0200000621", $"OK {code}")).Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AStrangerCannotAnswerAFloatRequest()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, _) = await BusinessAsync("0244000631", "0200000631");
        await TextAsync("0244000631", "FLOAT 300 MTN");
        var code = System.Text.RegularExpressions.Regex.Match(SentTo("0200000631").Single(), @"OK (\d{4})").Groups[1].Value;

        var stranger = await TextAsync("0559999631", $"OK {code}");
        Assert.Equal(KeypadOutcome.UnknownSender, stranger.Outcome);

        await using var db = _postgres.CreateContext();
        Assert.Equal(FloatRequestStatus.Pending, (await db.FloatRequests.SingleAsync(r => r.OrganizationId == tenant.OrganizationId)).Status);
    }

    [SkippableFact]
    public async Task TheAppAsksForFloatAndTheOwnerDeclinesInThePortal()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, ownerId) = await BusinessAsync("0244000641", "0200000641");
        using var client = AppFor(tenant);

        var bad = await client.PostAsJsonAsync("/api/v1/float-requests", new { network = "MTN", amount = 0m });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var posted = await client.PostAsJsonAsync("/api/v1/float-requests", new { network = "telecel", amount = 250m });
        posted.EnsureSuccessStatusCode();
        var request = (await posted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await ServiceAsync<IFloatRequestService>(s => s.DecideAsync(tenant.OrganizationId, request, false, ownerId, "Portal"));

        var mine = await client.GetFromJsonAsync<JsonElement>("/api/v1/float-requests/mine");
        Assert.Equal((int)FloatRequestStatus.Declined, mine[0].GetProperty("status").GetInt32());
        Assert.Contains(SentTo("0244000641"), m => m.Contains("was declined", StringComparison.Ordinal));
    }

    // ─── Languages ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AnAgentCanChooseASwitchedOnLanguageButNotOneStillBeingTranslated()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        await BusinessAsync("0244000651", "0200000651");

        var ga = await TextAsync("0244000651", "LANG GA");
        Assert.Contains("Ga replies are not switched on yet", ga.Message, StringComparison.Ordinal);

        var twi = await TextAsync("0244000651", "LANG TWI");
        Assert.Contains("Twi", twi.Message, StringComparison.Ordinal);

        var recorded = await TextAsync("0244000651", "CO 40 0244123456");
        Assert.StartsWith("Zazi: Yeakyerew.", recorded.Message, StringComparison.Ordinal);
        Assert.All(recorded.Message, c => Assert.True(c < 128, $"Non-GSM character '{c}' in a reply."));

        await TextAsync("0244000651", "LANG EN");
        Assert.StartsWith("Zazi OK:", (await TextAsync("0244000651", "CO 41 0244123456")).Message, StringComparison.Ordinal);
    }

    // ─── Commission and the trading record ───────────────────────────────────

    [SkippableFact]
    public async Task CommissionAndTheTradingRecordComeFromTheLedger()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, _) = await BusinessAsync("0244000661", "0200000661");

        Assert.Contains("none recorded yet", (await TextAsync("0244000661", "COMM")).Message, StringComparison.Ordinal);

        await TextAsync("0244000661", "CO 100 0244123456");
        await TextAsync("0244000661", "CI 250 0244123456");
        using (var scope = _app!.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITransactionService>().CreateTransactionAsync(
                new CreateTransactionRequest(tenant.OrganizationId, tenant.BranchId, tenant.UserId, null, "MTN",
                    TransactionType.Commission, 12.5m, "GHS", null, "CM123456", TransactionSource.Manual, "Commission"));
        }

        var comm = await TextAsync("0244000661", "COMM");
        Assert.Contains("MTN GHS 12.50", comm.Message, StringComparison.Ordinal);

        TradingRecord? record = null;
        await ServiceAsync<IReportService>(async s => record = await s.TradingRecordAsync(tenant.OrganizationId, null, tenant.UserId));
        Assert.Equal(3, record!.Transactions);
        Assert.Equal(350m, record.Volume);
        Assert.Equal(12.5m, record.Commission);
        Assert.Equal(1, record.ActiveDays);
        Assert.StartsWith("ZT-", record.DocumentNumber, StringComparison.Ordinal);

        using var client = AppFor(tenant);
        var pdf = await client.GetAsync("/api/v1/reports/trading-record");
        pdf.EnsureSuccessStatusCode();
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

        CommissionReport? report = null;
        await ServiceAsync<IReportService>(async s => report = await s.CommissionAsync(tenant.OrganizationId, null, null));
        var row = Assert.Single(report!.Rows);
        Assert.Equal(12.5m, row.Amount);
        await ServiceAsync<IReportService>(s =>
        {
            var csv = s.CommissionCsv(report);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csv.Take(3).ToArray());
            return Task.CompletedTask;
        });
    }

    // ─── Settings ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SettingsStoreTheOwnersNumberNormalisedAndRefuseANonGhanaianOne()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);

        await ServiceAsync<IBusinessSettingsService>(s => s.SaveAsync(tenant.OrganizationId, "+233 24 400 0671", true, tenant.UserId));
        await ServiceAsync<IBusinessSettingsService>(async s =>
        {
            var settings = await s.GetAsync(tenant.OrganizationId);
            Assert.Equal("0244000671", settings.SmsPhoneNumber);
            Assert.True(settings.SendCustomerReceipts);
            await Assert.ThrowsAsync<ArgumentException>(() => s.SaveAsync(tenant.OrganizationId, "12345", false, tenant.UserId));
        });
    }
}
