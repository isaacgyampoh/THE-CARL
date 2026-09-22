extern alias portal;

using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Zazi.Application.Security;
using NavMenu = portal::Zazi.Web.Components.Layout.NavMenu;

namespace Zazi.IntegrationTests;

/// <summary>The menu offers only pages the signed-in person may open.</summary>
public class NavMenuTests : TestContext
{
    private readonly TestAuthorizationContext _authorization;

    public NavMenuTests()
    {
        // No organisation, so the menu skips reading the business name.
        Services.AddSingleton<ICurrentUserContext>(new NoOrganization());
        _authorization = this.AddTestAuthorization();
        _authorization.SetAuthorized("Kofi Supervisor");
    }

    [Fact]
    public void ASupervisorIsNotShownOwnerPages()
    {
        _authorization.SetPolicies(ZaziPolicies.TransactionRead, ZaziPolicies.ReconciliationRead, ZaziPolicies.DashboardBranch);

        var menu = RenderComponent<NavMenu>().Markup;

        Assert.Contains("Transactions", menu);
        Assert.Contains("Closing", menu);
        Assert.DoesNotContain(">Settings<", menu);
        Assert.DoesNotContain(">Team<", menu);
        Assert.DoesNotContain(">Cash &amp; float<", menu);
        // A heading with nothing under it is not shown either.
        Assert.DoesNotContain(">Business<", menu);
    }

    [Fact]
    public void AnOwnerIsShownEverything()
    {
        _authorization.SetPolicies(ZaziPolicies.TransactionRead, ZaziPolicies.ReconciliationRead, ZaziPolicies.DashboardBranch,
            ZaziPolicies.BranchManage, ZaziPolicies.StaffManage, ZaziPolicies.OrganizationManage, ZaziPolicies.OrganizationRead);

        var menu = RenderComponent<NavMenu>().Markup;

        foreach (var label in new[] { ">Settings<", ">Team<", ">Reports<", ">Operations<", ">Business<" })
        {
            Assert.Contains(label, menu);
        }
    }

    private sealed class NoOrganization : ICurrentUserContext
    {
        public bool IsAuthenticated => true;
        public Guid UserId => Guid.NewGuid();
        public Guid OrganizationId => Guid.Empty;
        public Guid? BranchId => null;
        public IReadOnlyCollection<string> Roles => ["SUPERVISOR"];
        public bool HasOrganizationWideScope => false;
        public string? SecurityStamp => "stamp";
        public string CorrelationId => "test";
        public bool IsInRole(string role) => Roles.Contains(role);
        public bool CanAccessBranch(Guid branchId) => false;
        public void EnsureBranchAccess(Guid branchId) { }
        public void EnsureOrganizationMatches(Guid organizationId) { }
    }
}
