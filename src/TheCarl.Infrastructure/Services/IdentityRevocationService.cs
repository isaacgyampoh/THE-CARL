using Microsoft.EntityFrameworkCore;
using TheCarl.Application.Security;
using TheCarl.Domain;

namespace TheCarl.Infrastructure.Services;

/// <inheritdoc />
public sealed class IdentityRevocationService : IIdentityRevocationService
{
    private readonly ApplicationDbContext _dbContext;

    public IdentityRevocationService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<int> RevokeAllSessionsAsync(
        Guid userId,
        RevocationTrigger trigger,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new KeyNotFoundException("User was not found.");

        // Rotating the stamp is what actually invalidates the sessions: any session holding
        // the previous value fails its next refresh, including sessions this query misses
        // because they were created concurrently.
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.UpdatedAt = DateTimeOffset.UtcNow;

        var sessions = await _dbContext.AuthSessions
            .Where(x => x.UserId == userId && x.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.Revoke(MapStatus(trigger), trigger.ToString());
        }

        var familyIds = sessions.Select(x => x.FamilyId).ToList();
        await RevokeTokensAsync(userId, familyIds, trigger.ToString(), cancellationToken);

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = user.OrganizationId,
            UserId = actorUserId ?? userId,
            Action = "SESSIONS_REVOKED",
            Details = $"{sessions.Count} session(s) revoked for user {userId}. Trigger: {trigger}.",
            ActorType = actorUserId is null ? "System" : "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return sessions.Count;
    }

    public async Task<bool> RevokeSessionAsync(
        Guid sessionId,
        RevocationTrigger trigger,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.AuthSessions
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);

        if (session is null || session.RevokedAtUtc is not null)
        {
            return false;
        }

        session.Revoke(MapStatus(trigger), trigger.ToString());

        // The user's stamp is deliberately left alone: rotating it here would sign the user
        // out of every other device, which is not what revoking one session means.
        await RevokeTokensAsync(session.UserId, [session.FamilyId], trigger.ToString(), cancellationToken);

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = session.OrganizationId,
            UserId = actorUserId ?? session.UserId,
            DeviceId = session.DeviceId,
            Action = "SESSION_REVOKED",
            Details = $"Session {sessionId} revoked. Trigger: {trigger}.",
            ActorType = actorUserId is null ? "System" : "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> RevokeDeviceSessionsAsync(
        Guid deviceId,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var sessions = await _dbContext.AuthSessions
            .Where(x => x.DeviceId == deviceId && x.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.Revoke(AuthSessionStatus.RevokedByDeviceRevocation, "Device revoked.");
            await RevokeTokensAsync(session.UserId, [session.FamilyId], "Device revoked.", cancellationToken);
        }

        if (sessions.Count > 0)
        {
            _dbContext.AuditLogs.Add(new AuditLogEntry
            {
                OrganizationId = sessions[0].OrganizationId,
                UserId = actorUserId,
                DeviceId = deviceId,
                Action = "DEVICE_REVOKED",
                Details = $"{sessions.Count} session(s) closed because device {deviceId} was revoked.",
                ActorType = actorUserId is null ? "System" : "User"
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return sessions.Count;
    }

    private async Task RevokeTokensAsync(
        Guid userId,
        IReadOnlyCollection<string> familyIds,
        string reason,
        CancellationToken cancellationToken)
    {
        if (familyIds.Count == 0)
        {
            return;
        }

        var tokens = await _dbContext.RefreshTokens
            .Where(x => x.UserId == userId && x.RevokedAtUtc == null && familyIds.Contains(x.FamilyId))
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.RevokedAtUtc = DateTimeOffset.UtcNow;
            token.RevokedReason = reason;
            token.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static AuthSessionStatus MapStatus(RevocationTrigger trigger) => trigger switch
    {
        RevocationTrigger.PasswordChanged => AuthSessionStatus.RevokedBySecurityChange,
        RevocationTrigger.RolesChanged => AuthSessionStatus.RevokedBySecurityChange,
        RevocationTrigger.MfaEnrolmentChanged => AuthSessionStatus.RevokedBySecurityChange,
        RevocationTrigger.AccountDisabled => AuthSessionStatus.RevokedByAdministrator,
        RevocationTrigger.AdministratorRevoked => AuthSessionStatus.RevokedByAdministrator,
        RevocationTrigger.SuspiciousActivity => AuthSessionStatus.RevokedByAdministrator,
        RevocationTrigger.DeviceRevoked => AuthSessionStatus.RevokedByDeviceRevocation,
        _ => AuthSessionStatus.RevokedByAdministrator
    };
}
