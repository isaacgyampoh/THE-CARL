using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zazi.Api.Models;
using Zazi.Api.Security;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class TransactionsController : ControllerBase
{
    private readonly ITransactionService _transactionService;
    private readonly ICurrentUserContext _currentUser;
    private readonly ITenantGuard _tenantGuard;

    public TransactionsController(
        ITransactionService transactionService,
        ICurrentUserContext currentUser,
        ITenantGuard tenantGuard)
    {
        _transactionService = transactionService;
        _currentUser = currentUser;
        _tenantGuard = tenantGuard;
    }

    [HttpGet]
    [Authorize(Policy = ZaziPolicies.TransactionRead)]
    [ProducesResponseType(typeof(PagedResult<TransactionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<TransactionDto>>> GetAll(
        [FromQuery] PageQuery page,
        [FromQuery] Guid? branchId,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        CancellationToken cancellationToken)
    {
        // Branch-scoped roles are pinned to their own branch regardless of what they ask
        // for; organization-wide roles may filter to any branch inside their tenant.
        Guid? effectiveBranchId;
        if (_currentUser.HasOrganizationWideScope)
        {
            if (branchId is { } requested)
            {
                await _tenantGuard.EnsureBranchInTenantAsync(requested, cancellationToken);
            }

            effectiveBranchId = branchId;
        }
        else
        {
            effectiveBranchId = _currentUser.BranchId
                ?? throw new TenantAccessDeniedException(
                    "The caller has no branch assignment and no organization-wide scope.");
        }

        var result = await _transactionService.GetTransactionsAsync(
            new TransactionQuery(
                _currentUser.OrganizationId,
                effectiveBranchId,
                page.Page,
                page.Take,
                fromUtc,
                toUtc),
            cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// The caller's own transactions, from every phone they use.
    /// </summary>
    /// <remarks>
    /// The handset lists what it recorded itself, which is right for working offline — but an
    /// agent who also forwards messages from a keypad phone, or works a second handset, would
    /// not see those. With a connection, the app merges this in so the agent sees their whole
    /// day. Always the caller's own: the agent comes from the token, never the query.
    /// </remarks>
    [HttpGet("mine")]
    [Authorize(Policy = ZaziPolicies.TransactionRead)]
    [ProducesResponseType(typeof(PagedResult<TransactionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<TransactionDto>>> GetMine(
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] string? customer,
        [FromQuery] int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        var result = await _transactionService.GetTransactionsAsync(
            new TransactionQuery(
                _currentUser.OrganizationId,
                BranchId: null,
                Page: 1,
                PageSize: Math.Clamp(pageSize, 1, 200),
                FromUtc: fromUtc,
                ToUtc: toUtc,
                CustomerPhone: customer,
                AgentId: _currentUser.UserId),
            cancellationToken);

        return Ok(result);
    }

    /// <summary>Records a manually entered transaction.</summary>
    /// <remarks>
    /// <see cref="TransactionSource"/> is forced to <see cref="TransactionSource.Manual"/>.
    /// A client must never be able to claim its entry came from verified SMS evidence:
    /// manual entries and parsed evidence carry different trust and must stay distinguishable.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = ZaziPolicies.TransactionRecord)]
    [ProducesResponseType(typeof(TransactionDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<TransactionDto>> Create(
        [FromBody] CreateTransactionApiRequest request,
        CancellationToken cancellationToken)
    {
        var branchId = await _tenantGuard.ResolveWritableBranchAsync(request.BranchId, cancellationToken);

        if (request.DeviceId is { } deviceId)
        {
            await _tenantGuard.EnsureDeviceInTenantAsync(deviceId, cancellationToken);
        }

        var transaction = await _transactionService.CreateTransactionAsync(
            new CreateTransactionRequest(
                _currentUser.OrganizationId,
                branchId,
                // The acting agent is the authenticated user, not a body field: otherwise a
                // clerk could attribute their entries to a colleague.
                _currentUser.UserId,
                request.DeviceId,
                request.Network,
                request.Type,
                request.Amount,
                string.IsNullOrWhiteSpace(request.Currency) ? Money.DefaultCurrency : request.Currency,
                request.CustomerPhoneNumber,
                request.ProviderReference,
                TransactionSource.Manual,
                request.Notes),
            cancellationToken);

        return CreatedAtAction(nameof(GetAll), null, transaction);
    }
}
