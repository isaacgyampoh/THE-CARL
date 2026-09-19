using System.Text.RegularExpressions;

namespace Zazi.Application.Email;

/// <summary>
/// What may be written about an email, as opposed to what is in it.
/// </summary>
/// <remarks>
/// Here rather than on one sender, because the rules are about Zazi's logs and not about any
/// particular provider: a second transport must not get to decide that it masks recipients
/// differently, or that it does not mask them at all.
/// </remarks>
public static class EmailLogSafety
{
    /// <summary>
    /// Anything shaped like a provider API key. Matches on the published <c>re_</c> prefix rather
    /// than on the configured value, so it also catches a key that is not this deployment's — one
    /// echoed by a misconfigured proxy, or pasted somewhere by someone who should not have.
    /// </summary>
    private static readonly Regex ApiKeyPattern = new(
        @"re_[A-Za-z0-9_\-]{8,}",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Removes anything key-shaped from text about to be logged.</summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("re_", StringComparison.Ordinal))
        {
            return value;
        }

        return ApiKeyPattern.Replace(value, "[redacted]");
    }

    /// <summary>
    /// Keeps enough of an address to match it to a support request, and not enough to build a
    /// mailing list out of the log file.
    /// </summary>
    public static string MaskRecipient(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "(none)";
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
        {
            return "***";
        }

        var local = address[..at];
        var domain = address[at..];
        var shown = local.Length <= 2 ? local[..1] : local[..2];
        return shown + "***" + domain;
    }
}
