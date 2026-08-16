namespace TheCarl.Application;

public interface ISyncService
{
    Task<bool> SynchronizePendingAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

public static class ApplicationMarker
{
    public const string Name = "TheCarl.Application";
}
