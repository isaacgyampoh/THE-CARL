using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Zazi.Domain;

/// <summary>
/// Deterministic fingerprint of an observed transaction event, used for duplicate
/// detection without depending on retaining raw SMS text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm:</b> SHA-256 over a canonical, version-prefixed, newline-delimited field
/// list; rendered as lowercase hex.
/// </para>
/// <para>
/// <b>Why not hash the raw message:</b> providers pad, re-wrap and re-word templates
/// without changing the underlying financial event. Hashing raw text makes the same event
/// fingerprint differently after a cosmetic template change, which produces duplicate
/// ledger entries. Hashing the canonicalised <i>extracted</i> fields is stable across
/// template changes.
/// </para>
/// <para>
/// <b>Retention:</b> the fingerprint is derived from the fields, so raw SMS text can be
/// discarded on its retention schedule while duplicate detection keeps working.
/// </para>
/// </remarks>
public static class EvidenceFingerprint
{
    /// <summary>
    /// Fingerprint scheme version. Bump when canonicalisation changes.
    /// Stored alongside each fingerprint so historical values stay interpretable and are
    /// never compared against values produced by a different scheme.
    /// </summary>
    public const string Version = "v1";

    private const char FieldSeparator = '\n';

    /// <summary>
    /// Computes the canonical fingerprint of an observed event.
    /// </summary>
    /// <param name="organizationId">Tenant boundary; keeps fingerprints from colliding across tenants.</param>
    /// <param name="provider">Network provider, e.g. MTN.</param>
    /// <param name="transactionType">Classified type.</param>
    /// <param name="amount">Transaction magnitude.</param>
    /// <param name="providerReference">Provider's own reference, when present.</param>
    /// <param name="customerPhone">Counterparty MSISDN, when present.</param>
    /// <param name="occurredAtUtc">
    /// Event time, truncated to the minute. Providers report seconds inconsistently between
    /// the message body and delivery metadata, and a one-second difference must not defeat
    /// duplicate detection.
    /// </param>
    public static string Compute(
        Guid organizationId,
        string provider,
        TransactionType transactionType,
        decimal amount,
        string? providerReference,
        string? customerPhone,
        DateTimeOffset occurredAtUtc)
    {
        var canonical = string.Join(FieldSeparator,
            Version,
            organizationId.ToString("D"),
            NormalizeToken(provider),
            transactionType.ToString().ToUpperInvariant(),
            NormalizeAmount(amount),
            NormalizeReference(providerReference),
            NormalizeMsisdn(customerPhone),
            occurredAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Hash of the raw message, retained separately from the canonical fingerprint so an
    /// exact byte-for-byte redelivery is detectable even before parsing succeeds.
    /// </summary>
    public static string ComputeRawHash(string rawMessage)
    {
        var collapsed = CollapseWhitespace(rawMessage ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(collapsed));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Uppercase, whitespace-collapsed, punctuation-free token.</summary>
    private static string NormalizeToken(string? value) =>
        CollapseWhitespace(value ?? string.Empty).ToUpperInvariant();

    /// <summary>
    /// Fixed-scale decimal string. Prevents "50", "50.0" and "50.00" fingerprinting
    /// differently for the same amount.
    /// </summary>
    private static string NormalizeAmount(decimal amount) =>
        Math.Round(amount, LedgerPolicy.StorageScale, MidpointRounding.AwayFromZero)
            .ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>Uppercase alphanumerics only: providers vary separators between templates.</summary>
    private static string NormalizeReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(reference.Length);
        foreach (var character in reference)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Ghanaian MSISDN reduced to its national significant number, so +233241234567,
    /// 233241234567 and 0241234567 all fingerprint identically.
    /// </summary>
    private static string NormalizeMsisdn(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return string.Empty;
        }

        var digits = new StringBuilder(phone.Length);
        foreach (var character in phone)
        {
            if (char.IsDigit(character))
            {
                digits.Append(character);
            }
        }

        var value = digits.ToString();

        if (value.StartsWith("233", StringComparison.Ordinal) && value.Length >= 12)
        {
            value = value[3..];
        }
        else if (value.StartsWith('0') && value.Length >= 10)
        {
            value = value[1..];
        }

        return value;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;

        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }
            }
            else
            {
                builder.Append(character);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }
}
