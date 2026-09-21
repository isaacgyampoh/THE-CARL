using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zazi.Application;
using Zazi.Application.Sync;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

/// <summary>
/// Accepts batches of transactions captured while a device was offline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomicity is per item, not per batch.</b> Each accepted transaction commits its own
/// evidence, ledger row, balance projection and audit entry in one database transaction.
/// Wrapping the whole batch in a single transaction would make partial success impossible:
/// one invalid item would roll back ninety-nine valid ones, and the client would have no way
/// to make progress.
/// </para>
/// <para>
/// <b>Arrival order does not matter.</b> Balance projection is additive over stored deltas,
/// so a device that reconnects and sends 10:05 before 10:00 produces the same balances as
/// one that sends them in order. Financial reporting uses the event time; audit ordering
/// uses server receipt time.
/// </para>
/// </remarks>
public sealed class SyncTransactionService : ISyncTransactionService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILedgerService _ledgerService;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncTransactionService> _logger;

    public SyncTransactionService(
        ApplicationDbContext dbContext,
        ILedgerService ledgerService,
        IOptions<SyncOptions> options,
        ILogger<SyncTransactionService> logger)
    {
        _dbContext = dbContext;
        _ledgerService = ledgerService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SyncTransactionsResponse> SynchronizeAsync(
        SyncTransactionsRequest request,
        SyncCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var batchId = Guid.NewGuid();
        var serverReceivedAt = DateTimeOffset.UtcNow;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        var items = request.Transactions ?? [];
        if (items.Count > _options.MaxBatchSize)
        {
            // Refused whole rather than truncated: a client that believed the remainder was
            // accepted would silently lose transactions.
            throw new SyncBatchTooLargeException(items.Count, _options.MaxBatchSize);
        }

        var resolver = new TenantResolver(_dbContext, caller);
        var results = new List<SyncTransactionResult>(items.Count);

        foreach (var item in items)
        {
            results.Add(await ProcessItemAsync(item, caller, resolver, serverReceivedAt, cancellationToken));
        }

        var accepted = results.Count(r => r.Status == SyncItemStatus.Accepted);
        var duplicate = results.Count(r => r.Status == SyncItemStatus.Duplicate);
        var rejected = results.Count(r => r.Status == SyncItemStatus.Rejected);
        var conflict = results.Count(r => r.Status == SyncItemStatus.Conflict);

        await WriteBatchAuditAsync(batchId, caller, resolver, items.Count, accepted, duplicate, rejected, conflict, cancellationToken);

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);

        // Structured fields only. No SMS bodies, no customer numbers, no tokens.
        _logger.LogInformation(
            "Sync batch {BatchId} org {OrganizationId} branch {BranchId} device {DeviceId} " +
            "submitted {Submitted} accepted {Accepted} duplicate {Duplicate} rejected {Rejected} " +
            "conflict {Conflict} in {DurationMs}ms correlation {CorrelationId}",
            batchId,
            caller.OrganizationId,
            resolver.LastBranchId,
            resolver.LastDeviceId,
            items.Count,
            accepted,
            duplicate,
            rejected,
            conflict,
            (long)elapsed.TotalMilliseconds,
            caller.CorrelationId);

        return new SyncTransactionsResponse(
            batchId, items.Count, accepted, duplicate, rejected, conflict, serverReceivedAt, results);
    }

    private async Task<SyncTransactionResult> ProcessItemAsync(
        SyncTransactionItem item,
        SyncCallerContext caller,
        TenantResolver resolver,
        DateTimeOffset serverReceivedAt,
        CancellationToken cancellationToken)
    {
        var clientId = item.ClientTransactionId ?? string.Empty;

        // ─── 1. Payload shape ────────────────────────────────────────────────
        if (!ClientTransactionId.IsWellFormed(clientId))
        {
            return Reject(clientId, SyncReasonCodes.InvalidClientTransactionId,
                "ClientTransactionId is missing or malformed.");
        }

        if (!Enum.IsDefined(item.TransactionType))
        {
            return Reject(clientId, SyncReasonCodes.InvalidTransactionType, "Unsupported transaction type.");
        }

        if (string.IsNullOrWhiteSpace(item.Provider) || item.Provider.Length > 80)
        {
            return Reject(clientId, SyncReasonCodes.InvalidProvider, "Provider is required.");
        }

        if (!Enum.IsDefined(item.SourceType))
        {
            return Reject(clientId, SyncReasonCodes.InvalidSourceType, "Unsupported evidence source type.");
        }

        // Parsed evidence must say which parser produced it, so a template change later
        // remains traceable to the code that interpreted the message.
        if (item.SourceType == EvidenceSourceType.AndroidSms && string.IsNullOrWhiteSpace(item.ParserVersion))
        {
            return Reject(clientId, SyncReasonCodes.MissingParserVersion,
                "SMS-sourced evidence must carry a parser version.");
        }

        if (item.EvidenceFingerprint is { } suppliedFingerprint
            && (suppliedFingerprint.Length != 64
                || !suppliedFingerprint.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'))))
        {
            return Reject(clientId, SyncReasonCodes.InvalidEvidenceFingerprint,
                "EvidenceFingerprint must be a 64-character lowercase hex SHA-256.");
        }

        // ─── 2. Amount and accounting rules ──────────────────────────────────
        if (item.TransactionType != TransactionType.Adjustment)
        {
            if (item.Amount < LedgerPolicy.MinimumAmount
                || decimal.Round(item.Amount, LedgerPolicy.StorageScale) != item.Amount)
            {
                return Reject(clientId, SyncReasonCodes.InvalidAmount,
                    $"Amount must be at least {LedgerPolicy.MinimumAmount:0.00} with at most " +
                    $"{LedgerPolicy.StorageScale} decimal places.");
            }
        }

        if (item.TransactionType == TransactionType.Unknown)
        {
            // An unclassified transaction must never post. Accepting it would let a parser
            // failure corrupt branch balances.
            return Reject(clientId, SyncReasonCodes.UnknownTypeRequiresReview,
                "Unknown-typed transactions cannot be posted and must be classified by a person.");
        }

        if (item.TransactionType == TransactionType.Adjustment)
        {
            if (item.AdjustmentCashDelta is null && item.AdjustmentFloatDelta is null)
            {
                return Reject(clientId, SyncReasonCodes.AdjustmentRequiresExplicitDeltas,
                    "An adjustment must carry an explicit signed cash and/or float delta.");
            }

            if (string.IsNullOrWhiteSpace(item.CorrectionReason))
            {
                return Reject(clientId, SyncReasonCodes.AdjustmentRequiresReason,
                    "An adjustment must carry a reason.");
            }
        }

        // ─── 3. Timestamp bounds ─────────────────────────────────────────────
        if (item.TransactionTimestamp > serverReceivedAt + _options.MaxClockSkewAhead)
        {
            return Reject(clientId, SyncReasonCodes.TimestampInFuture,
                "Transaction timestamp is too far in the future.");
        }

        if (item.TransactionTimestamp < serverReceivedAt - _options.MaxBacklogAge)
        {
            return Reject(clientId, SyncReasonCodes.TimestampTooOld,
                "Transaction timestamp is older than the accepted backlog window.");
        }

        // ─── 4. Tenant relationships ─────────────────────────────────────────
        var branchResolution = await resolver.ResolveBranchAsync(item.BranchId, cancellationToken);
        if (branchResolution.ReasonCode is { } branchReason)
        {
            return Reject(clientId, branchReason, "Branch is not accessible to the caller.");
        }

        var branchId = branchResolution.Value!.Value;

        if (item.DeviceId is { } requestedDeviceId)
        {
            var deviceReason = await resolver.ValidateDeviceAsync(requestedDeviceId, branchId, cancellationToken);
            if (deviceReason is not null)
            {
                return Reject(clientId, deviceReason, "Device is not permitted to synchronise here.");
            }
        }

        if (item.SessionId is { } requestedSessionId)
        {
            var sessionReason = await resolver.ValidateSessionAsync(
                requestedSessionId, branchId, item.TransactionTimestamp, cancellationToken);
            if (sessionReason is not null)
            {
                return Reject(clientId, sessionReason, "Session is not valid for this transaction.");
            }
        }

        // ─── 5. Idempotency pre-check ────────────────────────────────────────
        // Runs BEFORE the reversal checks. A client retrying a reversal after a lost
        // response is replaying its own accepted submission, not attempting a second
        // reversal — resolving it as a conflict would raise a spurious conflict record and
        // stall an outbox that is behaving correctly.
        // The fingerprint is always recomputed server-side. A client value is never used for
        // duplicate detection: trusting it would let a device dodge deduplication by sending
        // a fabricated fingerprint, and would leave legitimate clients that omit it (as they
        // may) unprotected against double posting.
        var fingerprint = EvidenceFingerprint.Compute(
            caller.OrganizationId,
            item.Provider,
            item.TransactionType,
            item.Amount,
            item.TransactionReference,
            item.CustomerPhone,
            item.TransactionTimestamp);

        // This lookup is an optimisation only. Two concurrent batches can both pass it before
        // either commits, so correctness rests on the unique indexes and the 23505 handler.
        var existing = await FindExistingAsync(caller.OrganizationId, clientId, fingerprint, cancellationToken);
        if (existing is not null)
        {
            return DuplicateOf(existing, clientId);
        }

        // The same real transaction already recorded by another route — forwarded from a keypad
        // phone, say — carries the same network transaction ID but a different fingerprint,
        // because each route stamps its own time. Recognised as the same event, not posted twice.
        if (await CrossRouteDuplicates.FindAsync(
                _dbContext, caller.OrganizationId, item.Provider, item.TransactionType,
                item.Amount, item.TransactionReference, cancellationToken) is { } sameEvent)
        {
            return DuplicateOf(new ExistingTransaction(sameEvent, false), clientId);
        }

        // ─── 6. Reversal eligibility ─────────────────────────────────────────
        TransactionType? originalType = null;
        if (item.TransactionType == TransactionType.Reversal)
        {
            if (item.ReversesTransactionId is not { } originalId)
            {
                return Reject(clientId, SyncReasonCodes.ReversalRequiresOriginal,
                    "A reversal must reference the transaction it reverses.");
            }

            var original = await _dbContext.Transactions
                .AsNoTracking()
                .Where(x => x.Id == originalId && x.OrganizationId == caller.OrganizationId)
                .Select(x => new { x.Type, x.State })
                .SingleOrDefaultAsync(cancellationToken);

            if (original is null)
            {
                return Reject(clientId, SyncReasonCodes.ReversalOriginalNotFound,
                    "The referenced original transaction does not exist in this organization.");
            }

            // Only a posted transaction can be reversed, and only once.
            if (original.State is not (TransactionLifecycleState.Accepted or TransactionLifecycleState.Synced))
            {
                return Reject(clientId, SyncReasonCodes.ReversalOriginalNotEligible,
                    $"The original transaction is {original.State} and cannot be reversed.");
            }

            originalType = original.Type;

            // Fast path for a clear message. The unique index is the real guarantee: two
            // concurrent reversals can both pass this check before either commits.
            var alreadyReversed = await _dbContext.Transactions
                .AsNoTracking()
                .AnyAsync(
                    x => x.OrganizationId == caller.OrganizationId && x.ReversesTransactionId == originalId,
                    cancellationToken);

            if (alreadyReversed)
            {
                return await RaiseConflictAsync(
                    item, caller, SyncConflictType.DuplicateReversalAttempt,
                    SyncReasonCodes.AlreadyReversed,
                    "The original transaction has already been reversed.",
                    relatedTransactionId: originalId,
                    cancellationToken: cancellationToken);
            }
        }

        // ─── 7. Resolve the ledger movement ──────────────────────────────────
        LedgerMovement movement;
        try
        {
            movement = LedgerPolicy.MovementFor(
                item.TransactionType,
                item.Amount,
                originalType,
                item.AdjustmentCashDelta,
                item.AdjustmentFloatDelta);
        }
        catch (ArgumentException exception)
        {
            return Reject(clientId, SyncReasonCodes.InvalidTransactionType, exception.Message);
        }

        if (movement.RequiresReview)
        {
            return Reject(clientId, SyncReasonCodes.UnknownTypeRequiresReview, movement.Rationale);
        }

        // ─── 8. Persist atomically ───────────────────────────────────────────
        try
        {
            return await PersistAsync(item, caller, branchId, movement, fingerprint, serverReceivedAt, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // A concurrent request won the race. Resolve as an idempotent replay so the
            // client marks the row synced rather than retrying forever.
            _dbContext.ChangeTracker.Clear();

            var winner = await FindExistingAsync(
                caller.OrganizationId, clientId, fingerprint, cancellationToken);

            if (winner is not null)
            {
                return DuplicateOf(winner, clientId);
            }

            // A reversal that lost the race: another reversal for the same original
            // committed first. Refusing the second is what stops money being created.
            if (item.ReversesTransactionId is { } racedOriginalId
                && IsConstraintViolation(exception, "UX_Transactions_Organization_ReversesTransactionId"))
            {
                return await RaiseConflictAsync(
                    item, caller, SyncConflictType.DuplicateReversalAttempt,
                    SyncReasonCodes.AlreadyReversed,
                    "The original transaction was reversed by a concurrent request.",
                    relatedTransactionId: racedOriginalId,
                    cancellationToken: cancellationToken);
            }

            _logger.LogWarning(
                "Unique violation on sync for client id {ClientTransactionId} but no winning row was found.",
                clientId);

            return await RaiseConflictAsync(
                item, caller, SyncConflictType.UnresolvableUniqueViolation,
                SyncReasonCodes.ClientIdReusedWithDifferentPayload,
                "The submission conflicts with an existing record and could not be resolved.",
                relatedTransactionId: null,
                cancellationToken: cancellationToken);
        }
    }

    private async Task<SyncTransactionResult> PersistAsync(
        SyncTransactionItem item,
        SyncCallerContext caller,
        Guid branchId,
        LedgerMovement movement,
        string fingerprint,
        DateTimeOffset serverReceivedAt,
        CancellationToken cancellationToken)
    {
        var currency = string.IsNullOrWhiteSpace(item.Currency) ? Money.DefaultCurrency : item.Currency;

        var evidence = new TransactionEvidence
        {
            OrganizationId = caller.OrganizationId,
            BranchId = branchId,
            DeviceId = item.DeviceId,
            SessionId = item.SessionId,
            SubmittedByUserId = caller.UserId,
            SourceType = item.SourceType,
            Provider = item.Provider,
            CustomerPhoneNumber = GhanaPhoneNumber.NormaliseOrKeep(item.CustomerPhone),
            Amount = item.Amount,
            Currency = currency,
            ObservedType = item.TransactionType,
            ProviderReference = item.TransactionReference,
            OccurredAtUtc = item.TransactionTimestamp,
            DeviceReceivedAtUtc = item.DeviceReceivedAt,
            ServerReceivedAtUtc = serverReceivedAt,
            Fingerprint = fingerprint,
            ParserName = item.SourceType.ToString(),
            ParserVersion = item.ParserVersion ?? string.Empty,
            ConfidenceScore = item.SourceType == EvidenceSourceType.ManualEntry ? 1m : 0.9m,
            State = TransactionLifecycleState.Accepted
        };

        var transaction = new FinancialTransaction
        {
            OrganizationId = caller.OrganizationId,
            BranchId = branchId,
            SessionId = item.SessionId,
            AgentId = caller.UserId,
            DeviceId = item.DeviceId,
            EvidenceId = evidence.Id,
            Network = item.Provider,
            Type = item.TransactionType,
            Amount = item.Amount,
            Currency = currency,
            CustomerPhoneNumber = GhanaPhoneNumber.NormaliseOrKeep(item.CustomerPhone),
            ProviderReference = item.TransactionReference,
            TransactionAtUtc = item.TransactionTimestamp,
            AcceptedAtUtc = serverReceivedAt,
            Source = MapSource(item.SourceType),
            State = TransactionLifecycleState.Accepted,
            ConfidenceScore = evidence.ConfidenceScore,
            ClientTransactionId = item.ClientTransactionId,
            EvidenceFingerprint = fingerprint,
            ReversesTransactionId = item.ReversesTransactionId,
            CorrectionReason = item.CorrectionReason,
            Notes = item.Notes
        };

        transaction.ApplyMovement(movement);
        evidence.FinancialTransactionId = transaction.Id;

        _dbContext.TransactionEvidence.Add(evidence);
        _dbContext.Transactions.Add(transaction);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = caller.OrganizationId,
            RelatedTransactionId = transaction.Id,
            UserId = caller.UserId,
            DeviceId = item.DeviceId,
            Action = "TRANSACTION_CREATED",
            Details =
                $"Synchronised {item.TransactionType} {item.Amount:0.00} {currency} on {item.Provider}. " +
                $"Cash {movement.CashDelta:+0.00;-0.00;0.00}, float {movement.FloatDelta:+0.00;-0.00;0.00}.",
            ActorType = "Device"
        });

        // Evidence, ledger row, balance projection and audit commit together or not at all.
        // The transaction is opened first so the ledger upsert runs inside it: a committed
        // balance change with no transaction to explain it would be unauditable.
        if (_dbContext.Database.IsRelational())
        {
            await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _ledgerService.ApplyTransactionAsync(transaction, cancellationToken);
            await dbTransaction.CommitAsync(cancellationToken);
        }
        else
        {
            await _ledgerService.ApplyTransactionAsync(transaction, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        _dbContext.ChangeTracker.Clear();

        return new SyncTransactionResult(
            item.ClientTransactionId, SyncItemStatus.Accepted, transaction.Id, null, null);
    }

    private async Task<ExistingTransaction?> FindExistingAsync(
        Guid organizationId,
        string clientTransactionId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var byClientId = await _dbContext.Transactions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ClientTransactionId == clientTransactionId)
            .Select(x => new ExistingTransaction(x.Id, true))
            .SingleOrDefaultAsync(cancellationToken);

        if (byClientId is not null)
        {
            return byClientId;
        }

        // A different submission identity describing the same real-world event: two devices
        // that both witnessed one SMS. One financial event, one ledger row.
        return await _dbContext.Transactions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EvidenceFingerprint == fingerprint)
            .Select(x => new ExistingTransaction(x.Id, false))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task WriteBatchAuditAsync(
        Guid batchId,
        SyncCallerContext caller,
        TenantResolver resolver,
        int submitted,
        int accepted,
        int duplicate,
        int rejected,
        int conflict,
        CancellationToken cancellationToken)
    {
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = caller.OrganizationId,
            UserId = caller.UserId,
            DeviceId = resolver.LastDeviceId,
            Action = "SYNC_BATCH_PROCESSED",
            Details =
                $"Batch {batchId}: submitted {submitted}, accepted {accepted}, duplicate {duplicate}, " +
                $"rejected {rejected}, conflict {conflict}. Branch {resolver.LastBranchId}. " +
                $"Correlation {caller.CorrelationId}.",
            ActorType = "Device"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.ChangeTracker.Clear();
    }

    private static TransactionSource MapSource(EvidenceSourceType sourceType) => sourceType switch
    {
        EvidenceSourceType.AndroidSms => TransactionSource.AutomaticSms,
        EvidenceSourceType.GsmGateway => TransactionSource.Bridge,
        EvidenceSourceType.Relay => TransactionSource.Bridge,
        EvidenceSourceType.ProviderApi => TransactionSource.FutureIntegration,
        _ => TransactionSource.Manual
    };

    private static SyncTransactionResult Reject(string clientId, string reasonCode, string message) =>
        new(clientId, SyncItemStatus.Rejected, null, reasonCode, message);

    private static SyncTransactionResult DuplicateOf(ExistingTransaction existing, string clientId) =>
        new(clientId,
            SyncItemStatus.Duplicate,
            existing.Id,
            existing.MatchedOnClientId
                ? SyncReasonCodes.DuplicateClientTransactionId
                : SyncReasonCodes.DuplicateEvidenceFingerprint,
            existing.MatchedOnClientId
                ? "This submission was already recorded."
                : "This financial event was already recorded from other evidence.");

    /// <summary>
    /// Records a durable conflict and returns the client-facing result.
    /// </summary>
    /// <remarks>
    /// A conflict returned to a client but not stored server-side would leave the device
    /// believing something needs attention while the business has no record of it. Every
    /// conflict outcome therefore writes a row an operator can find.
    /// </remarks>
    private async Task<SyncTransactionResult> RaiseConflictAsync(
        SyncTransactionItem item,
        SyncCallerContext caller,
        SyncConflictType conflictType,
        string reasonCode,
        string message,
        Guid? relatedTransactionId,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();

        // Idempotent: retrying a conflicting submission must not pile up duplicate rows for
        // the operator to wade through.
        var existing = await _dbContext.SyncConflicts
            .AsNoTracking()
            .Where(x => x.OrganizationId == caller.OrganizationId
                && x.ClientTransactionId == item.ClientTransactionId
                && x.ReasonCode == reasonCode)
            .Select(x => new { x.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return new SyncTransactionResult(
                item.ClientTransactionId, SyncItemStatus.Conflict, null, reasonCode, message)
            {
                ConflictId = existing.Id
            };
        }

        var conflict = new SyncConflict
        {
            OrganizationId = caller.OrganizationId,
            BranchId = item.BranchId ?? caller.CallerBranchId,
            DeviceId = item.DeviceId,
            SubmittedByUserId = caller.UserId,
            ClientTransactionId = item.ClientTransactionId,
            RelatedTransactionId = relatedTransactionId,
            ConflictType = conflictType,
            ReasonCode = reasonCode,
            CorrelationId = caller.CorrelationId,
            SubmittedAmount = item.Amount,
            SubmittedCurrency = item.Currency ?? Money.DefaultCurrency,
            SubmittedType = item.TransactionType,
            Status = SyncConflictStatus.Open
        };

        _dbContext.SyncConflicts.Add(conflict);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = caller.OrganizationId,
            UserId = caller.UserId,
            DeviceId = item.DeviceId,
            RelatedTransactionId = relatedTransactionId,
            Action = "SYNC_CONFLICT_RAISED",
            Details = $"{conflictType} ({reasonCode}) for client transaction {item.ClientTransactionId}.",
            ActorType = "System"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.ChangeTracker.Clear();

        _logger.LogWarning(
            "Sync conflict {ConflictId} type {ConflictType} reason {ReasonCode} org {OrganizationId} " +
            "device {DeviceId} correlation {CorrelationId}",
            conflict.Id, conflictType, reasonCode, caller.OrganizationId, item.DeviceId, caller.CorrelationId);

        return new SyncTransactionResult(
            item.ClientTransactionId, SyncItemStatus.Conflict, null, reasonCode, message)
        {
            ConflictId = conflict.Id
        };
    }

    /// <summary>Whether the violation came from a specific named constraint.</summary>
    private static bool IsConstraintViolation(DbUpdateException exception, string constraintName)
    {
        var inner = exception.InnerException;
        if (inner is null)
        {
            return false;
        }

        var constraint = inner.GetType().GetProperty("ConstraintName")?.GetValue(inner) as string;
        return string.Equals(constraint, constraintName, StringComparison.Ordinal);
    }

    /// <summary>PostgreSQL SQLSTATE 23505 — unique_violation.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException?.GetType().GetProperty("SqlState")?.GetValue(exception.InnerException) as string == "23505";

    private sealed record ExistingTransaction(Guid Id, bool MatchedOnClientId);
}

/// <summary>Raised when a batch exceeds the configured maximum. Surfaces as HTTP 413.</summary>
public sealed class SyncBatchTooLargeException : Exception
{
    public SyncBatchTooLargeException(int submitted, int maximum)
        : base($"The batch contains {submitted} transactions; the maximum is {maximum}.")
    {
        Submitted = submitted;
        Maximum = maximum;
    }

    public int Submitted { get; }
    public int Maximum { get; }
}
