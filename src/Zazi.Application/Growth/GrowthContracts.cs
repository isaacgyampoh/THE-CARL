using Zazi.Domain;

namespace Zazi.Application.Growth;

// ─── Business settings ───────────────────────────────────────────────────────

/// <summary>The settings an owner controls for how Zazi talks to people by SMS.</summary>
/// <param name="SmsPhoneNumber">
/// The owner's number, normalised. Shortage alerts and float requests are texted here, and an
/// owner answers float requests from it ("OK 4821").
/// </param>
public sealed record BusinessSettings(string Name, string? SmsPhoneNumber, bool SendCustomerReceipts, bool SendDailyDigest);

public interface IBusinessSettingsService
{
    Task<BusinessSettings> GetAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <exception cref="ArgumentException">The number is not a Ghanaian mobile number.</exception>
    Task SaveAsync(Guid organizationId, string? smsPhoneNumber, bool sendCustomerReceipts, bool sendDailyDigest,
        Guid actorUserId, CancellationToken cancellationToken = default);
}

// ─── Customer receipts ───────────────────────────────────────────────────────

/// <summary>
/// Texts the customer a receipt for a cash in or cash out, when the business has chosen to.
/// </summary>
public interface ICustomerReceipts
{
    /// <summary>Best effort: returns whether a receipt was sent, never throws for a failed send.</summary>
    Task<bool> SendAsync(Guid transactionId, CancellationToken cancellationToken = default);
}

// ─── Float requests ──────────────────────────────────────────────────────────

public sealed record FloatRequestDto(
    Guid Id,
    Guid AgentId,
    string AgentName,
    string Network,
    decimal Amount,
    string Code,
    string Channel,
    FloatRequestStatus Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc);

public sealed class FloatRequestRejectedException(string message) : Exception(message);

public static class FloatRequestRules
{
    public const decimal Minimum = 1m;
    public const decimal Maximum = 1_000_000m;

    /// <summary>An agent may have this many waiting at once; more is a slip or a stuck phone.</summary>
    public const int MaximumPendingPerAgent = 3;
}

public interface IFloatRequestService
{
    Task<FloatRequestDto> RequestAsync(Guid organizationId, Guid branchId, Guid agentId, string network,
        decimal amount, string channel, CancellationToken cancellationToken = default);

    /// <summary>Approving records the float given to the agent, through the ledger, in the same step.</summary>
    Task<FloatRequestDto> DecideAsync(Guid organizationId, Guid requestId, bool approve, Guid deciderUserId,
        string via, CancellationToken cancellationToken = default);

    /// <summary>An owner's SMS answer, by the four-digit code. Null when no pending request has it.</summary>
    Task<FloatRequestDto?> DecideByCodeAsync(Guid organizationId, string code, bool approve,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FloatRequestDto>> PendingAsync(Guid organizationId, Guid? branchId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FloatRequestDto>> MineAsync(Guid organizationId, Guid agentId, int take = 5,
        CancellationToken cancellationToken = default);
}

// ─── Reports ─────────────────────────────────────────────────────────────────

/// <summary>Commission earned in one month on one network, by one agent.</summary>
public sealed record CommissionRow(DateOnly Month, Guid AgentId, string AgentName, string Network, int Count, decimal Amount);

public sealed record CommissionReport(
    string BusinessName,
    string ScopeLabel,
    DateOnly FromMonth,
    DateOnly ToMonth,
    IReadOnlyList<CommissionRow> Rows)
{
    public decimal Total => Rows.Sum(r => r.Amount);
}

/// <summary>One month of trading, as a lender reads it.</summary>
public sealed record TradingMonth(
    DateOnly Month,
    int Transactions,
    int ActiveDays,
    decimal Deposits,
    decimal Withdrawals,
    decimal Commission)
{
    public decimal Volume => Deposits + Withdrawals;
}

/// <summary>
/// A summary of how a business or an agent has traded — what a microfinance officer asks to
/// see before lending for float. Built from the ledger, so it cannot be dressed up.
/// </summary>
public sealed record TradingRecord(
    string BusinessName,
    string SubjectLabel,
    DateOnly FromMonth,
    DateOnly ToMonth,
    DateTimeOffset FirstTransactionAtUtc,
    IReadOnlyList<TradingMonth> Months,
    int Closes,
    int BalancedCloses,
    DateTimeOffset GeneratedAtUtc,
    string DocumentNumber)
{
    public int Transactions => Months.Sum(m => m.Transactions);
    public decimal Volume => Months.Sum(m => m.Volume);
    public decimal Commission => Months.Sum(m => m.Commission);
    public int ActiveDays => Months.Sum(m => m.ActiveDays);
}

public interface IReportService
{
    /// <param name="agentId">One agent, or everyone in scope when null.</param>
    Task<CommissionReport> CommissionAsync(Guid organizationId, Guid? branchId, Guid? agentId, int months = 12,
        CancellationToken cancellationToken = default);

    Task<TradingRecord> TradingRecordAsync(Guid organizationId, Guid? branchId, Guid? agentId, int months = 12,
        CancellationToken cancellationToken = default);

    byte[] CommissionCsv(CommissionReport report);

    byte[] TradingRecordPdf(TradingRecord record);
}

// ─── Dashboard insights ──────────────────────────────────────────────────────

/// <summary>One business day's trading, for the dashboard's charts and comparisons.</summary>
public sealed record DailyActivity(DateOnly Day, int Transactions, decimal CashIn, decimal CashOut, decimal Commission)
{
    public decimal Volume => CashIn + CashOut;
}

public interface IInsightsService
{
    /// <summary>
    /// The last <paramref name="days"/> business days, oldest first, today included — every
    /// day present, a quiet day as zeros, so a chart's gaps are real.
    /// </summary>
    Task<IReadOnlyList<DailyActivity>> DailyAsync(Guid organizationId, Guid? branchId, int days = 31,
        CancellationToken cancellationToken = default);
}

// ─── Expenses and profit ─────────────────────────────────────────────────────

public sealed record ExpenseDto(
    Guid Id,
    DateOnly SpentOn,
    ExpenseCategory Category,
    decimal Amount,
    string? Note,
    Guid? AgentId,
    string? AgentName,
    string RecordedBy);

/// <summary>What a period earned, what it cost, and what was left.</summary>
/// <remarks>
/// Commission is what the networks paid the business. Expenses are what the business paid to
/// keep trading. Profit is the difference — the figure an owner actually lives on, and the one
/// no rival app shows them.
/// </remarks>
public sealed record ProfitSummary(
    DateOnly From,
    DateOnly To,
    decimal Commission,
    decimal Expenses,
    IReadOnlyList<CategorySpend> ByCategory)
{
    public decimal Profit => Commission - Expenses;

    /// <summary>Share of commission kept, or null when nothing was earned to keep.</summary>
    public decimal? Margin => Commission <= 0m ? null : decimal.Round(Profit / Commission * 100m, 1);
}

public sealed record CategorySpend(ExpenseCategory Category, decimal Amount);

public sealed class ExpenseRejectedException(string message) : Exception(message);

public interface IExpenseService
{
    /// <exception cref="ExpenseRejectedException">The amount or the date is not usable.</exception>
    Task<ExpenseDto> RecordAsync(Guid organizationId, Guid? branchId, Guid? agentId, ExpenseCategory category,
        decimal amount, DateOnly spentOn, string? note, Guid recordedByUserId, string? submissionToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExpenseDto>> ListAsync(Guid organizationId, Guid? branchId, DateOnly from, DateOnly to,
        CancellationToken cancellationToken = default);

    Task<ProfitSummary> ProfitAsync(Guid organizationId, Guid? branchId, DateOnly from, DateOnly to,
        CancellationToken cancellationToken = default);
}

// ─── The evening email ───────────────────────────────────────────────────────

/// <summary>A business's day, as the owner's evening email reports it.</summary>
public sealed record DailyDigest(
    Guid OrganizationId,
    string BusinessName,
    string ToAddress,
    DateOnly Day,
    int Transactions,
    decimal Volume,
    decimal CashIn,
    decimal CashOut,
    decimal Commission,
    decimal MonthCommission,
    decimal MonthExpenses,
    int AgentsClosed,
    int AgentsShort,
    IReadOnlyList<string> NotClosed,
    int FloatRequestsWaiting,
    decimal CashHeld,
    decimal FloatHeld)
{
    public decimal MonthProfit => MonthCommission - MonthExpenses;

    /// <summary>Nothing traded, nothing waiting, nobody short: an email would be noise.</summary>
    public bool WorthSending =>
        Transactions > 0 || AgentsShort > 0 || FloatRequestsWaiting > 0 || NotClosed.Count > 0;
}

public interface IDailyDigestService
{
    /// <summary>Builds one business's day. Null when there is no owner address to send to.</summary>
    Task<DailyDigest?> BuildAsync(Guid organizationId, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>Sends the evening email to every business that wants one. Returns how many went.</summary>
    Task<int> SendAllAsync(DateOnly day, CancellationToken cancellationToken = default);
}
