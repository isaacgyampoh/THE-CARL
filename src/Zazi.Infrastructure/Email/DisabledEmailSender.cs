using Microsoft.Extensions.Logging;
using Zazi.Application.Email;

namespace Zazi.Infrastructure.Email;

/// <summary>
/// Reports every send as failed, without logging the message.
/// </summary>
/// <remarks>
/// <para>
/// What a non-development deployment gets when no provider is configured. It exists so that
/// shipping the email feature to an environment that has not been given a key is a visible,
/// recoverable failure rather than either of the two alternatives: refusing to start, which takes
/// a working deployment down over a feature it may not use yet, or falling back to
/// <see cref="LoggingEmailSender"/>, which would write one-time verification links into a
/// production log.
/// </para>
/// <para>
/// It reports failure rather than success so that callers surface it. A caller told the mail was
/// sent leaves the person waiting for a message that will never arrive.
/// </para>
/// </remarks>
public sealed class DisabledEmailSender : IEmailSender
{
    private readonly ILogger<DisabledEmailSender> _logger;

    public DisabledEmailSender(ILogger<DisabledEmailSender> logger)
    {
        _logger = logger;
    }

    public Task<EmailResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        // Subject, not body: enough to identify which feature tried to send, without putting the
        // contents of the message into the log.
        _logger.LogError(
            "Email not sent: no email provider is configured for this deployment. "
            + "Set {Section}:Provider=Resend and {Variable} to enable delivery. Subject: {Subject}",
            EmailOptions.SectionName,
            EmailOptions.ApiKeyEnvironmentVariable,
            message.Subject);

        return Task.FromResult(
            EmailResult.PermanentFailure("no email provider is configured for this deployment"));
    }
}
