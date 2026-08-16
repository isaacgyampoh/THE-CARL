namespace TheCarl.Domain;

public enum DeviceEnrollmentCodeStatus
{
    /// <summary>Issued and still usable.</summary>
    Active = 0,

    /// <summary>Consumed by a successful enrolment. Terminal.</summary>
    Redeemed = 1,

    /// <summary>Withdrawn by an administrator before use. Terminal.</summary>
    Revoked = 2
}

/// <summary>
/// A short-lived, single-use credential that lets a handset enrol itself into an
/// organization without an administrator handling the phone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Registering a device requires <c>device.manage</c>, which agents
/// do not hold. Without an enrolment code, every handset has to be provisioned by a manager
/// in person — unworkable for an organization with fifty branches.
/// </para>
/// <para>
/// <b>The code is a password.</b> It is stored only as a SHA-256 hash, never in clear text,
/// and the plaintext is returned exactly once at issue. A database disclosure must not hand
/// an attacker a working route into a tenant.
/// </para>
/// <para>
/// <b>Scope is fixed at issue, not at redemption.</b> The organization, branch and device
/// role are decided by the administrator issuing the code. The enrolling handset supplies
/// only its own hardware details, so a stolen code cannot be redeemed into a different
/// branch or with elevated privileges.
/// </para>
/// </remarks>
public sealed class DeviceEnrollmentCode : AggregateRoot
{
    /// <summary>Default validity. Long enough to walk a phone to an agent, short enough to matter.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);

    /// <summary>Attempts allowed against one code before it is locked out.</summary>
    public const int MaxFailedAttempts = 10;

    public Guid OrganizationId { get; set; }

    /// <summary>Branch the enrolled device will belong to. Fixed at issue.</summary>
    public Guid BranchId { get; set; }

    /// <summary>
    /// SHA-256 of the plaintext code. The code itself is never persisted: it is shown once
    /// at issue and cannot be recovered afterwards.
    /// </summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>
    /// Non-secret leading characters, for display in an admin list ("CARL-7F3A…"). Enough to
    /// identify which code is which without disclosing anything usable.
    /// </summary>
    public string CodePrefix { get; set; } = string.Empty;

    /// <summary>Role the enrolled device takes. Fixed at issue so a code cannot self-elevate.</summary>
    public DeviceRole DeviceRole { get; set; } = DeviceRole.TransactionDevice;

    /// <summary>Optional: restricts redemption to one intended user.</summary>
    public Guid? IntendedUserId { get; set; }

    public Guid IssuedByUserId { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DeviceEnrollmentCodeStatus Status { get; set; } = DeviceEnrollmentCodeStatus.Active;

    public DateTimeOffset? RedeemedAtUtc { get; set; }

    /// <summary>The device created by redeeming this code, for audit.</summary>
    public Guid? RedeemedByDeviceId { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid? RevokedByUserId { get; set; }

    /// <summary>
    /// Failed redemption attempts. Guessing is already impractical against 160 bits of
    /// entropy; this bounds the effort and gives an operator a visible signal.
    /// </summary>
    public int FailedAttempts { get; set; }

    public string? Label { get; set; }

    /// <summary>
    /// True only when the code is active, unexpired and under its attempt limit.
    /// Evaluated server-side at redemption; never trusted from a client.
    /// </summary>
    public bool IsRedeemable(DateTimeOffset nowUtc) =>
        Status == DeviceEnrollmentCodeStatus.Active
        && RedeemedAtUtc is null
        && nowUtc < ExpiresAtUtc
        && FailedAttempts < MaxFailedAttempts;
}
