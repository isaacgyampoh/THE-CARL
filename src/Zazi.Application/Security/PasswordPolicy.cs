namespace Zazi.Application.Security;

/// <summary>
/// What counts as an acceptable password, in one place.
/// </summary>
/// <remarks>
/// Extracted from <c>AuthService</c> when self-service signup appeared, because the alternative
/// was a second copy of the rules. Two copies of a password policy do not stay identical, and
/// the way they diverge is silent: the weaker path simply starts accepting what the other
/// refuses, and nothing reports it.
/// </remarks>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    /// <summary>How many of upper, lower, digit and symbol a password must draw on.</summary>
    public const int RequiredCharacterCategories = 3;

    /// <summary>The rule, phrased for someone about to choose a password.</summary>
    public const string Requirement =
        "At least 12 characters, combining at least three of: uppercase, lowercase, digits, symbols.";

    /// <summary>Returns the reason a password is unacceptable, or null if it is fine.</summary>
    public static string? Describe(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumLength)
        {
            return $"Password must be at least {MinimumLength} characters long.";
        }

        var categories = 0;
        if (password.Any(char.IsUpper)) categories++;
        if (password.Any(char.IsLower)) categories++;
        if (password.Any(char.IsDigit)) categories++;
        if (password.Any(c => !char.IsLetterOrDigit(c))) categories++;

        return categories < RequiredCharacterCategories
            ? "Password must combine at least three of: uppercase, lowercase, digits, symbols."
            : null;
    }

    /// <summary>Throws <see cref="ArgumentException"/> if the password is unacceptable.</summary>
    public static void Validate(string? password, string parameterName = "password")
    {
        if (Describe(password) is { } reason)
        {
            throw new ArgumentException(reason, parameterName);
        }
    }
}
