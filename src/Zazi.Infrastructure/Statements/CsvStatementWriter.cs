using System.Globalization;
using System.Text;
using Zazi.Application.Statements;
using Zazi.Domain;

namespace Zazi.Infrastructure.Statements;

/// <summary>
/// A statement as a CSV file that opens cleanly in Excel and Google Sheets.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A UTF-8 byte-order mark, without which Excel reads the file as Latin-1 and mangles
/// any name with an accent.</item>
/// <item>Customer numbers written grouped — "024 412 3456". Written as "0244123456", Excel
/// treats them as numbers and drops the leading zero, turning every customer into a
/// nine-digit number nobody can look up.</item>
/// <item>Text that begins with = + - or @ is prefixed with an apostrophe, so a name or a
/// reference cannot run as a formula in the spreadsheet of whoever opens it.</item>
/// </list>
/// </remarks>
public static class CsvStatementWriter
{
    private static string Count(int n) => n == 1 ? "1 transaction" : $"{n:N0} transactions";

    public static byte[] Write(Statement statement)
    {
        var csv = new StringBuilder();
        var t = statement.Totals;

        Row(csv, statement.BusinessName);
        Row(csv, "Statement", statement.PeriodLabel);
        Row(csv, "Covering", statement.ScopeLabel);
        Row(csv, "Generated", statement.GeneratedAtUtc.ToString("d MMM yyyy HH:mm 'GMT'", CultureInfo.InvariantCulture));
        if (statement.Truncated)
        {
            Row(csv, "Note", $"This statement stops at {statement.Lines.Count:N0} transactions. Choose a shorter period for the rest.");
        }

        csv.AppendLine();
        Row(csv, "Transactions", t.TransactionCount.ToString(CultureInfo.InvariantCulture));
        Row(csv, "Deposits (cash in)", Money(t.Deposits), Count(t.DepositCount));
        Row(csv, "Withdrawals (cash out)", Money(t.Withdrawals), Count(t.WithdrawalCount));
        Row(csv, "Commission recorded", Money(t.Commission));
        Row(csv, "Net change in cash", Money(t.NetCash));
        Row(csv, "Net change in float", Money(t.NetFloat));
        csv.AppendLine();

        Row(csv, "Date", "Time", "Agent", "Type", "Network", "Customer number", "Reference",
            "Amount (GHS)", "Cash change (GHS)", "Float change (GHS)");

        foreach (var line in statement.Lines)
        {
            Row(csv,
                line.At.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                line.At.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                line.AgentName,
                StatementText.Describe(line.Type),
                StatementText.Network(line.Network),
                line.CustomerPhone is null ? string.Empty : GhanaPhoneNumber.Display(line.CustomerPhone),
                line.Reference ?? string.Empty,
                Money(line.Amount),
                Money(line.CashDelta),
                Money(line.FloatDelta));
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(csv.ToString());
        return [.. preamble, .. body];
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static void Row(StringBuilder csv, params string[] cells)
    {
        csv.AppendJoin(',', cells.Select(Escape));
        csv.Append("\r\n");
    }

    private static string Escape(string value)
    {
        // A formula lead is neutralised — but not a plain negative number, which is data.
        if (value.Length > 0 && "=+-@".Contains(value[0])
            && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}

/// <summary>Words shared by every statement format, so the PDF and the CSV say the same thing.</summary>
public static class StatementText
{
    public static string Describe(TransactionType type) => type switch
    {
        TransactionType.CashIn => "Deposit (cash in)",
        TransactionType.CashOut => "Withdrawal (cash out)",
        TransactionType.Commission => "Commission",
        TransactionType.Reversal => "Reversal",
        TransactionType.Adjustment => "Cash or float given",
        TransactionType.Transfer => "Transfer",
        _ => "Other"
    };

    public static string Network(string network) => network.ToUpperInvariant() switch
    {
        "MTN" => "MTN",
        "TELECEL" => "Telecel",
        "AIRTELTIGO" => "AirtelTigo",
        _ => network
    };
}
