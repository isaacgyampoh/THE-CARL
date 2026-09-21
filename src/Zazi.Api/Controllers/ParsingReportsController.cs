using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zazi.Api.Models;
using Zazi.Api.Security;
using Zazi.Application.Parsing;
using Zazi.Application.Security;

namespace Zazi.Api.Controllers;

/// <summary>
/// The path by which a real provider message reaches whoever maintains the parser.
/// </summary>
/// <remarks>
/// <para>
/// Raw message bodies deliberately do not sync: the ordinary payload carries the parsed result
/// and nothing else, which is the right default for a system that sits on other people's
/// financial correspondence. The consequence is that when a transaction comes out wrong, the
/// message that caused it cannot be seen from here at all.
/// </para>
/// <para>
/// This endpoint is the exception, and it is narrow on purpose. One body, about one transaction,
/// sent because an agent decided to send it. Nothing enables it in bulk and nothing sends
/// anything in the background.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/parsing-reports")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class ParsingReportsController : ControllerBase
{
    private readonly IParsingReportService _reports;
    private readonly ICurrentUserContext _currentUser;
    private readonly ITenantGuard _tenantGuard;

    public ParsingReportsController(
        IParsingReportService reports,
        ICurrentUserContext currentUser,
        ITenantGuard tenantGuard)
    {
        _reports = reports;
        _currentUser = currentUser;
        _tenantGuard = tenantGuard;
    }

    /// <summary>Reports that a message was read wrongly, and sends the message.</summary>
    [HttpPost]
    [Authorize(Policy = ZaziPolicies.EvidenceSubmit)]
    [ProducesResponseType(typeof(ParsingReportReceipt), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ParsingReportReceipt>> Submit(
        [FromBody] SubmitParsingReportApiRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DeviceId is { } deviceId)
        {
            // A handset may name itself, so the claim is checked. Knowing which device read a
            // message is useful when one build parses differently from another.
            await _tenantGuard.EnsureDeviceInTenantAsync(deviceId, cancellationToken);
        }

        var receipt = await _reports.SubmitAsync(
            new SubmitParsingReportRequest(
                request.ClientTransactionId,
                request.RawMessage,
                request.Verdict,
                request.SenderIdentity,
                request.ObservedNetwork,
                request.ObservedType,
                request.ObservedAmountMinor,
                request.Note,
                request.ParserVersion,
                request.AppVersion),
            // All four from the token. A body that could name its own organisation would let
            // one agent file a report against another tenant's transaction.
            _currentUser.OrganizationId,
            _currentUser.UserId,
            _currentUser.BranchId,
            request.DeviceId,
            cancellationToken);

        // Accepted rather than Created: nothing is published at a URL the agent can fetch, and
        // the work this starts — someone reading the message — happens later and elsewhere.
        return Accepted(receipt);
    }

    /// <summary>The unreviewed backlog for this organisation, oldest first.</summary>
    [HttpGet]
    [Authorize(Policy = ZaziPolicies.AuditRead)]
    [ProducesResponseType(typeof(IReadOnlyList<ParsingReportDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ParsingReportDto>>> ListUnreviewed(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var reports = await _reports.ListUnreviewedAsync(
            _currentUser.OrganizationId, limit, cancellationToken);

        return Ok(reports);
    }
}
