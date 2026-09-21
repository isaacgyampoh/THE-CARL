using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zazi.Api.Security;
using Zazi.Application.Closing;
using Zazi.Application.Security;

namespace Zazi.Api.Controllers;

/// <summary>
/// Closing the day from the handset: the agent counts their cash and float, and is told at once
/// whether it agrees with what they recorded.
/// </summary>
[ApiController]
[Route("api/v1/day-close")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class DayCloseController : ControllerBase
{
    private readonly IDayCloseService _closes;
    private readonly ICurrentUserContext _currentUser;

    public DayCloseController(IDayCloseService closes, ICurrentUserContext currentUser)
    {
        _closes = closes;
        _currentUser = currentUser;
    }

    /// <summary>Records the signed-in agent's own count. Never anyone else's.</summary>
    [HttpPost]
    [Authorize(Policy = ZaziPolicies.TransactionRecord)]
    [ProducesResponseType(typeof(DayCloseResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DayCloseResult>> Close(
        [FromBody] DayCloseApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _closes.CloseAsync(
                new DayCloseRequest(
                    _currentUser.OrganizationId,
                    _currentUser.BranchId ?? Guid.Empty,
                    _currentUser.UserId,
                    request.CountedCash,
                    request.CountedFloat,
                    DayCloseChannels.App,
                    request.Note),
                cancellationToken);
            return Ok(result);
        }
        catch (DayCloseRejectedException rejected)
        {
            return BadRequest(new { error = "invalid_count", message = rejected.Message });
        }
    }

    /// <summary>The signed-in agent's latest close; 204 if they have never closed.</summary>
    [HttpGet("latest")]
    [Authorize(Policy = ZaziPolicies.TransactionRead)]
    [ProducesResponseType(typeof(DayCloseResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<DayCloseResult>> Latest(CancellationToken cancellationToken)
    {
        var latest = await _closes.LatestAsync(_currentUser.OrganizationId, _currentUser.UserId, cancellationToken);
        return latest is null ? NoContent() : Ok(latest);
    }
}

public sealed record DayCloseApiRequest(decimal CountedCash, decimal CountedFloat, string? Note);
