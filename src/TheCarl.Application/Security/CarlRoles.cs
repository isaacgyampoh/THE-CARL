namespace TheCarl.Application.Security;

/// <summary>
/// Canonical role names for THE CARL. These are the only role names the authorization
/// layer recognises. Historic role names that predate the canonical set are mapped by
/// <see cref="Normalize"/> so that already-provisioned tenants keep working.
/// </summary>
public static class CarlRoles
{
    public const string PlatformAdmin = "PLATFORM_ADMIN";
    public const string Owner = "OWNER";
    public const string OrganizationAdmin = "ORGANIZATION_ADMIN";
    public const string BranchManager = "BRANCH_MANAGER";
    public const string Supervisor = "SUPERVISOR";
    public const string Agent = "AGENT";
    public const string Auditor = "AUDITOR";

    public static readonly IReadOnlyList<string> All =
    [
        PlatformAdmin,
        Owner,
        OrganizationAdmin,
        BranchManager,
        Supervisor,
        Agent,
        Auditor
    ];

    /// <summary>Roles whose visibility spans every branch in their organization.</summary>
    public static readonly IReadOnlyList<string> OrganizationWide =
    [
        PlatformAdmin,
        Owner,
        OrganizationAdmin,
        Auditor
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PlatformAdmin"] = PlatformAdmin,
        ["PLATFORM_ADMIN"] = PlatformAdmin,
        ["OrganizationOwner"] = Owner,
        ["Owner"] = Owner,
        ["OWNER"] = Owner,
        ["OrganizationAdmin"] = OrganizationAdmin,
        ["ORGANIZATION_ADMIN"] = OrganizationAdmin,
        ["BranchManager"] = BranchManager,
        ["BRANCH_MANAGER"] = BranchManager,
        ["Supervisor"] = Supervisor,
        ["SUPERVISOR"] = Supervisor,
        ["Agent"] = Agent,
        ["AGENT"] = Agent,
        ["Auditor"] = Auditor,
        ["AUDITOR"] = Auditor,
        // "Accountant" historically carried read + reconciliation rights, which is what
        // SUPERVISOR now expresses. Mapped rather than dropped so existing users keep access.
        ["Accountant"] = Supervisor
    };

    /// <summary>
    /// Maps a stored or supplied role name onto a canonical role.
    /// Returns <c>null</c> for names that are not recognised; callers must reject those
    /// rather than defaulting to a role, so a typo can never widen access.
    /// </summary>
    public static string? Normalize(string? roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return null;
        }

        return Aliases.TryGetValue(roleName.Trim(), out var canonical) ? canonical : null;
    }

    public static bool IsOrganizationWide(string canonicalRole) =>
        OrganizationWide.Contains(canonicalRole, StringComparer.Ordinal);
}
