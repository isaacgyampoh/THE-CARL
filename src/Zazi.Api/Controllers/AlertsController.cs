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
public class AlertsController : ControllerBase
{
    private readonly IAlertService _alertService;
    private readonly ICurrentUserContext _currentUser;
    private readonly ITenantGuard _tenantGuard;

    public AlertsController(
        IAlertService alertService,
        ICurrentUserContext currentUser,
        ITenantGuard tenantGuard)
    {
        _alertService = alertService;
        _currentUser = currentUser;
        _tenantGuard = tenantGuard;
    }

    [HttpGet]
    [Authorize(Policy = ZaziPolicies.AlertRead)]
    [ProducesResponseType(typeof(IReadOnlyList<AlertDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AlertDto>>> GetAll(
        [FromQuery] Guid? branchId,
        CancellationToken cancellationToken)
    {
        var effectiveBranchId = await ResolveReadableBranchAsync(branchId, cancellationToken);

        var alerts = await _alertService.GetAlertsAsync(
            _currentUser.OrganizationId,
            effectiveBranchId,
            cancellationToken);

        return Ok(alerts);
    }

    [HttpPost("thresholds")]
    [Authorize(Policy = ZaziPolicies.AlertManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> SetThreshold(
        [FromBody] AlertThresholdApiRequest request,
        CancellationToken cancellationToken)
    {
        Guid? branchId = null;
        if (request.BranchId is { } requested)
        {
            await _tenantGuard.EnsureBranchInTenantAsync(requested, cancellationToken);
            branchId = requested;
        }
        else if (!_currentUser.HasOrganizationWideScope)
        {
            // A branch manager cannot set an organization-wide default threshold.
            branchId = _currentUser.BranchId
                ?? throw new TenantAccessDeniedException(
                    "The caller may not set organization-wide thresholds.");
        }

        var threshold = await _alertService.SetThresholdAsync(
            new AlertThresholdRequest(
                _currentUser.OrganizationId,
                branchId,
                request.Network,
                request.WarningThreshold,
                request.CriticalThreshold,
                request.IsEnabled),
            cancellationToken);

        return Ok(threshold);
    }

    [HttpPost("evaluate")]
    [Authorize(Policy = ZaziPolicies.AlertManage)]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<ActionResult<int>> Evaluate(CancellationToken cancellationToken)
    {
        var count = await _alertService.EvaluateFloatAlertsAsync(_currentUser.OrganizationId, cancellationToken);
        return Ok(count);
    }

    private async Task<Guid?> ResolveReadableBranchAsync(Guid? requestedBranchId, CancellationToken cancellationToken)
    {
        if (_currentUser.HasOrganizationWideScope)
        {
            if (requestedBranchId is { } requested)
            {
                await _tenantGuard.EnsureBranchInTenantAsync(requested, cancellationToken);
            }

            return requestedBranchId;
        }

        return _currentUser.BranchId
            ?? throw new TenantAccessDeniedException(
                "The caller has no branch assignment and no organization-wide scope.");
    }
}
