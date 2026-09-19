using Zazi.Application.Email;

namespace Zazi.Infrastructure.Email;

/// <summary>
/// The Resend API key, held on its own.
/// </summary>
/// <remarks>
/// <para>
/// A dedicated type rather than a property on <see cref="EmailOptions"/>, so that the one object
/// in the container holding the credential is one whose only purpose is to hold it. Nothing binds
/// it from configuration, nothing serialises it, and <see cref="ToString"/> is overridden because
/// the default would print it into any log line that interpolated the object.
/// </para>
/// <para>
/// Read from the environment exactly once, at startup, by
/// <see cref="EmailServiceCollectionExtensions.AddZaziEmail"/>.
/// </para>
/// </remarks>
public sealed class ResendCredential
{
    public ResendCredential(string apiKey)
    {
        ApiKey = apiKey ?? string.Empty;
    }

    public string ApiKey { get; }

    public bool IsPresent => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Reads the credential from the process environment, and from nowhere else.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>IConfiguration</c>. Configuration would also match a key placed in
    /// <c>appsettings.json</c> or a user-secrets file, and a credential that can be committed
    /// eventually is.
    /// </remarks>
    public static ResendCredential FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(EmailOptions.ApiKeyEnvironmentVariable) ?? string.Empty);

    /// <summary>Never the key. See the remarks on the type.</summary>
    public override string ToString() => IsPresent ? "ResendCredential(present)" : "ResendCredential(absent)";
}
