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
/// Ghana keeps GMT all year, so the configured UTC hour is the local hour. One instance runs the
/// API; if that ever changes this needs a lock, or agents would get the summary twice.
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
