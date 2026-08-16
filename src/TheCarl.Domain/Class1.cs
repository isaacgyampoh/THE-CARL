namespace TheCarl.Domain;

// TransactionType now lives in Financial/TransactionType.cs, expanded to cover reversals,
// commission, adjustments and the explicitly-unclassified case.

public enum TransactionSource
{
    Manual,
    AutomaticSms,
    Bridge,
    FutureIntegration
}

public enum TransactionStatus
{
    Pending,
    Captured,
    Validated,
    NeedsReview,
    Reconciled,
    Rejected,
    Duplicate,
    Failed
}

public enum DeviceRole
{
    Hub,
    TransactionDevice,
    OwnerDevice,
    ManagerDevice
}

public enum DeviceStatus
{
    Pending,
    Active,
    Offline,
    Revoked,
    Quarantined,
    RequiresReauth
}

public enum SyncStatus
{
    Pending,
    Queued,
    InFlight,
    Synced,
    Failed,
    DeadLetter
}

public enum TransactionSyncStatus
{
    Pending,
    Queued,
    InFlight,
    Synced,
    Failed,
    DeadLetter,
    Duplicate,
    Conflict
}

public enum SmsParsingStatus
{
    Received,
    Parsed,
    Duplicate,
    Rejected,
    Accepted
}

public sealed record Money(decimal Amount, string Currency)
{
    public const string DefaultCurrency = "GHS";

    public static Money FromGhanaianCedi(decimal amount) => new(amount, DefaultCurrency);

    public override string ToString() => $"{Currency} {Amount:0.00}";
}

public abstract class AggregateRoot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Organization : AggregateRoot
{
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public string Country { get; set; } = "GH";
    public string CurrencyCode { get; set; } = Money.DefaultCurrency;

    /// <summary>
    /// Second-factor policy for this tenant. Defaults to <see cref="MfaPolicy.Optional"/>:
    /// mandatory MFA is never switched on by default, because doing so before enrolment and
    /// recovery flows exist would lock tenants out of their own accounts.
    /// </summary>
    public MfaPolicy MfaPolicy { get; set; } = MfaPolicy.Optional;

    public List<Branch> Branches { get; set; } = new();
    public List<User> Users { get; set; } = new();
}

public sealed class Branch : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Location { get; set; }
    public List<Device> Devices { get; set; } = new();
    public List<Session> Sessions { get; set; } = new();
}

public sealed class User : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; } = true;
    public bool EmailVerified { get; set; }
    public bool PhoneVerified { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public int FailedLoginAttempts { get; set; }
    public DateTimeOffset? LockoutUntilUtc { get; set; }
    public DateTimeOffset? LastLoginAtUtc { get; set; }
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public List<Role> Roles { get; set; } = new();
}

public sealed class Role : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<Permission> Permissions { get; set; } = new();
}

public sealed class Permission : AggregateRoot
{
    public Guid RoleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class RefreshToken : AggregateRoot
{
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The session that owns this token. Refresh checks the session — and through it the
    /// device — so revoking either stops refresh immediately.
    /// </summary>
    public Guid? SessionId { get; set; }

    /// <summary>
    /// SHA-256 hash of the refresh token. The token itself is returned to the client once
    /// and never stored: a database disclosure must not hand an attacker usable sessions.
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Groups every token descended from one login. Reusing a rotated token revokes the
    /// whole family, because reuse means the token leaked.
    /// </summary>
    public string FamilyId { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedReason { get; set; }

    /// <summary>Hash of the token that superseded this one, for rotation auditing.</summary>
    public string? ReplacedByTokenHash { get; set; }

    public bool IsRevoked => RevokedAtUtc.HasValue;
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAtUtc;
    public bool IsActive => !IsRevoked && !IsExpired;
}

public sealed class Device : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DeviceIdentifier { get; set; } = string.Empty;
    public string Platform { get; set; } = "Android";
    public DeviceRole Role { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Active;
    public string Network { get; set; } = "MTN";
    public string AppVersion { get; set; } = "1.0.0";
    public string OsVersion { get; set; } = "Android 14";
    public bool IsRevoked { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DeviceRegistration : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid DeviceId { get; set; }
    public string DeviceIdentifier { get; set; } = string.Empty;
    public string Platform { get; set; } = "Android";
    public string RegistrationToken { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset RegisteredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DeviceLink : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid SourceDeviceId { get; set; }
    public Guid LinkedDeviceId { get; set; }
    public string LinkType { get; set; } = "ApprovedCompanion";
    public string Status { get; set; } = "Active";
    public DateTimeOffset LinkedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UnlinkedAtUtc { get; set; }
}

public sealed class NetworkProvider : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public sealed class Session : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
    public bool IsClosed { get; set; }
    public decimal OpeningCash { get; set; }
    public decimal OpeningFloat { get; set; }
}

public sealed class SyncAttempt : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? TransactionId { get; set; }
    public string EntityType { get; set; } = "Transaction";
    public int AttemptNumber { get; set; }
    public TransactionSyncStatus Status { get; set; } = TransactionSyncStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset AttemptedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DeadLetterTransaction : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? TransactionId { get; set; }
    public string EntityType { get; set; } = "Transaction";
    public string Payload { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public DateTimeOffset DeadLetteredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditLogEntry : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid? RelatedTransactionId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string ActorType { get; set; } = "System";
}

public sealed class ReconciliationRecord : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public Guid SessionId { get; set; }
    public decimal OpeningCash { get; set; }
    public decimal CashInflow { get; set; }
    public decimal CashOutflow { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal ActualCash { get; set; }
    public decimal Difference { get; set; }
    public string Status { get; set; } = "Balanced";
}

public sealed class FloatBalance : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public string Network { get; set; } = string.Empty;
    public decimal OpeningFloat { get; set; }
    public decimal CurrentFloat { get; set; }
    public decimal Threshold { get; set; }
}

public sealed class CashBalance : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public decimal OpeningCash { get; set; }
    public decimal CurrentCash { get; set; }
}

public sealed class AlertRecord : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid BranchId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsAcknowledged { get; set; }
    public string Severity { get; set; } = "Medium";
    public string Network { get; set; } = string.Empty;
    public decimal Threshold { get; set; }
}

public sealed class AlertThreshold : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }
    public string Network { get; set; } = string.Empty;
    public decimal WarningThreshold { get; set; }
    public decimal CriticalThreshold { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class SyncQueueEntry : AggregateRoot
{
    public Guid OrganizationId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? EntityId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public int RetryCount { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsManual { get; set; }
}

// Transaction evidence now lives in Financial/TransactionEvidence.cs, which replaces the
// former CapturedSmsMessage. Evidence is source-agnostic (SMS, manual, gateway, relay) and
// is deliberately separate from the accepted ledger row, FinancialTransaction.
