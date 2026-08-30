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
public class OrganizationsController : ControllerBase
{
    private readonly IOrganizationService _organizationService;
    private readonly ICurrentUserContext _currentUser;

    public OrganizationsController(IOrganizationService organizationService, ICurrentUserContext currentUser)
    {
        _organizationService = organizationService;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Lists every organization on the platform. Restricted to PLATFORM_ADMIN: this is the
    /// one endpoint that legitimately crosses tenant boundaries, so it is the one endpoint
    /// that must be hardest to reach. It was previously anonymous and enumerated every tenant.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = ZaziPolicies.PlatformAdministration)]
    [ProducesResponseType(typeof(IReadOnlyList<OrganizationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrganizationDto>>> GetAll(CancellationToken cancellationToken)
    {
        var organizations = await _organizationService.GetOrganizationsAsync(cancellationToken);
        return Ok(organizations);
    }

    /// <summary>Returns the caller's own organization.</summary>
    [HttpGet("current")]
    [Authorize(Policy = ZaziPolicies.OrganizationRead)]
    [ProducesResponseType(typeof(OrganizationDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrganizationDto>> GetCurrent(CancellationToken cancellationToken)
    {
        var organizations = await _organizationService.GetOrganizationsAsync(cancellationToken);
        var mine = organizations.SingleOrDefault(x => x.Id == _currentUser.OrganizationId);
        return mine is null ? NotFound() : Ok(mine);
    }

    /// <summary>Provisions a new tenant. Platform administration only.</summary>
    [HttpPost]
    [Authorize(Policy = ZaziPolicies.PlatformAdministration)]
    [ProducesResponseType(typeof(OrganizationDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<OrganizationDto>> Create(
        [FromBody] CreateOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        var organization = await _organizationService.CreateOrganizationAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetAll), new { id = organization.Id }, organization);
    }
}
