using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zazi.Api.Security;
using Zazi.Application.Float;
using Zazi.Application.Security;

namespace Zazi.Api.Controllers;

/// <summary>
/// What the signed-in agent is holding: the cash in their hand and the float on each network,
/// as the ledger has it. The answer to "how much have I got left" without calling the owner.
/// </summary>
[ApiController]
[Route("api/v1/balances")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class BalancesController : ControllerBase
{
    private readonly IFloatService _floats;
    private readonly ICurrentUserContext _currentUser;

    public BalancesController(IFloatService floats, ICurrentUserContext currentUser)
    {
        _floats = floats;
        _currentUser = currentUser;
    }

    /// <summary>The caller's own holdings. Never anyone else's, whatever the request says.</summary>
    [HttpGet("mine")]
    [Authorize(Policy = ZaziPolicies.TransactionRead)]
    [ProducesResponseType(typeof(MyBalancesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<MyBalancesDto>> Mine(CancellationToken cancellationToken)
    {
        var ledger = await _floats.GetAgentLedgerAsync(
            _currentUser.OrganizationId, _currentUser.UserId, historyLimit: 1, cancellationToken);
        if (ledger is null)
        {
            return Ok(new MyBalancesDto(0m, [], 0m, 0m, 0m));
        }

        return Ok(new MyBalancesDto(
            ledger.Cash,
            ledger.Floats.Select(f => new NetworkFloatDto(f.Network, f.Amount)).ToArray(),
            ledger.TotalFloat,
            ledger.Today.CashAllocated + ledger.Today.CashAdjusted,
            ledger.Today.FloatAllocated + ledger.Today.FloatAdjusted));
    }
}

/// <param name="CashGivenToday">What the owner handed over today, net of anything taken back.</param>
public sealed record MyBalancesDto(
    decimal Cash,
    IReadOnlyList<NetworkFloatDto> Floats,
    decimal TotalFloat,
    decimal CashGivenToday,
    decimal FloatGivenToday)
{
    public decimal Total => Cash + TotalFloat;
}
