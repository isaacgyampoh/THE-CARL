using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TheCarl.Api.Models;
using TheCarl.Api.Security;
using TheCarl.Application;
using TheCarl.Application.Security;

namespace TheCarl.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class ReconciliationController : ControllerBase
{
    private readonly IReconciliationService _reconciliationService;
    private readonly ITenantGuard _tenantGuard;

    public ReconciliationController(IReconciliationService reconciliationService, ITenantGuard tenantGuard)
    {
        _reconciliationService = reconciliationService;
        _tenantGuard = tenantGuard;
    }

    /// <summary>
    /// Reconciles a session's counted cash and float against the expected position.
    /// </summary>
    /// <remarks>
    /// Counted figures move from query string to request body: query strings are routinely
    /// written to access logs and proxy logs, and a branch's cash position does not belong there.
    /// </remarks>
    [HttpPost("sessions/{sessionId:guid}/reconcile")]
    [Authorize(Policy = CarlPolicies.ReconciliationPerform)]
    [ProducesResponseType(typeof(ReconciliationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ReconciliationResultDto>> ReconcileSession(
        Guid sessionId,
        [FromBody] ReconcileSessionApiRequest request,
        CancellationToken cancellationToken)
    {
        await _tenantGuard.EnsureSessionInTenantAsync(sessionId, cancellationToken);

        var result = await _reconciliationService.ReconcileSessionAsync(
            sessionId,
            request.ActualCash,
            request.ActualFloat,
            cancellationToken);

        return Ok(result);
    }
}
