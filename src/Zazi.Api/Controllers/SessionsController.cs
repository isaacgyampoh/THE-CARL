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
public class SessionsController : ControllerBase
{
    private readonly ISessionService _sessionService;
    private readonly ICurrentUserContext _currentUser;
    private readonly ITenantGuard _tenantGuard;

    public SessionsController(
        ISessionService sessionService,
        ICurrentUserContext currentUser,
        ITenantGuard tenantGuard)
    {
        _sessionService = sessionService;
        _currentUser = currentUser;
        _tenantGuard = tenantGuard;
    }

    [HttpGet("{sessionId:guid}")]
    [Authorize(Policy = ZaziPolicies.SessionRead)]
    [ProducesResponseType(typeof(SessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SessionDto>> Get(Guid sessionId, CancellationToken cancellationToken)
    {
        // Ownership is proved before the record is fetched, so a session id belonging to
        // another tenant yields 403 rather than that tenant's cash figures.
        await _tenantGuard.EnsureSessionInTenantAsync(sessionId, cancellationToken);

        var session = await _sessionService.GetSessionAsync(sessionId, cancellationToken);
        return session is null ? NotFound() : Ok(session);
    }

    [HttpPost("open")]
    [Authorize(Policy = ZaziPolicies.SessionManage)]
    [ProducesResponseType(typeof(SessionDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<SessionDto>> Open(
        [FromBody] OpenSessionApiRequest request,
        CancellationToken cancellationToken)
    {
        var branchId = await _tenantGuard.ResolveWritableBranchAsync(request.BranchId, cancellationToken);

        if (request.DeviceId is { } deviceId)
        {
            await _tenantGuard.EnsureDeviceInTenantAsync(deviceId, cancellationToken);
        }

        var session = await _sessionService.OpenSessionAsync(
            new CreateSessionRequest(
                _currentUser.OrganizationId,
                branchId,
                // The session belongs to the authenticated user; accepting a user id from the
                // body would let one agent open a session in another agent's name.
                _currentUser.UserId,
                request.DeviceId,
                request.OpeningCash,
                request.OpeningFloat),
            cancellationToken);

        return CreatedAtAction(nameof(Get), new { sessionId = session.Id }, session);
    }

    [HttpPost("close")]
    [Authorize(Policy = ZaziPolicies.SessionManage)]
    [ProducesResponseType(typeof(SessionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionDto>> Close(
        [FromBody] CloseSessionApiRequest request,
        CancellationToken cancellationToken)
    {
        await _tenantGuard.EnsureSessionInTenantAsync(request.SessionId, cancellationToken);

        var session = await _sessionService.CloseSessionAsync(
            new CloseSessionRequest(request.SessionId, _currentUser.UserId),
            cancellationToken);

        return session is null ? NotFound() : Ok(session);
    }
}
