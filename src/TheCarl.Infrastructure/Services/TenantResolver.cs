using Microsoft.EntityFrameworkCore;
using TheCarl.Application.Sync;
using TheCarl.Domain;

namespace TheCarl.Infrastructure.Services;

/// <summary>
/// Resolves and authorises the branch, device and session named by each item in a sync batch.
/// </summary>
/// <remarks>
/// <para>
/// Every lookup is filtered by the caller's organization, taken from the access token. A
/// client-supplied identifier is only ever a <i>request</i> to use something; it is never
/// authority. An id belonging to another tenant is indistinguishable from one that does not
/// exist, so ids cannot be probed across tenants.
/// </para>
/// <para>
/// Results are cached for the lifetime of one batch. A hundred transactions from one device
/// would otherwise issue three hundred identical lookups.
/// </para>
/// </remarks>
internal sealed class TenantResolver
{
    private readonly ApplicationDbContext _dbContext;
    private readonly SyncCallerContext _caller;

    private readonly Dictionary<Guid, bool> _branchCache = [];
    private readonly Dictionary<Guid, string?> _deviceCache = [];
    private readonly Dictionary<Guid, SessionSnapshot?> _sessionCache = [];

    public TenantResolver(ApplicationDbContext dbContext, SyncCallerContext caller)
    {
        _dbContext = dbContext;
        _caller = caller;
    }

    /// <summary>Last successfully resolved branch, for batch-level audit and logging.</summary>
    public Guid? LastBranchId { get; private set; }

    /// <summary>Last successfully validated device, for batch-level audit and logging.</summary>
    public Guid? LastDeviceId { get; private set; }

    public async Task<(Guid? Value, string? ReasonCode)> ResolveBranchAsync(
        Guid? requestedBranchId,
        CancellationToken cancellationToken)
    {
        // Branch-scoped callers are pinned to their own branch. A request naming a different
        // one is refused rather than silently redirected, so the client learns its mistake.
        if (requestedBranchId is not { } branchId || branchId == Guid.Empty)
        {
            if (_caller.CallerBranchId is { } assigned)
            {
                LastBranchId = assigned;
                return (assigned, null);
            }

            return (null, SyncReasonCodes.BranchNotInTenant);
        }

        if (!_caller.HasOrganizationWideScope
            && _caller.CallerBranchId is { } callerBranch
            && callerBranch != branchId)
        {
            return (null, SyncReasonCodes.BranchNotInTenant);
        }

        if (!_branchCache.TryGetValue(branchId, out var exists))
        {
            exists = await _dbContext.Branches
                .AsNoTracking()
                .AnyAsync(x => x.Id == branchId && x.OrganizationId == _caller.OrganizationId, cancellationToken);
            _branchCache[branchId] = exists;
        }

        if (!exists)
        {
            return (null, SyncReasonCodes.BranchNotInTenant);
        }

        LastBranchId = branchId;
        return (branchId, null);
    }

    /// <summary>Returns a reason code when the device may not synchronise, otherwise null.</summary>
    public async Task<string?> ValidateDeviceAsync(
        Guid deviceId,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        if (!_deviceCache.TryGetValue(deviceId, out var cachedReason))
        {
            var device = await _dbContext.Devices
                .AsNoTracking()
                .Where(x => x.Id == deviceId && x.OrganizationId == _caller.OrganizationId)
                .Select(x => new { x.BranchId, x.Status, x.IsRevoked })
                .SingleOrDefaultAsync(cancellationToken);

            cachedReason = device switch
            {
                null => SyncReasonCodes.DeviceNotInTenant,

                // A revoked device loses synchronisation authority immediately. This is
                // checked per batch, not only at login, so a device revoked mid-session
                // cannot keep posting with an access token it already holds.
                { IsRevoked: true } => SyncReasonCodes.DeviceRevoked,
                { Status: DeviceStatus.Revoked or DeviceStatus.Quarantined } => SyncReasonCodes.DeviceRevoked,

                _ => device.BranchId == branchId ? null : SyncReasonCodes.DeviceNotInTenant
            };

            _deviceCache[deviceId] = cachedReason;
        }

        if (cachedReason is null)
        {
            LastDeviceId = deviceId;
        }

        return cachedReason;
    }

    /// <summary>
    /// Returns a reason code when the session cannot carry the transaction, otherwise null.
    /// </summary>
    /// <remarks>
    /// <b>Session-close rule.</b> A transaction is judged against the session window it
    /// claims, not against the moment it happens to arrive. A device that captured work at
    /// 14:00, went offline, and reconnects after the session closed at 17:00 still posts:
    /// the event time falls inside the window. Only an event time outside the window is
    /// refused. Rejecting late arrivals purely because the session has since closed would
    /// discard genuine financial records for being slow to sync.
    /// </remarks>
    public async Task<string?> ValidateSessionAsync(
        Guid sessionId,
        Guid branchId,
        DateTimeOffset eventTime,
        CancellationToken cancellationToken)
    {
        if (!_sessionCache.TryGetValue(sessionId, out var snapshot))
        {
            snapshot = await _dbContext.Sessions
                .AsNoTracking()
                .Where(x => x.Id == sessionId && x.OrganizationId == _caller.OrganizationId)
                .Select(x => new SessionSnapshot(x.BranchId, x.OpenedAt, x.ClosedAt))
                .SingleOrDefaultAsync(cancellationToken);

            _sessionCache[sessionId] = snapshot;
        }

        if (snapshot is null)
        {
            return SyncReasonCodes.SessionNotInTenant;
        }

        if (snapshot.BranchId != branchId)
        {
            return SyncReasonCodes.SessionBranchMismatch;
        }

        if (eventTime < snapshot.OpenedAt)
        {
            return SyncReasonCodes.SessionNotOpenAtEventTime;
        }

        if (snapshot.ClosedAt is { } closedAt && eventTime > closedAt)
        {
            return SyncReasonCodes.SessionNotOpenAtEventTime;
        }

        return null;
    }

    private sealed record SessionSnapshot(Guid BranchId, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt);
}
