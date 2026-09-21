namespace Zazi.Application.Keypad;

/// <summary>
/// A text message that reached Zazi's SMS number.
/// </summary>
/// <remarks>
/// Most Ghanaian mobile money agents work on a keypad phone with no internet. SMS is what
/// that phone can do everywhere, so it is the bridge: an agent links their phone by texting
/// their activation code, then forwards each MoMo confirmation — or types a short command —
/// to Zazi's number. Nothing here assumes a smartphone.
/// </remarks>
public sealed record InboundSms(
    string From,
    string Text,
    DateTimeOffset ReceivedAtUtc,
    string? ProviderMessageId = null);

/// <summary>What Zazi did with a message, and what it texted back.</summary>
public sealed record KeypadReply(KeypadOutcome Outcome, string Message);

public enum KeypadOutcome
{
    /// <summary>The phone was linked to its worker.</summary>
    Linked,

    /// <summary>A transaction was recorded.</summary>
    Recorded,

    /// <summary>The same MoMo message had already been recorded.</summary>
    AlreadyRecorded,

    /// <summary>Received, but not clear enough to post; held for the owner to check.</summary>
    HeldForReview,

    /// <summary>A lookup or summary was answered.</summary>
    Answered,

    /// <summary>The message was not understood; the reply explains what to send.</summary>
    NotUnderstood,

    /// <summary>The number is not linked to anyone.</summary>
    UnknownSender,

    /// <summary>The activation code was not accepted.</summary>
    LinkRefused,

    /// <summary>The agent closed the day with a count.</summary>
    Closed
}

public interface IKeypadSmsService
{
    /// <summary>Handles one inbound message and texts the sender a reply.</summary>
    Task<KeypadReply> HandleAsync(InboundSms message, CancellationToken cancellationToken = default);

    /// <summary>Texts every linked keypad agent who traded today a summary of their day.</summary>
    Task<int> SendDailySummariesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Sends a text message. Implemented per gateway.</summary>
public interface ISmsSender
{
    /// <summary>True when sent, false when the gateway refused or could not be reached.</summary>
    Task<bool> SendAsync(string toPhoneNumber, string message, CancellationToken cancellationToken = default);
}

/// <summary>
/// The SMS gateway. The key is not here: it is read from the server environment only, as the
/// email key is, so it cannot be committed, logged or sent to a browser by accident.
/// </summary>
public sealed class SmsGatewayOptions
{
    public const string SectionName = "Sms";

    /// <summary>"AfricasTalking", "Log" (development: writes messages to the log), or "Disabled".</summary>
    public string Provider { get; set; } = "Disabled";

    /// <summary>The gateway account's username. "sandbox" for Africa's Talking's sandbox.</summary>
    public string? Username { get; set; }

    /// <summary>The sender name or number replies come from, if the gateway assigns one.</summary>
    public string? SenderId { get; set; }

    /// <summary>
    /// Zazi's number agents text, shown to owners on the Team page next to each activation
    /// code — "On a keypad phone, text the code to 0XX XXX XXXX."
    /// </summary>
    public string? InboundNumber { get; set; }

    /// <summary>
    /// A secret the gateway's callback URL must carry. Gateways such as Africa's Talking cannot
    /// sign their callbacks, so without this anyone could post fake "messages" to Zazi.
    /// </summary>
    public string? InboundSecret { get; set; }

    /// <summary>Hour of the day (GMT, which is Ghana time) the daily summary goes out.</summary>
    public int DailySummaryHourUtc { get; set; } = 20;

    public bool DailySummaryEnabled { get; set; } = true;

    /// <summary>
    /// Languages keypad agents may choose with LANG, comma-separated: EN, TWI, GA, EWE.
    /// English only by default. A language is switched on here only after a native speaker
    /// has checked its wording — a reply about money that is misunderstood is worse than one
    /// in English.
    /// </summary>
    public string Languages { get; set; } = "EN";
}
