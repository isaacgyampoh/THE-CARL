using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zazi.Api.Models;
using Zazi.Api.Security;
using Zazi.Application;
using Zazi.Application.Security;

namespace Zazi.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class BranchesController : ControllerBase
{
    private readonly IOrganizationService _organizationService;
    private readonly ICurrentUserContext _currentUser;

    public BranchesController(IOrganizationService organizationService, ICurrentUserContext currentUser)
    {
        _organizationService = organizationService;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Lists branches in the caller's organization. The route no longer accepts an
    /// organization id — it is read from the access token, so there is nothing to tamper with.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = ZaziPolicies.BranchRead)]
    [ProducesResponseType(typeof(IReadOnlyList<BranchDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BranchDto>>> GetAll(CancellationToken cancellationToken)
    {
        var branches = await _organizationService.GetBranchesAsync(_currentUser.OrganizationId, cancellationToken);

        // Branch-scoped staff see only their own branch in the list.
        if (!_currentUser.HasOrganizationWideScope)
        {
            var assigned = _currentUser.BranchId;
            branches = branches.Where(b => b.Id == assigned).ToList();
        }

        return Ok(branches);
    }

    [HttpPost]
    [Authorize(Policy = ZaziPolicies.BranchManage)]
    [ProducesResponseType(typeof(BranchDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<BranchDto>> Create(
        [FromBody] CreateBranchApiRequest request,
        CancellationToken cancellationToken)
    {
        var branch = await _organizationService.CreateBranchAsync(
            new CreateBranchRequest(_currentUser.OrganizationId, request.Name, request.Location),
            cancellationToken);

        return branch is null
            ? NotFound()
            : CreatedAtAction(nameof(GetAll), null, branch);
    }
}
