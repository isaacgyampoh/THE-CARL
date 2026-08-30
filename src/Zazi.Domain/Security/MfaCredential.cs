namespace Zazi.Domain;

/// <summary>Organization-level second-factor policy.</summary>
public enum MfaPolicy
{
    /// <summary>Users may enrol; enrolment is not required to sign in.</summary>
    Optional = 0,

    /// <summary>
    /// Every user must have an active second factor. Enforced only for users who have
    /// completed enrolment plus recovery-code generation, so switching the policy on can
    /// never lock an organization out of its own account.
    /// </summary>
    Required = 1
}

public enum MfaMethod
{
    /// <summary>RFC 6238 time-based one-time password.</summary>
    Totp = 0,

    /// <summary>Single-use recovery code.</summary>
    RecoveryCode = 1
}

public enum MfaCredentialStatus
{
    /// <summary>Secret issued but the first code has not been verified yet.</summary>
    PendingVerification = 0,

    Active = 1,
    Disabled = 2
}

/// <summary>
/// A user's second-factor credential.
/// </summary>
/// <remarks>
/// <para>
/// <b>Secret storage:</b> the TOTP shared secret is stored encrypted, never in plain text,
/// in <see cref="EncryptedSecret"/> with the wrapping key identified by
/// <see cref="KeyId"/>. Recovery codes are stored only as PBKDF2 hashes, exactly like
/// passwords, because a recovery code is a password equivalent.
/// </para>
/// <para>
/// <b>Enrolment order:</b> a credential is created <see cref="MfaCredentialStatus.PendingVerification"/>
/// and only becomes <see cref="MfaCredentialStatus.Active"/> after the user proves they can
/// generate a valid code. This prevents a user locking themselves out by enrolling a
/// mis-scanned secret.
/// </para>
/// </remarks>
public sealed class MfaCredential : AggregateRoot
{
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }
    public MfaMethod Method { get; set; } = MfaMethod.Totp;
    public MfaCredentialStatus Status { get; set; } = MfaCredentialStatus.PendingVerification;

    /// <summary>Encrypted TOTP secret. Never populated for recovery codes.</summary>
    public byte[]? EncryptedSecret { get; set; }

    /// <summary>Identifier of the wrapping key, so keys can be rotated without re-enrolment.</summary>
    public string? KeyId { get; set; }

    /// <summary>PBKDF2 hash of a recovery code. Never populated for TOTP.</summary>
    public string? CodeHash { get; set; }
    public string? CodeSalt { get; set; }

    /// <summary>Consumed recovery codes are retained, marked used, for audit.</summary>
    public DateTimeOffset? UsedAtUtc { get; set; }

    public DateTimeOffset? VerifiedAtUtc { get; set; }
    public DateTimeOffset? LastUsedAtUtc { get; set; }

    /// <summary>
    /// Last accepted TOTP counter window. Stored to reject replay of a code that is still
    /// inside its validity window but has already been used.
    /// </summary>
    public long? LastAcceptedTimeStep { get; set; }

    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }

    public bool IsUsableSecondFactor =>
        Status == MfaCredentialStatus.Active
        && (Method != MfaMethod.RecoveryCode || UsedAtUtc is null);
}
