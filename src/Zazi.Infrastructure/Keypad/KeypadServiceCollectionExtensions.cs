using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zazi.Application.Keypad;

namespace Zazi.Infrastructure.Keypad;

public static class KeypadServiceCollectionExtensions
{
    /// <summary>
    /// Zazi by SMS, for agents on keypad phones: the router, the gateway, and the nightly summary.
    /// </summary>
    public static IServiceCollection AddZaziKeypad(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZaziSms(configuration);
        services.AddScoped<IKeypadSmsService, KeypadSmsService>();
        services.AddHostedService<KeypadDailySummaryService>();
        return services;
    }

    /// <summary>
    /// Only the ability to send an SMS — for the portal, where an owner's decision on a float
    /// request is texted to a keypad agent — without the inbound router or the nightly job.
    /// </summary>
    public static IServiceCollection AddZaziSms(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmsGatewayOptions>(configuration.GetSection(SmsGatewayOptions.SectionName));

        var provider = configuration[$"{SmsGatewayOptions.SectionName}:Provider"] ?? "Disabled";
        switch (provider.Trim().ToLowerInvariant())
        {
            case "africastalking":
                services.AddHttpClient<ISmsSender, AfricasTalkingSmsSender>(client => client.Timeout = TimeSpan.FromSeconds(15));
                break;
            case "log":
                services.AddSingleton<ISmsSender, LoggingSmsSender>();
                break;
            default:
                services.AddSingleton<ISmsSender, DisabledSmsSender>();
                break;
        }

        return services;
    }
}

/// <summary>
/// Texts each keypad agent who traded today a summary of their day, once, at the configured hour.
/// </summary>
/// <remarks>
/// <para>Ghana keeps GMT all year, so the configured UTC hour is the local hour. One instance
/// runs the API; if that ever changes this needs a lock, or agents would get the summary
/// twice.</para>
/// <para><b>Deliberately does not catch up after a restart</b>, unlike the email digest beside
/// it. Nothing records that an agent has already been texted today, so a catch-up would re-text
/// everyone who traded, on every restart after the hour — which costs money and teaches people
/// to ignore the message. A missed evening is the lesser harm. Giving this the same catch-up
/// means first recording what was sent, per agent per day.</para>
/// </remarks>
public sealed class KeypadDailySummaryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SmsGatewayOptions _options;
    private readonly ILogger<KeypadDailySummaryService> _logger;

    public KeypadDailySummaryService(
        IServiceScopeFactory scopes,
        IOptions<SmsGatewayOptions> options,
        ILogger<KeypadDailySummaryService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _options.DailySummaryEnabled
            && !string.Equals(_options.Provider, "Disabled", StringComparison.OrdinalIgnoreCase);
        if (!enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var next = new DateTimeOffset(now.UtcDateTime.Date.AddHours(Math.Clamp(_options.DailySummaryHourUtc, 0, 23)), TimeSpan.Zero);
            if (next <= now)
            {
                next = next.AddDays(1);
            }

            try
            {
                await Task.Delay(next - now, stoppingToken);

                using var scope = _scopes.CreateScope();
                var keypad = scope.ServiceProvider.GetRequiredService<IKeypadSmsService>();
                var sent = await keypad.SendDailySummariesAsync(stoppingToken);
                _logger.LogInformation("Keypad daily summaries sent: {Count}.", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // One bad night must not stop tomorrow's.
                _logger.LogError(exception, "Keypad daily summaries failed.");
            }
        }
    }
}

/// <summary>
/// Sends every owner their evening email, once, at the configured hour.
/// </summary>
/// <remarks>
/// Ghana keeps GMT all year, so the configured UTC hour is the local hour. One instance runs
/// the API; if that ever changes this needs a lock, or owners would be written to twice — which
/// the send's idempotency key would catch, but the work would still be done twice.
/// </remarks>
public sealed class DailyDigestHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;
    private readonly ILogger<DailyDigestHostedService> _logger;

    public DailyDigestHostedService(
        IServiceScopeFactory scopes,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        ILogger<DailyDigestHostedService> logger)
    {
        _scopes = scopes;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Digest:Enabled", true))
        {
            return;
        }

        var hour = Math.Clamp(_configuration.GetValue("Digest:HourUtc", 20), 0, 23);

        // A deploy or a restart at the wrong minute would otherwise lose a whole evening: the
        // loop would compute tomorrow's hour and nothing would ever go back for today. So the
        // first thing this does is catch up. Sending is idempotent per business per day — the
        // key travels to the provider — so a catch-up after a send that already happened costs
        // a request and delivers nothing.
        var caughtUp = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var todayAt = new DateTimeOffset(now.UtcDateTime.Date.AddHours(hour), TimeSpan.Zero);

            try
            {
                if (!caughtUp)
                {
                    caughtUp = true;
                    if (now >= todayAt)
                    {
                        await SendAsync(DateOnly.FromDateTime(now.UtcDateTime), "caught up", stoppingToken);
                    }
                }

                var next = todayAt <= now ? todayAt.AddDays(1) : todayAt;
                await Task.Delay(next - now, stoppingToken);
                await SendAsync(DateOnly.FromDateTime(DateTime.UtcNow), "sent", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // One bad evening must not stop tomorrow's.
                _logger.LogError(exception, "Evening summaries failed.");

                // Without this the loop would spin: a failure before the delay leaves the
                // clock where it was, and the next pass would fail again immediately.
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task SendAsync(DateOnly day, string what, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var digests = scope.ServiceProvider.GetRequiredService<Zazi.Application.Growth.IDailyDigestService>();
        var sent = await digests.SendAllAsync(day, cancellationToken);
        _logger.LogInformation("Evening summaries {What}: {Count} for {Day}.", what, sent, day);
    }
}
