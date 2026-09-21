using Zazi.Domain;

namespace Zazi.Application.Statements;

/// <summary>The periods a statement can cover, in the words an agent uses.</summary>
public enum StatementPeriod
{
    Today,
    ThisWeek,
    ThisMonth,
    ThisYear,
    Custom
}

public enum StatementFormat
{
    Pdf,
    Csv
}

/// <summary>
/// What to put on a statement.
/// </summary>
/// <remarks>
/// Scope — which organisation, which branch, which agent — is decided by the caller from the
/// signed-in identity, never taken from a query string as given. An agent's statement is
/// always their own; an owner may ask for one agent or everyone.
/// </remarks>
public sealed record StatementRequest(
    Guid OrganizationId,
    StatementPeriod Period,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? BranchId = null,
    Guid? AgentId = null,
    string? CustomerPhone = null);

/// <summary>One transaction as it appears on a statement.</summary>
public sealed record StatementLine(
    DateTimeOffset At,
    string AgentName,
    TransactionType Type,
    string Network,
    string? CustomerPhone,
    string? Reference,
    decimal Amount,
    decimal CashDelta,
    decimal FloatDelta);

/// <summary>The figures at the top of a statement.</summary>
public sealed record StatementTotals(
    int TransactionCount,
    int DepositCount,
    decimal Deposits,
    int WithdrawalCount,
    decimal Withdrawals,
    decimal Commission,
    decimal NetCash,
    decimal NetFloat);

public sealed record Statement(
    string BusinessName,
    string ScopeLabel,
    string PeriodLabel,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset GeneratedAtUtc,
    StatementTotals Totals,
    IReadOnlyList<StatementLine> Lines,
    bool Truncated);

/// <summary>A statement rendered to a file, ready to send.</summary>
public sealed record StatementFile(string FileName, string ContentType, byte[] Content);

public interface IStatementService
{
    Task<Statement> BuildAsync(StatementRequest request, CancellationToken cancellationToken = default);

    Task<StatementFile> RenderAsync(
        StatementRequest request,
        StatementFormat format,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a named period into a range. Ghana keeps UTC all year, so the UTC day is the
/// business day and no time-zone conversion is needed.
/// </summary>
public static class StatementPeriods
{
    /// <summary>Half-open: from inclusive, to exclusive.</summary>
    public static (DateTimeOffset From, DateTimeOffset To, string Label) Resolve(
        StatementPeriod period,
        DateOnly? from,
        DateOnly? to,
        DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        static DateTimeOffset At(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        switch (period)
        {
            case StatementPeriod.Today:
                return (At(today), At(today.AddDays(1)), today.ToString("d MMMM yyyy"));

            case StatementPeriod.ThisWeek:
            {
                // Monday to today, as a Ghanaian trading week is counted.
                var offset = ((int)today.DayOfWeek + 6) % 7;
                var monday = today.AddDays(-offset);
                return (At(monday), At(today.AddDays(1)),
                    $"Week of {monday:d MMMM yyyy}");
            }

            case StatementPeriod.ThisMonth:
            {
                var first = new DateOnly(today.Year, today.Month, 1);
                return (At(first), At(today.AddDays(1)), first.ToString("MMMM yyyy"));
            }

            case StatementPeriod.ThisYear:
            {
                var first = new DateOnly(today.Year, 1, 1);
                return (At(first), At(today.AddDays(1)), today.Year.ToString());
            }

            case StatementPeriod.Custom:
            {
                if (from is not { } start || to is not { } end)
                {
                    throw new ArgumentException("A custom statement needs a start and an end date.");
                }

                if (end < start)
                {
                    throw new ArgumentException("The end date is before the start date.");
                }

                if (end.DayNumber - start.DayNumber > 366 * 2)
                {
                    throw new ArgumentException("A statement can cover at most two years.");
                }

                return (At(start), At(end.AddDays(1)),
                    start == end ? start.ToString("d MMMM yyyy") : $"{start:d MMM yyyy} – {end:d MMM yyyy}");
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(period));
        }
    }
}
