using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zazi.Application.Email;

namespace Zazi.Infrastructure.Email;

/// <summary>
/// Sends through Resend's HTTP API.
/// </summary>
/// <remarks>
/// <para>
/// Written against the REST endpoint rather than a client library. Resend publishes no official
/// .NET SDK; the community package is pre-1.0, and what it would save is one POST. For a system
/// that reconciles other people's money, a dependency that can reach the network and holds the
/// credential is worth more scrutiny than forty lines of <see cref="HttpClient"/> deserve.
/// </para>
/// <para>
/// The credential travels in the Authorization header and is never placed in a URL or a request
/// body, so it cannot come back in a response. Everything logged here goes through
/// <see cref="EmailLogSafety"/> regardless, because that guarantee should not rest on nobody
/// ever changing how the request is built.
/// </para>
/// </remarks>
public sealed class ResendEmailSender : IEmailSender
{
    /// <summary>The name the typed client is registered under.</summary>
    public const string HttpClientName = "resend";

    private const string SendPath = "emails";

    private readonly HttpClient _http;
    private readonly EmailOptions _options;
    private readonly ResendCredential _credential;
    private readonly ILogger<ResendEmailSender> _logger;

    public ResendEmailSender(
        HttpClient http,
        IOptions<EmailOptions> options,
        ResendCredential credential,
        ILogger<ResendEmailSender> logger)
    {
        _http = http;
        _options = options.Value;
        _credential = credential;
        _logger = logger;
    }

    public async Task<EmailResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_credential.IsPresent)
        {
            // Startup validates this for Production, so reaching here means a deployment that
            // permits an absent key. Say so once, plainly, rather than reporting a network error
            // that sends whoever reads it looking at the wrong thing.
            _logger.LogError(
                "Email not sent: {Variable} is not set in this process's environment.",
                EmailOptions.ApiKeyEnvironmentVariable);
            return EmailResult.PermanentFailure("email provider credential is not configured");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, SendPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential.ApiKey);

        if (!string.IsNullOrWhiteSpace(message.IdempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", message.IdempotencyKey);
        }

        request.Content = JsonContent.Create(new ResendSendRequest(
            From: _options.FormattedFrom,
            To: new[] { message.ToAddress },
            Subject: message.Subject,
            Html: message.HtmlBody,
            Text: message.TextBody,
            ReplyTo: string.IsNullOrWhiteSpace(_options.ReplyToAddress)
                ? null
                : new[] { _options.ReplyToAddress }));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up, not the provider. Let that propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient surfaces its own timeout as a cancellation with no token attached.
            _logger.LogWarning(
                "Email to {Recipient} timed out after {Timeout}s.",
                EmailLogSafety.MaskRecipient(message.ToAddress), _options.TimeoutSeconds);
            return EmailResult.TransientFailure("email provider timed out");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Email to {Recipient} could not reach the provider: {Reason}",
                EmailLogSafety.MaskRecipient(message.ToAddress), EmailLogSafety.Redact(ex.Message));
            return EmailResult.TransientFailure("email provider unreachable");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Email sent to {Recipient}.", EmailLogSafety.MaskRecipient(message.ToAddress));
                return EmailResult.Success();
            }

            var providerMessage = await ReadProviderMessageAsync(response, cancellationToken)
                .ConfigureAwait(false);

            // 401/403 is the deployment's own credential being wrong, which no retry fixes and
            // which an operator needs to see at a level they will actually notice.
            var authFailure = response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden;

            var transient = response.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.RequestTimeout
                || (int)response.StatusCode >= 500;

            _logger.Log(
                authFailure ? LogLevel.Error : LogLevel.Warning,
                "Email to {Recipient} rejected with {StatusCode}: {Reason}",
                EmailLogSafety.MaskRecipient(message.ToAddress),
                (int)response.StatusCode,
                EmailLogSafety.Redact(providerMessage));

            var reason = authFailure
                ? "email provider rejected the credential"
                : $"email provider returned {(int)response.StatusCode}";

            return transient
                ? EmailResult.TransientFailure(reason)
                : EmailResult.PermanentFailure(reason);
        }
    }

    /// <summary>
    /// Pulls the provider's own explanation out of an error body.
    /// </summary>
    /// <remarks>
    /// Worth the effort: a 422 without its message tells an operator that something about the
    /// request was wrong but not what, and "from domain is not verified" is the single most
    /// likely thing it says on a new deployment.
    /// </remarks>
    private static async Task<string> ReadProviderMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return "(no response body)";
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("message", out var element) &&
                    element.ValueKind == JsonValueKind.String)
                {
                    return Truncate(element.GetString() ?? string.Empty);
                }
            }
            catch (JsonException)
            {
                // Not JSON — a proxy's HTML error page, most likely. Fall through.
            }

            return Truncate(body);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return "(response body could not be read)";
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";

    private sealed record ResendSendRequest(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] IReadOnlyList<string> To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html")] string Html,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("reply_to"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? ReplyTo);
}
