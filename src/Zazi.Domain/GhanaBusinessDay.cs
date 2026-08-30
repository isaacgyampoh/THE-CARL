namespace Zazi.Domain;

/// <summary>
/// Business-day boundaries for Zazi's operating market.
/// </summary>
/// <remarks>
/// Financial records are stored in UTC and reported against the Ghanaian business day.
/// Ghana observes UTC+0 with no daylight saving, so the offset is fixed — but the zone is
/// stated explicitly rather than assumed, so "today" never silently means "the server's
/// today" if the platform is ever hosted elsewhere or extended to another market.
/// </remarks>
public static class GhanaBusinessDay
{
    public const string TimeZoneId = "Africa/Accra";

    /// <summary>Ghana has no daylight saving; the offset is constant.</summary>
    public static readonly TimeSpan Offset = TimeSpan.Zero;

    /// <summary>
    /// Half-open UTC range covering the current business day: <c>[start, end)</c>.
    /// Half-open so a transaction at exactly midnight belongs to one day only.
    /// </summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) CurrentUtcRange(DateTimeOffset? nowUtc = null) =>
        UtcRangeFor(nowUtc ?? DateTimeOffset.UtcNow);

    /// <summary>Half-open UTC range covering the business day containing <paramref name="instant"/>.</summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) UtcRangeFor(DateTimeOffset instant)
    {
        var local = instant.ToOffset(Offset);
        var startLocal = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, Offset);

        return (startLocal.ToUniversalTime(), startLocal.AddDays(1).ToUniversalTime());
    }
}
