using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zazi.Application;
using Zazi.Application.Keypad;
using Zazi.Domain;

namespace Zazi.IntegrationTests.Postgres;

/// <summary>
/// Zazi for keypad phones, end to end over SMS.
/// </summary>
/// <remarks>
/// Most Ghanaian MoMo agents work on a keypad phone with no internet. These drive the whole
/// SMS path against a real database with the gateway replaced by a recorder: linking a phone
/// with an activation code, recording by forwarding the MoMo message and by typed command,
/// looking a customer up, and the refusals that keep a stranger's texts out.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class KeypadSmsTests : IDisposable
{
    private const string Secret = "test-inbound-secret";

    /// <summary>MTN's cash-out confirmation, from the shared parser fixtures. It never says "MTN".</summary>
    private const string MtnCashOut =
        "Cash Out of GHS 250.50 to 0241000002 AMA SYNTHETIC. Ref: MP240815.1202.A00002. Your MoMo agent balance is GHS 12,089.50";

    private const string TelecelCashIn =
        "Telecel Cash: Deposit of GHS 1,250.00 from 0201000001. Transaction ID: TC98765432. Balance GHS 8,400.00";

    private readonly PostgresFixture _postgres;
    private readonly PostgresApiFactory? _factory;
    private readonly WebApplicationFactory<Program>? _app;
    private readonly RecordingSms _outbox = new();

    public KeypadSmsTests(PostgresFixture postgres)
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
            builder.UseSetting("Sms:InboundSecret", Secret);
            builder.UseSetting("Sms:DailySummaryEnabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISmsSender>();
                services.AddSingleton<ISmsSender>(_outbox);
            });
        });
    }

    public void Dispose()
    {
        _app?.Dispose();
        _factory?.Dispose();
    }

    private sealed class RecordingSms : ISmsSender
    {
        private readonly List<(string To, string Message)> _sent = new();

        public IReadOnlyList<(string To, string Message)> Sent { get { lock (_sent) return _sent.ToList(); } }

        public Task<bool> SendAsync(string toPhoneNumber, string message, CancellationToken cancellationToken = default)
        {
            lock (_sent) _sent.Add((toPhoneNumber, message));
            return Task.FromResult(true);
        }
    }

    private async Task<KeypadReply> TextAsync(string from, string text, string? id = null)
    {
        using var scope = _app!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IKeypadSmsService>()
            .HandleAsync(new InboundSms(from, text, DateTimeOffset.UtcNow, id));
    }

    private async Task<string> IssueCodeAsync(TenantSeed tenant)
    {
        using var scope = _app!.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentService>().IssueCodeAsync(
            new IssueEnrollmentCodeRequest(tenant.BranchId, IntendedUserId: tenant.UserId),
            tenant.OrganizationId, tenant.BranchId, tenant.UserId);
        return issued.Code;
    }

    private async Task<(TenantSeed Tenant, string Phone)> LinkedAgentAsync(string phone = "0244000111")
    {
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var reply = await TextAsync(phone, "ZAZI " + await IssueCodeAsync(tenant));
        Assert.Equal(KeypadOutcome.Linked, reply.Outcome);
        return (tenant, phone);
    }

    // ─── Linking ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TextingTheActivationCodeLinksTheKeypadPhone()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var code = await IssueCodeAsync(tenant);

        var reply = await TextAsync("+233 24 400 0111", "ZAZI " + code);

        Assert.Equal(KeypadOutcome.Linked, reply.Outcome);
        Assert.Contains("linked", reply.Message, StringComparison.OrdinalIgnoreCase);

        // The reply goes back to the phone that texted, and is plain GSM text — one character
        // outside it would double what every reply costs.
        var sent = Assert.Single(_outbox.Sent, s => s.To == "0244000111");
        Assert.All(sent.Message, c => Assert.True(c < 128, $"Non-GSM character '{c}' in a reply."));

        await using var db = _postgres.CreateContext();
        var phone = await db.Devices.AsNoTracking().SingleAsync(d => d.DeviceIdentifier == "sms:0244000111");
        Assert.Equal("SMS", phone.Platform);
        Assert.Equal(tenant.BranchId, phone.BranchId);
        Assert.Equal(Networks.Mtn, phone.Network);

        // A keypad phone has nowhere to keep an app session, so none is issued.
        Assert.False(await db.AuthSessions.AnyAsync(s => s.DeviceId == phone.Id));
    }

    [SkippableFact]
    public async Task ACodeWorksOnceOnly()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var tenant = await TenantSeedFactory.CreateAsync(_postgres);
        var code = await IssueCodeAsync(tenant);

        Assert.Equal(KeypadOutcome.Linked, (await TextAsync("0244000121", "ZAZI " + code)).Outcome);
        Assert.Equal(KeypadOutcome.LinkRefused, (await TextAsync("0244000122", "ZAZI " + code)).Outcome);
    }

    [SkippableFact]
    public async Task AnUnlinkedNumberCannotRecordAnything()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);

        var reply = await TextAsync("0244999888", MtnCashOut);

        Assert.Equal(KeypadOutcome.UnknownSender, reply.Outcome);
        await using var db = _postgres.CreateContext();
        Assert.False(await db.TransactionEvidence.AnyAsync(e => e.SenderAddress == "0244999888"));
    }

    [SkippableFact]
    public async Task ARevokedPhoneIsTreatedAsUnlinked()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (_, phone) = await LinkedAgentAsync("0244000131");

        await using (var db = _postgres.CreateContext())
        {
            var device = await db.Devices.SingleAsync(d => d.DeviceIdentifier == "sms:" + phone);
            device.IsRevoked = true;
            device.Status = DeviceStatus.Revoked;
            await db.SaveChangesAsync();
        }

        Assert.Equal(KeypadOutcome.UnknownSender, (await TextAsync(phone, MtnCashOut)).Outcome);
    }

    // ─── Recording by forwarding ─────────────────────────────────────────────

    [SkippableFact]
    public async Task AForwardedMtnMessageIsRecordedForTheAgentWithItsCustomer()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, phone) = await LinkedAgentAsync("0244000141");

        var reply = await TextAsync(phone, "Fwd: " + MtnCashOut);

        Assert.Equal(KeypadOutcome.Recorded, reply.Outcome);
        Assert.Contains("Cash out GHS 250.50", reply.Message, StringComparison.Ordinal);
        Assert.Contains("024 100 0002", reply.Message, StringComparison.Ordinal);

        await using var db = _postgres.CreateContext();
        var recorded = await db.Transactions.AsNoTracking().SingleAsync(t => t.OrganizationId == tenant.OrganizationId);
        Assert.Equal(TransactionType.CashOut, recorded.Type);
        Assert.Equal(250.50m, recorded.Amount);
        Assert.Equal(tenant.UserId, recorded.AgentId);
        Assert.Equal("0241000002", recorded.CustomerPhoneNumber);
        // The message never says "MTN"; the agent SIM's network fills the gap.
        Assert.Equal("MTN", recorded.Network);
    }

    [SkippableFact]
    public async Task ForwardingTheSameMessageTwiceRecordsItOnce()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, phone) = await LinkedAgentAsync("0244000151");

        Assert.Equal(KeypadOutcome.Recorded, (await TextAsync(phone, MtnCashOut)).Outcome);
        Assert.Equal(KeypadOutcome.AlreadyRecorded, (await TextAsync(phone, MtnCashOut)).Outcome);

        await using var db = _postgres.CreateContext();
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.OrganizationId == tenant.OrganizationId));
    }

    [SkippableFact]
    public async Task AMessageThatNamesItsNetworkIsFiledUnderThatNetwork()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        // An MTN-numbered keypad phone with a Telecel SIM in its second slot.
        var (tenant, phone) = await LinkedAgentAsync("0244000161");

        Assert.Equal(KeypadOutcome.Recorded, (await TextAsync(phone, TelecelCashIn)).Outcome);

        await using var db = _postgres.CreateContext();
        var recorded = await db.Transactions.AsNoTracking().SingleAsync(t => t.OrganizationId == tenant.OrganizationId);
        Assert.Equal("TELECEL", recorded.Network);
        Assert.Equal(TransactionType.CashIn, recorded.Type);
    }

    [SkippableFact]
    public async Task SomethingThatIsNotAMoMoMessageIsAnsweredAndNotStored()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, phone) = await LinkedAgentAsync("0244000171");

        var reply = await TextAsync(phone, "hello are you there");

        Assert.Equal(KeypadOutcome.NotUnderstood, reply.Outcome);
        Assert.Contains("CO 50 0244123456", reply.Message, StringComparison.Ordinal);
        await using var db = _postgres.CreateContext();
        Assert.False(await db.TransactionEvidence.AnyAsync(e => e.OrganizationId == tenant.OrganizationId));
    }

    // ─── Recording by typed command ──────────────────────────────────────────

    [SkippableFact]
    public async Task ATypedCashOutIsRecordedWithItsCustomer()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, phone) = await LinkedAgentAsync("0244000181");

        var reply = await TextAsync(phone, "co 50 024 412 3456", id: "gw-1");

        Assert.Equal(KeypadOutcome.Recorded, reply.Outcome);
        Assert.Contains("Cash out GHS 50.00, 024 412 3456, MTN", reply.Message, StringComparison.Ordinal);

        await using var db = _postgres.CreateContext();
        var recorded = await db.Transactions.AsNoTracking().SingleAsync(t => t.OrganizationId == tenant.OrganizationId);
        Assert.Equal(TransactionType.CashOut, recorded.Type);
        Assert.Equal(50m, recorded.Amount);
        Assert.Equal("0244123456", recorded.CustomerPhoneNumber);
        Assert.Equal(TransactionSource.Bridge, recorded.Source);
    }

    [SkippableFact]
    public async Task TheGatewayDeliveringACommandTwiceRecordsItOnce()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, phone) = await LinkedAgentAsync("0244000191");

        await TextAsync(phone, "CI 200 0201234567", id: "gw-dup");
        await TextAsync(phone, "CI 200 0201234567", id: "gw-dup");

        await using var db = _postgres.CreateContext();
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.OrganizationId == tenant.OrganizationId));
    }

    [SkippableFact]
    public async Task ACommandCanNameItsNetwork()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, phone) = await LinkedAgentAsync("0244000201");

        Assert.Equal(KeypadOutcome.Recorded, (await TextAsync(phone, "CI 75 0271112222 AT")).Outcome);

        await using var db = _postgres.CreateContext();
        Assert.Equal("AIRTELTIGO", (await db.Transactions.AsNoTracking()
            .SingleAsync(t => t.OrganizationId == tenant.OrganizationId)).Network);
    }

    [SkippableFact]
    public async Task ABadCommandSaysHowToFixIt()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (_, phone) = await LinkedAgentAsync("0244000211");

        var noNumber = await TextAsync(phone, "CO 50");
        Assert.Equal(KeypadOutcome.NotUnderstood, noNumber.Outcome);
        Assert.Contains("CO 50 0244123456", noNumber.Message, StringComparison.Ordinal);

        var badNumber = await TextAsync(phone, "CO 50 12345");
        Assert.Contains("customer number", badNumber.Message, StringComparison.Ordinal);
    }

    // ─── Answers ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task FindAnswersWithTheCustomersRecentTransactions()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (_, phone) = await LinkedAgentAsync("0244000221");
        await TextAsync(phone, "CO 50 0244123456");
        await TextAsync(phone, "CI 300 0244123456");
        await TextAsync(phone, "CI 999 0209999999");

        var reply = await TextAsync(phone, "FIND 0244123456");

        Assert.Equal(KeypadOutcome.Answered, reply.Outcome);
        Assert.Contains("Cash out GHS 50.00", reply.Message, StringComparison.Ordinal);
        Assert.Contains("Cash in GHS 300.00", reply.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("999", reply.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TodayAnswersWithTheAgentsTotals()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (_, phone) = await LinkedAgentAsync("0244000231");
        await TextAsync(phone, "CO 50 0244123456");
        await TextAsync(phone, "CI 300 0201234567");

        var reply = await TextAsync(phone, "today");

        Assert.Equal(KeypadOutcome.Answered, reply.Outcome);
        Assert.Contains("2 transactions", reply.Message, StringComparison.Ordinal);
        Assert.Contains("Cash in GHS 300.00 (1)", reply.Message, StringComparison.Ordinal);
        Assert.Contains("Cash out GHS 50.00 (1)", reply.Message, StringComparison.Ordinal);
    }

    // ─── The gateway's front door ────────────────────────────────────────────

    [SkippableFact]
    public async Task TheGatewayMustCarryTheSecret()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        using var client = _app!.CreateClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["from"] = "+233244000241", ["to"] = "12345", ["text"] = MtnCashOut, ["id"] = "x1"
        });

        // Without the secret anyone could post a fake "MoMo message" in an agent's name.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/sms-gateway/inbound", form)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/sms-gateway/inbound?key=wrong", form)).StatusCode);
    }

    [SkippableFact]
    public async Task AGatewayDeliveryWithTheSecretIsRecorded()
    {
        Skip.IfNot(_postgres.IsAvailable, _postgres.SkipReason);
        var (tenant, phone) = await LinkedAgentAsync("0244000251");
        using var client = _app!.CreateClient();

        var response = await client.PostAsync($"/api/v1/sms-gateway/inbound?key={Secret}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["from"] = "+233244000251", ["to"] = "12345", ["text"] = MtnCashOut,
                ["date"] = DateTimeOffset.UtcNow.ToString("O"), ["id"] = "at-123"
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = _postgres.CreateContext();
        Assert.Equal(1, await db.Transactions.CountAsync(t => t.OrganizationId == tenant.OrganizationId));
    }
}
