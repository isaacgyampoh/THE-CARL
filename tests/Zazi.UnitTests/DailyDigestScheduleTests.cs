using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zazi.Application.Growth;
using Zazi.Infrastructure.Keypad;

namespace Zazi.UnitTests;

/// <summary>
/// When the evening summary actually goes out.
/// </summary>
/// <remarks>
/// <para>The fault this covers: the service computed the next send time and slept until it. A
/// deploy at 20:01 — which is when deploys happen, after the working day — produced a service
/// whose next send was <b>tomorrow</b>, and nothing ever went back for today. The owner simply
/// never got that evening's summary, and no error was logged because nothing failed.</para>
/// <para>Sending is idempotent per business per day, so catching up is free when the send
/// already happened.</para>
/// </remarks>
public class DailyDigestScheduleTests
{
    /// <summary>Records which days it was asked to send for, and never sends anything.</summary>
    private sealed class RecordingDigests : IDailyDigestService
    {
        public List<DateOnly> Days { get; } = [];

        public Task<int> SendAllAsync(DateOnly day, CancellationToken cancellationToken = default)
        {
            lock (Days)
            {
                Days.Add(day);
            }

            return Task.FromResult(0);
        }

        public Task<DailyDigest?> BuildAsync(
            Guid organizationId, DateOnly day, CancellationToken cancellationToken = default) =>
            Task.FromResult<DailyDigest?>(null);
    }

    private static (DailyDigestHostedService Service, RecordingDigests Digests) Build(int hourUtc)
    {
        var digests = new RecordingDigests();
        var services = new ServiceCollection();
        services.AddSingleton<IDailyDigestService>(digests);
        var provider = services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Digest:HourUtc"] = hourUtc.ToString(),
                ["Digest:Enabled"] = "true"
            })
            .Build();

        return (
            new DailyDigestHostedService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                NullLogger<DailyDigestHostedService>.Instance),
            digests);
    }

    /// <summary>Runs the service briefly, then stops it, and reports what it sent in that time.</summary>
    private static async Task<IReadOnlyList<DateOnly>> RunBrieflyAsync(int hourUtc)
    {
        var (service, digests) = Build(hourUtc);

        await service.StartAsync(CancellationToken.None);
        // Long enough for the first pass — which is synchronous up to the first delay — to run.
        await Task.Delay(250);
        await service.StopAsync(CancellationToken.None);

        lock (digests.Days)
        {
            return digests.Days.ToArray();
        }
    }

    [Fact]
    public async Task Starting_after_the_hour_sends_today_rather_than_waiting_for_tomorrow()
    {
        // An hour that has certainly passed, whatever time the suite runs — unless it runs in
        // the first hour of the day, which the second assertion below allows for.
        var now = DateTimeOffset.UtcNow;
        if (now.Hour == 0)
        {
            return;
        }

        var sentFor = await RunBrieflyAsync(hourUtc: 0);

        var day = Assert.Single(sentFor);
        Assert.Equal(DateOnly.FromDateTime(now.UtcDateTime), day);
    }

    [Fact]
    public async Task Starting_before_the_hour_waits_and_sends_nothing_yet()
    {
        var now = DateTimeOffset.UtcNow;
        if (now.Hour == 23)
        {
            return;
        }

        // An hour still ahead: the service must sleep until it, not send on startup.
        var sentFor = await RunBrieflyAsync(hourUtc: 23);

        Assert.Empty(sentFor);
    }

    [Fact]
    public async Task Catching_up_happens_once_not_on_every_pass()
    {
        var now = DateTimeOffset.UtcNow;
        if (now.Hour == 0)
        {
            return;
        }

        var (service, digests) = Build(hourUtc: 0);

        await service.StartAsync(CancellationToken.None);
        // Several times longer than the first pass needs. A catch-up that re-armed itself
        // would send repeatedly in this window, which is a request per pass to the provider.
        await Task.Delay(750);
        await service.StopAsync(CancellationToken.None);

        lock (digests.Days)
        {
            Assert.Single(digests.Days);
        }
    }

    [Fact]
    public async Task The_summary_is_disabled_by_configuration()
    {
        var digests = new RecordingDigests();
        var services = new ServiceCollection();
        services.AddSingleton<IDailyDigestService>(digests);
        var provider = services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Digest:Enabled"] = "false" })
            .Build();

        var service = new DailyDigestHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            NullLogger<DailyDigestHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(250);
        await service.StopAsync(CancellationToken.None);

        lock (digests.Days)
        {
            Assert.Empty(digests.Days);
        }
    }
}
