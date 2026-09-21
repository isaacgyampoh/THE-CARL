using System.ComponentModel.DataAnnotations;
using Zazi.Domain;

namespace Zazi.Api.Models;

// Validation attributes on a record's primary-constructor parameters must target the
// parameter, not the generated property. MVC throws when it finds validation metadata on a
// record property instead of the constructor parameter, so no `[property: ...]` here.

/// <summary>
/// Client-facing request models.
/// <para>
/// None of these carry an <c>OrganizationId</c>. The tenant is always taken from the
/// caller's access token, so it is structurally impossible for a client to address another
/// organization by supplying a different identifier. Likewise, actor identity (agent id,
/// user id) is derived from the token rather than accepted from the body.
/// </para>
/// </summary>
public sealed record CreateBranchApiRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [StringLength(255)] string? Location);

public sealed record OpenSessionApiRequest(
    Guid? BranchId,
    Guid? DeviceId,
    [Range(0, 99_999_999.99)] decimal OpeningCash,
    [Range(0, 99_999_999.99)] decimal OpeningFloat,
    /// <summary>
    /// Which network the opening float is held on — MTN, Telecel or AirtelTigo.
    /// </summary>
    /// <remarks>
    /// Required whenever <see cref="OpeningFloat"/> is not zero. The service used to assume
    /// MTN when this was unsaid, and this record had no field to say it with, so every session
    /// opened over HTTP filed its opening float against MTN whatever the agent was actually
    /// holding. Optional at zero because there is then no balance to misattribute.
    /// </remarks>
    [StringLength(32)] string? Network = null);

public sealed record CloseSessionApiRequest(
    [Required] Guid SessionId,
    [StringLength(1000)] string? Notes);

public sealed record CreateTransactionApiRequest(
    Guid? BranchId,
    Guid? DeviceId,
    [Required, StringLength(80)] string Network,
    [Required] TransactionType Type,
    [Range(0.0001, 99_999_999.99)] decimal Amount,
    [StringLength(10)] string? Currency,
    [StringLength(30)] string? CustomerPhoneNumber,
    [StringLength(200)] string? ProviderReference,
    [StringLength(1000)] string? Notes);

public sealed record RegisterDeviceApiRequest(
    Guid? BranchId,
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required, StringLength(200, MinimumLength = 1)] string DeviceIdentifier,
    [Required, StringLength(50)] string Platform,
    [Required, StringLength(50)] string Network,
    [Required] DeviceRole Role,
    [StringLength(50)] string AppVersion,
    [StringLength(80)] string OsVersion);

public sealed record RegisterStaffApiRequest(
    Guid? BranchId,
    [Required, StringLength(200, MinimumLength = 1)] string FullName,
    [Required, EmailAddress, StringLength(255)] string Email,
    [Required, StringLength(256, MinimumLength = 12)] string Password,
    [StringLength(30)] string? PhoneNumber,
    [Required, MinLength(1)] string[] Roles);

public sealed record QueueSyncApiRequest(
    Guid? BranchId,
    Guid? DeviceId,
    Guid? EntityId,
    [Required, StringLength(100)] string EntityType,
    [Required, StringLength(100)] string EventType,
    [Required, StringLength(65536, MinimumLength = 1)] string Payload,
    bool IsManual = false);

public sealed record SmsCaptureApiRequest(
    Guid? BranchId,
    Guid? DeviceId,
    [Required, StringLength(50)] string SourcePhoneNumber,
    [Required, StringLength(2000, MinimumLength = 1)] string RawMessage,
    [StringLength(80)] string? ProviderHint,
    DateTimeOffset? MessageTimestampUtc);

public sealed record AlertThresholdApiRequest(
    Guid? BranchId,
    [Required, StringLength(80)] string Network,
    [Range(0, 99_999_999.99)] decimal WarningThreshold,
    [Range(0, 99_999_999.99)] decimal CriticalThreshold,
    bool IsEnabled = true);

public sealed record ReconcileSessionApiRequest(
    [Range(0, 99_999_999.99)] decimal ActualCash,
    [Range(0, 99_999_999.99)] decimal ActualFloat,
    [StringLength(1000)] string? Explanation);

/// <summary>Page request used by list endpoints.</summary>
public sealed record PageQuery
{
    private const int MaxPageSize = 200;

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, MaxPageSize)]
    public int PageSize { get; init; } = 50;

    /// <summary>Clamped so an oversized or absent page size can never mean "everything".</summary>
    public int Take => Math.Clamp(PageSize, 1, MaxPageSize);
}

/// <summary>
/// Issue a device enrolment code. Scope is decided here by an authorised issuer, never by
/// the handset that later redeems it.
/// </summary>
public sealed record IssueEnrollmentCodeApiRequest(
    Guid? BranchId,
    DeviceRole DeviceRole = DeviceRole.TransactionDevice,
    Guid? IntendedUserId = null,
    [Range(1, 168)] int? LifetimeHours = null,
    [StringLength(120)] string? Label = null);

/// <summary>
/// Redeem an enrolment code. The handset supplies only its own details; organization,
/// branch and role come from the code.
/// </summary>
public sealed record RedeemEnrollmentCodeApiRequest(
    [Required, StringLength(64, MinimumLength = 8)] string Code,
    [Required, StringLength(200, MinimumLength = 8)] string DeviceIdentifier,
    [Required, StringLength(200)] string Name,
    [StringLength(50)] string? Platform,
    [StringLength(50)] string? Network,
    [StringLength(50)] string? AppVersion,
    [StringLength(80)] string? OsVersion);

/// <summary>
/// A handset activating itself with an owner-issued code, before it has any identity.
/// </summary>
/// <remarks>
/// There is no organization, branch, role or user field here, and that omission is the
/// security property. All of those are read from the code server-side. A client that could
/// name its own organization could join any tenant it liked.
/// </remarks>
public sealed record ActivateDeviceApiRequest(
    string Code,
    string DeviceIdentifier,
    string? Name = null,
    string? Platform = null,
    string? Network = null,
    string? AppVersion = null,
    string? OsVersion = null);

/// <summary>An owner creating a worker who will activate by code. No email, no password.</summary>
public sealed record CreateWorkerApiRequest(
    Guid BranchId,
    string FullName,
    string[] Roles,
    string? PhoneNumber = null);
