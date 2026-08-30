namespace Zazi.Domain;

/// <summary>
/// The physical platform and form factor of a device. <b>Nothing else.</b>
/// </summary>
/// <remarks>
/// <para>
/// Zazi keeps four concepts deliberately separate, and this is one of them:
/// </para>
/// <list type="bullet">
/// <item><description><b>DeviceType</b> — what the device <i>is</i> (this enum)</description></item>
/// <item><description><b><see cref="DeviceRole"/></b> — what the device <i>does</i> for the business</description></item>
/// <item><description><b><see cref="PlatformCapability"/></b> — what the device <i>can technically do</i></description></item>
/// <item><description><b>Authorization policies</b> — what the authenticated principal <i>may do</i></description></item>
/// </list>
/// <para>
/// Values such as "AndroidOwner" were deliberately rejected: they fold a business role into
/// a platform identity, duplicating <see cref="DeviceRole.OwnerDevice"/> and creating two
/// sources of truth that can silently disagree. An owner's Android handset is
/// <see cref="AndroidPhone"/> + <see cref="DeviceRole.OwnerDevice"/>.
/// </para>
/// <para>
/// <b>The numeric values are the persisted contract.</b> Enums are stored as integer
/// ordinals throughout Zazi, so renumbering reclassifies existing hardware. Append only.
/// </para>
/// <para>
/// <see cref="Other"/> is deliberately 0 so that an unmapped or defaulted row reads as
/// "unknown platform" rather than being mistaken for a real one — the safe reading, since
/// capabilities are granted from this value.
/// </para>
/// </remarks>
public enum DeviceType
{
    /// <summary>Platform not known or not recognised. Grants the narrowest capabilities.</summary>
    Other = 0,

    AndroidPhone = 1,
    AndroidTablet = 2,
    iPhone = 3,
    iPad = 4,

    /// <summary>A browser session rather than an installed application.</summary>
    WebBrowser = 5,

    /// <summary>
    /// A GSM gateway forwarding provider messages. Architecture only — no gateway
    /// integration is implemented.
    /// </summary>
    GsmGateway = 6
}

/// <summary>
/// Maps the legacy free-text <c>Device.Platform</c> string onto a <see cref="DeviceType"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Platform</c> predates this enum, was never validated, and is retained for backward
/// compatibility. This mapping backfills existing rows and normalises new input.
/// </para>
/// <para>
/// <b>Deliberately conservative.</b> Anything not confidently recognised becomes
/// <see cref="DeviceType.Other"/>. Guessing would grant capabilities — including SMS capture
/// — to a device that may not support them, and a wrong capability claim is worse than an
/// absent one.
/// </para>
/// </remarks>
public static class DeviceTypeMapping
{
    /// <summary>
    /// Resolves a platform string. Returns <see cref="DeviceType.Other"/> for anything
    /// unrecognised, empty, or ambiguous.
    /// </summary>
    public static DeviceType FromPlatformString(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return DeviceType.Other;
        }

        // Normalised so "Android Phone", "android-phone" and "ANDROID_PHONE" agree.
        var normalized = new string(platform
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        if (normalized.Length == 0)
        {
            return DeviceType.Other;
        }

        // Tablet checks precede phone checks: "androidtablet" contains "android", so testing
        // the broader term first would classify every tablet as a phone.
        if (normalized.Contains("ipad"))
        {
            return DeviceType.iPad;
        }

        // "ios" must anchor at the start, not match as a substring. KaiOS — a real
        // feature-phone OS — normalises to "kaios", which contains "ios" and would otherwise
        // be classified as an iPhone and handed iOS capabilities it does not have.
        if (normalized.Contains("iphone") || normalized.StartsWith("ios", StringComparison.Ordinal))
        {
            return DeviceType.iPhone;
        }

        if (normalized.Contains("android"))
        {
            return normalized.Contains("tablet") || normalized.Contains("tab")
                ? DeviceType.AndroidTablet
                : DeviceType.AndroidPhone;
        }

        if (normalized.Contains("gsmgateway") || normalized.Contains("gateway"))
        {
            return DeviceType.GsmGateway;
        }

        if (normalized.Contains("web") || normalized.Contains("browser"))
        {
            return DeviceType.WebBrowser;
        }

        return DeviceType.Other;
    }

    /// <summary>True when the value is a defined member. Mirrors the database CHECK constraint.</summary>
    public static bool IsDefined(DeviceType deviceType) => Enum.IsDefined(deviceType);
}
