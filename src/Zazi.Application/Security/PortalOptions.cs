namespace Zazi.Application.Security;

/// <summary>
/// Facts about how the portal is reached from outside.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>SignUpOptions</c>, where the public URL originally lived, because it is not
/// a signup setting. Password reset needs the same URL and must work on deployments that never
/// open signup at all — leaving it on the signup options would have meant a deployment with
/// signup disabled could not send a reset link.
/// </para>
/// <para>
/// Every link Zazi emails is built from here and never from the incoming request. A URL
/// assembled from the <c>Host</c> header is a URL the caller chooses, and these particular
/// links either activate an account or change its password.
/// </para>
/// </remarks>
public sealed class PortalOptions
{
    public const string SectionName = "Portal";

    /// <summary>The public origin of the dashboard, for example <c>https://app.getzazi.com</c>.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Whether links can be built at all. Features that email links check this.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicBaseUrl);

    public string SignInUrl() => Absolute("/sign-in");

    public string VerifyEmailUrl(string token) => Absolute($"/verify-email?token={Uri.EscapeDataString(token)}");

    public string ResetPasswordUrl(string token) => Absolute($"/reset-password?token={Uri.EscapeDataString(token)}");

    public string ForgotPasswordUrl() => Absolute("/forgot-password");

    private string Absolute(string path) => $"{PublicBaseUrl.TrimEnd('/')}{path}";

    /// <summary>
    /// Throws when a URL is present but unusable.
    /// </summary>
    /// <remarks>
    /// An absent URL is permitted here and refused by the individual features that need one, so
    /// that adding this setting does not stop an existing deployment from starting. A malformed
    /// one is always an error: it would produce links that look right and go nowhere.
    /// </remarks>
    public void Validate()
    {
        if (!IsConfigured)
        {
            return;
        }

        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"{SectionName}:PublicBaseUrl must be an absolute http or https URL, for example "
                + $"https://app.getzazi.com. It is currently '{PublicBaseUrl}'.");
        }
    }
}
