namespace Zazi.Application.Email;

/// <summary>A transactional email, already rendered.</summary>
/// <remarks>
/// Both bodies are required. A text part is not decoration: mail that arrives as HTML alone
/// scores worse with spam filters, and verification mail that lands in spam is the same as
/// verification mail that was never sent.
/// </remarks>
public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string HtmlBody,
    string TextBody)
{
    /// <summary>
    /// Optional key that makes a repeated send safe.
    /// </summary>
    /// <remarks>
    /// When a send times out, the caller cannot tell whether the provider accepted it. Retrying
    /// with the same key means the person gets one email rather than two — which matters most
    /// for the messages people act on, where a duplicate reads as a second, contradictory event.
    /// </remarks>
    public string? IdempotencyKey { get; init; }
}

/// <summary>What happened when a message was handed to the provider.</summary>
/// <remarks>
/// A result rather than an exception, because the caller nearly always has a decision to make.
/// Signup that creates an account and then fails to send the verification email must not roll
/// the account back — the person exists, they just need the mail resending — and an exception
/// there forces the caller to choose between losing the account and swallowing the error.
/// </remarks>
public sealed record EmailResult(bool Sent, string? FailureReason = null, bool Transient = false)
{
    public static EmailResult Success() => new(true);

    /// <summary>A failure the caller may usefully retry: a timeout, a 429, a provider 5xx.</summary>
    public static EmailResult TransientFailure(string reason) => new(false, reason, Transient: true);

    /// <summary>A failure retrying will not fix: a rejected credential, an unroutable address.</summary>
    public static EmailResult PermanentFailure(string reason) => new(false, reason, Transient: false);
}

/// <summary>
/// Sends transactional email.
/// </summary>
/// <remarks>
/// <para>
/// Server-side only. No implementation of this may be resolved from anything that renders into
/// a browser, and no implementation carries its credential anywhere but the server environment
/// it was configured from.
/// </para>
/// <para>
/// One interface rather than one per message type. What separates a verification email from any
/// later transactional message is its content, not its transport.
/// </para>
/// </remarks>
public interface IEmailSender
{
    /// <summary>
    /// Attempts delivery. Does not throw for provider failures — those come back as a result so
    /// the caller can decide. Throws only for a cancelled <paramref name="cancellationToken"/>.
    /// </summary>
    Task<EmailResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
