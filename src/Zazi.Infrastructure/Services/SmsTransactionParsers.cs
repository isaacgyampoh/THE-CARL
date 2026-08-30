using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Zazi.Application;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

public abstract class BaseSmsTransactionParser : ISmsTransactionParser
{
    public abstract string ProviderName { get; }

    public virtual bool CanHandle(string provider, string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(rawMessage))
        {
            return false;
        }

        var normalizedProvider = (provider ?? string.Empty).Trim();
        var message = (rawMessage ?? string.Empty).ToUpperInvariant();
        return normalizedProvider.Contains(ProviderName, StringComparison.OrdinalIgnoreCase)
            || message.Contains(ProviderName, StringComparison.OrdinalIgnoreCase);
    }

    public SmsParsedTransaction Parse(string rawMessage, string provider, string? sourcePhoneNumber, Guid? deviceId, Guid? branchId, Guid organizationId, Guid? sourceDeviceId)
    {
        var normalizedText = NormalizeMessage(rawMessage);
        var providerName = string.IsNullOrWhiteSpace(provider) ? ProviderName : provider;
        var amount = TryParseAmount(normalizedText);
        var customerPhone = TryExtractPhone(normalizedText);
        var reference = TryExtractReference(normalizedText);
        var transactionType = DetermineTransactionType(normalizedText);

        // Confidence and the auto-post bar are decided by SmsEvidencePolicy, the single
        // authority shared conceptually with the Android client. IsValid means "complete
        // enough to post", not merely "something was extracted": a truncated message that
        // yields a type and an amount but no provider reference is NOT valid for posting.
        var confidence = ScoreConfidence(transactionType, amount, reference);
        var isValid = confidence >= SmsEvidencePolicy.MinimumAutoPostConfidence;
        var fingerprint = ComputeHash(rawMessage);

        return new SmsParsedTransaction(
            providerName,
            providerName,
            transactionType,
            amount,
            customerPhone ?? sourcePhoneNumber,
            reference,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            confidence,
            "v1.0",
            isValid,
            fingerprint,
            "SMS",
            deviceId,
            sourceDeviceId);
    }

    protected abstract string DetermineTransactionType(string normalizedText);

    /// <summary>
    /// Confidence for this parser. Provider parsers defer to <see cref="SmsEvidencePolicy"/>;
    /// the generic fallback overrides this to stay permanently below the auto-post bar.
    /// </summary>
    protected virtual decimal ScoreConfidence(string transactionType, decimal amount, string? reference) =>
        SmsEvidencePolicy.ScoreProviderEvidence(transactionType, amount, reference);

    protected static string NormalizeMessage(string text)
    {
        return Regex.Replace(text.Trim(), @"\s+", " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    /// <summary>
    /// Extracts the transaction amount.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The currency marker is <b>mandatory</b>. It was previously optional, which meant the
    /// pattern matched any bare number in the message — a digit inside a reference, a date,
    /// or a phone number could be read as the amount.
    /// </para>
    /// <para>
    /// Thousands separators are stripped rather than translated to a decimal point. The
    /// previous implementation replaced every comma with a period, so "GHS 1,250.00" matched
    /// only "1,25" and parsed as <b>1.25</b> — understating a transaction by three orders of
    /// magnitude.
    /// </para>
    /// </remarks>
    protected static decimal TryParseAmount(string message)
    {
        for (var match = AmountPattern.Match(message); match.Success; match = match.NextMatch())
        {
            var start = match.Index;
            var lookbehind = Math.Min(BalanceLookbehind, start);
            var preceding = message.Substring(start - lookbehind, lookbehind);

            // A closing balance is not the transaction amount. Taking the first currency
            // figure in the message posts the balance instead — "balance is GHS 12,340.00
            // after Cash In of GHS 500.00" would move twenty-four times the real money.
            if (preceding.Contains("BALANCE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var captured = match.Groups[1].Value;

            // "GHS 1 250.00" matches only "1". Reading that as one cedi understates the
            // transaction by three orders of magnitude, and it carries a real reference so
            // nothing else would stop it posting. The grouping is genuinely ambiguous —
            // a space-separated thousands group and two adjacent numbers look identical —
            // so this refuses to choose and lets SmsEvidencePolicy hold it for review.
            var tail = message[(match.Index + match.Length)..];
            if (!captured.Contains('.') && SplitNumberTail.IsMatch(tail))
            {
                return 0m;
            }

            var raw = captured.Replace(",", string.Empty);
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0m;
        }

        return 0m;
    }

    /// <summary>Whether the message reports a reversal, rather than merely mentioning one.</summary>
    /// <remarks>
    /// A cash-in whose footer reads "call 100 to report a reversal" is a cash-in. Matching the
    /// bare word sent every such message to review.
    /// </remarks>
    protected static bool MentionsReversal(string text) =>
        text.Contains("REVERSAL OF", StringComparison.OrdinalIgnoreCase)
        || text.Contains("REVERSED", StringComparison.OrdinalIgnoreCase);

    protected static string? TryExtractPhone(string message)
    {
        var match = Regex.Match(message, @"(?:\+?233|0)\d{9,10}");
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// Extracts the provider's transaction reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Label-anchored only. The previous implementation fell back to
    /// <c>([A-Z0-9]{8,20})</c>, which matches any alphanumeric run — a phone number, a date,
    /// a bundle code. That fallback made a reference appear to be present on messages that
    /// had none, which would defeat the auto-post bar entirely.
    /// </para>
    /// <para>
    /// References legitimately contain dots (MP240815.1201.A00001), so the pattern admits
    /// them and then trims trailing sentence punctuation.
    /// </para>
    /// </remarks>
    protected static string? TryExtractReference(string message)
    {
        var match = ReferencePattern.Match(message);
        if (!match.Success)
        {
            return null;
        }

        var captured = match.Groups[1].Value.TrimEnd('.', '-', ',').ToUpperInvariant();
        return string.IsNullOrWhiteSpace(captured) ? null : captured;
    }

    /// <summary>How far back to look for a balance label before a currency figure.</summary>
    private const int BalanceLookbehind = 24;

    /// <summary>A further number immediately after the match, i.e. the grouping is ambiguous.</summary>
    private static readonly Regex SplitNumberTail = new(@"^\s+[0-9]", RegexOptions.Compiled);

    /// <summary>Currency marker is required; thousands separators permitted.</summary>
    private static readonly Regex AmountPattern = new(
        @"(?:GHS|GH¢|GHC|GH₵|₵|CEDIS)\s*([0-9][0-9,]*(?:\.[0-9]{1,2})?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Label-anchored reference. No catch-all fallback, by design.</summary>
    private static readonly Regex ReferencePattern = new(
        @"(?:REF|REFERENCE|TRANSACTION ID|TXN ID|TRANS\. ID)[:.\s]*([A-Z0-9][A-Z0-9.-]{3,39}?)(?=[\s,]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    protected static string ComputeHash(string raw)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes((raw ?? string.Empty).Trim());
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }
}

public sealed class MtnSmsParser : BaseSmsTransactionParser
{
    public override string ProviderName => "MTN";

    public override bool CanHandle(string provider, string rawMessage)
    {
        var upperProvider = (provider ?? string.Empty).ToUpperInvariant();
        var message = (rawMessage ?? string.Empty).ToUpperInvariant();
        return upperProvider.Contains("MTN") || message.Contains("MTN") || message.Contains("MOMO");
    }

    protected override string DetermineTransactionType(string normalizedText)
    {
        var text = normalizedText.ToUpperInvariant();

        // Order matters. REVERSAL and COMMISSION come first because their wording contains
        // words the movement patterns also match: "You have received Commission of GHS 12.75"
        // matches RECEIVED and would otherwise post as a cash-in, moving cash up and float
        // down when a commission only credits float.
        if (MentionsReversal(text)) return "REVERSAL";
        if (text.Contains("COMMISSION")) return "COMMISSION";
        if (text.Contains("CASH IN") || text.Contains("CASH-IN")) return "CASH_IN";
        if (text.Contains("CASH OUT") || text.Contains("CASH-OUT")) return "CASH_OUT";
        if (text.Contains("DEPOSIT") || text.Contains("RECEIVED FROM") || text.Contains("MOMO CREDIT")) return "CASH_IN";
        if (text.Contains("WITHDRAW") || text.Contains("PAID TO") || text.Contains("PAY OUT")) return "CASH_OUT";
        if (text.Contains("TRANSFER") || text.Contains("TRANSFERRED") || text.Contains("SENT TO")) return "TRANSFER";
        return "UNKNOWN";
    }
}

public sealed class AirtelTigoSmsParser : BaseSmsTransactionParser
{
    public override string ProviderName => "AIRTELTIGO";

    public override bool CanHandle(string provider, string rawMessage)
    {
        var upperProvider = (provider ?? string.Empty).ToUpperInvariant();
        var message = (rawMessage ?? string.Empty).ToUpperInvariant();
        return upperProvider.Contains("AIRTEL") || upperProvider.Contains("ATL") || message.Contains("AIRTELTIGO") || message.Contains("ATL");
    }

    protected override string DetermineTransactionType(string normalizedText)
    {
        var text = normalizedText.ToUpperInvariant();
        if (MentionsReversal(text)) return "REVERSAL";
        if (text.Contains("COMMISSION")) return "COMMISSION";
        if (text.Contains("CASH IN") || text.Contains("CASH-IN") || text.Contains("DEPOSIT")) return "CASH_IN";
        if (text.Contains("CASH OUT") || text.Contains("CASH-OUT") || text.Contains("WITHDRAW")) return "CASH_OUT";
        if (text.Contains("TRANSFER") || text.Contains("SENT TO")) return "TRANSFER";
        return "UNKNOWN";
    }
}

public sealed class TelecelSmsParser : BaseSmsTransactionParser
{
    public override string ProviderName => "TELECEL";

    public override bool CanHandle(string provider, string rawMessage)
    {
        var upperProvider = (provider ?? string.Empty).ToUpperInvariant();
        var message = (rawMessage ?? string.Empty).ToUpperInvariant();
        return upperProvider.Contains("TELECEL") || message.Contains("TELECEL");
    }

    protected override string DetermineTransactionType(string normalizedText)
    {
        var text = normalizedText.ToUpperInvariant();
        if (MentionsReversal(text)) return "REVERSAL";
        if (text.Contains("COMMISSION")) return "COMMISSION";
        if (text.Contains("DEPOSIT") || text.Contains("CASH IN") || text.Contains("TOP UP")) return "CASH_IN";
        if (text.Contains("WITHDRAW") || text.Contains("CASH OUT")) return "CASH_OUT";
        if (text.Contains("TRANSFER")) return "TRANSFER";
        return "UNKNOWN";
    }
}

public sealed class GenericSmsParser : BaseSmsTransactionParser
{
    public override string ProviderName => "UNKNOWN";

    public override bool CanHandle(string provider, string rawMessage)
    {
        return true;
    }

    /// <summary>
    /// Fixed below the auto-post bar regardless of what was extracted. An unrecognised
    /// template is precisely where a confident guess is most dangerous, so generic results
    /// always reach a person.
    /// </summary>
    protected override decimal ScoreConfidence(string transactionType, decimal amount, string? reference) =>
        SmsEvidencePolicy.ScoreGenericEvidence();

    protected override string DetermineTransactionType(string normalizedText)
    {
        // The generic parser classifies for review only; its confidence is capped below the
        // auto-post bar regardless of what it decides here.
        var text = normalizedText.ToUpperInvariant();
        if (MentionsReversal(text)) return "REVERSAL";
        if (text.Contains("COMMISSION")) return "COMMISSION";
        if (text.Contains("DEPOSIT") || text.Contains("CASH IN")) return "CASH_IN";
        if (text.Contains("WITHDRAW") || text.Contains("CASH OUT")) return "CASH_OUT";
        if (text.Contains("TRANSFER")) return "TRANSFER";
        return "UNKNOWN";
    }
}
