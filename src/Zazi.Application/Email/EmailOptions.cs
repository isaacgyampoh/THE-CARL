namespace Zazi.Application.Email;

/// <summary>Which sender is wired up.</summary>
public enum EmailProvider
{
    /// <summary>
    /// Nothing is sent. The message is written to the log so a developer can follow a
    /// verification link without a provider account.
    /// </summary>
    None = 0,

    /// <summary>Delivery through Resend.</summary>
    Resend = 1,
}

/// <summary>
/// Email configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no API key on this type.</b> The key is read once, directly from
/// the <c>RESEND_API_KEY</c> environment variable, and held privately by the sender. Binding it
/// here would mean it could be supplied from <c>appsettings.json</c> — a committed file — and
/// would put it inside an object that anything holding <c>IOptions&lt;EmailOptions&gt;</c> could
/// read, serialise into a diagnostics page, or write to a log. Neither is worth the convenience.
/// </para>
/// <para>
/// What is here is the part that is not secret and does belong in configuration: who the mail is
/// from, and whether a deployment sends at all.
/// </para>
/// </remarks>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>The environment variable the Resend credential is read from, and nowhere else.</summary>
    public const string ApiKeyEnvironmentVariable = "RESEND_API_KEY";

    public EmailProvider Provider { get; set; } = EmailProvider.None;

    /// <summary>
    /// The envelope sender. Must be on a domain verified with the provider, or every send is
    /// rejected.
    /// </summary>
    public string FromAddress { get; set; } = "no-reply@getzazi.com";

    public string FromName { get; set; } = "Zazi";

    /// <summary>Where replies go, when a message invites one. Unset means replies go nowhere useful.</summary>
    public string? ReplyToAddress { get; set; }

    /// <summary>
    /// How long to wait on the provider before giving up.
    /// </summary>
    /// <remarks>
    /// Short on purpose. A person is waiting on a signup response while this runs, and an
    /// unsent email is a better outcome than a request that appears to hang.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>The From header as the provider wants it.</summary>
    public string FormattedFrom =>
        string.IsNullOrWhiteSpace(FromName) ? FromAddress : $"{FromName} <{FromAddress}>";

    /// <summary>
    /// Throws when the configuration cannot send. Called at startup so a misconfigured
    /// deployment fails while someone is watching, rather than on the first person to sign up.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FromAddress) || !FromAddress.Contains('@'))
        {
            throw new InvalidOperationException(
                $"{SectionName}:FromAddress must be an email address. It is the envelope sender " +
                "and must be on a domain verified with the email provider.");
        }

        if (FromName.Contains('<') || FromName.Contains('>') ||
            FromAddress.Contains('<') || FromAddress.Contains('>') ||
            FromName.Contains('\n') || FromName.Contains('\r') ||
            FromAddress.Contains('\n') || FromAddress.Contains('\r'))
        {
            throw new InvalidOperationException(
                $"{SectionName}:FromName and {SectionName}:FromAddress must not contain angle " +
                "brackets or line breaks; the sender is assembled into a header from them.");
        }

        if (TimeoutSeconds is <= 0 or > 120)
        {
            throw new InvalidOperationException(
                $"{SectionName}:TimeoutSeconds must be between 1 and 120.");
        }
    }
}
