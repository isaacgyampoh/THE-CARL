namespace Zazi.Application.Security;

/// <summary>
/// Token signing configuration. There is deliberately no default for <see cref="Key"/>:
/// a shipped fallback key means every deployment that forgets to configure one shares a
/// publicly known secret, and anyone can mint valid tokens for any tenant.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Minimum HMAC-SHA256 key length in bytes.</summary>
    public const int MinimumKeyBytes = 32;

    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "zazi";
    public string Audience { get; set; } = "zazi-clients";
    public int AccessTokenMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>
    /// Throws when the configuration cannot safely sign tokens. Called at startup so a
    /// misconfigured deployment fails immediately rather than serving forgeable tokens.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new InvalidOperationException(
                "Jwt:Key is not configured. Set the 'Jwt:Key' configuration value or the " +
                "ZAZI_JWT_KEY environment variable to a random secret of at least " +
                $"{MinimumKeyBytes} bytes. Zazi will not start without one.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(Key) < MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"Jwt:Key must be at least {MinimumKeyBytes} bytes for HMAC-SHA256 signing.");
        }

        if (AccessTokenMinutes is <= 0 or > 1440)
        {
            throw new InvalidOperationException("Jwt:AccessTokenMinutes must be between 1 and 1440.");
        }

        if (RefreshTokenDays is <= 0 or > 365)
        {
            throw new InvalidOperationException("Jwt:RefreshTokenDays must be between 1 and 365.");
        }
    }
}
