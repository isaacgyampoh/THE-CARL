using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zazi.Api.Security;
using Zazi.Application;
using Zazi.Application.Security;

namespace Zazi.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboardService;
    private readonly ICurrentUserContext _currentUser;

    public DashboardController(IDashboardService dashboardService, ICurrentUserContext currentUser)
    {
        _dashboardService = dashboardService;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Organization-wide operating summary for owners and administrators.
    /// The organization is taken from the token; the route no longer names one.
    /// </summary>
    [HttpGet("organization")]
    [Authorize(Policy = ZaziPolicies.DashboardOrganization)]
    [ProducesResponseType(typeof(DashboardSummaryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardSummaryDto>> GetOrganizationSummary(CancellationToken cancellationToken)
    {
        var summary = await _dashboardService.GetOrganizationDashboardAsync(
            _currentUser.OrganizationId,
            cancellationToken);

        return Ok(summary);
    }
}
