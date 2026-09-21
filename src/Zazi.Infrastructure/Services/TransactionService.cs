using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

public class TransactionService : ITransactionService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILedgerService _ledgerService;

    public TransactionService(ApplicationDbContext dbContext, ILedgerService ledgerService)
    {
        _dbContext = dbContext;
        _ledgerService = ledgerService;
    }

    /// <summary>
    /// Accepts a transaction into the ledger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ledger row, its balance projection and the audit entry are committed in one
    /// database transaction. A partial commit would leave a balance that no transaction
    /// explains, or a transaction that no balance reflects.
    /// </para>
    /// <para>
    /// Replay is resolved by the unique index on
    /// <c>(OrganizationId, ClientTransactionId)</c>. The pre-check below is an optimisation
    /// only; correctness rests on the constraint, because two concurrent requests can both
    /// pass a pre-check before either commits.
    /// </para>
    /// </remarks>
    public async Task<TransactionDto> CreateTransactionAsync(
        CreateTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        LedgerPolicy.ValidateAmount(request.Amount);

        if (request.ClientTransactionId is { } clientId && !ClientTransactionId.IsWellFormed(clientId))
        {
            throw new ArgumentException(
                "ClientTransactionId is not well formed.", nameof(request));
        }

        // Fast path: a replay we have already accepted returns the original row unchanged.
        if (request.ClientTransactionId is not null)
        {
            var existing = await FindByClientIdAsync(
                request.OrganizationId, request.ClientTransactionId, cancellationToken);

            if (existing is not null)
            {
                return Map(existing);
            }
        }

        var movement = ResolveMovement(request);

        var entity = new FinancialTransaction
        {
            OrganizationId = request.OrganizationId,
            BranchId = request.BranchId,
            SessionId = request.SessionId,
            AgentId = request.AgentId,
            DeviceId = request.DeviceId,
            Network = request.Network,
            Type = request.Type,
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? Money.DefaultCurrency : request.Currency,
            CustomerPhoneNumber = request.CustomerPhoneNumber,
            ProviderReference = request.ProviderReference,
            TransactionAtUtc = DateTimeOffset.UtcNow,
            AcceptedAtUtc = DateTimeOffset.UtcNow,
            Source = request.Source,
            ConfidenceScore = request.Source == TransactionSource.Manual ? 1m : 0m,
            ClientTransactionId = request.ClientTransactionId,
            ReversesTransactionId = request.ReversesTransactionId,
            CorrectionReason = request.CorrectionReason,
            Notes = request.Notes,
            // An unclassified or unreferenced transaction is held, not posted.
            State = movement.RequiresReview
                ? TransactionLifecycleState.PendingReview
                : TransactionLifecycleState.Accepted
        };

        entity.ApplyMovement(movement);

        _dbContext.Transactions.Add(entity);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = request.OrganizationId,
            RelatedTransactionId = entity.Id,
            UserId = request.AgentId,
            DeviceId = request.DeviceId,
            Action = "TRANSACTION_CREATED",
            Details =
                $"{request.Type} {request.Amount:0.00} {entity.Currency} on {request.Network}. " +
                $"Cash {movement.CashDelta:+0.00;-0.00;0.00}, float {movement.FloatDelta:+0.00;-0.00;0.00}. " +
                movement.Rationale,
            ActorType = "Agent"
        });

        try
        {
            await SaveAtomicallyAsync(entity, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Another request won the race on the same idempotency identity. That request's
            // row is the accepted one; this call resolves as an idempotent replay rather
            // than an error, and no second ledger entry exists.
            _dbContext.ChangeTracker.Clear();

            var winner = request.ClientTransactionId is not null
                ? await FindByClientIdAsync(request.OrganizationId, request.ClientTransactionId, cancellationToken)
                : null;

            if (winner is null)
            {
                throw new ConflictException(
                    "The transaction conflicts with an existing record and could not be resolved as a replay.");
            }

            return Map(winner);
        }

        return Map(entity);
    }

    public async Task<PagedResult<TransactionDto>> GetTransactionsAsync(
        TransactionQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId is required.", nameof(query));
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var filtered = _dbContext.Transactions
            .AsNoTracking()
            .Where(x => x.OrganizationId == query.OrganizationId);

        if (query.BranchId is { } branchId)
        {
            filtered = filtered.Where(x => x.BranchId == branchId);
        }

        if (query.FromUtc is { } fromUtc)
        {
            filtered = filtered.Where(x => x.TransactionAtUtc >= fromUtc);
        }

        if (query.ToUtc is { } toUtc)
        {
            filtered = filtered.Where(x => x.TransactionAtUtc <= toUtc);
        }

        var totalCount = await filtered.LongCountAsync(cancellationToken);

        var items = await filtered
            .OrderByDescending(x => x.TransactionAtUtc)
            .ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => Projection(x))
            .ToListAsync(cancellationToken);

        return new PagedResult<TransactionDto>(items, page, pageSize, totalCount);
    }

    /// <summary>
    /// Commits the ledger row and its balance projection as one unit.
    /// </summary>
    /// <remarks>
    /// The projection runs <i>inside</i> the transaction. Applying it beforehand would
    /// increment a branch balance that the subsequent insert might reject as a duplicate,
    /// leaving money on the books with no transaction to explain it.
    /// </remarks>
    private async Task SaveAtomicallyAsync(FinancialTransaction entity, CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational())
        {
            await _ledgerService.ApplyTransactionAsync(entity, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // Reuse an ambient transaction when the caller already opened one.
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _ledgerService.ApplyTransactionAsync(entity, cancellationToken);
            return;
        }

        await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _ledgerService.ApplyTransactionAsync(entity, cancellationToken);
        await dbTransaction.CommitAsync(cancellationToken);
    }

    private Task<FinancialTransaction?> FindByClientIdAsync(
        Guid organizationId,
        string clientTransactionId,
        CancellationToken cancellationToken) =>
        _dbContext.Transactions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.ClientTransactionId == clientTransactionId,
                cancellationToken);

    private static LedgerMovement ResolveMovement(CreateTransactionRequest request)
    {
        if (request.Type != TransactionType.Reversal)
        {
            // The deltas are passed through rather than ignored: an adjustment is the one type
            // whose direction the caller supplies, and dropping them here made LedgerPolicy
            // throw for want of the values it was already being given.
            return LedgerPolicy.MovementFor(
                request.Type,
                request.Amount,
                explicitCashDelta: request.AdjustmentCashDelta,
                explicitFloatDelta: request.AdjustmentFloatDelta);
        }

        if (request.ReversesTransactionId is null)
        {
            return LedgerPolicy.MovementFor(TransactionType.Reversal, request.Amount);
        }

        // The original's type is needed to invert it. Resolved by the caller for reversals
        // raised through the correction workflow; a bare reversal is held for review.
        return LedgerPolicy.MovementFor(TransactionType.Reversal, request.Amount);
    }

    /// <summary>PostgreSQL SQLSTATE 23505 — unique_violation.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException?.GetType().GetProperty("SqlState")?.GetValue(exception.InnerException) as string == "23505";

    private static TransactionDto Map(FinancialTransaction x) => Projection(x);

    private static TransactionDto Projection(FinancialTransaction x) => new(
        x.Id,
        x.OrganizationId,
        x.BranchId,
        x.AgentId,
        x.DeviceId,
        x.Network,
        x.Type,
        x.Amount,
        x.Currency,
        x.CustomerPhoneNumber,
        x.ProviderReference,
        x.TransactionAtUtc,
        x.Source,
        x.State,
        x.ConfidenceScore,
        x.CashDelta,
        x.FloatDelta,
        x.ClientTransactionId,
        x.Notes);
}
