namespace TheCarl.Domain;

public enum AuthSessionStatus
{
    Active = 0,
    RevokedByUser = 1,
    RevokedByAdministrator = 2,
    RevokedBySecurityChange = 3,
    RevokedByDeviceRevocation = 4,
    RevokedByTokenReuse = 5,
    Expired = 6
}

/// <summary>
/// An authenticated session: one login on one device, owning one refresh-token family.
/// </summary>
/// <remarks>
/// <para>
/// The chain is <c>User → Device → AuthSession → RefreshToken family</c>. Refresh is
/// permitted only when every link is still valid, so revoking a device or a session stops
/// refresh immediately without waiting for any token to expire.
/// </para>
/// <para>
/// Adding a device id claim to a token would not achieve this: a claim asserts which device
/// obtained the token, but nothing checks whether that device is still trusted. Revocation
/// has to be a server-side lookup at refresh time, which is what this entity provides.
/// </para>
/// </remarks>
public sealed class AuthSession : AggregateRoot
{
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The device this session is bound to. Null for browser sessions, which are bound by
    /// <see cref="ClientFingerprint"/> instead.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>Refresh-token family owned by this session.</summary>
    public string FamilyId { get; set; } = string.Empty;

    public AuthSessionStatus Status { get; set; } = AuthSessionStatus.Active;

    /// <summary>
    /// Security stamp captured at login. Refresh compares it against the user's current
    /// stamp; any mismatch means a security-sensitive change happened and the session dies.
    /// </summary>
    public string SecurityStampAtIssue { get; set; } = string.Empty;

    /// <summary>Coarse client fingerprint (user agent hash). Never a tracking identifier.</summary>
    public string? ClientFingerprint { get; set; }

    /// <summary>Truncated to /24 (IPv4) or /48 (IPv6) — enough to spot session hijacking, not to track.</summary>
    public string? CreatedFromIpPrefix { get; set; }

    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedReason { get; set; }

    /// <summary>Whether the second factor was satisfied for this session.</summary>
    public bool MfaSatisfied { get; set; }

    public bool IsActive =>
        Status == AuthSessionStatus.Active
        && RevokedAtUtc is null
        && DateTimeOffset.UtcNow < ExpiresAtUtc;

    public void Revoke(AuthSessionStatus reason, string detail)
    {
        Status = reason;
        RevokedAtUtc = DateTimeOffset.UtcNow;
        RevokedReason = detail;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
