namespace TheCarl.Application.Sync;

/// <summary>Server-side limits for batch synchronisation.</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>
    /// Hard maximum transactions per request. A request above this is refused whole with
    /// HTTP 413 — the server never silently truncates a batch, because a client that
    /// believes 200 items were accepted when 100 were would lose the remainder.
    /// </summary>
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>
    /// How far into the future a client-supplied event time may sit before rejection.
    /// Small but non-zero: device clocks drift, and rejecting a transaction because a
    /// handset is forty seconds fast would lose real financial records.
    /// </summary>
    public TimeSpan MaxClockSkewAhead { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far back an event time may sit. Generous by design: a device may legitimately
    /// return after weeks offline, and its backlog must still post.
    /// </summary>
    public TimeSpan MaxBacklogAge { get; set; } = TimeSpan.FromDays(90);

    public void Validate()
    {
        if (MaxBatchSize is < 1 or > 1000)
        {
            throw new InvalidOperationException("Sync:MaxBatchSize must be between 1 and 1000.");
        }

        if (MaxClockSkewAhead < TimeSpan.Zero || MaxClockSkewAhead > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("Sync:MaxClockSkewAhead must be between 0 and 1 hour.");
        }

        if (MaxBacklogAge < TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException(
                "Sync:MaxBacklogAge must be at least 1 day so offline backlogs can still post.");
        }
    }
}
