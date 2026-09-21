using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zazi.Application;
using Zazi.Application.Keypad;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure.Services;

namespace Zazi.Infrastructure.Keypad;

/// <summary>
/// Zazi for keypad phones: everything an agent without a smartphone does, done by SMS.
/// </summary>
/// <remarks>
/// <para>
/// Most Ghanaian MoMo agents work on a keypad phone with no internet. SMS is what that phone can
/// do anywhere, so it is the bridge. An agent:
/// </para>
/// <list type="bullet">
/// <item>links their phone once by texting their activation code — <c>ZAZI ABCD…</c>;</item>
/// <item>records each transaction by forwarding the MoMo confirmation, which is read by the same
/// parsers the handset uses, so the amount, customer and MTN transaction ID come from the
/// network's own message and nothing is typed;</item>
/// <item>or records by hand — <c>CO 50 0244123456</c>, <c>CI 200 0201234567</c>;</item>
/// <item>looks a customer up at the counter — <c>FIND 0244123456</c>;</item>
/// <item>checks the day — <c>TODAY</c>.</item>
/// </list>
/// <para>
/// Every reply is plain GSM text: no ✓, no ₵, no curly quotes. A single character outside that
/// alphabet switches the whole message to Unicode, which cuts a text from 160 characters to 70
/// and roughly doubles what every reply costs.
/// </para>
/// </remarks>
public sealed class KeypadSmsService : IKeypadSmsService
{
    private const int FindLimit = 5;

    private readonly ApplicationDbContext _dbContext;
    private readonly IDeviceEnrollmentService _enrollment;
    private readonly ISmsProcessingService _smsProcessing;
    private readonly ITransactionService _transactions;
    private readonly ISmsSender _sms;
    private readonly ILogger<KeypadSmsService> _logger;
    private readonly TimeProvider _clock;

    public KeypadSmsService(
        ApplicationDbContext dbContext,
        IDeviceEnrollmentService enrollment,
        ISmsProcessingService smsProcessing,
        ITransactionService transactions,
        ISmsSender sms,
        ILogger<KeypadSmsService> logger,
        TimeProvider? clock = null)
    {
        _dbContext = dbContext;
        _enrollment = enrollment;
        _smsProcessing = smsProcessing;
        _transactions = transactions;
        _sms = sms;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<KeypadReply> HandleAsync(InboundSms message, CancellationToken cancellationToken = default)
    {
        var reply = await DecideAsync(message, cancellationToken);

        if (GhanaPhoneNumber.Normalise(message.From) is { } to)
        {
            await _sms.SendAsync(to, reply.Message, cancellationToken);
        }

        // The outcome, never the text: messages carry customers' numbers and amounts.
        _logger.LogInformation("Keypad SMS handled: {Outcome}.", reply.Outcome);
        return reply;
    }

    private async Task<KeypadReply> DecideAsync(InboundSms message, CancellationToken cancellationToken)
    {
        var from = GhanaPhoneNumber.Normalise(message.From);
        var text = (message.Text ?? string.Empty).Trim();

        if (from is null)
        {
            return new KeypadReply(KeypadOutcome.UnknownSender, "Zazi works with Ghanaian mobile numbers only.");
        }

        if (text.Length == 0)
        {
            return Help(KeypadOutcome.NotUnderstood);
        }

        // Linking comes first: it is the only thing an unlinked number may do.
        if (text.StartsWith("ZAZI", StringComparison.OrdinalIgnoreCase))
        {
            return await LinkAsync(from, text, cancellationToken);
        }

        var phone = await FindPhoneAsync(from, cancellationToken);
        if (phone is null)
        {
            return new KeypadReply(
                KeypadOutcome.UnknownSender,
                "Zazi: this number is not linked yet. Ask your business owner for an activation code, then text: ZAZI followed by the code.");
        }

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var command = words[0].ToUpperInvariant();

        return command switch
        {
            "HELP" or "?" or "MENU" => Help(KeypadOutcome.Answered),
            "TODAY" or "TOTAL" or "TOTALS" => new KeypadReply(KeypadOutcome.Answered, await TodayAsync(phone, cancellationToken)),
            "FIND" or "CHECK" => await FindAsync(phone, words, cancellationToken),
            "CO" or "OUT" or "CASHOUT" => await RecordByHandAsync(phone, TransactionType.CashOut, words, message, cancellationToken),
            "CI" or "IN" or "CASHIN" => await RecordByHandAsync(phone, TransactionType.CashIn, words, message, cancellationToken),
            _ => await RecordForwardedAsync(phone, from, text, message, cancellationToken)
        };
    }

    // ─── Linking ─────────────────────────────────────────────────────────────

    private async Task<KeypadReply> LinkAsync(string from, string text, CancellationToken cancellationToken)
    {
        // "ZAZI ABCD-…" or the code itself, which starts ZAZI-. Tried as sent, then without
        // the leading word, so either way of texting it works.
        var candidates = new List<string> { text };
        var afterWord = Regex.Match(text, @"^ZAZI\s+(.+)$", RegexOptions.IgnoreCase);
        if (afterWord.Success)
        {
            candidates.Add(afterWord.Groups[1].Value.Trim());
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var link = await _enrollment.LinkKeypadPhoneAsync(candidate, from, cancellationToken);
                var network = await _dbContext.Devices.AsNoTracking()
                    .Where(d => d.Id == link.DeviceId).Select(d => d.Network).SingleAsync(cancellationToken);

                return new KeypadReply(
                    KeypadOutcome.Linked,
                    $"Zazi: welcome {FirstName(link.WorkerName)}. This phone is linked to {link.OrganizationName}. " +
                    $"Forward every {NetworkName(network)} MoMo message to this number to record it. Text HELP for more.");
            }
            catch (ConflictException)
            {
                return new KeypadReply(KeypadOutcome.Linked, "Zazi: this phone is already linked. Forward MoMo messages to record them.");
            }
            catch (UnauthorizedAccessException)
            {
                // Try the next spelling.
            }
            catch (ArgumentException)
            {
                // Try the next spelling.
            }
        }

        return new KeypadReply(
            KeypadOutcome.LinkRefused,
            "Zazi: that code did not work. Codes are used once and expire. Ask your business owner for a new one.");
    }

    // ─── Recording ───────────────────────────────────────────────────────────

    /// <summary>Currency wording every network's messages use for an amount.</summary>
    private static readonly Regex AmountMarker = new(@"GHS|GH¢|GHC|₵|CEDI", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task<KeypadReply> RecordForwardedAsync(
        LinkedPhone phone,
        string from,
        string text,
        InboundSms message,
        CancellationToken cancellationToken)
    {
        // Not a MoMo message at all — "hello", a question — is answered with what to send, and
        // not stored as evidence of a transaction that never happened.
        if (!AmountMarker.IsMatch(text))
        {
            return Help(KeypadOutcome.NotUnderstood, "Zazi did not understand that. ");
        }

        var body = StripForwardMarkers(text);

        // The network is read from the message first. Forwarding loses the original sender, and
        // many keypad phones hold two SIMs, so a Telecel message forwarded from an MTN number is
        // Telecel. Only when the message names no network is the agent SIM's own used.
        var network = NetworkNamedIn(body) ?? (phone.Network == Networks.Unspecified ? null : phone.Network);

        // The time the transaction happened, when the message says — an agent may forward at
        // the end of the day, and a complaint is about when the customer came, not when the
        // agent caught up. Otherwise the time it reached Zazi.
        var occurredAt = TimeStatedIn(body, message.ReceivedAtUtc);

        var result = await _smsProcessing.ProcessIncomingSmsAsync(
            new SmsCaptureRequest(
                phone.OrganizationId,
                phone.BranchId,
                phone.DeviceId,
                SourcePhoneNumber: from,
                RawMessage: body,
                ProviderHint: network,
                MessageTimestampUtc: occurredAt),
            phone.WorkerId,
            cancellationToken);

        if (result.IsDuplicate)
        {
            return new KeypadReply(KeypadOutcome.AlreadyRecorded, "Zazi: already recorded. Nothing was added twice.");
        }

        var summary = $"{Describe(result.TransactionType)} GHS {result.Amount:N2}" +
                      (result.CustomerPhoneNumber is { } customer && GhanaPhoneNumber.IsValid(customer)
                          ? $", {GhanaPhoneNumber.Display(customer)}"
                          : string.Empty) +
                      $", {NetworkName(result.Network)}, {occurredAt:HH:mm}";

        return result.State == TransactionLifecycleState.Accepted
            ? new KeypadReply(KeypadOutcome.Recorded, $"Zazi OK: {summary}.")
            : new KeypadReply(
                KeypadOutcome.HeldForReview,
                "Zazi: received, but some details were unclear, so it is waiting for your owner to check. " +
                "Your balances are not changed until then.");
    }

    private async Task<KeypadReply> RecordByHandAsync(
        LinkedPhone phone,
        TransactionType type,
        IReadOnlyList<string> words,
        InboundSms message,
        CancellationToken cancellationToken)
    {
        var usage = type == TransactionType.CashOut
            ? "Send: CO amount number, e.g. CO 50 0244123456"
            : "Send: CI amount number, e.g. CI 50 0244123456";

        if (words.Count < 3
            || !decimal.TryParse(words[1].Replace("GHS", "", StringComparison.OrdinalIgnoreCase), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0 || amount > 1_000_000 || decimal.Round(amount, 2) != amount)
        {
            return new KeypadReply(KeypadOutcome.NotUnderstood, "Zazi: check the amount. " + usage);
        }

        // Everything after the amount is the number, however it was spaced — "024 412 3456" is
        // three words — except a network name at the very end.
        var rest = words.Skip(2).ToList();
        string? namedNetwork = null;
        if (rest.Count > 1 && NetworkFromWord(rest[^1]) is { } last)
        {
            namedNetwork = last;
            rest.RemoveAt(rest.Count - 1);
        }

        var customer = GhanaPhoneNumber.Normalise(string.Concat(rest));
        if (customer is null)
        {
            return new KeypadReply(KeypadOutcome.NotUnderstood, "Zazi: check the customer number (10 digits). " + usage);
        }

        var network = namedNetwork ?? phone.Network;
        if (network is null || network == Networks.Unspecified)
        {
            return new KeypadReply(KeypadOutcome.NotUnderstood,
                $"Zazi: add the network at the end, e.g. {words[0].ToUpperInvariant()} {words[1]} {words[2]} MTN");
        }

        // The gateway may deliver the same message twice; an id derived from it makes the second
        // one a no-op rather than a second transaction.
        var clientId = DeterministicClientId(
            "keypad:" + message.From,
            message.ProviderMessageId ?? $"{message.From}|{message.Text}|{message.ReceivedAtUtc:yyyyMMddHHmm}");

        var recorded = await _transactions.CreateTransactionAsync(new CreateTransactionRequest(
            phone.OrganizationId,
            phone.BranchId,
            phone.WorkerId,
            phone.DeviceId,
            network,
            type,
            amount,
            "GHS",
            customer,
            null,
            TransactionSource.Bridge,
            "Recorded by SMS from a keypad phone.",
            ClientTransactionId: clientId), cancellationToken);

        return new KeypadReply(
            KeypadOutcome.Recorded,
            $"Zazi OK: {Describe(type)} GHS {amount:N2}, {GhanaPhoneNumber.Display(customer)}, {NetworkName(network)}, {recorded.TransactionAt:HH:mm}.");
    }

    /// <summary>
    /// A transaction id in the handset's own format — CTX, an 8-hex device tag, 26 base-32
    /// characters — derived from the message rather than random, so the same text delivered twice
    /// yields the same id and the ledger's idempotency catches it.
    /// </summary>
    private static string DeterministicClientId(string deviceKey, string messageKey)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceKey)), 0, 4).ToLowerInvariant();

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(messageKey)).AsSpan(0, 16).ToArray();
        var value = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return $"{ClientTransactionId.Prefix}-{tag}-{new string(chars)}";
    }

    // ─── Answers ─────────────────────────────────────────────────────────────

    private async Task<KeypadReply> FindAsync(LinkedPhone phone, IReadOnlyList<string> words, CancellationToken cancellationToken)
    {
        var customer = words.Count > 1 ? GhanaPhoneNumber.Normalise(string.Concat(words.Skip(1))) : null;
        if (customer is null)
        {
            return new KeypadReply(KeypadOutcome.NotUnderstood, "Zazi: send FIND and the customer's number, e.g. FIND 0244123456");
        }

        var lastNine = customer[1..];
        var rows = await _dbContext.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == phone.OrganizationId
                && t.BranchId == phone.BranchId
                && t.CustomerPhoneNumber != null
                && (t.CustomerPhoneNumber == customer || t.CustomerPhoneNumber.EndsWith(lastNine)))
            .OrderByDescending(t => t.TransactionAtUtc)
            .Take(FindLimit)
            .Select(t => new { t.TransactionAtUtc, t.Type, t.Amount, t.Network })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return new KeypadReply(KeypadOutcome.Answered, $"Zazi: no transactions for {GhanaPhoneNumber.Display(customer)}.");
        }

        var lines = rows.Select(r =>
            $"{r.TransactionAtUtc:d MMM HH:mm} {Short(r.Type)} GHS {r.Amount:N2} {NetworkName(r.Network)}");

        return new KeypadReply(
            KeypadOutcome.Answered,
            $"Zazi {GhanaPhoneNumber.Display(customer)}:\n" + string.Join("\n", lines));
    }

    private async Task<string> TodayAsync(LinkedPhone phone, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        var today = await _dbContext.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == phone.OrganizationId
                && t.AgentId == phone.WorkerId
                && t.TransactionAtUtc >= dayStart)
            .Select(t => new { t.Type, t.Amount })
            .ToListAsync(cancellationToken);

        var cashIn = today.Where(t => t.Type == TransactionType.CashIn).ToList();
        var cashOut = today.Where(t => t.Type == TransactionType.CashOut).ToList();

        var cash = await _dbContext.CashBalances.AsNoTracking()
            .Where(b => b.OrganizationId == phone.OrganizationId && b.AgentId == phone.WorkerId)
            .SumAsync(b => (decimal?)b.CurrentCash, cancellationToken) ?? 0m;

        var floats = await _dbContext.FloatBalances.AsNoTracking()
            .Where(b => b.OrganizationId == phone.OrganizationId && b.AgentId == phone.WorkerId)
            .GroupBy(b => b.Network)
            .Select(g => new { Network = g.Key, Amount = g.Sum(b => b.CurrentFloat) })
            .ToListAsync(cancellationToken);

        var floatText = floats.Count == 0
            ? "no float recorded"
            : string.Join(", ", floats.OrderBy(f => f.Network).Select(f => $"{NetworkName(f.Network)} GHS {f.Amount:N2}"));

        return $"Zazi today {now:d MMM}: {today.Count} transactions. " +
               $"Cash in GHS {cashIn.Sum(t => t.Amount):N2} ({cashIn.Count}). " +
               $"Cash out GHS {cashOut.Sum(t => t.Amount):N2} ({cashOut.Count}). " +
               $"Holding cash GHS {cash:N2}; float {floatText}.";
    }

    public async Task<int> SendDailySummariesAsync(CancellationToken cancellationToken = default)
    {
        var dayStart = new DateTimeOffset(_clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);

        var phones = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.DeviceIdentifier.StartsWith(DeviceEnrollmentService.KeypadIdentifierPrefix)
                && !d.IsRevoked && d.Status == DeviceStatus.Active)
            .Select(d => d.DeviceIdentifier)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var identifier in phones)
        {
            var number = identifier[DeviceEnrollmentService.KeypadIdentifierPrefix.Length..];
            var phone = await FindPhoneAsync(number, cancellationToken);
            if (phone is null)
            {
                continue;
            }

            // Only agents who traded. A nightly text saying "0 transactions" to someone who had
            // the day off is how people learn to ignore the one that matters.
            var traded = await _dbContext.Transactions.AsNoTracking()
                .AnyAsync(t => t.OrganizationId == phone.OrganizationId
                    && t.AgentId == phone.WorkerId
                    && t.TransactionAtUtc >= dayStart, cancellationToken);
            if (!traded)
            {
                continue;
            }

            if (await _sms.SendAsync(number, await TodayAsync(phone, cancellationToken), cancellationToken))
            {
                sent++;
            }
        }

        return sent;
    }

    // ─── The linked phone ────────────────────────────────────────────────────

    private sealed record LinkedPhone(Guid DeviceId, Guid OrganizationId, Guid BranchId, Guid WorkerId, string Network);

    /// <summary>
    /// The active keypad phone for a number, and the worker its latest activation code named.
    /// </summary>
    /// <remarks>
    /// Revoking it from the Team page, or disabling the worker, stops it here: the next text
    /// from that number is answered as unlinked.
    /// </remarks>
    private async Task<LinkedPhone?> FindPhoneAsync(string number, CancellationToken cancellationToken)
    {
        var identifier = DeviceEnrollmentService.KeypadIdentifierPrefix + number;

        var device = await _dbContext.Devices
            .SingleOrDefaultAsync(d => d.DeviceIdentifier == identifier
                && !d.IsRevoked && d.Status == DeviceStatus.Active, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var workerId = await _dbContext.DeviceEnrollmentCodes.AsNoTracking()
            .Where(c => c.RedeemedByDeviceId == device.Id && c.IntendedUserId != null)
            .OrderByDescending(c => c.RedeemedAtUtc)
            .Select(c => c.IntendedUserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (workerId is not { } worker)
        {
            return null;
        }

        var active = await _dbContext.Users.AsNoTracking()
            .AnyAsync(u => u.Id == worker && u.OrganizationId == device.OrganizationId && u.IsActive, cancellationToken);
        if (!active)
        {
            return null;
        }

        device.LastSeenAt = _clock.GetUtcNow();
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new LinkedPhone(device.Id, device.OrganizationId, device.BranchId, worker, device.Network);
    }

    // ─── Words ───────────────────────────────────────────────────────────────

    private static KeypadReply Help(KeypadOutcome outcome, string lead = "") => new(outcome,
        lead + "Zazi: forward any MoMo message to record it. Or send:\n" +
        "CO 50 0244123456 (cash out)\n" +
        "CI 50 0244123456 (cash in)\n" +
        "FIND 0244123456\n" +
        "TODAY");

    /// <summary>"Fwd:", "FW:" and similar, which some phones add in front of a forwarded text.</summary>
    private static string StripForwardMarkers(string text) =>
        Regex.Replace(text, @"^\s*(fwd?|fw)\s*:\s*", string.Empty, RegexOptions.IgnoreCase).Trim();

    private static string? NetworkNamedIn(string text)
    {
        var upper = text.ToUpperInvariant();
        if (upper.Contains("AIRTELTIGO")) return Networks.AirtelTigo;
        if (upper.Contains("TELECEL") || upper.Contains("VODAFONE CASH")) return Networks.Telecel;
        if (upper.Contains("MTN")) return Networks.Mtn;
        return null;
    }

    private static string? NetworkFromWord(string word) => word.ToUpperInvariant() switch
    {
        "MTN" => Networks.Mtn,
        "TELECEL" or "VODA" or "VODAFONE" or "TC" => Networks.Telecel,
        "AT" or "AIRTELTIGO" or "TIGO" or "AIRTEL" => Networks.AirtelTigo,
        _ => null
    };

    /// <summary>
    /// The transaction time a MoMo message states, if it states one within the past week.
    /// </summary>
    private static DateTimeOffset TimeStatedIn(string text, DateTimeOffset received)
    {
        var patterns = new[]
        {
            (@"(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})[ T](?<h>\d{1,2}):(?<n>\d{2})(:(?<s>\d{2}))?", false),
            (@"(?<d>\d{1,2})/(?<m>\d{1,2})/(?<y>\d{2,4})\s+(?<h>\d{1,2}):(?<n>\d{2})(:(?<s>\d{2}))?", false)
        };

        foreach (var (pattern, _) in patterns)
        {
            var match = Regex.Match(text, pattern);
            if (!match.Success)
            {
                continue;
            }

            try
            {
                var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
                if (year < 100) year += 2000;
                var stated = new DateTimeOffset(
                    year,
                    int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture),
                    match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture) : 0,
                    TimeSpan.Zero);

                // Ghana keeps GMT, so the stated time is UTC. Trusted only if plausible: not in
                // the future, and not older than a week.
                if (stated <= received.AddMinutes(5) && stated >= received.AddDays(-7))
                {
                    return stated;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // Not a real date; fall through.
            }
        }

        return received;
    }

    private static string Describe(string type) => type.ToUpperInvariant() switch
    {
        "CASHIN" => "Cash in",
        "CASHOUT" => "Cash out",
        _ => type
    };

    private static string Describe(TransactionType type) => type switch
    {
        TransactionType.CashIn => "Cash in",
        TransactionType.CashOut => "Cash out",
        _ => type.ToString()
    };

    private static string Short(TransactionType type) => type switch
    {
        TransactionType.CashIn => "Cash in",
        TransactionType.CashOut => "Cash out",
        TransactionType.Commission => "Commission",
        TransactionType.Reversal => "Reversal",
        _ => "Other"
    };

    private static string NetworkName(string? network) => network?.ToUpperInvariant() switch
    {
        "MTN" => "MTN",
        "TELECEL" => "Telecel",
        "AIRTELTIGO" => "AirtelTigo",
        _ => "MoMo"
    };

    private static string FirstName(string name) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
}
