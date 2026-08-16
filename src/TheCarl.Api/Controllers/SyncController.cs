using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TheCarl.Api.Models;
using TheCarl.Api.Security;
using TheCarl.Application;
using TheCarl.Application.Security;
using TheCarl.Application.Sync;

namespace TheCarl.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class SyncController : ControllerBase
{
    private readonly IOfflineSyncService _syncService;
    private readonly ISyncTransactionService _syncTransactionService;
    private readonly ICurrentUserContext _currentUser;
    private readonly ITenantGuard _tenantGuard;

    public SyncController(
        IOfflineSyncService syncService,
        ISyncTransactionService syncTransactionService,
        ICurrentUserContext currentUser,
        ITenantGuard tenantGuard)
    {
        _syncService = syncService;
        _syncTransactionService = syncTransactionService;
        _currentUser = currentUser;
        _tenantGuard = tenantGuard;
    }

    /// <summary>
    /// Accepts a batch of transactions captured while a device was offline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retry-safe: submitting the same batch any number of times produces the same ledger.
    /// Items already recorded return <c>DUPLICATE</c>, which is a success — it tells the
    /// client an earlier attempt landed and it should stop retrying.
    /// </para>
    /// <para>
    /// Partial success is normal. Each item resolves independently, so one malformed
    /// transaction never blocks the rest of a batch. Callers must read per-item results
    /// rather than inferring outcome from the HTTP status.
    /// </para>
    /// <para>See docs/BATCH_SYNC.md and docs/OFFLINE_TRANSACTION_CONTRACT.md.</para>
    /// </remarks>
    [HttpPost("transactions")]
    [Authorize(Policy = CarlPolicies.SyncSubmit)]
    [ProducesResponseType(typeof(SyncTransactionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<SyncTransactionsResponse>> SynchronizeTransactions(
        [FromBody] SyncTransactionsApiRequest request,
        CancellationToken cancellationToken)
    {
        var caller = new SyncCallerContext(
            _currentUser.OrganizationId,
            _currentUser.UserId,
            _currentUser.BranchId,
            _currentUser.HasOrganizationWideScope,
            _currentUser.CorrelationId);

        var items = request.Transactions
            .Select(x => new SyncTransactionItem(
                x.ClientTransactionId,
                x.TransactionType,
                x.Amount,
                x.Provider,
                x.TransactionTimestamp,
                x.DeviceReceivedAt,
                x.BranchId,
                x.DeviceId,
                x.SessionId,
                x.Currency,
                x.CustomerPhone,
                x.TransactionReference,
                x.EvidenceFingerprint,
                x.ParserVersion,
                x.SourceType,
                x.ReversesTransactionId,
                x.AdjustmentCashDelta,
                x.AdjustmentFloatDelta,
                x.CorrectionReason,
                x.Notes))
            .ToList();

        var response = await _syncTransactionService.SynchronizeAsync(
            new SyncTransactionsRequest(items), caller, cancellationToken);

        return Ok(response);
    }

    [HttpPost("queue")]
    [Authorize(Policy = CarlPolicies.SyncSubmit)]
    [ProducesResponseType(typeof(SyncQueueItemDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SyncQueueItemDto>> Queue(
        [FromBody] QueueSyncApiRequest request,
        CancellationToken cancellationToken)
    {
        var branchId = await _tenantGuard.ResolveWritableBranchAsync(request.BranchId, cancellationToken);

        if (request.DeviceId is { } deviceId)
        {
            // A revoked or foreign device must not be able to push work into the queue.
            await _tenantGuard.EnsureDeviceInTenantAsync(deviceId, cancellationToken);
        }

        var item = await _syncService.QueueEventAsync(
            new QueueSyncEventRequest(
                _currentUser.OrganizationId,
                branchId,
                request.DeviceId,
                request.EntityId,
                request.EntityType,
                request.EventType,
                request.Payload,
                request.IsManual),
            cancellationToken);

        return Ok(item);
    }

    [HttpGet("pending")]
    [Authorize(Policy = CarlPolicies.SyncSubmit)]
    [ProducesResponseType(typeof(IReadOnlyList<SyncQueueItemDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SyncQueueItemDto>>> Pending(CancellationToken cancellationToken)
    {
        var queued = await _syncService.GetPendingAsync(_currentUser.OrganizationId, cancellationToken);

        if (!_currentUser.HasOrganizationWideScope)
        {
            var assigned = _currentUser.BranchId;
            queued = queued.Where(x => x.BranchId == assigned).ToList();
        }

        return Ok(queued);
    }

    /// <summary>
    /// Drains the queue. Restricted to organization administration: draining is an
    /// operational action affecting the whole tenant, not something a single agent triggers.
    /// </summary>
    [HttpPost("process")]
    [Authorize(Policy = CarlPolicies.SyncAdminister)]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<ActionResult<int>> Process(CancellationToken cancellationToken)
    {
        var processed = await _syncService.ProcessPendingAsync(_currentUser.OrganizationId, cancellationToken);
        return Ok(processed);
    }
}
