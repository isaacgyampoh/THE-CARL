using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace Zazi.Infrastructure.Security;

/// <summary>
/// How Zazi turns a password into something it can store.
/// </summary>
/// <remarks>
/// <para>
/// PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte salt, 32-byte key. The iteration count is
/// OWASP's current floor for this construction, and it is the parameter to raise over time
/// rather than the algorithm to swap.
/// </para>
/// <para>
/// Extracted when self-service signup needed to create accounts that <c>AuthService</c> would
/// later have to verify. Two copies of these constants is the kind of duplication that does not
/// announce itself: every account made by one path simply fails to log in through the other,
/// with a wrong-password error and nothing to suggest the password was fine.
/// </para>
/// </remarks>
public static class PasswordHashing
{
    private const int Pbkdf2Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int DerivedKeyBytes = 32;

    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));

    public static string Hash(string password, string salt) =>
        Convert.ToBase64String(KeyDerivation.Pbkdf2(
            password,
            Encoding.UTF8.GetBytes(salt),
            KeyDerivationPrf.HMACSHA256,
            Pbkdf2Iterations,
            DerivedKeyBytes));

    /// <summary>
    /// Constant-time comparison of a candidate password against a stored hash.
    /// </summary>
    /// <remarks>
    /// Fails closed on a blank hash or salt. That is what stops an activation-only worker — who
    /// has neither — from being signed into with any password at all, and it is a security
    /// boundary rather than a tidiness check.
    /// </remarks>
    public static bool Verify(string password, string passwordHash, string salt)
    {
        if (string.IsNullOrWhiteSpace(salt) || string.IsNullOrWhiteSpace(passwordHash))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(passwordHash),
                Convert.FromBase64String(Hash(password, salt)));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
