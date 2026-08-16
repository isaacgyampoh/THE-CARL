namespace TheCarl.Application.Security;

/// <summary>Why a user's active sessions were invalidated.</summary>
public enum RevocationTrigger
{
    PasswordChanged,
    AccountDisabled,
    RolesChanged,
    AdministratorRevoked,
    SuspiciousActivity,
    DeviceRevoked,
    MfaEnrolmentChanged
}

/// <summary>
/// Invalidates active sessions without waiting for access tokens to expire.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strategy.</b> Rotating the user's security stamp invalidates every session bound to
/// the previous stamp. Refresh compares the stamp recorded on the session against the
/// user's current stamp, so the next refresh fails and the session is closed. This is one
/// indexed PostgreSQL read on a path that already loads the user — no cache, no Redis, and
/// no extra round trip.
/// </para>
/// <para>
/// <b>Bounded exposure.</b> An already-issued access token is self-contained and stays
/// valid until it expires. Revocation is therefore bounded by the access-token lifetime
/// (60 minutes by default), not by the 14-day refresh window. Shortening
/// <c>Jwt:AccessTokenMinutes</c> tightens that bound at the cost of more refresh traffic.
/// </para>
/// <para>
/// <b>If per-request revocation is later required</b>, the same stamp comparison can move
/// into a token-validated event. That costs one indexed read per request, which is why it is
/// not on by default; it is a configuration decision, not a redesign. A cache would sit
/// behind an abstraction at that point — it is not required for correctness now.
/// </para>
/// </remarks>
public interface IIdentityRevocationService
{
    /// <summary>
    /// Rotates the user's security stamp and closes their active sessions and token
    /// families. Returns the number of sessions closed.
    /// </summary>
    Task<int> RevokeAllSessionsAsync(
        Guid userId,
        RevocationTrigger trigger,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Closes one session, leaving the user's other sessions intact.</summary>
    Task<bool> RevokeSessionAsync(
        Guid sessionId,
        RevocationTrigger trigger,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes every session bound to a device. Called when a device is revoked so the
    /// handset loses synchronisation authority immediately.
    /// </summary>
    Task<int> RevokeDeviceSessionsAsync(
        Guid deviceId,
        Guid? actorUserId = null,
        CancellationToken cancellationToken = default);
}
