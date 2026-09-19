using Microsoft.Extensions.Logging;
using Zazi.Application.Email;

namespace Zazi.Infrastructure.Email;

/// <summary>
/// Writes the message to the log instead of sending it.
/// </summary>
/// <remarks>
/// <para>
/// So that a developer can run the whole signup flow — including clicking the verification link,
/// which is in the text body — without a provider account, and so that the integration tests
/// never touch the network.
/// </para>
/// <para>
/// <b>This logs the full body, including one-time verification links.</b> That is the point of it
/// in development and unacceptable anywhere else, which is why
/// <see cref="EmailServiceCollectionExtensions.AddZaziEmail"/> refuses to register it when the
/// provider is configured for a real one, and refuses to leave the provider unconfigured outside
/// Development.
/// </para>
/// </remarks>
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger)
    {
        _logger = logger;
    }

    public Task<EmailResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogWarning(
            "Email NOT SENT (no provider configured). To: {Recipient}  Subject: {Subject}\n{Body}",
            message.ToAddress,
            message.Subject,
            message.TextBody);

        // Reported as sent. The caller's job is to react to delivery failure, and in development
        // there is no failure to react to — a false failure here would send anyone testing the
        // signup flow chasing an error that only exists because they have no API key.
        return Task.FromResult(EmailResult.Success());
    }
}
