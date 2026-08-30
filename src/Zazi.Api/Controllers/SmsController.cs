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
public class SmsController : ControllerBase
{
    private readonly ISmsProcessingService _smsProcessingService;
    private readonly ICurrentUserContext _currentUser;
    private readonly ITenantGuard _tenantGuard;

    public SmsController(
        ISmsProcessingService smsProcessingService,
        ICurrentUserContext currentUser,
        ITenantGuard tenantGuard)
    {
        _smsProcessingService = smsProcessingService;
        _currentUser = currentUser;
        _tenantGuard = tenantGuard;
    }

    /// <summary>
    /// Submits captured SMS evidence for parsing.
    /// </summary>
    /// <remarks>
    /// This endpoint creates financial records from its input, so it was the single most
    /// damaging anonymous endpoint on the API: anyone could have injected transactions into
    /// any organization. It now requires an authenticated caller with evidence-submission
    /// rights, and both the organization and the branch are established server-side.
    /// </remarks>
    [HttpPost("capture")]
    [Authorize(Policy = ZaziPolicies.EvidenceSubmit)]
    [ProducesResponseType(typeof(SmsParseResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SmsParseResultDto>> Capture(
        [FromBody] SmsCaptureApiRequest request,
        CancellationToken cancellationToken)
    {
        var branchId = await _tenantGuard.ResolveWritableBranchAsync(request.BranchId, cancellationToken);

        if (request.DeviceId is { } deviceId)
        {
            await _tenantGuard.EnsureDeviceInTenantAsync(deviceId, cancellationToken);
        }

        var result = await _smsProcessingService.ProcessIncomingSmsAsync(
            new SmsCaptureRequest(
                _currentUser.OrganizationId,
                branchId,
                request.DeviceId,
                request.SourcePhoneNumber,
                request.RawMessage,
                request.ProviderHint,
                request.MessageTimestampUtc),
            _currentUser.UserId,
            cancellationToken);

        return Ok(result);
    }
}
