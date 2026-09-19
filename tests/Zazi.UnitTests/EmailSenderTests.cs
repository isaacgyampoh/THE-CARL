using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zazi.Application.Email;
using Zazi.Infrastructure.Email;

namespace Zazi.UnitTests;

/// <summary>
/// The email transport, and in particular the two things about it that are not cosmetic: that
/// the credential cannot escape into a log or a response, and that a failure to send is reported
/// as a failure rather than swallowed.
/// </summary>
public class EmailSenderTests
{
    /// <summary>A key shaped like a real one, so the redaction pattern is exercised honestly.</summary>
    private const string FakeApiKey = "re_TestKey_0123456789abcdefghijklmnop";

    private static EmailOptions Options() => new()
    {
        Provider = EmailProvider.Resend,
        FromAddress = "no-reply@getzazi.com",
        FromName = "Zazi",
        TimeoutSeconds = 5
    };

    private static EmailMessage Message() => new(
        ToAddress: "ama.owusu@example.com",
        Subject: "Verify your Zazi account",
        HtmlBody: "<p>Verify</p>",
        TextBody: "Verify");

    private static ResendEmailSender Sender(
        StubHandler handler,
        out CapturingLogger log,
        EmailOptions? options = null,
        string apiKey = FakeApiKey)
    {
        options ??= Options();
        log = new CapturingLogger();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.resend.com/"),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };

        return new ResendEmailSender(
            http,
            Microsoft.Extensions.Options.Options.Create(options),
            new ResendCredential(apiKey),
            log);
    }

    // ─── The happy path, and what the request actually looks like ────────────

    [Fact]
    public async Task Successful_send_is_reported_as_sent()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK, """{"id":"abc-123"}""");

        var result = await Sender(handler, out _).SendAsync(Message());

        Assert.True(result.Sent);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Credential_travels_in_the_authorization_header_and_nowhere_else()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK, """{"id":"abc-123"}""");

        await Sender(handler, out _).SendAsync(Message());

        var request = Assert.Single(handler.Requests);

        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(FakeApiKey, request.AuthorizationParameter);

        // The point of this assertion: a key in the URL lands in every proxy and access log
        // between here and the provider, and a key in the body can be echoed back by an error
        // response. Neither can happen while both of these hold.
        Assert.DoesNotContain(FakeApiKey, request.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeApiKey, request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_carries_the_configured_sender_recipient_and_both_bodies()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK, """{"id":"abc-123"}""");

        await Sender(handler, out _).SendAsync(Message());

        var body = Assert.Single(handler.Requests).Body;

        Assert.Contains("\"from\":\"Zazi \\u003Cno-reply@getzazi.com\\u003E\"", body, StringComparison.Ordinal);
        Assert.Contains("ama.owusu@example.com", body, StringComparison.Ordinal);
        Assert.Contains("\"html\":", body, StringComparison.Ordinal);
        Assert.Contains("\"text\":", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Idempotency_key_is_sent_when_supplied_and_omitted_when_not()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK, """{"id":"abc-123"}""");
        var sender = Sender(handler, out _);

        await sender.SendAsync(Message() with { IdempotencyKey = "signup-verify-42" });
        await sender.SendAsync(Message());

        Assert.Equal("signup-verify-42", handler.Requests[0].IdempotencyKey);
        Assert.Null(handler.Requests[1].IdempotencyKey);
    }

    // ─── Failure, classified ─────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Provider_trouble_is_reported_as_transient(HttpStatusCode status)
    {
        var handler = StubHandler.Returning(status, """{"message":"try again"}""");

        var result = await Sender(handler, out _).SendAsync(Message());

        Assert.False(result.Sent);
        Assert.True(result.Transient);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_rejected_message_is_reported_as_permanent(HttpStatusCode status)
    {
        var handler = StubHandler.Returning(status, """{"message":"The from domain is not verified."}""");

        var result = await Sender(handler, out _).SendAsync(Message());

        Assert.False(result.Sent);
        Assert.False(result.Transient);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_credential_is_permanent_and_logged_as_an_error(HttpStatusCode status)
    {
        var handler = StubHandler.Returning(status, """{"message":"API key is invalid"}""");

        var result = await Sender(handler, out var log).SendAsync(Message());

        Assert.False(result.Sent);
        Assert.False(result.Transient);
        // An operator has to be able to find this. A wrong key is the deployment's own fault
        // and no amount of retrying fixes it.
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task An_unreachable_provider_is_transient_rather_than_an_exception()
    {
        var handler = StubHandler.Throwing(new HttpRequestException("no such host"));

        var result = await Sender(handler, out _).SendAsync(Message());

        Assert.False(result.Sent);
        Assert.True(result.Transient);
    }

    [Fact]
    public async Task A_provider_timeout_is_transient_rather_than_an_exception()
    {
        var options = Options();
        options.TimeoutSeconds = 1;
        var handler = StubHandler.Delaying(TimeSpan.FromSeconds(30));

        var result = await Sender(handler, out _, options).SendAsync(Message());

        Assert.False(result.Sent);
        Assert.True(result.Transient);
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_reported_as_a_delivery_failure()
    {
        // The distinction matters: the caller walking away is not evidence about the provider,
        // and recording it as a send failure would mean retrying something nobody asked for.
        var handler = StubHandler.Delaying(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sender(handler, out _).SendAsync(Message(), cts.Token));
    }

    [Fact]
    public async Task A_missing_credential_fails_without_calling_the_provider()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK, "{}");

        var result = await Sender(handler, out var log, apiKey: "").SendAsync(Message());

        Assert.False(result.Sent);
        Assert.False(result.Transient);
        Assert.Empty(handler.Requests);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
    }

    // ─── Nothing secret reaches the log, and nothing personal reaches it in full ──

    [Fact]
    public async Task No_log_line_from_a_failed_send_contains_the_credential()
    {
        // The provider echoing the key back is not something Resend does. It is something a
        // misconfigured proxy in front of it could do, and the log is the wrong place to find out.
        var handler = StubHandler.Returning(
            HttpStatusCode.Unauthorized,
            $$"""{"message":"key {{FakeApiKey}} is invalid"}""");

        var result = await Sender(handler, out var log).SendAsync(Message());

        Assert.False(result.Sent);
        Assert.All(log.Entries, e =>
            Assert.DoesNotContain(FakeApiKey, e.Message, StringComparison.Ordinal));
        Assert.DoesNotContain(FakeApiKey, result.FailureReason ?? "", StringComparison.Ordinal);
        Assert.Contains(log.Entries, e => e.Message.Contains("[redacted]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recipients_are_masked_in_the_log()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK, """{"id":"abc"}""");

        await Sender(handler, out var log).SendAsync(Message());

        Assert.All(log.Entries, e =>
            Assert.DoesNotContain("ama.owusu@example.com", e.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ama.owusu@example.com", "am***@example.com")]
    [InlineData("a@example.com", "a***@example.com")]
    // A two-character local part shown in full is the whole address, so it gets one character.
    [InlineData("ab@example.com", "a***@example.com")]
    [InlineData("not-an-address", "***")]
    [InlineData("", "(none)")]
    public void Masking_keeps_the_domain_and_drops_the_person(string input, string expected) =>
        Assert.Equal(expected, EmailLogSafety.MaskRecipient(input));

    [Theory]
    [InlineData("key re_abcdefgh12345 rejected", "key [redacted] rejected")]
    [InlineData("nothing sensitive here", "nothing sensitive here")]
    [InlineData("re_short", "re_short")] // below the length threshold; not key-shaped
    public void Redaction_removes_anything_shaped_like_a_key(string input, string expected) =>
        Assert.Equal(expected, EmailLogSafety.Redact(input));

    // ─── Test doubles ────────────────────────────────────────────────────────

    private sealed record CapturedRequest(
        string Uri,
        string Body,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? IdempotencyKey);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        private StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public List<CapturedRequest> Requests { get; } = new();

        public static StubHandler Returning(HttpStatusCode status, string body) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }));

        public static StubHandler Throwing(Exception exception) =>
            new((_, _) => Task.FromException<HttpResponseMessage>(exception));

        public static StubHandler Delaying(TimeSpan delay) =>
            new(async (_, token) =>
            {
                await Task.Delay(delay, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new CapturedRequest(
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.FirstOrDefault()
                    : null));

            return await _respond(request, cancellationToken);
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<ResendEmailSender>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
