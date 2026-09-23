using Zazi.Domain;

namespace Zazi.Application;

public record CreateOrganizationRequest(
    string Name,
    string? Email,
    string? PhoneNumber,
    string Country,
    string CurrencyCode);

public record OrganizationDto(
    Guid Id,
    string Name,
    string? Email,
    string? PhoneNumber,
    string Country,
    string CurrencyCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record CreateBranchRequest(
    Guid OrganizationId,
    string Name,
    string? Location);

public record BranchDto(
    Guid Id,
    Guid OrganizationId,
    string Name,
    string? Location,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record CreateSessionRequest(
    Guid OrganizationId,
    Guid BranchId,
    Guid UserId,
    Guid? DeviceId,
    decimal OpeningCash,
    decimal OpeningFloat,
    /// <summary>
    /// Which network the opening float is held on. Optional, defaulting to unknown rather
    /// than to a network: guessing MTN filed a Telecel agent's opening float against a
    /// balance their own transactions never touched, so the figure they were given and the
    /// figure that moved were two different rows.
    /// </summary>
    string? Network = null);

public record SessionDto(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    Guid UserId,
    Guid? DeviceId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    bool IsClosed,
    decimal OpeningCash,
    decimal OpeningFloat);

public record CloseSessionRequest(Guid SessionId, Guid UserId);

public record CreateTransactionRequest(
    Guid OrganizationId,
    Guid BranchId,
    Guid AgentId,
    Guid? DeviceId,
    string Network,
    TransactionType Type,
    decimal Amount,
    string Currency,
    string? CustomerPhoneNumber,
    string? ProviderReference,
    TransactionSource Source,
    string? Notes,
    Guid? SessionId = null,
    // Device-generated identity enabling exactly-once submission. Untrusted input.
    string? ClientTransactionId = null,
    // Required when Type is Reversal.
    Guid? ReversesTransactionId = null,
    // Required for corrections; recorded on the audit trail.
    string? CorrectionReason = null,
    /// <summary>
    /// Signed cash movement, required when <c>Type</c> is <c>Adjustment</c>.
    /// </summary>
    /// <remarks>
    /// An owner handing an agent cash or float is an adjustment: it moves a balance without a
    /// customer on the other side, and only the owner knows the direction and size. Every other
    /// type derives its movement from the type itself, which is why these are the one case
    /// LedgerPolicy asks the caller for.
    /// </remarks>
    decimal? AdjustmentCashDelta = null,
    decimal? AdjustmentFloatDelta = null);

public record TransactionDto(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    Guid AgentId,
    Guid? DeviceId,
    string Network,
    TransactionType Type,
    decimal Amount,
    string Currency,
    string? CustomerPhoneNumber,
    string? ProviderReference,
    DateTimeOffset TransactionAt,
    TransactionSource Source,
    TransactionLifecycleState State,
    decimal ConfidenceScore,
    decimal CashDelta,
    decimal FloatDelta,
    string? ClientTransactionId,
    string? Notes,
    /// <summary>The counterparty's registered name, where the network stated one.</summary>
    string? CustomerName = null);

public record CreateDeviceRequest(
    Guid OrganizationId,
    Guid BranchId,
    string Name,
    string DeviceIdentifier,
    string Platform,
    string Network,
    DeviceRole Role,
    string AppVersion,
    string OsVersion);

public record DeviceDto(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    string Name,
    string DeviceIdentifier,
    string Platform,
    string Network,
    DeviceRole Role,
    DeviceStatus Status,
    string AppVersion,
    string OsVersion,
    DateTimeOffset LastSeenAt);

public record RegisterUserRequest(
    Guid OrganizationId,
    Guid? BranchId,
    string FullName,
    string Email,
    string Password,
    string? PhoneNumber,
    string[] Roles,
    bool EmailVerified = false,
    bool PhoneVerified = false);

public record UserDto(
    Guid Id,
    Guid OrganizationId,
    Guid? BranchId,
    string FullName,
    /// <summary>Null for a worker activated by code, who has no account of their own.</summary>
    string? Email,
    string? PhoneNumber,
    bool IsActive,
    bool EmailVerified,
    bool PhoneVerified,
    DateTimeOffset CreatedAt,
    /// <summary>Whether this identity signs in with a password or activates with a code.</summary>
    UserCredentialType CredentialType,
    IReadOnlyList<string> Roles);

public record LoginRequest(
    string Email,
    string Password,
    string? DeviceIdentifier = null);

public record AuthTokenResult(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    UserDto User);

public record RefreshTokenRequest(
    string RefreshToken,
    Guid? OrganizationId = null);

public record ReconciliationResultDto(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    Guid SessionId,
    decimal OpeningCash,
    decimal CashInflow,
    decimal CashOutflow,
    decimal ExpectedCash,
    decimal ActualCash,
    decimal Difference,
    string Status);

public record AlertThresholdRequest(
    Guid OrganizationId,
    Guid? BranchId,
    string Network,
    decimal WarningThreshold,
    decimal CriticalThreshold,
    bool IsEnabled = true);

public record AlertDto(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    string Type,
    string Message,
    string Severity,
    string Network,
    decimal Threshold,
    bool IsAcknowledged,
    DateTimeOffset CreatedAt);

public record DashboardSummaryDto(
    int TotalBranches,
    int TodayTransactions,
    decimal TodayVolume,
    decimal CashPosition,
    decimal TotalNetworkFloat,
    int LowFloatAlerts,
    int Discrepancies,
    int ActiveSessions,
    int ActiveDevices,
    decimal BalanceVariance);

public record SmsCaptureRequest(
    Guid OrganizationId,
    Guid? BranchId,
    Guid? DeviceId,
    string SourcePhoneNumber,
    string RawMessage,
    string? ProviderHint = null,
    DateTimeOffset? MessageTimestampUtc = null);

public record SmsParseResultDto(
    Guid Id,
    Guid OrganizationId,
    Guid? BranchId,
    Guid? DeviceId,
    string Provider,
    string Network,
    string TransactionType,
    decimal Amount,
    string? CustomerPhoneNumber,
    string? ProviderReference,
    decimal ConfidenceScore,
    TransactionLifecycleState State,
    bool IsDuplicate,
    // Canonical evidence fingerprint, not a hash of the raw message body.
    string Fingerprint,
    DateTimeOffset ServerReceivedAtUtc);

public interface IOrganizationService
{
    Task<IReadOnlyList<OrganizationDto>> GetOrganizationsAsync(CancellationToken cancellationToken = default);
    Task<OrganizationDto> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default);
    Task<BranchDto?> CreateBranchAsync(CreateBranchRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BranchDto>> GetBranchesAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public interface ISessionService
{
    Task<SessionDto> OpenSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default);
    Task<SessionDto?> CloseSessionAsync(CloseSessionRequest request, CancellationToken cancellationToken = default);
    Task<SessionDto?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

/// <summary>A single page of results plus the total, so clients can drive paging controls.</summary>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount)
{
    public bool HasMore => (long)Page * PageSize < TotalCount;
}

/// <summary>
/// Query for transaction listing. <paramref name="OrganizationId"/> is supplied by the
/// caller's authenticated context, never by request input.
/// </summary>
public record TransactionQuery(
    Guid OrganizationId,
    Guid? BranchId,
    int Page,
    int PageSize,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    /// <summary>
    /// A customer number, whole or in part. A whole number finds that customer however it was
    /// stored; three or more digits find every number containing them.
    /// </summary>
    string? CustomerPhone = null,
    TransactionType? Type = null,
    Guid? AgentId = null,
    string? Network = null);

public interface ITransactionService
{
    Task<TransactionDto> CreateTransactionAsync(CreateTransactionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one page of transactions. There is deliberately no unbounded overload:
    /// an organization's full transaction history is expected to reach tens of millions
    /// of rows, and a single unpaged read would exhaust both server memory and the client.
    /// </summary>
    Task<PagedResult<TransactionDto>> GetTransactionsAsync(TransactionQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Request to issue a device enrolment code. Scope is set by the issuer, not the device.</summary>
public record IssueEnrollmentCodeRequest(
    Guid? BranchId,
    DeviceRole DeviceRole = DeviceRole.TransactionDevice,
    Guid? IntendedUserId = null,
    int? LifetimeHours = null,
    string? Label = null);

/// <summary>
/// Result of issuing a code. <paramref name="Code"/> is the only time the plaintext exists;
/// it is stored hashed and cannot be recovered.
/// </summary>
public record EnrollmentCodeIssuedDto(
    Guid Id,
    string Code,
    string CodePrefix,
    Guid OrganizationId,
    Guid BranchId,
    DeviceRole DeviceRole,
    DateTimeOffset ExpiresAtUtc,
    string? Label);

/// <summary>Administrative view of a code. Deliberately carries no secret material.</summary>
public record EnrollmentCodeDto(
    Guid Id,
    string CodePrefix,
    Guid BranchId,
    DeviceRole DeviceRole,
    DeviceEnrollmentCodeStatus Status,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RedeemedAtUtc,
    Guid? RedeemedByDeviceId,
    int FailedAttempts,
    string? Label,
    DateTimeOffset CreatedAt);

/// <summary>
/// A handset redeeming a code. It supplies only its own hardware details: organization,
/// branch and role come from the code, so a leaked code cannot widen its own scope.
/// </summary>
public record RedeemEnrollmentCodeRequest(
    string Code,
    string DeviceIdentifier,
    string Name,
    string Platform,
    string Network,
    string AppVersion,
    string OsVersion);

/// <summary>Issued to a freshly enrolled device.</summary>
public record DeviceEnrolledDto(
    Guid DeviceId,
    Guid OrganizationId,
    Guid BranchId,
    string Name,
    DeviceRole Role,
    DeviceStatus Status,
    DateTimeOffset EnrolledAtUtc);

/// <summary>
/// A device's own live state.
/// </summary>
/// <remarks>
/// Lets a handset discover revocation directly instead of inferring it from a failed sync.
/// <paramref name="IsRevoked"/> and <paramref name="Status"/> are machine-readable so the
/// client can clear local credentials deterministically rather than parsing a message.
/// </remarks>
public record DeviceSelfDto(
    Guid DeviceId,
    Guid OrganizationId,
    Guid BranchId,
    string Name,
    string DeviceIdentifier,
    DeviceRole Role,
    DeviceStatus Status,
    bool IsRevoked,
    string Platform,
    string Network,
    string AppVersion,
    string OsVersion,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? LastSyncAtUtc,
    // Retained verbatim for backward compatibility. Existing clients read this list of
    // action strings ("sync.submit", "sms.capture"); removing it would break them. New
    // clients should read PlatformCapabilities, which is the structured, authoritative form.
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ServerTimeUtc)
{
    /// <summary>
    /// The branch's name, for display on the handset.
    /// </summary>
    /// <remarks>
    /// The device already knows its <see cref="BranchId"/>, but an identifier is not
    /// something to show an agent — the dashboard printed a raw UUID at them. Sent as an
    /// init-only property rather than a constructor parameter so existing callers of the
    /// positional record keep compiling, and null-tolerant on the client so an older server
    /// simply shows nothing rather than failing to deserialise.
    /// </remarks>
    public string? BranchName { get; init; }

    /// <summary>
    /// Validated platform and form factor. Clients read this instead of inferring a platform
    /// from the legacy <c>Platform</c> string.
    /// </summary>
    public DeviceType DeviceType { get; init; } = DeviceType.Other;

    /// <summary>
    /// What this platform can technically do, decided server-side by
    /// <see cref="PlatformCapabilityPolicy"/>.
    /// </summary>
    /// <remarks>
    /// The client must branch on this rather than on its own platform. That is what allows
    /// an iPhone to be told, authoritatively, that SMS capture is unavailable and manual
    /// capture is the method — without the app hardcoding an assumption that could drift
    /// from the server.
    /// </remarks>
    public IReadOnlyList<PlatformCapability> PlatformCapabilities { get; init; } = [];

    /// <summary>
    /// Convenience flag mirroring <see cref="PlatformCapability.SmsCapture"/>. Present so a
    /// client never re-derives the product-level question "can this device capture SMS?"
    /// </summary>
    public bool CanCaptureSms { get; init; }

    /// <summary>Sync limits the device must honour, so nothing is hardcoded client-side.</summary>
    public DeviceSyncConfigurationDto? SyncConfiguration { get; init; }
}

/// <summary>
/// Sync limits reported to a device.
/// </summary>
/// <remarks>
/// Served so a client re-chunks from the server's value rather than a compiled-in constant.
/// Without it, a device discovers the batch limit only by being rejected with 413.
/// </remarks>
public record DeviceSyncConfigurationDto(
    int MaxBatchSize,
    int MaxClockSkewAheadSeconds,
    int MaxBacklogAgeDays);

public interface IDeviceService
{
    Task<DeviceDto> RegisterDeviceAsync(CreateDeviceRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a device and kills every session bound to it.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Marking the device revoked stops it starting a new session; killing
    /// its sessions stops the one it already has from being refreshed. Doing only the first
    /// would leave a stolen handset working until its refresh token expired on its own.
    /// </remarks>
    /// <param name="requiredBranchId">
    /// The one branch the caller may act in, or <c>null</c> when they legitimately act across
    /// the whole organization. A branch-scoped caller holds <c>device.manage</c> too, so
    /// organization scoping alone would let them revoke any handset in the business — the
    /// listing hides such a device from them, but hiding an id is not an authorisation
    /// control, and the endpoint takes whatever id it is given.
    /// </param>
    Task RevokeDeviceAsync(
        Guid deviceId,
        Guid organizationId,
        Guid actorUserId,
        Guid? requiredBranchId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A handset redeeming an activation code. Anonymous: there is no caller identity yet.
/// </summary>
/// <remarks>
/// Note what is absent. No organization, no branch, no role, no user. Those are read from the
/// code server-side, because a client that could name its own organization could join any
/// tenant. The handset supplies only facts about itself.
/// </remarks>
public record ActivateDeviceRequest(
    string Code,
    string DeviceIdentifier,
    string? Name = null,
    string? Platform = null,
    string? Network = null,
    string? AppVersion = null,
    string? OsVersion = null);

/// <summary>What the handset is told after a successful activation.</summary>
/// <remarks>
/// Carries the session and the worker's own context — their name, their branch, their
/// business — because the first screen after activation shows it. It carries nothing about
/// the code, nothing about other users, and no internal security state.
/// </remarks>
/// <summary>A keypad phone, linked to its worker by SMS.</summary>
public sealed record KeypadPhoneLink(
    Guid DeviceId,
    Guid OrganizationId,
    Guid BranchId,
    Guid WorkerId,
    string WorkerName,
    string OrganizationName);

public record DeviceActivationResult(
    AuthTokenResult Session,
    Guid DeviceId,
    string DeviceName,
    Guid BranchId,
    string BranchName,
    string OrganizationName,
    string WorkerName);

public interface IDeviceEnrollmentService
{
    /// <summary>
    /// Redeems an activation code with no prior authentication, creating the device and a
    /// session for the worker the code was issued to.
    /// </summary>
    Task<DeviceActivationResult> ActivateAsync(
        ActivateDeviceRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Links a keypad phone to the worker an activation code was issued to, by the phone's
    /// number, when the worker texts the code to Zazi.
    /// </summary>
    /// <remarks>
    /// The same rules as <see cref="ActivateAsync"/> — single use, expiring, bound to a named
    /// worker, and the phone is revocable from the Team page like any other device — but no
    /// app session is issued, because a keypad phone has nowhere to keep one. Everything it
    /// does afterwards arrives by SMS from the linked number.
    /// </remarks>
    Task<KeypadPhoneLink> LinkKeypadPhoneAsync(
        string code,
        string phoneNumber,
        CancellationToken cancellationToken = default);

    Task<EnrollmentCodeIssuedDto> IssueCodeAsync(
        IssueEnrollmentCodeRequest request,
        Guid organizationId,
        Guid branchId,
        Guid issuedByUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnrollmentCodeDto>> GetCodesAsync(
        Guid organizationId,
        Guid? branchId,
        CancellationToken cancellationToken = default);

    Task RevokeCodeAsync(
        Guid organizationId,
        Guid codeId,
        Guid revokedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a code and creates the device. Authenticated but deliberately not
    /// policy-gated on <c>device.manage</c>: enrolment is exactly the case where the caller
    /// does not hold that capability.
    /// </summary>
    Task<DeviceEnrolledDto> RedeemAsync(
        RedeemEnrollmentCodeRequest request,
        Guid organizationId,
        Guid redeemingUserId,
        CancellationToken cancellationToken = default);

    Task<DeviceSelfDto?> GetDeviceSelfAsync(
        Guid organizationId,
        string deviceIdentifier,
        CancellationToken cancellationToken = default);
}

public interface IReconciliationService
{
    Task<ReconciliationResultDto> ReconcileSessionAsync(Guid sessionId, decimal actualCash, decimal actualFloat, CancellationToken cancellationToken = default);
}

public interface IAlertService
{
    Task<IReadOnlyList<AlertDto>> GetAlertsAsync(Guid organizationId, Guid? branchId = null, CancellationToken cancellationToken = default);
    Task<AlertThresholdRequest> SetThresholdAsync(AlertThresholdRequest request, CancellationToken cancellationToken = default);
    Task<int> EvaluateFloatAlertsAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public interface IDashboardService
{
    Task<DashboardSummaryDto> GetOrganizationDashboardAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public record SyncQueueItemDto(
     Guid Id,
     Guid OrganizationId,
     Guid? BranchId,
     Guid? DeviceId,
     Guid? EntityId,
     string EntityType,
     string EventType,
     string Payload,
     SyncStatus Status,
     int RetryCount,
     DateTimeOffset? NextAttemptAtUtc,
     string? ErrorMessage,
     bool IsManual,
     DateTimeOffset CreatedAt);

public record QueueSyncEventRequest(
     Guid OrganizationId,
     Guid? BranchId,
     Guid? DeviceId,
     Guid? EntityId,
     string EntityType,
     string EventType,
     string Payload,
     bool IsManual = false);

public record BranchLedgerSnapshotDto(
     Guid OrganizationId,
     Guid BranchId,
     decimal CashOnHand,
     decimal NetworkFloat,
     string Network,
     decimal Threshold,
     DateTimeOffset UpdatedAt);

public interface ILedgerService
{
    Task<BranchLedgerSnapshotDto> GetBranchLedgerAsync(Guid organizationId, Guid branchId, string network, CancellationToken cancellationToken = default);
    Task ApplyTransactionAsync(FinancialTransaction transaction, CancellationToken cancellationToken = default);
}

/// <summary>
/// An owner creating a worker who will authenticate by activation code, not by password.
/// </summary>
/// <remarks>
/// There is deliberately no email and no password here. A worker operates a business device;
/// requiring them to invent and remember an account to do that is the obstacle this whole
/// change exists to remove. Their identity is created by the owner and proven by redeeming a
/// code on a handset.
/// </remarks>
public record CreateWorkerRequest(
    Guid OrganizationId,
    Guid BranchId,
    string FullName,
    string[] Roles,
    string? PhoneNumber = null);

public interface IAuthService
{
    Task<UserDto> RegisterUserAsync(RegisterUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates a credential-less worker identity. See <see cref="CreateWorkerRequest"/>.</summary>
    Task<UserDto> CreateWorkerAsync(CreateWorkerRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a real session for a worker who has just redeemed an activation code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This method performs no authentication.</b> It presumes the caller has already
    /// proven the worker's identity by redeeming a valid, unexpired, unused, unrevoked code
    /// and binding it to a device. It exists so activation produces the same
    /// <c>AuthSession</c> and refresh family that login produces, rather than a parallel
    /// "worker session" that the rest of the security model would not recognise.
    /// </para>
    /// <para>
    /// It is deliberately not reachable over HTTP. The only caller is the activation path in
    /// <c>IDeviceEnrollmentService</c>, which calls it inside the same transaction as the
    /// code claim — so a session cannot exist without the code that authorised it.
    /// </para>
    /// </remarks>
    Task<AuthTokenResult> IssueActivationSessionAsync(
        Guid userId,
        Guid deviceId,
        CancellationToken cancellationToken = default);
    Task<AuthTokenResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<AuthTokenResult> RefreshTokenAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserDto>> GetUsersAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public record SmsParsedTransaction(
    string Provider,
    string Network,
    string TransactionType,
    decimal Amount,
    string? CustomerPhoneNumber,
    string? ProviderReference,
    DateTimeOffset TransactionTimestampUtc,
    DateTimeOffset ReceivedAtUtc,
    decimal ConfidenceScore,
    string ParserVersion,
    bool IsValid,
    string Fingerprint,
    string SourceType,
    Guid? DeviceId,
    Guid? SourceDeviceId);

public interface ISmsTransactionParser
{
    string ProviderName { get; }
    bool CanHandle(string provider, string rawMessage);
    SmsParsedTransaction Parse(string rawMessage, string provider, string? sourcePhoneNumber, Guid? deviceId, Guid? branchId, Guid organizationId, Guid? sourceDeviceId);
}

public interface IOfflineSyncService
{
    Task<SyncQueueItemDto> QueueEventAsync(QueueSyncEventRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncQueueItemDto>> GetPendingAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<int> ProcessPendingAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<SyncQueueItemDto> MarkDeadLetterAsync(Guid organizationId, Guid itemId, string reason, CancellationToken cancellationToken = default);
}

public interface ISmsProcessingService
{
    /// <summary>
    /// Parses captured SMS evidence and, when it is valid and not a duplicate, records a
    /// transaction against it.
    /// </summary>
    /// <param name="submittedByUserId">
    /// The authenticated user submitting the evidence. This becomes the transaction's agent.
    /// It is a separate parameter rather than a field on the request because it must come
    /// from the access token, never from client-supplied data.
    /// </param>
    Task<SmsParseResultDto> ProcessIncomingSmsAsync(
        SmsCaptureRequest request,
        Guid submittedByUserId,
        CancellationToken cancellationToken = default);
}

public interface IDeviceLinkService
{
    Task<DeviceLinkDto> LinkDevicesAsync(Guid organizationId, Guid branchId, Guid sourceDeviceId, Guid linkedDeviceId, string linkType, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceLinkDto>> GetDeviceLinksAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public record DeviceLinkDto(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    Guid SourceDeviceId,
    Guid LinkedDeviceId,
    string LinkType,
    string Status,
    DateTimeOffset LinkedAtUtc);

public interface IAuthorizationPolicy
{
    bool CanViewOrganization(Guid userId, Guid organizationId);
    bool CanManageBranch(Guid userId, Guid branchId);
    bool CanRecordTransaction(Guid userId, Guid branchId);
    bool CanCloseSession(Guid userId, Guid sessionId);
}
