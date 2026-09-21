using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zazi.Application.Keypad;
using Zazi.Domain;

namespace Zazi.Infrastructure.Keypad;

/// <summary>
/// Sends through Africa's Talking's messaging API.
/// </summary>
/// <remarks>
/// A single form POST — no SDK, the same choice as the email sender, so there is nothing to
/// keep current. The key comes from the <c>AFRICASTALKING_API_KEY</c> environment variable
/// and nowhere else, and is never logged. Username "sandbox" talks to the sandbox, which is
/// where this is built and tested before a live account exists.
/// </remarks>
public sealed class AfricasTalkingSmsSender : ISmsSender
{
    public const string ApiKeyVariable = "AFRICASTALKING_API_KEY";

    private readonly HttpClient _http;
    private readonly SmsGatewayOptions _options;
    private readonly ILogger<AfricasTalkingSmsSender> _logger;
    private readonly string? _apiKey;

    public AfricasTalkingSmsSender(HttpClient http, IOptions<SmsGatewayOptions> options, ILogger<AfricasTalkingSmsSender> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
    }

    public async Task<bool> SendAsync(string toPhoneNumber, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_options.Username))
        {
            _logger.LogError("SMS not sent: {Variable} or Sms:Username is not configured.", ApiKeyVariable);
            return false;
        }

        // International form, as the gateway requires: +233 and the nine digits.
        var local = GhanaPhoneNumber.Normalise(toPhoneNumber);
        if (local is null)
        {
            _logger.LogWarning("SMS not sent: the recipient is not a Ghanaian mobile number.");
            return false;
        }

        var sandbox = string.Equals(_options.Username, "sandbox", StringComparison.OrdinalIgnoreCase);
        var endpoint = sandbox
            ? "https://api.sandbox.africastalking.com/version1/messaging"
            : "https://api.africastalking.com/version1/messaging";

        var fields = new Dictionary<string, string>
        {
            ["username"] = _options.Username!,
            ["to"] = "+233" + local[1..],
            ["message"] = message
        };
        if (!string.IsNullOrWhiteSpace(_options.SenderId))
        {
            fields["from"] = _options.SenderId!;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Add("apiKey", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning("SMS gateway refused a message: {Status}.", (int)response.StatusCode);
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("SMS gateway could not be reached: {Error}.", exception.GetType().Name);
            return false;
        }
    }
}

/// <summary>
/// Development only: writes each message to the log instead of sending it, so the keypad flow
/// can be exercised end to end without a gateway account.
/// </summary>
public sealed class LoggingSmsSender : ISmsSender
{
    private readonly ILogger<LoggingSmsSender> _logger;

    public LoggingSmsSender(ILogger<LoggingSmsSender> logger) => _logger = logger;

    public Task<bool> SendAsync(string toPhoneNumber, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SMS to {Recipient}: {Message}", GhanaPhoneNumber.Display(toPhoneNumber), message);
        return Task.FromResult(true);
    }
}

/// <summary>No gateway configured: nothing is sent, and the reason is logged once per message.</summary>
public sealed class DisabledSmsSender : ISmsSender
{
    private readonly ILogger<DisabledSmsSender> _logger;

    public DisabledSmsSender(ILogger<DisabledSmsSender> logger) => _logger = logger;

    public Task<bool> SendAsync(string toPhoneNumber, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("SMS not sent: no gateway is configured (Sms:Provider).");
        return Task.FromResult(false);
    }
}
