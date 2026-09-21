using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zazi.Application.Growth;
using Zazi.Domain;
using Zazi.Infrastructure.Statements;
using P = Zazi.Infrastructure.Statements.PdfStatementWriter;

namespace Zazi.Infrastructure.Growth;

/// <summary>
/// Commission by month and network, and the trading record a lender asks for.
/// </summary>
/// <remarks>
/// Both are read from the ledger's stored figures and only from transactions that affect it,
/// so a report cannot say something the ledger does not. The ledger is append-only, which is
/// what lets a trading record be shown to a third party at all.
/// </remarks>
public sealed class ReportService : IReportService
{
    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _clock;

    public ReportService(ApplicationDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<CommissionReport> CommissionAsync(Guid organizationId, Guid? branchId, Guid? agentId, int months = 12,
        CancellationToken cancellationToken = default)
    {
        var (from, to) = Window(months);
        var rows = await Postable(organizationId, branchId, agentId, from, to)
            .Where(t => t.Type == TransactionType.Commission)
            .Select(t => new { t.TransactionAtUtc, t.AgentId, t.Network, t.Amount })
            .ToListAsync(cancellationToken);

        var names = await NamesAsync(rows.Select(r => r.AgentId), cancellationToken);
        var grouped = rows
            .GroupBy(r => (Month: MonthOf(r.TransactionAtUtc), r.AgentId, Network: r.Network.ToUpperInvariant()))
            .Select(g => new CommissionRow(g.Key.Month, g.Key.AgentId, names.GetValueOrDefault(g.Key.AgentId) ?? "Unknown",
                g.Key.Network, g.Count(), g.Sum(r => r.Amount)))
            .OrderByDescending(r => r.Month).ThenBy(r => r.AgentName).ThenBy(r => r.Network)
            .ToList();

        return new CommissionReport(
            await BusinessNameAsync(organizationId, cancellationToken),
            await ScopeAsync(organizationId, branchId, agentId, cancellationToken),
            DateOnly.FromDateTime(from.UtcDateTime), DateOnly.FromDateTime(to.UtcDateTime.AddDays(-1)), grouped);
    }

    public async Task<TradingRecord> TradingRecordAsync(Guid organizationId, Guid? branchId, Guid? agentId, int months = 12,
        CancellationToken cancellationToken = default)
    {
        var (from, to) = Window(months);
        var rows = await Postable(organizationId, branchId, agentId, from, to)
            .Select(t => new { t.TransactionAtUtc, t.Type, t.Amount })
            .ToListAsync(cancellationToken);

        var monthList = new List<TradingMonth>();
        for (var m = DateOnly.FromDateTime(from.UtcDateTime); m.ToDateTime(TimeOnly.MinValue) < to.UtcDateTime; m = m.AddMonths(1))
        {
            var inMonth = rows.Where(r => MonthOf(r.TransactionAtUtc) == m).ToList();
            monthList.Add(new TradingMonth(
                m,
                inMonth.Count,
                inMonth.Select(r => r.TransactionAtUtc.UtcDateTime.Date).Distinct().Count(),
                inMonth.Where(r => r.Type == TransactionType.CashIn).Sum(r => r.Amount),
                inMonth.Where(r => r.Type == TransactionType.CashOut).Sum(r => r.Amount),
                inMonth.Where(r => r.Type == TransactionType.Commission).Sum(r => r.Amount)));
        }

        var first = await _db.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId)
            .Where(t => branchId == null || t.BranchId == branchId)
            .Where(t => agentId == null || t.AgentId == agentId)
            .MinAsync(t => (DateTimeOffset?)t.TransactionAtUtc, cancellationToken);

        // Months before the business used Zazi are not zero trading, they are no record — a
        // lender reading a column of zeros would take them for a dead year. The record starts
        // where the ledger does.
        if (first is { } firstAt && MonthOf(firstAt) > DateOnly.FromDateTime(from.UtcDateTime))
        {
            monthList = monthList.Where(m => m.Month >= MonthOf(firstAt)).ToList();
            from = new DateTimeOffset(MonthOf(firstAt).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        var closes = await _db.DayCloses.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.ClosedAtUtc >= from && c.ClosedAtUtc < to)
            .Where(c => branchId == null || c.BranchId == branchId)
            .Where(c => agentId == null || c.AgentId == agentId)
            .Where(c => c.ExpectedCash != null)
            .Select(c => new { c.CountedCash, c.CountedFloat, c.ExpectedCash, c.ExpectedFloat })
            .ToListAsync(cancellationToken);
        var balanced = closes.Count(c => Math.Abs(c.CountedCash - c.ExpectedCash!.Value) < 1m
            && Math.Abs(c.CountedFloat - (c.ExpectedFloat ?? c.CountedFloat)) < 1m);

        var business = await BusinessNameAsync(organizationId, cancellationToken);
        var subject = await ScopeAsync(organizationId, branchId, agentId, cancellationToken);
        var generated = _clock.GetUtcNow();

        // A document number derived from what the record says, so two copies of the same record
        // carry the same number and an altered one would not match a copy from the owner.
        var fingerprint = string.Join('|', business, subject, from.ToString("O"), string.Join(';',
            monthList.Select(m => $"{m.Month:yyyy-MM}:{m.Transactions}:{m.Volume:0.00}:{m.Commission:0.00}")));
        var number = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..10];

        return new TradingRecord(business, subject,
            DateOnly.FromDateTime(from.UtcDateTime), DateOnly.FromDateTime(to.UtcDateTime.AddDays(-1)),
            first ?? generated, monthList, closes.Count, balanced, generated,
            $"ZT-{number[..5]}-{number[5..]}");
    }

    public byte[] CommissionCsv(CommissionReport report)
    {
        var csv = new StringBuilder();
        csv.AppendLine(Cell(report.BusinessName) + ",Commission report");
        csv.AppendLine(Cell(report.ScopeLabel) + "," + Cell($"{report.FromMonth:MMMM yyyy} to {report.ToMonth:MMMM yyyy}"));
        csv.AppendLine();
        csv.AppendLine("Month,Agent,Network,Commission payments,Commission (GHS)");
        foreach (var r in report.Rows)
        {
            csv.AppendLine(string.Join(',',
                Cell(r.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture)),
                Cell(r.AgentName),
                Cell(r.Network),
                r.Count.ToString(CultureInfo.InvariantCulture),
                Cell(r.Amount.ToString("#,##0.00", CultureInfo.InvariantCulture))));
        }
        csv.AppendLine(",,,Total," + Cell(report.Total.ToString("#,##0.00", CultureInfo.InvariantCulture)));

        // With a byte-order mark, so Excel reads it as UTF-8 rather than guessing.
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    /// <summary>Quoted, with anything that could start a spreadsheet formula defused.</summary>
    private static string Cell(string value)
    {
        var safe = value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }

    public byte[] TradingRecordPdf(TradingRecord record)
    {
        var page = new StringBuilder();
        var top = P.PageHeight - P.Margin;
        var width = P.PageWidth - 2 * P.Margin;

        P.Rect(page, 0, top - 30, P.PageWidth, 30 + P.Margin, P.Accent);
        P.Text(page, P.Margin, top - 20, 16, bold: true, "1 1 1", record.BusinessName);
        P.TextRight(page, P.PageWidth - P.Margin, top - 19, 9, bold: true, P.Gold, "TRADING RECORD");

        P.Text(page, P.Margin, top - 56, 14, bold: true, P.Ink, record.SubjectLabel);
        P.Text(page, P.Margin, top - 72, 9, bold: false, P.Muted,
            $"{record.FromMonth:MMMM yyyy} to {record.ToMonth:MMMM yyyy}  -  trading with Zazi since {record.FirstTransactionAtUtc:d MMMM yyyy}");

        // Five headline figures.
        var boxTop = top - 88;
        const float boxHeight = 58;
        P.Rect(page, P.Margin, boxTop - boxHeight, width, boxHeight, P.Zebra);
        var figures = new (string Label, string Value)[]
        {
            ("Transactions", record.Transactions.ToString("N0", CultureInfo.InvariantCulture)),
            ("Volume (GHS)", record.Volume.ToString("N2", CultureInfo.InvariantCulture)),
            ("Commission (GHS)", record.Commission.ToString("N2", CultureInfo.InvariantCulture)),
            ("Days traded", record.ActiveDays.ToString("N0", CultureInfo.InvariantCulture)),
            ("Day closes balanced", record.Closes == 0 ? "-" : $"{record.BalancedCloses} of {record.Closes}")
        };
        var cellWidth = width / figures.Length;
        for (var i = 0; i < figures.Length; i++)
        {
            var x = P.Margin + 12 + i * cellWidth;
            P.Text(page, x, boxTop - 20, 8, bold: false, P.Muted, figures[i].Label);
            P.Text(page, x, boxTop - 40, 13, bold: true, P.Ink, figures[i].Value);
        }

        // Month by month.
        var tableTop = boxTop - boxHeight - 30;
        string[] headings = ["Month", "Transactions", "Days traded", "Deposits", "Withdrawals", "Volume", "Commission"];
        float[] widths = [96, 70, 64, 76, 76, 76, 73];
        float RowX(int column) => P.Margin + widths.Take(column).Sum();

        P.Rect(page, P.Margin, tableTop - 6, width, 18, P.Accent);
        for (var c = 0; c < headings.Length; c++)
        {
            if (c == 0) P.Text(page, RowX(c) + 6, tableTop, 8, bold: true, "1 1 1", headings[c]);
            else P.TextRight(page, RowX(c) + widths[c] - 6, tableTop, 8, bold: true, "1 1 1", headings[c]);
        }

        var y = tableTop - 20;
        var index = 0;
        foreach (var m in record.Months.OrderBy(m => m.Month))
        {
            if (index++ % 2 == 1)
            {
                P.Rect(page, P.Margin, y - 5, width, 16, P.Zebra);
            }

            string[] values =
            [
                m.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                m.Transactions.ToString("N0", CultureInfo.InvariantCulture),
                m.ActiveDays.ToString("N0", CultureInfo.InvariantCulture),
                m.Deposits.ToString("N2", CultureInfo.InvariantCulture),
                m.Withdrawals.ToString("N2", CultureInfo.InvariantCulture),
                m.Volume.ToString("N2", CultureInfo.InvariantCulture),
                m.Commission.ToString("N2", CultureInfo.InvariantCulture)
            ];
            for (var c = 0; c < values.Length; c++)
            {
                if (c == 0) P.Text(page, RowX(c) + 6, y, 8.5f, bold: false, P.Ink, values[c]);
                else P.TextRight(page, RowX(c) + widths[c] - 6, y, 8.5f, bold: c == 5, P.Ink, values[c]);
            }
            y -= 16;
        }
        P.HLine(page, P.Margin, y + 8, width, P.Line);

        var noteTop = y - 18;
        P.Text(page, P.Margin, noteTop, 8, bold: false, P.Muted,
            "Figures are taken from the Zazi ledger, in which a recorded transaction cannot be edited or deleted.");
        P.Text(page, P.Margin, noteTop - 12, 8, bold: false, P.Muted,
            "Volume is deposits plus withdrawals. A day close is balanced when the counted cash and float agree with the");
        P.Text(page, P.Margin, noteTop - 24, 8, bold: false, P.Muted,
            "records to within one cedi.");

        P.HLine(page, P.Margin, P.Margin + 22, width, P.Line);
        P.Text(page, P.Margin, P.Margin + 8, 8, bold: false, P.Muted,
            $"Document no. {record.DocumentNumber}  -  generated {record.GeneratedAtUtc:d MMM yyyy, HH:mm} GMT");
        P.TextRight(page, P.PageWidth - P.Margin, P.Margin + 8, 8, bold: true, P.Accent, "Zazi");

        return P.Assemble([page.ToString()]);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Whole months: the current one and the <paramref name="months"/> − 1 before it.</summary>
    private (DateTimeOffset From, DateTimeOffset To) Window(int months)
    {
        months = Math.Clamp(months, 1, 24);
        var now = _clock.GetUtcNow().UtcDateTime;
        var thisMonth = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), TimeSpan.Zero);
        return (thisMonth.AddMonths(-(months - 1)), thisMonth.AddMonths(1));
    }

    private static DateOnly MonthOf(DateTimeOffset at) => new(at.UtcDateTime.Year, at.UtcDateTime.Month, 1);

    private IQueryable<FinancialTransaction> Postable(Guid organizationId, Guid? branchId, Guid? agentId,
        DateTimeOffset from, DateTimeOffset to) =>
        _db.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.TransactionAtUtc >= from && t.TransactionAtUtc < to)
            .Where(t => branchId == null || t.BranchId == branchId)
            .Where(t => agentId == null || t.AgentId == agentId)
            .Where(t => t.State == TransactionLifecycleState.Accepted
                || t.State == TransactionLifecycleState.Synced
                || t.State == TransactionLifecycleState.Reversed
                || t.State == TransactionLifecycleState.Adjusted);

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return await _db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
    }

    private async Task<string> BusinessNameAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await _db.Organizations.AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.Name)
            .SingleAsync(cancellationToken);

    private async Task<string> ScopeAsync(Guid organizationId, Guid? branchId, Guid? agentId, CancellationToken cancellationToken)
    {
        if (agentId is { } agent)
        {
            var name = await _db.Users.AsNoTracking().Where(u => u.Id == agent && u.OrganizationId == organizationId)
                .Select(u => u.FullName).FirstOrDefaultAsync(cancellationToken);
            return name ?? "Agent";
        }

        if (branchId is { } branch)
        {
            var name = await _db.Branches.AsNoTracking().Where(b => b.Id == branch).Select(b => b.Name)
                .FirstOrDefaultAsync(cancellationToken);
            return name is null ? "Branch" : $"{name} branch";
        }

        return "Whole business";
    }
}
