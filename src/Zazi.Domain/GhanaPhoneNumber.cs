namespace Zazi.Domain;

/// <summary>
/// A Ghanaian mobile number, in the one spelling Zazi stores and searches by.
/// </summary>
/// <remarks>
/// <para>
/// The customer number is what settles a complaint at the counter — "I came at 11:50 and
/// withdrew fifty cedis" — so it has to be found every time. The same customer arrives as
/// "0244 123 456" typed by hand, "233244123456" lifted from a provider SMS and
/// "+233 24 412 3456" pasted from a contact; stored as given, a search for one misses the
/// other two. Everything is reduced to ten digits with a leading zero before it is kept.
/// </para>
/// <para>
/// Mirrored in Kotlin as <c>app.zazi.core.domain.model.GhanaPhoneNumber</c>. The two must agree,
/// or a number normalised on the handset would be rewritten differently here.
/// </para>
/// </remarks>
public static class GhanaPhoneNumber
{
    /// <summary>
    /// Mobile prefixes in use. Not used to guess the network: numbers have been portable
    /// between networks since 2011.
    /// </summary>
    public static readonly IReadOnlySet<string> MobilePrefixes = new HashSet<string>(StringComparer.Ordinal)
    {
        "020", "023", "024", "025", "026", "027",
        "050", "053", "054", "055", "056", "057", "059"
    };

    /// <summary>The stored form, or null when this is not a Ghanaian mobile number.</summary>
    public static string? Normalise(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());

        string local;
        if (digits.Length == 12 && digits.StartsWith("233", StringComparison.Ordinal))
        {
            local = "0" + digits[3..];
        }
        else if (digits.Length == 13 && digits.StartsWith("2330", StringComparison.Ordinal))
        {
            local = digits[3..];
        }
        else if (digits.Length == 10 && digits[0] == '0')
        {
            local = digits;
        }
        else if (digits.Length == 9 && digits[0] != '0')
        {
            local = "0" + digits;
        }
        else
        {
            return null;
        }

        return MobilePrefixes.Contains(local[..3]) ? local : null;
    }

    public static bool IsValid(string? input) => Normalise(input) is not null;

    /// <summary>
    /// Keeps a number that cannot be normalised exactly as it was, rather than dropping it.
    /// </summary>
    /// <remarks>
    /// For the paths that must never lose data — sync above all. A queued transaction whose
    /// number the parser read oddly is still the agent's record, and refusing it would throw
    /// the whole transaction away over one field.
    /// </remarks>
    public static string? NormaliseOrKeep(string? input) =>
        Normalise(input) ?? (string.IsNullOrWhiteSpace(input) ? null : input.Trim());

    /// <summary>Grouped as people read it back: 024 412 3456.</summary>
    public static string Display(string? input)
    {
        var n = Normalise(input);
        return n is null ? input ?? string.Empty : $"{n[..3]} {n[3..6]} {n[6..]}";
    }

    public const string Requirement =
        "Enter the customer's 10-digit mobile number, for example 024 412 3456.";
}
