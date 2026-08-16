using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TheCarl.Application;
using TheCarl.Domain;

namespace TheCarl.Infrastructure.Services;

/// <summary>
/// Turns an observed SMS into evidence, and — only when that evidence is valid, classified
/// and not a duplicate — into an accepted financial transaction.
/// </summary>
/// <remarks>
/// Evidence is recorded for every observation, including rejected and duplicate ones. The
/// record of what was seen and why it was not posted is auditable in its own right.
/// </remarks>
public class SmsProcessingService : ISmsProcessingService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly ILedgerService _ledgerService;
    private readonly IReadOnlyList<ISmsTransactionParser> _parsers;

    public SmsProcessingService(ApplicationDbContext dbContext, ILedgerService ledgerService)
        : this(dbContext, ledgerService,
        [
            new MtnSmsParser(),
            new AirtelTigoSmsParser(),
            new TelecelSmsParser(),
            new GenericSmsParser()
        ])
    {
    }

    public SmsProcessingService(
        ApplicationDbContext dbContext,
        ILedgerService ledgerService,
        IEnumerable<ISmsTransactionParser> parsers)
    {
        _dbContext = dbContext;
        _ledgerService = ledgerService;
        _parsers = parsers.ToList();
    }

    public async Task<SmsParseResultDto> ProcessIncomingSmsAsync(
        SmsCaptureRequest request,
        Guid submittedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RawMessage))
        {
            throw new ArgumentException("SMS content is required.", nameof(request));
        }

        if (request.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId is required.", nameof(request));
        }

        if (submittedByUserId == Guid.Empty)
        {
            throw new ArgumentException("A submitting user is required.", nameof(submittedByUserId));
        }

        if (request.BranchId is not { } branchId || branchId == Guid.Empty)
        {
            throw new ArgumentException(
                "A branch is required to record transaction evidence.", nameof(request));
        }

        var provider = DetectProvider(request.RawMessage, request.ProviderHint);
        var parser = _parsers.FirstOrDefault(x => x.CanHandle(provider, request.RawMessage))
            ?? new GenericSmsParser();

        var parsed = parser.Parse(
            request.RawMessage, provider, request.SourcePhoneNumber,
            request.DeviceId, request.BranchId, request.OrganizationId, request.DeviceId);

        var observedType = ClassifyType(parsed.TransactionType);
        var occurredAt = request.MessageTimestampUtc ?? DateTimeOffset.UtcNow;

        var fingerprint = EvidenceFingerprint.Compute(
            request.OrganizationId,
            provider,
            observedType,
            parsed.Amount,
            parsed.ProviderReference,
            parsed.CustomerPhoneNumber,
            occurredAt);

        var rawHash = EvidenceFingerprint.ComputeRawHash(request.RawMessage);

        var evidence = new TransactionEvidence
        {
            OrganizationId = request.OrganizationId,
            BranchId = branchId,
            DeviceId = request.DeviceId,
            SubmittedByUserId = submittedByUserId,
            SourceType = EvidenceSourceType.AndroidSms,
            Provider = provider,
            SenderAddress = request.SourcePhoneNumber,
            CustomerPhoneNumber = parsed.CustomerPhoneNumber,
            Amount = parsed.Amount,
            ObservedType = observedType,
            ProviderReference = parsed.ProviderReference,
            OccurredAtUtc = occurredAt,
            DeviceReceivedAtUtc = request.MessageTimestampUtc,
            ServerReceivedAtUtc = DateTimeOffset.UtcNow,
            Fingerprint = fingerprint,
            RawHash = rawHash,
            ParserName = parser.ProviderName,
            ParserVersion = parsed.ParserVersion,
            ConfidenceScore = parsed.ConfidenceScore,
            RawMessage = request.RawMessage,
            State = TransactionLifecycleState.Parsed
        };

        // Duplicate detection is scoped to the organization: the same real-world event seen
        // by two devices in one tenant is one financial event, while two tenants may
        // legitimately receive byte-identical provider messages.
        var priorTransaction = await _dbContext.Transactions
            .AsNoTracking()
            .Where(x => x.OrganizationId == request.OrganizationId && x.EvidenceFingerprint == fingerprint)
            .Select(x => new { x.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (priorTransaction is not null)
        {
            evidence.IsDuplicate = true;
            evidence.State = TransactionLifecycleState.Rejected;
            evidence.OutcomeReason = "Duplicate of an already-accepted transaction.";
            evidence.FinancialTransactionId = priorTransaction.Id;

            _dbContext.TransactionEvidence.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return MapEvidence(evidence);
        }

        // Nothing usable at all — no amount and no classification. Terminal.
        if (observedType == TransactionType.Unknown && parsed.Amount < LedgerPolicy.MinimumAmount)
        {
            evidence.State = TransactionLifecycleState.Rejected;
            evidence.OutcomeReason = "Parser could not extract a usable amount or transaction type.";
            _dbContext.TransactionEvidence.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return MapEvidence(evidence);
        }

        // Parsed something, but not completely enough to post. The commonest case is a
        // truncated delivery that lost its provider reference: it still looks like a valid
        // transaction and would post a plausible but wrong amount.
        //
        // Held rather than rejected — the evidence is real and a person can classify it. No
        // financial transaction is created, so cash and float are untouched.
        if (!SmsEvidencePolicy.MeetsAutoPostBar(parsed.ConfidenceScore, parsed.Amount, observedType))
        {
            evidence.State = TransactionLifecycleState.PendingReview;
            evidence.OutcomeReason = string.IsNullOrWhiteSpace(parsed.ProviderReference)
                ? "Incomplete evidence: no provider reference, so the amount cannot be trusted. Held for review."
                : $"Evidence confidence {parsed.ConfidenceScore:0.00} is below the " +
                  $"{SmsEvidencePolicy.MinimumAutoPostConfidence:0.00} auto-post bar. Held for review.";

            _dbContext.TransactionEvidence.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return MapEvidence(evidence);
        }

        if (!LedgerPolicy.CanPostAutomatically(observedType))
        {
            // Unknown, reversal and adjustment types never post from a parser. An unknown
            // type reaching the ledger would let a parser failure corrupt branch balances.
            evidence.State = TransactionLifecycleState.PendingReview;
            evidence.OutcomeReason = $"Type '{observedType}' requires human classification before posting.";
            _dbContext.TransactionEvidence.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return MapEvidence(evidence);
        }

        var movement = LedgerPolicy.MovementFor(observedType, parsed.Amount);

        var transaction = new FinancialTransaction
        {
            OrganizationId = request.OrganizationId,
            BranchId = branchId,
            AgentId = submittedByUserId,
            DeviceId = request.DeviceId,
            EvidenceId = evidence.Id,
            Network = provider,
            Type = observedType,
            Amount = parsed.Amount,
            Currency = Money.DefaultCurrency,
            CustomerPhoneNumber = parsed.CustomerPhoneNumber,
            ProviderReference = parsed.ProviderReference,
            TransactionAtUtc = occurredAt,
            AcceptedAtUtc = DateTimeOffset.UtcNow,
            Source = TransactionSource.AutomaticSms,
            State = TransactionLifecycleState.Accepted,
            ConfidenceScore = parsed.ConfidenceScore,
            EvidenceFingerprint = fingerprint,
            Notes = $"Accepted from {provider} SMS evidence via {parser.ProviderName} {parsed.ParserVersion}."
        };

        transaction.ApplyMovement(movement);

        evidence.State = TransactionLifecycleState.Accepted;
        evidence.FinancialTransactionId = transaction.Id;

        _dbContext.TransactionEvidence.Add(evidence);
        _dbContext.Transactions.Add(transaction);
        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = request.OrganizationId,
            RelatedTransactionId = transaction.Id,
            UserId = submittedByUserId,
            DeviceId = request.DeviceId,
            Action = "TRANSACTION_CREATED",
            Details = $"Accepted from SMS evidence {evidence.Id}. {movement.Rationale}",
            ActorType = "System"
        });

        try
        {
            await SaveAtomicallyAsync(transaction, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // A concurrent submission of the same evidence won the race. Exactly one ledger
            // entry exists; this observation is recorded as the duplicate it turned out to be.
            _dbContext.ChangeTracker.Clear();

            evidence.IsDuplicate = true;
            evidence.State = TransactionLifecycleState.Rejected;
            evidence.OutcomeReason = "Duplicate resolved at commit time by the database constraint.";
            evidence.FinancialTransactionId = null;

            _dbContext.TransactionEvidence.Add(evidence);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return MapEvidence(evidence);
    }

    /// <summary>
    /// Commits evidence, the ledger row and the balance projection as one unit. The
    /// projection runs inside the transaction so a rejected duplicate cannot leave a
    /// balance change behind it.
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

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException?.GetType().GetProperty("SqlState")?.GetValue(exception.InnerException) as string == "23505";

    private static SmsParseResultDto MapEvidence(TransactionEvidence evidence) => new(
        evidence.Id,
        evidence.OrganizationId,
        evidence.BranchId,
        evidence.DeviceId,
        evidence.Provider,
        evidence.Provider,
        evidence.ObservedType.ToString(),
        evidence.Amount,
        evidence.CustomerPhoneNumber,
        evidence.ProviderReference,
        evidence.ConfidenceScore,
        evidence.State,
        evidence.IsDuplicate,
        evidence.Fingerprint,
        evidence.ServerReceivedAtUtc);

    /// <summary>Maps a parser's textual verdict onto the canonical type.</summary>
    private static TransactionType ClassifyType(string parserVerdict) =>
        parserVerdict.ToUpperInvariant() switch
        {
            "DEPOSIT" or "CASH_IN" or "CASHIN" => TransactionType.CashIn,
            "WITHDRAWAL" or "CASH_OUT" or "CASHOUT" => TransactionType.CashOut,
            "TRANSFER" => TransactionType.Transfer,
            "COMMISSION" => TransactionType.Commission,
            "REVERSAL" => TransactionType.Reversal,
            _ => TransactionType.Unknown
        };

    private static string DetectProvider(string rawMessage, string? providerHint)
    {
        if (!string.IsNullOrWhiteSpace(providerHint))
        {
            return providerHint.Trim().ToUpperInvariant();
        }

        var text = rawMessage.ToUpperInvariant();
        if (text.Contains("MTN") || text.Contains("MOMO")) return "MTN";
        if (text.Contains("AIRTELTIGO") || text.Contains("ATL")) return "AIRTELTIGO";
        if (text.Contains("TELECEL")) return "TELECEL";
        return "UNKNOWN";
    }
}
