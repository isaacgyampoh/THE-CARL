using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zazi.Application.Closing;
using Zazi.Application.Email;
using Zazi.Application.Growth;
using Zazi.Domain;

namespace Zazi.Infrastructure.Growth;

/// <summary>
/// The owner's evening email: what the business did today, and anything waiting on them.
/// </summary>
/// <remarks>
/// <para>
/// The product's promise is that an owner does not have to ring round to find out how the day
/// went. A dashboard only keeps that promise for owners who open it; an email keeps it for the
/// rest — which in this market is most of them.
/// </para>
/// <para>
/// Sent once per business per day. The send carries an idempotency key of the business and the
/// date, so a retry after a timeout cannot produce a second, contradictory summary.
/// </para>
/// </remarks>
public sealed class DailyDigestService : IDailyDigestService
{
    private readonly ApplicationDbContext _db;
    private readonly IDayCloseService _closes;
    private readonly IExpenseService _expenses;
    private readonly IEmailSender _email;
    private readonly ILogger<DailyDigestService>? _logger;
    private readonly string _portalUrl;

    public DailyDigestService(
        ApplicationDbContext db,
        IDayCloseService closes,
        IExpenseService expenses,
        IEmailSender email,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        ILogger<DailyDigestService>? logger = null)
    {
        _db = db;
        _closes = closes;
        _expenses = expenses;
        _email = email;
        _logger = logger;
        _portalUrl = configuration["Portal:PublicBaseUrl"]?.TrimEnd('/') ?? "https://app.getzazi.com";
    }

    public async Task<DailyDigest?> BuildAsync(Guid organizationId, DateOnly day, CancellationToken cancellationToken = default)
    {
        var organization = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => new { o.Id, o.Name, o.Email })
            .SingleOrDefaultAsync(cancellationToken);
        if (organization is null)
        {
            return null;
        }

        // The business's own address, or the owner's. Without one there is nobody to write to.
        var to = organization.Email ?? await _db.Users.AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.IsActive && u.EmailVerified && u.Email != null)
            .OrderBy(u => u.CreatedAt)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(to))
        {
            return null;
        }

        // Ghana keeps GMT all year, so the UTC day is the business day.
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var until = from.AddDays(1);

        var today = await _db.Transactions.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.TransactionAtUtc >= from && t.TransactionAtUtc < until)
            .Where(t => t.State == TransactionLifecycleState.Accepted
                || t.State == TransactionLifecycleState.Synced
                || t.State == TransactionLifecycleState.Reversed
                || t.State == TransactionLifecycleState.Adjusted)
            .Select(t => new { t.Type, t.Amount })
            .ToListAsync(cancellationToken);

        var closes = await _closes.ForDayAsync(organizationId, null, day, cancellationToken);
        var notClosed = await _closes.NotClosedAsync(organizationId, null, day, cancellationToken);

        var waiting = await _db.FloatRequests.AsNoTracking()
            .CountAsync(r => r.OrganizationId == organizationId && r.Status == FloatRequestStatus.Pending, cancellationToken);

        var cashHeld = await _db.CashBalances.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId)
            .SumAsync(c => (decimal?)c.CurrentCash, cancellationToken) ?? 0m;
        var floatHeld = await _db.FloatBalances.AsNoTracking()
            .Where(f => f.OrganizationId == organizationId)
            .SumAsync(f => (decimal?)f.CurrentFloat, cancellationToken) ?? 0m;

        var monthStart = new DateOnly(day.Year, day.Month, 1);
        var profit = await _expenses.ProfitAsync(organizationId, null, monthStart, day, cancellationToken);

        return new DailyDigest(
            organization.Id,
            organization.Name,
            to!,
            day,
            today.Count(t => t.Type is TransactionType.CashIn or TransactionType.CashOut),
            today.Where(t => t.Type is TransactionType.CashIn or TransactionType.CashOut).Sum(t => t.Amount),
            today.Where(t => t.Type == TransactionType.CashIn).Sum(t => t.Amount),
            today.Where(t => t.Type == TransactionType.CashOut).Sum(t => t.Amount),
            today.Where(t => t.Type == TransactionType.Commission).Sum(t => t.Amount),
            profit.Commission,
            profit.Expenses,
            closes.Count,
            closes.Count(c => c.Status == "Short"),
            notClosed,
            waiting,
            cashHeld,
            floatHeld);
    }

    public async Task<int> SendAllAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var organizations = await _db.Organizations.AsNoTracking()
            .Where(o => o.SendDailyDigest)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var organizationId in organizations)
        {
            try
            {
                var digest = await BuildAsync(organizationId, day, cancellationToken);
                if (digest is null || !digest.WorthSending)
                {
                    continue;
                }

                var result = await _email.SendAsync(new EmailMessage(
                    digest.ToAddress,
                    Subject(digest),
                    Html(digest),
                    Text(digest))
                {
                    // One summary per business per day, whatever happens to the connection.
                    IdempotencyKey = $"digest-{digest.OrganizationId:N}-{digest.Day:yyyy-MM-dd}"
                }, cancellationToken);

                if (result.Sent)
                {
                    sent++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One business's summary failing must not stop the rest.
                _logger?.LogWarning(exception, "The evening summary could not be sent for one business.");
            }
        }

        return sent;
    }

    internal static string Subject(DailyDigest d) => d.Transactions == 0
        ? $"Zazi: nothing recorded on {d.Day:ddd d MMM}"
        : $"Zazi: {d.Transactions} transactions, {Money(d.Volume)} on {d.Day:ddd d MMM}";

    internal static string Text(DailyDigest d)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{d.BusinessName} — {d.Day:dddd d MMMM}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Transactions: {d.Transactions}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Volume: {Money(d.Volume)} (in {Money(d.CashIn)}, out {Money(d.CashOut)})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Commission today: {Money(d.Commission)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"This month: commission {Money(d.MonthCommission)}, costs {Money(d.MonthExpenses)}, profit {Money(d.MonthProfit)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Held by your agents: cash {Money(d.CashHeld)}, float {Money(d.FloatHeld)}");
        text.AppendLine();

        if (d.AgentsShort > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{d.AgentsShort} agent(s) closed short — see Closing.");
        }

        if (d.NotClosed.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Not closed: {string.Join(", ", d.NotClosed)}");
        }

        if (d.FloatRequestsWaiting > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{d.FloatRequestsWaiting} float request(s) waiting for you.");
        }

        text.AppendLine();
        text.AppendLine("Open Zazi to see more.");
        return text.ToString();
    }

    internal string Html(DailyDigest d)
    {
        // Plain, table-free HTML: it has to read the same in Gmail on a phone as anywhere else.
        var attention = new StringBuilder();
        if (d.AgentsShort > 0)
        {
            attention.Append(CultureInfo.InvariantCulture,
                $"<li><strong>{d.AgentsShort}</strong> agent(s) closed short — <a href=\"{_portalUrl}/closing\">see closing</a></li>");
        }

        if (d.NotClosed.Count > 0)
        {
            attention.Append(CultureInfo.InvariantCulture,
                $"<li>Not closed: {System.Net.WebUtility.HtmlEncode(string.Join(", ", d.NotClosed))}</li>");
        }

        if (d.FloatRequestsWaiting > 0)
        {
            attention.Append(CultureInfo.InvariantCulture,
                $"<li><strong>{d.FloatRequestsWaiting}</strong> float request(s) waiting — <a href=\"{_portalUrl}/float\">answer them</a></li>");
        }

        var attentionBlock = attention.Length == 0
            ? "<p style=\"color:#067647;margin:16px 0 0\">Nothing is waiting on you.</p>"
            : $"<ul style=\"margin:16px 0 0;padding-left:18px;color:#8a5300\">{attention}</ul>";

        return $"""
            <div style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;color:#0d1726;max-width:560px">
              <div style="background:#0b1f33;color:#fff;padding:20px 22px;border-radius:12px 12px 0 0">
                <div style="font-size:13px;color:#9fb1c6">{System.Net.WebUtility.HtmlEncode(d.BusinessName)}</div>
                <div style="font-size:20px;font-weight:700;margin-top:2px">{d.Day:dddd d MMMM}</div>
              </div>
              <div style="border:1px solid #e5e9f0;border-top:0;border-radius:0 0 12px 12px;padding:20px 22px">
                <div style="font-size:13px;color:#5d6879">Traded today</div>
                <div style="font-size:26px;font-weight:700;letter-spacing:-.5px">{Money(d.Volume)}</div>
                <div style="font-size:13px;color:#5d6879;margin-top:2px">
                  {d.Transactions} transactions · in {Money(d.CashIn)} · out {Money(d.CashOut)}
                </div>

                <hr style="border:0;border-top:1px solid #e5e9f0;margin:18px 0" />

                <div style="font-size:13px;color:#5d6879">This month</div>
                <div style="font-size:15px;margin-top:4px">
                  Commission <strong>{Money(d.MonthCommission)}</strong> · costs <strong>{Money(d.MonthExpenses)}</strong> ·
                  profit <strong style="color:#067647">{Money(d.MonthProfit)}</strong>
                </div>

                <div style="font-size:13px;color:#5d6879;margin-top:14px">Held by your agents</div>
                <div style="font-size:15px;margin-top:4px">
                  Cash <strong>{Money(d.CashHeld)}</strong> · float <strong>{Money(d.FloatHeld)}</strong>
                </div>

                {attentionBlock}

                <p style="margin:20px 0 0">
                  <a href="{_portalUrl}" style="display:inline-block;background:#0f2a44;color:#fff;text-decoration:none;padding:10px 18px;border-radius:8px;font-weight:600">Open Zazi</a>
                </p>
                <p style="font-size:12px;color:#8a94a3;margin:18px 0 0">
                  You get this because you own a Zazi business. Turn it off in Settings.
                </p>
              </div>
            </div>
            """;
    }

    private static string Money(decimal value) =>
        (value < 0 ? "-GHS " : "GHS ") + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);
}
