namespace Zazi.Application.Security;

/// <summary>
/// Named authorization policies. Controllers reference these instead of listing roles
/// inline, so the role-to-capability mapping lives in exactly one place.
/// </summary>
public static class ZaziPolicies
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
            [PlatformAdministration] = [ZaziRoles.PlatformAdmin],

            [OrganizationRead] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.Auditor],
            [OrganizationManage] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner],

            [BranchRead] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Agent, ZaziRoles.Auditor],
            [BranchManage] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin],

            [StaffManage] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager],

            [TransactionRead] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Agent, ZaziRoles.Auditor],
            [TransactionRecord] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Agent],

            [SessionRead] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Agent, ZaziRoles.Auditor],
            [SessionManage] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Agent],

            [ReconciliationRead] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Auditor],
            [ReconciliationPerform] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor],

            [DeviceRead] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Agent, ZaziRoles.Auditor],
            [DeviceManage] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager],

            [AlertRead] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Auditor],
            [AlertManage] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager],

            [SyncSubmit] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Agent],
            [SyncAdminister] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin],

            [EvidenceSubmit] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Agent],

            [AuditRead] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.Auditor],

            [DashboardOrganization] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.Auditor],
            [DashboardBranch] = [ZaziRoles.PlatformAdmin, ZaziRoles.Owner, ZaziRoles.OrganizationAdmin, ZaziRoles.BranchManager, ZaziRoles.Supervisor, ZaziRoles.Auditor]
        };
}
