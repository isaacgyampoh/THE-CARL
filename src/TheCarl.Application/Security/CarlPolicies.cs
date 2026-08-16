namespace TheCarl.Application.Security;

/// <summary>
/// Named authorization policies. Controllers reference these instead of listing roles
/// inline, so the role-to-capability mapping lives in exactly one place.
/// </summary>
public static class CarlPolicies
{
    public const string PlatformAdministration = "platform.administration";
    public const string OrganizationRead = "organization.read";
    public const string OrganizationManage = "organization.manage";
    public const string BranchRead = "branch.read";
    public const string BranchManage = "branch.manage";
    public const string StaffManage = "staff.manage";
    public const string TransactionRead = "transaction.read";
    public const string TransactionRecord = "transaction.record";
    public const string SessionRead = "session.read";
    public const string SessionManage = "session.manage";
    public const string ReconciliationRead = "reconciliation.read";
    public const string ReconciliationPerform = "reconciliation.perform";
    public const string DeviceRead = "device.read";
    public const string DeviceManage = "device.manage";
    public const string AlertRead = "alert.read";
    public const string AlertManage = "alert.manage";
    public const string SyncSubmit = "sync.submit";
    public const string SyncAdminister = "sync.administer";
    public const string EvidenceSubmit = "evidence.submit";
    public const string AuditRead = "audit.read";
    public const string DashboardOrganization = "dashboard.organization";
    public const string DashboardBranch = "dashboard.branch";

    /// <summary>
    /// The single source of truth for which canonical roles satisfy which policy.
    /// AUDITOR is deliberately absent from every mutating policy: auditors are read-only.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> RolesByPolicy =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [PlatformAdministration] = [CarlRoles.PlatformAdmin],

            [OrganizationRead] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.Auditor],
            [OrganizationManage] = [CarlRoles.PlatformAdmin, CarlRoles.Owner],

            [BranchRead] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Agent, CarlRoles.Auditor],
            [BranchManage] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin],

            [StaffManage] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager],

            [TransactionRead] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Agent, CarlRoles.Auditor],
            [TransactionRecord] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Agent],

            [SessionRead] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Agent, CarlRoles.Auditor],
            [SessionManage] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Agent],

            [ReconciliationRead] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Auditor],
            [ReconciliationPerform] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor],

            [DeviceRead] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Agent, CarlRoles.Auditor],
            [DeviceManage] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager],

            [AlertRead] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Auditor],
            [AlertManage] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager],

            [SyncSubmit] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Agent],
            [SyncAdminister] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin],

            [EvidenceSubmit] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Agent],

            [AuditRead] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.Auditor],

            [DashboardOrganization] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.Auditor],
            [DashboardBranch] = [CarlRoles.PlatformAdmin, CarlRoles.Owner, CarlRoles.OrganizationAdmin, CarlRoles.BranchManager, CarlRoles.Supervisor, CarlRoles.Auditor]
        };
}
