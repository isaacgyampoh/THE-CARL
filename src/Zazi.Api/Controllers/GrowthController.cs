using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zazi.Api.Security;
using Zazi.Application.Growth;
using Zazi.Application.Security;

namespace Zazi.Api.Controllers;

/// <summary>
/// Float requests from the handset. An agent asks for their own float and sees their own
/// requests; answering is the owner's, in the portal or by SMS.
/// </summary>
[ApiController]
[Route("api/v1/float-requests")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class FloatRequestsController : ControllerBase
{
    private readonly IFloatRequestService _requests;
    private readonly ICurrentUserContext _currentUser;

    public FloatRequestsController(IFloatRequestService requests, ICurrentUserContext currentUser)
    {
        _requests = requests;
        _currentUser = currentUser;
    }

    [HttpPost]
    [Authorize(Policy = ZaziPolicies.TransactionRecord)]
    [ProducesResponseType(typeof(FloatRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FloatRequestDto>> Request(
        [FromBody] FloatRequestApiRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _requests.RequestAsync(
                _currentUser.OrganizationId,
                _currentUser.BranchId ?? Guid.Empty,
                _currentUser.UserId,
                request.Network,
                request.Amount,
                "App",
                cancellationToken));
        }
        catch (FloatRequestRejectedException rejected)
        {
            return BadRequest(new { error = "invalid_float_request", message = rejected.Message });
        }
    }

    [HttpGet("mine")]
    [Authorize(Policy = ZaziPolicies.TransactionRead)]
    public async Task<ActionResult<IReadOnlyList<FloatRequestDto>>> Mine(CancellationToken cancellationToken) =>
        Ok(await _requests.MineAsync(_currentUser.OrganizationId, _currentUser.UserId, 5, cancellationToken));
}

public sealed record FloatRequestApiRequest(string Network, decimal Amount);

/// <summary>
/// The agent's own trading record, as a PDF — to show a lender, from the handset.
/// </summary>
[ApiController]
[Route("api/v1/reports")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reports;
    private readonly ICurrentUserContext _currentUser;

    public ReportsController(IReportService reports, ICurrentUserContext currentUser)
    {
        _reports = reports;
        _currentUser = currentUser;
    }

    [HttpGet("trading-record")]
    [Authorize(Policy = ZaziPolicies.TransactionRead)]
    public async Task<IActionResult> TradingRecord([FromQuery] int months = 12, CancellationToken cancellationToken = default)
    {
        // Always the caller's own: a record shown to a lender speaks for one person.
        var record = await _reports.TradingRecordAsync(
            _currentUser.OrganizationId, null, _currentUser.UserId, months, cancellationToken);
        return File(_reports.TradingRecordPdf(record), "application/pdf",
            $"zazi-trading-record-{record.ToMonth:yyyy-MM}.pdf");
    }
}
