using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zazi.Application.Parsing;
using Zazi.Domain;

namespace Zazi.Infrastructure.Parsing;

/// <summary>
/// Stores agents' reports that a message was read wrongly.
/// </summary>
/// <remarks>
/// The whole value of this is that the body is kept verbatim. Normalising, trimming or
/// re-parsing it here would destroy the evidence: the parser's own reading is already recorded
/// alongside, and the point of the report is to compare the two.
/// </remarks>
public sealed class ParsingReportService : IParsingReportService
{
    /// <summary>
    /// Long enough for any provider message, short enough not to be an upload channel.
    /// </summary>
    /// <remarks>
    /// The longest real message in the fixture corpus is under 300 characters. A cap this far
    /// above it truncates nothing an agent could legitimately report, while keeping an
    /// authenticated endpoint that accepts free text from becoming somewhere to put a file.
    /// </remarks>
    public const int MaximumRawMessageLength = 4_000;

    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<ParsingReportService> _logger;

    public ParsingReportService(ApplicationDbContext dbContext, ILogger<ParsingReportService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ParsingReportReceipt> SubmitAsync(
        SubmitParsingReportRequest request,
        Guid organizationId,
        Guid reportedByUserId,
        Guid? branchId,
        Guid? deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientTransactionId))
        {
            throw new ArgumentException(
                "A report must say which transaction it is about.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.RawMessage))
        {
            throw new ArgumentException(
                "A report without the message is the one thing it cannot be.", nameof(request));
        }

        if (request.RawMessage.Length > MaximumRawMessageLength)
        {
            throw new ArgumentException(
                $"The message is longer than {MaximumRawMessageLength} characters.", nameof(request));
        }

        // Re-reporting the same transaction is a double tap on a slow connection, not a second
        // opinion. Checked here and enforced by a unique index, because two taps can race.
        var existing = await _dbContext.ParsingReports
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId
                    && x.ReportedByUserId == reportedByUserId
                    && x.ClientTransactionId == request.ClientTransactionId,
                cancellationToken);

        if (existing is not null)
        {
            return new ParsingReportReceipt(existing.Id, AlreadyReported: true);
        }

        var report = new ParsingReport
        {
            OrganizationId = organizationId,
            ReportedByUserId = reportedByUserId,
            BranchId = branchId,
            DeviceId = deviceId,
            ClientTransactionId = request.ClientTransactionId.Trim(),
            RawMessage = request.RawMessage,
            SenderIdentity = request.SenderIdentity?.Trim(),
            ObservedNetwork = Networks.Normalise(request.ObservedNetwork),
            ObservedType = request.ObservedType,
            ObservedAmountMinor = request.ObservedAmountMinor,
            Verdict = request.Verdict,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            ParserVersion = request.ParserVersion,
            AppVersion = request.AppVersion
        };

        _dbContext.ParsingReports.Add(report);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two taps that both passed the check above and raced to insert. The unique index
            // settles which one wins; this reads the winner and tells the agent the same thing
            // either way, because from where they are standing both taps succeeded.
            //
            // The insert is detached first: a failed SaveChanges leaves the entity tracked as
            // Added, and querying through that graph would return this same doomed row.
            _dbContext.Entry(report).State = EntityState.Detached;

            var raced = await AlreadyReportedAsync(
                organizationId, reportedByUserId, report.ClientTransactionId, cancellationToken);

            if (raced is null)
            {
                // Not the duplicate index, then. Something else rejected the write and
                // swallowing it would lose the report silently.
                throw;
            }

            return new ParsingReportReceipt(raced.Value, AlreadyReported: true);
        }

        // Logged without the body. The message is a customer's financial correspondence and
        // belongs in the row the agent consented to send, not scattered through log storage.
        _logger.LogInformation(
            "Parsing report {ReportId}: agent reports {Verdict} for a {ObservedType} read as {Network}.",
            report.Id,
            report.Verdict,
            report.ObservedType,
            report.ObservedNetwork);

        return new ParsingReportReceipt(report.Id, AlreadyReported: false);
    }

    public async Task<IReadOnlyList<ParsingReportDto>> ListUnreviewedAsync(
        Guid organizationId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        // Oldest first: a report that has waited longest is the one most likely to describe a
        // defect still shipping.
        return await _dbContext.ParsingReports
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ReviewedAtUtc == null)
            .OrderBy(x => x.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(x => new ParsingReportDto(
                x.Id,
                x.OrganizationId,
                x.ClientTransactionId,
                x.RawMessage,
                x.SenderIdentity,
                x.ObservedNetwork,
                x.ObservedType,
                x.ObservedAmountMinor,
                x.Verdict,
                x.Note,
                x.ParserVersion,
                x.AppVersion,
                x.CreatedAt,
                x.ReviewedAtUtc))
            .ToListAsync(cancellationToken);
    }

    private async Task<Guid?> AlreadyReportedAsync(
        Guid organizationId,
        Guid reportedByUserId,
        string clientTransactionId,
        CancellationToken cancellationToken)
    {
        // A fresh context: the failed SaveChanges left the tracked graph in a state that
        // cannot be queried through.
        var winner = await _dbContext.ParsingReports
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                && x.ReportedByUserId == reportedByUserId
                && x.ClientTransactionId == clientTransactionId)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);

        return winner;
    }
}
