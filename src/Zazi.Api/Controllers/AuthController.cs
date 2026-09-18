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
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ICurrentUserContext _currentUser;
    private readonly ITenantGuard _tenantGuard;

    public AuthController(IAuthService authService, ICurrentUserContext currentUser, ITenantGuard tenantGuard)
    {
        _authService = authService;
        _currentUser = currentUser;
        _tenantGuard = tenantGuard;
    }

    /// <summary>
    /// Creates a staff member inside the caller's organization.
    /// </summary>
    /// <remarks>
    /// This endpoint was previously anonymous, which allowed anyone to create a user in any
    /// organization — including an OWNER. It now requires an authenticated caller holding
    /// staff-management rights, and the organization is taken from their token.
    /// </remarks>
    [HttpPost("staff")]
    [Authorize(Policy = ZaziPolicies.StaffManage)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<UserDto>> RegisterStaff(
        [FromBody] RegisterStaffApiRequest request,
        CancellationToken cancellationToken)
    {
        // A BRANCH_MANAGER may only create staff in a branch they control.
        Guid? branchId = null;
        if (request.BranchId is { } requested)
        {
            await _tenantGuard.EnsureBranchInTenantAsync(requested, cancellationToken);
            branchId = requested;
        }
        else if (!_currentUser.HasOrganizationWideScope)
        {
            branchId = _currentUser.BranchId;
        }

        // Only organization-wide roles may mint other organization-wide roles; a branch
        // manager must not be able to escalate by creating an OWNER.
        if (!_currentUser.HasOrganizationWideScope)
        {
            foreach (var role in request.Roles)
            {
                var canonical = ZaziRoles.Normalize(role);
                if (canonical is null || ZaziRoles.IsOrganizationWide(canonical))
                {
                    throw new TenantAccessDeniedException(
                        "The caller may not grant organization-wide roles.");
                }
            }
        }

        var user = await _authService.RegisterUserAsync(
            new RegisterUserRequest(
                _currentUser.OrganizationId,
                branchId,
                request.FullName,
                request.Email,
                request.Password,
                request.PhoneNumber,
                request.Roles),
            cancellationToken);

        return CreatedAtAction(nameof(GetUsers), null, user);
    }

    /// <summary>
    /// Creates a worker who authenticates by activation code rather than by password.
    /// </summary>
    /// <remarks>
    /// The owner-facing half of worker activation. The worker gets a name, a branch and a
    /// role — no address and no credential — and the owner then issues them an activation
    /// code bound to this identity.
    /// <para>
    /// Branch-scoped by the same guard as staff registration, and organization-wide roles are
    /// refused outright: a worker who could hold one would see the whole business rather than
    /// their branch.
    /// </para>
    /// </remarks>
    [HttpPost("workers")]
    [Authorize(Policy = ZaziPolicies.StaffManage)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<UserDto>> CreateWorker(
        [FromBody] CreateWorkerApiRequest request,
        CancellationToken cancellationToken)
    {
        // A branch manager may only create workers in a branch they control.
        await _tenantGuard.EnsureBranchInTenantAsync(request.BranchId, cancellationToken);

        var worker = await _authService.CreateWorkerAsync(
            new CreateWorkerRequest(
                _currentUser.OrganizationId,
                request.BranchId,
                request.FullName,
                request.Roles,
                request.PhoneNumber),
            cancellationToken);

        return CreatedAtAction(nameof(GetUsers), null, worker);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Authentication)]
    [ProducesResponseType(typeof(AuthTokenResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthTokenResult>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request, cancellationToken);
        return Ok(result);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Authentication)]
    [ProducesResponseType(typeof(AuthTokenResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthTokenResult>> Refresh(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.RefreshTokenAsync(request, cancellationToken);
        return Ok(result);
    }

    [HttpGet("users")]
    [Authorize(Policy = ZaziPolicies.StaffManage)]
    [ProducesResponseType(typeof(IReadOnlyList<UserDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> GetUsers(CancellationToken cancellationToken)
    {
        var users = await _authService.GetUsersAsync(_currentUser.OrganizationId, cancellationToken);

        // Branch-scoped managers see only their own branch's staff. The rule lives in
        // BranchScoping so the owner portal, which calls the service directly, gets the same
        // answer as this endpoint.
        return Ok(_currentUser.LimitToVisibleBranch(users, u => u.BranchId));
    }

    [HttpGet("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetCurrentUser()
    {
        return Ok(new
        {
            userId = _currentUser.UserId,
            organizationId = _currentUser.OrganizationId,
            branchId = _currentUser.BranchId,
            roles = _currentUser.Roles,
            organizationWideScope = _currentUser.HasOrganizationWideScope
        });
    }
}
