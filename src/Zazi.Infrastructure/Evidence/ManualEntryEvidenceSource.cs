using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zazi.Application;
using Zazi.Application.Evidence;
using Zazi.Domain;

namespace Zazi.Infrastructure.Evidence;

/// <summary>
/// Evidence keyed in by a person.
/// </summary>
/// <remarks>
/// <para>
/// Serves <b>every</b> platform: iOS, Web, and Android when SMS permission is denied or the
/// transaction happened on a phone Zazi is not installed on. There is deliberately no
/// <c>IosManualEntry</c> or <c>WebManualEntry</c> — the platform is already recorded on the
/// device, and a source type per platform would fragment the evidence contract for nothing.
/// </para>
/// <para>
/// <b>Trust.</b> Manual entry is a person's account of a transaction, not a provider's. It
/// is marked <see cref="TransactionSource.Manual"/> and carries a confidence that reflects
/// deliberate human input, but it never masquerades as parsed provider evidence — the
/// distinction is what makes reconciliation meaningful.
/// </para>
/// <para>
/// This class creates <see cref="TransactionEvidence"/> and, when the observation is
/// complete and classifiable, one <see cref="FinancialTransaction"/> through
/// <see cref="LedgerPolicy"/>. It never computes a balance itself.
/// </para>
/// </remarks>
public sealed class ManualEntryEvidenceSource : ITransactionEvidenceSource
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILedgerService _ledgerService;
    private readonly ILogger<ManualEntryEvidenceSource> _logger;

    public ManualEntryEvidenceSource(
        ApplicationDbContext dbContext,
        ILedgerService ledgerService,
        ILogger<ManualEntryEvidenceSource> logger)
    {
        _dbContext = dbContext;
        _ledgerService = ledgerService;
        _logger = logger;
    }

    public EvidenceSourceType SourceType => EvidenceSourceType.ManualEntry;

    /// <summary>
    /// Available everywhere, including on unrecognised platforms.
    /// </summary>
    /// <remarks>
    /// This is the guarantee that no platform is ever left unable to record a transaction.
    /// When SMS capture is impossible — iOS always, Android without permission, a feature
    /// phone Zazi cannot reach — this is the path that keeps working.
    /// </remarks>
    public bool IsAvailableOn(DeviceType deviceType) => true;

    public async Task<EvidenceCaptureResult> CaptureAsync(
        EvidenceCapture capture,
        Guid organizationId,
        Guid submittedByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("An organization is required.", nameof(organizationId));
        }

        if (submittedByUserId == Guid.Empty)
        {
            throw new ArgumentException("A submitting user is required.", nameof(submittedByUserId));
        }

        if (capture.BranchId == Guid.Empty)
        {
            throw new ArgumentException("A branch is required to record evidence.", nameof(capture));
        }

        if (string.IsNullOrWhiteSpace(capture.Provider))
        {
            throw new ArgumentException("A provider is required.", nameof(capture));
        }

        if (!Enum.IsDefined(capture.ObservedType))
        {
            throw new ArgumentException("Unsupported transaction type.", nameof(capture));
        }

        var currency = string.IsNullOrWhiteSpace(capture.Currency) ? Money.DefaultCurrency : capture.Currency;
        var occurredAt = capture.OccurredAtUtc;

        // The same fingerprint algorithm every source uses. A manual entry describing the
        // same real event as a captured SMS must collide with it, otherwise an agent who
        // keys in a transaction that later arrives by SMS gets two ledger rows.
        var fingerprint = EvidenceFingerprint.Compute(
            organizationId,
            capture.Provider,
            capture.ObservedType,
            capture.Amount,
            capture.ProviderReference,
            capture.CustomerPhone,
            occurredAt);

        var evidence = new TransactionEvidence
        {
            OrganizationId = organizationId,
            BranchId = capture.BranchId,
            DeviceId = capture.DeviceId,
            SessionId = capture.SessionId,
            SubmittedByUserId = submittedByUserId,
            SourceType = EvidenceSourceType.ManualEntry,
            Provider = capture.Provider,
            SenderAddress = capture.SenderAddress,
            CustomerPhoneNumber = capture.CustomerPhone,
            Amount = capture.Amount,
            Currency = currency,
            ObservedType = capture.ObservedType,
            ProviderReference = capture.ProviderReference,
            OccurredAtUtc = occurredAt,
            DeviceReceivedAtUtc = capture.DeviceReceivedAtUtc,
            ServerReceivedAtUtc = DateTimeOffset.UtcNow,
            Fingerprint = fingerprint,
            ParserName = capture.ParserName ?? nameof(ManualEntryEvidenceSource),
            ParserVersion = capture.ParserVersion ?? "manual-v1",
            // A person deliberately entered these figures, so the reading is not uncertain
            // the way a parse is. It is still not provider-verified, which is what
            // TransactionSource.Manual records.
            ConfidenceScore = 1.0m,
            RawMessage = capture.RawMessage,
            State = TransactionLifecycleState.Parsed
        };

        // Organization-scoped duplicate detection, identical to the SMS path.
        var prior = await _dbContext.Transactions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EvidenceFingerprint == fingerprint)
            .Select(x => new { x.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (prior is not null)
        {
            evidence.IsDuplicate = true;
            evidence.State = TransactionLifecycleState.Rejected;
            evidence.OutcomeReason = "This transaction has already been recorded.";
            evidence.FinancialTransactionId = prior.Id;

            _dbContext.TransactionEvidence.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Map(evidence);
        }

        if (capture.Amount < LedgerPolicy.MinimumAmount)
        {
            evidence.State = TransactionLifecycleState.Rejected;
            evidence.OutcomeReason = $"Amount must be at least {LedgerPolicy.MinimumAmount:0.00}.";
            _dbContext.TransactionEvidence.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Map(evidence);
        }

        // Unknown, Reversal and Adjustment never post from a capture: the first is
        // unclassified, and the other two need a referenced original or explicit signed
        // deltas that no capture supplies.
        if (!LedgerPolicy.CanPostAutomatically(capture.ObservedType))
        {
            evidence.State = TransactionLifecycleState.PendingReview;
            evidence.OutcomeReason = $"'{capture.ObservedType}' requires review before it can be posted.";
            _dbContext.TransactionEvidence.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Map(evidence);
        }

        var movement = LedgerPolicy.MovementFor(capture.ObservedType, capture.Amount);

        var transaction = new FinancialTransaction
        {
            OrganizationId = organizationId,
            BranchId = capture.BranchId,
            SessionId = capture.SessionId,
            AgentId = submittedByUserId,
            DeviceId = capture.DeviceId,
            EvidenceId = evidence.Id,
            Network = capture.Provider,
            Type = capture.ObservedType,
            Amount = capture.Amount,
            Currency = currency,
            CustomerPhoneNumber = capture.CustomerPhone,
            ProviderReference = capture.ProviderReference,
            TransactionAtUtc = occurredAt,
            AcceptedAtUtc = DateTimeOffset.UtcNow,
            Source = TransactionSource.Manual,
            State = TransactionLifecycleState.Accepted,
            ConfidenceScore = evidence.ConfidenceScore,
            ClientTransactionId = capture.ClientTransactionId,
            EvidenceFingerprint = fingerprint,
            Notes = capture.Notes
        };

        transaction.ApplyMovement(movement);

        evidence.State = TransactionLifecycleState.Accepted;
        evidence.FinancialTransactionId = transaction.Id;

        _dbContext.TransactionEvidence.Add(evidence);
        _dbContext.Transactions.Add(transaction);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            RelatedTransactionId = transaction.Id,
            UserId = submittedByUserId,
            DeviceId = capture.DeviceId,
            Action = "TRANSACTION_CREATED",
            Details =
                $"Manual {capture.ObservedType} {capture.Amount:0.00} {currency} on {capture.Provider}. " +
                movement.Rationale,
            ActorType = "User"
        });

        try
        {
            await SaveAtomicallyAsync(transaction, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // A concurrent capture of the same real event won the race. Exactly one ledger
            // row exists; this observation is recorded as the duplicate it turned out to be.
            _dbContext.ChangeTracker.Clear();

            evidence.IsDuplicate = true;
            evidence.State = TransactionLifecycleState.Rejected;
            evidence.OutcomeReason = "Duplicate resolved at commit time by the database constraint.";
            evidence.FinancialTransactionId = null;

            _dbContext.TransactionEvidence.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Manual capture for organization {OrganizationId} resolved as a duplicate at commit.",
                organizationId);
        }

        return Map(evidence);
    }

    /// <summary>
    /// Commits evidence, the ledger row and the balance projection as one unit. The
    /// projection runs inside the transaction so a rejected duplicate cannot leave a balance
    /// change behind it.
    /// </summary>
    private async Task SaveAtomicallyAsync(FinancialTransaction transaction, CancellationToken cancellationToken)
    {
        if (!_dbContext.Database.IsRelational())
        {
            await _ledgerService.ApplyTransactionAsync(transaction, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (_dbContext.Database.CurrentTransaction is not null)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _ledgerService.ApplyTransactionAsync(transaction, cancellationToken);
            return;
        }

        await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _ledgerService.ApplyTransactionAsync(transaction, cancellationToken);
        await dbTransaction.CommitAsync(cancellationToken);
    }

    private static EvidenceCaptureResult Map(TransactionEvidence evidence) => new(
        evidence.Id,
        evidence.Fingerprint,
        evidence.State,
        evidence.State == TransactionLifecycleState.Accepted ? evidence.FinancialTransactionId : null,
        evidence.IsDuplicate,
        evidence.OutcomeReason);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException?.GetType().GetProperty("SqlState")?.GetValue(exception.InnerException) as string == "23505";
}
