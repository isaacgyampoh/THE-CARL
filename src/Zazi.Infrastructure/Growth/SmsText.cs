using System.Text;

namespace Zazi.Infrastructure.Growth;

/// <summary>Text that goes out by SMS.</summary>
public static class SmsText
{
    /// <summary>
    /// Plain ASCII, so a message is one GSM SMS rather than a Unicode one that costs double and
    /// holds less than half. A business called "Ama's Shöp ✨" is sent as "Ama's Shop".
    /// </summary>
    public static string Plain(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (c is >= ' ' and < (char)127 && c is not ('[' or ']' or '{' or '}' or '\\' or '^' or '~' or '|' or '`'))
            {
                builder.Append(c);
            }
        }

        var plain = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return plain.Length <= maxLength ? plain : plain[..maxLength].TrimEnd();
    }
}
