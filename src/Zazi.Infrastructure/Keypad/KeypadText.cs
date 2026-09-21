using System.Globalization;

namespace Zazi.Infrastructure.Keypad;

/// <summary>
/// What Zazi says to a keypad agent, in their language.
/// </summary>
/// <remarks>
/// <para>
/// English is complete and is the fallback for anything missing. Twi is a first draft and Ga
/// and Ewe are empty: none is used until it is listed in <c>Sms:Languages</c>, which should
/// happen only after a native speaker has read every line. Commands, numbers and the words
/// OK, SHORT and OVER stay in English in every language, so the part an agent acts on is the
/// same whatever they read.
/// </para>
/// <para>
/// Plain ASCII only: Twi's ɛ and ɔ are outside the GSM alphabet and would turn every reply into
/// a Unicode SMS at double the cost, so they are written e and o, as people text them.
/// </para>
/// </remarks>
public static class KeypadText
{
    public const string English = "EN";
    public static readonly string[] Supported = ["EN", "TWI", "GA", "EWE"];

    public static string NameOf(string language) => language switch
    {
        "TWI" => "Twi",
        "GA" => "Ga",
        "EWE" => "Ewe",
        _ => "English"
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["recorded"] = "Zazi OK: {0}.",
        ["already"] = "Zazi: already recorded. Nothing was added twice.",
        ["help"] = "Zazi: forward any MoMo message to record it. Or send:\nCO 50 0244123456 (cash out)\nCI 50 0244123456 (cash in)\nFIND 0244123456\nTODAY\nCLOSE cash float (end of day)\nFLOAT 500 MTN (ask for float)\nCOMM (commission)",
        ["float.low"] = " Low {0} float: GHS {1}. Top up soon.",
        ["float.verylow"] = " Very low {0} float: GHS {1}. Top up soon.",
        ["close.baseline"] = "Zazi: day closed. Cash GHS {0}, float GHS {1}. This is your starting count - from tomorrow Zazi will tell you if anything is short.",
        ["close.done"] = "Zazi: day closed. {0}. {1}. {2}",
        ["close.balanced"] = "All balanced. Well done.",
        ["close.short"] = "Check for a transaction you did not record, or forward its MoMo message now. Your owner can see this close.",
        ["close.over"] = "Check for a transaction recorded twice or not done. Your owner can see this close.",
        ["remind"] = " Count up and send: CLOSE cash float",
        ["lang.set"] = "Zazi: replies will now be in English.",
        ["lang.off"] = "Zazi: {0} replies are not switched on yet. Replies stay in English.",
        ["lang.usage"] = "Zazi: send LANG EN, LANG TWI, LANG GA or LANG EWE.",
        ["floatreq.sent"] = "Zazi: float request sent - GHS {0} {1}. Your owner will answer. Ref {2}.",
        ["floatreq.usage"] = "Zazi: send FLOAT amount network, e.g. FLOAT 500 MTN",
        ["comm"] = "Zazi commission {0}: {1}. Total GHS {2}.",
        ["comm.none"] = "Zazi commission {0}: none recorded yet. Forward the network's commission messages to record them."
    };

    /// <summary>First draft. Must be checked by a Twi speaker before TWI is switched on.</summary>
    private static readonly Dictionary<string, string> Twi = new()
    {
        ["recorded"] = "Zazi: Yeakyerew. {0}.",
        ["already"] = "Zazi: Yeakyerew yei dada. Yenkyerew no bio.",
        ["help"] = "Zazi: Fa MoMo nkra biara bra ha na yebekyerew. Anaa fa:\nCO 50 0244123456 (cash out)\nCI 50 0244123456 (cash in)\nFIND 0244123456\nTODAY\nCLOSE sika float\nFLOAT 500 MTN\nCOMM",
        ["float.low"] = " Wo {0} float asua: GHS {1}. To bi ntem.",
        ["float.verylow"] = " Wo {0} float asua pii: GHS {1}. To bi seesei.",
        ["close.baseline"] = "Zazi: Woato nkontaa no mu. Sika GHS {0}, float GHS {1}. Yei ne wo mfitiase nkontaa.",
        ["close.done"] = "Zazi: Woato nkontaa no mu. {0}. {1}. {2}",
        ["close.balanced"] = "Ne nyinaa ye pe. Ayekoo.",
        ["close.short"] = "Hwe se transaction bi wo ho a wonkyerewee. Wo wura behu yei.",
        ["close.over"] = "Hwe se wokyerew transaction bi mprenu. Wo wura behu yei.",
        ["remind"] = " Kan wo sika na fa: CLOSE sika float",
        ["lang.set"] = "Zazi: Yebeka Twi akyere wo afei.",
        ["floatreq.sent"] = "Zazi: Yede wo float abisade no akoma - GHS {0} {1}. Wo wura bebua. Ref {2}.",
        ["comm"] = "Zazi commission {0}: {1}. Ne nyinaa GHS {2}."
    };

    /// <summary>Empty until written by a Ga speaker; every line falls back to English.</summary>
    private static readonly Dictionary<string, string> Ga = new();

    /// <summary>Empty until written by an Ewe speaker; every line falls back to English.</summary>
    private static readonly Dictionary<string, string> Ewe = new();

    public static string Get(string? language, string key, params object[] args)
    {
        var table = language switch
        {
            "TWI" => Twi,
            "GA" => Ga,
            "EWE" => Ewe,
            _ => En
        };
        var template = table.TryGetValue(key, out var text) ? text : En[key];
        return args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    /// <summary>Whether every English line has a translation — the check to run before switching one on.</summary>
    public static IReadOnlyList<string> Missing(string language)
    {
        var table = language switch { "TWI" => Twi, "GA" => Ga, "EWE" => Ewe, _ => En };
        return En.Keys.Where(k => !table.ContainsKey(k)).ToList();
    }
}
