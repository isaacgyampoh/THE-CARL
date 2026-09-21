using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zazi.Api.Security;
using Zazi.Application.Security;
using Zazi.Application.Statements;

namespace Zazi.Api.Controllers;

/// <summary>
/// Statements for the handset: an agent downloads their own, for any day, week, month or year.
/// </summary>
[ApiController]
[Route("api/v1/statements")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class StatementsController : ControllerBase
{
    private readonly IStatementService _statements;
    private readonly ICurrentUserContext _currentUser;

    public StatementsController(IStatementService statements, ICurrentUserContext currentUser)
    {
        _statements = statements;
        _currentUser = currentUser;
    }

    [HttpGet]
    [Authorize(Policy = ZaziPolicies.TransactionRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Download(
        [FromQuery] StatementPeriod period,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] Guid? agentId,
        [FromQuery] string? customer,
        [FromQuery] StatementFormat format = StatementFormat.Pdf,
        CancellationToken cancellationToken = default)
    {
        // An agent's statement is their own, whatever the query says. Someone with
        // organisation-wide scope may ask for one agent or everyone; a branch-scoped caller is
        // confined to their branch.
        var supervises = _currentUser.HasOrganizationWideScope
            || _currentUser.IsInRole(ZaziRoles.BranchManager)
            || _currentUser.IsInRole(ZaziRoles.Supervisor);
        var ownOnly = !supervises;

        var request = new StatementRequest(
            _currentUser.OrganizationId,
            period,
            from,
            to,
            BranchId: _currentUser.HasOrganizationWideScope ? null : _currentUser.BranchId,
            AgentId: ownOnly ? _currentUser.UserId : agentId,
            CustomerPhone: customer);

        try
        {
            var file = await _statements.RenderAsync(request, format, cancellationToken);
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (ArgumentException problem)
        {
            ModelState.AddModelError(nameof(period), problem.Message);
            return ValidationProblem(ModelState);
        }
    }
}
