namespace TheCarl.Application.Security;

/// <summary>Claim names emitted on THE CARL access tokens.</summary>
public static class CarlClaimTypes
{
    public const string OrganizationId = "org_id";
    public const string BranchId = "branch_id";
    public const string SecurityStamp = "sec_stamp";
    public const string DeviceIdentifier = "device_id";

    /// <summary>
    /// Roles are emitted as one claim per role. A single comma-joined claim does not work
    /// with role-based authorization: it is compared as an opaque string, so a user holding
    /// "AGENT,BRANCH_MANAGER" would match neither role.
    /// </summary>
    public const string Role = "role";
}
