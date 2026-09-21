using System.Globalization;
using System.Text;
using Zazi.Application.Statements;
using Zazi.Domain;

namespace Zazi.Infrastructure.Statements;

/// <summary>
/// A statement as a PDF, written directly.
/// </summary>
/// <remarks>
/// <para>
/// No PDF library. The common ones draw through a native graphics stack, and a server image
/// missing one of its system packages fails only in production, only when someone first taps
/// "Download statement" — in front of a customer. A statement needs text, lines and shaded
/// rectangles, which the PDF format provides directly with its built-in Helvetica, so the whole
/// of it is a few hundred lines with nothing to install.
/// </para>
/// <para>
/// Helvetica is one of the fourteen fonts every PDF reader carries, so nothing is embedded
/// and a year's statement stays small enough to send over a slow connection. Its encoding has
/// no cedi sign; amounts say GHS, which is also how the networks' own SMS write them.
/// </para>
/// </remarks>
public static class PdfStatementWriter
{
    private static string Count(int n) => n == 1 ? "1 transaction" : $"{n:N0} transactions";

    // A4 portrait, in points.
    private const float PageWidth = 595.28f;
    private const float PageHeight = 841.89f;
    private const float Margin = 32f;
    private const float RowHeight = 14f;
    private const float TableFont = 7.5f;

    // The accent from the portal, so a printed statement looks like the product it came from.
    private const string Accent = "0.184 0.365 0.071";
    private const string Ink = "0.094 0.114 0.071";
    private const string Muted = "0.373 0.420 0.322";
    private const string Line = "0.890 0.914 0.839";
    private const string Zebra = "0.969 0.976 0.953";

    private sealed record Column(string Title, float Width, bool RightAligned);

    private static readonly Column[] Columns =
    [
        new("Date & time", 66, false),
        new("Agent", 70, false),
        new("Type", 84, false),
        new("Network", 44, false),
        new("Customer", 62, false),
        new("Reference", 60, false),
        new("Amount", 48, true),
        new("Cash", 48, true),
        new("Float", 48, true)
    ];

    public static byte[] Write(Statement statement)
    {
        // Paginate first, so every page can say "page n of N".
        const float firstPageTableTop = PageHeight - Margin - 190;
        const float otherPageTableTop = PageHeight - Margin - 40;
        const float tableBottom = Margin + 30;

        var firstCapacity = (int)((firstPageTableTop - tableBottom) / RowHeight) - 1;
        var otherCapacity = (int)((otherPageTableTop - tableBottom) / RowHeight) - 1;

        var pages = new List<(int Start, int Count)>();
        var remaining = statement.Lines.Count;
        var start = 0;
        var capacity = firstCapacity;
        do
        {
            var take = Math.Min(capacity, remaining);
            pages.Add((start, take));
            start += take;
            remaining -= take;
            capacity = otherCapacity;
        }
        while (remaining > 0);

        var contents = new List<string>();
        for (var index = 0; index < pages.Count; index++)
        {
            var page = new StringBuilder();
            var isFirst = index == 0;
            var tableTop = isFirst ? firstPageTableTop : otherPageTableTop;

            if (isFirst)
            {
                Header(page, statement);
            }
            else
            {
                Text(page, Margin, PageHeight - Margin - 12, 9, bold: true, Ink,
                    $"{statement.BusinessName} - statement, {statement.PeriodLabel} (continued)");
            }

            Table(page, statement, pages[index].Start, pages[index].Count, tableTop);

            var isLast = index == pages.Count - 1;
            if (isLast)
            {
                var after = tableTop - RowHeight * (pages[index].Count + 1) - 16;
                var closing = statement.Lines.Count == 0
                    ? "No transactions in this period."
                    : statement.Truncated
                        ? $"This statement stops at {statement.Lines.Count:N0} transactions. Choose a shorter period for the rest."
                        : "End of statement.";
                Text(page, Margin, Math.Max(after, tableBottom + 4), 8, bold: false, Muted, closing);
            }

            Footer(page, statement, index + 1, pages.Count);
            contents.Add(page.ToString());
        }

        return Assemble(contents);
    }

    // ─── Page parts ──────────────────────────────────────────────────────────

    private static void Header(StringBuilder page, Statement statement)
    {
        var top = PageHeight - Margin;

        // A band of accent across the top, with the business name in it.
        Rect(page, 0, top - 30, PageWidth, 30 + Margin, Accent);
        Text(page, Margin, top - 20, 16, bold: true, "1 1 1", statement.BusinessName);
        TextRight(page, PageWidth - Margin, top - 19, 9, bold: false, "1 1 1", "Zazi statement");

        Text(page, Margin, top - 52, 13, bold: true, Ink, statement.PeriodLabel);
        Text(page, Margin, top - 67, 9, bold: false, Muted, statement.ScopeLabel);
        Text(page, Margin, top - 80, 8, bold: false, Muted,
            "Generated " + statement.GeneratedAtUtc.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " GMT");

        // Totals: what came in, what went out, what was earned, what changed.
        var t = statement.Totals;
        var boxTop = top - 94;
        const float boxHeight = 76;
        Rect(page, Margin, boxTop - boxHeight, PageWidth - 2 * Margin, boxHeight, Zebra);
        StrokeRect(page, Margin, boxTop - boxHeight, PageWidth - 2 * Margin, boxHeight, Line);

        var cellWidth = (PageWidth - 2 * Margin) / 4;
        Figure(page, Margin + 10, boxTop - 16, "Transactions", t.TransactionCount.ToString("N0", CultureInfo.InvariantCulture), null);
        Figure(page, Margin + 10 + cellWidth, boxTop - 16, "Deposits (cash in)", "GHS " + Money(t.Deposits), Count(t.DepositCount));
        Figure(page, Margin + 10 + cellWidth * 2, boxTop - 16, "Withdrawals (cash out)", "GHS " + Money(t.Withdrawals), Count(t.WithdrawalCount));
        Figure(page, Margin + 10 + cellWidth * 3, boxTop - 16, "Commission recorded", "GHS " + Money(t.Commission), null);

        Text(page, Margin + 10, boxTop - boxHeight + 10, 8, bold: false, Muted,
            $"Net change in cash: GHS {Signed(t.NetCash)}     Net change in float: GHS {Signed(t.NetFloat)}");
    }

    private static void Figure(StringBuilder page, float x, float y, string label, string value, string? note)
    {
        Text(page, x, y, 7.5f, bold: false, Muted, label);
        Text(page, x, y - 15, 11.5f, bold: true, Ink, value);
        if (note is not null)
        {
            Text(page, x, y - 27, 7, bold: false, Muted, note);
        }
    }

    private static void Table(StringBuilder page, Statement statement, int start, int count, float top)
    {
        var width = PageWidth - 2 * Margin;

        // Column headings, on a line of their own with a rule beneath.
        var x = Margin;
        foreach (var column in Columns)
        {
            Cell(page, x, top - 10, column, column.Title.ToUpperInvariant(), 6.5f, bold: true, Muted);
            x += column.Width;
        }

        HLine(page, Margin, top - RowHeight + 1, width, Line);

        for (var i = 0; i < count; i++)
        {
            var line = statement.Lines[start + i];
            var rowTop = top - RowHeight * (i + 1);

            if (i % 2 == 1)
            {
                Rect(page, Margin, rowTop - RowHeight + 1, width, RowHeight, Zebra);
            }

            var cells = new[]
            {
                line.At.UtcDateTime.ToString("dd MMM yy HH:mm", CultureInfo.InvariantCulture),
                line.AgentName,
                StatementText.Describe(line.Type),
                StatementText.Network(line.Network),
                line.CustomerPhone is null ? "-" : GhanaPhoneNumber.Display(line.CustomerPhone),
                line.Reference ?? "-",
                Money(line.Amount),
                Signed(line.CashDelta),
                Signed(line.FloatDelta)
            };

            x = Margin;
            for (var c = 0; c < Columns.Length; c++)
            {
                Cell(page, x, rowTop - 10, Columns[c], cells[c], TableFont, bold: c == 6, Ink);
                x += Columns[c].Width;
            }
        }
    }

    private static void Footer(StringBuilder page, Statement statement, int number, int total)
    {
        HLine(page, Margin, Margin + 14, PageWidth - 2 * Margin, Line);
        Text(page, Margin, Margin + 3, 7, bold: false, Muted,
            $"{statement.BusinessName} - {statement.PeriodLabel} - {statement.ScopeLabel}");
        TextRight(page, PageWidth - Margin, Margin + 3, 7, bold: false, Muted, $"Page {number} of {total}");
    }

    // ─── Drawing ─────────────────────────────────────────────────────────────

    private static void Cell(StringBuilder page, float x, float y, Column column, string text, float size, bool bold, string colour)
    {
        const float padding = 3f;
        var fitted = Fit(text, column.Width - padding * 2, size, bold);
        if (column.RightAligned)
        {
            TextRight(page, x + column.Width - padding, y, size, bold, colour, fitted);
        }
        else
        {
            Text(page, x + padding, y, size, bold, colour, fitted);
        }
    }

    private static void Text(StringBuilder page, float x, float y, float size, bool bold, string colour, string text) =>
        page.Append(CultureInfo.InvariantCulture,
            $"BT {colour} rg /{(bold ? "F2" : "F1")} {F(size)} Tf {F(x)} {F(y)} Td ({Escape(text)}) Tj ET\n");

    private static void TextRight(StringBuilder page, float right, float y, float size, bool bold, string colour, string text) =>
        Text(page, right - Measure(text, size, bold), y, size, bold, colour, text);

    private static void Rect(StringBuilder page, float x, float y, float w, float h, string colour) =>
        page.Append(CultureInfo.InvariantCulture, $"{colour} rg {F(x)} {F(y)} {F(w)} {F(h)} re f\n");

    private static void StrokeRect(StringBuilder page, float x, float y, float w, float h, string colour) =>
        page.Append(CultureInfo.InvariantCulture, $"{colour} RG 0.6 w {F(x)} {F(y)} {F(w)} {F(h)} re S\n");

    private static void HLine(StringBuilder page, float x, float y, float w, string colour) =>
        page.Append(CultureInfo.InvariantCulture, $"{colour} RG 0.6 w {F(x)} {F(y)} m {F(x + w)} {F(y)} l S\n");

    private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Signed(decimal value) =>
        value > 0 ? "+" + Money(value) : value < 0 ? "-" + Money(-value) : "0.00";

    // ─── Text metrics ────────────────────────────────────────────────────────

    /// <summary>Helvetica's advance widths for printable ASCII, from its published metrics.</summary>
    private static readonly int[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, // space – /
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556,                               // 0 – 9
        278, 278, 584, 584, 584, 556, 1015,                                             // : – @
        667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,      // A – O
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,                          // P – Z
        278, 278, 278, 469, 556, 333,                                                   // [ – `
        556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,      // a – o
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500,                          // p – z
        334, 260, 334, 584                                                              // { – ~
    ];

    private static float Measure(string text, float size, bool bold)
    {
        var units = 0;
        foreach (var ch in text)
        {
            units += ch is >= ' ' and <= '~' ? HelveticaWidths[ch - ' '] : 556;
        }

        // Bold is set slightly wider; measured generously so a fitted cell never overruns.
        return units / 1000f * size * (bold ? 1.08f : 1f);
    }

    /// <summary>Shortens text to fit a column, ending in an ellipsis rather than overrunning.</summary>
    private static string Fit(string text, float width, float size, bool bold)
    {
        if (Measure(text, size, bold) <= width)
        {
            return text;
        }

        var cut = text;
        while (cut.Length > 1 && Measure(cut + "...", size, bold) > width)
        {
            cut = cut[..^1];
        }

        return cut.TrimEnd() + "...";
    }

    // ─── Encoding ────────────────────────────────────────────────────────────

    /// <summary>
    /// Escapes a string for a PDF literal and maps it into WinAnsi, the encoding the built-in
    /// fonts use. Characters it cannot show become "?" rather than corrupting the stream.
    /// </summary>
    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var mapped = ch switch
            {
                '–' => '\u0096',
                '—' => '\u0097',
                '·' => '·',
                '’' => '\u0092',
                '‘' => '\u0091',
                '“' => '\u0093',
                '”' => '\u0094',
                '₵' => 'C',
                _ when ch < 32 => ' ',
                _ when ch > 255 => '?',
                _ => ch
            };

            if (mapped is '(' or ')' or '\\')
            {
                escaped.Append('\\');
            }

            escaped.Append(mapped);
        }

        return escaped.ToString();
    }

    private static byte[] Assemble(IReadOnlyList<string> contents)
    {
        // Objects: 1 catalog, 2 page tree, 3 regular font, 4 bold font, then a page and its
        // content stream for each page.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty, // page tree, filled in once the page numbers are known
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"
        };

        var pageIds = new List<int>();
        foreach (var content in contents)
        {
            var pageId = objects.Count + 1;
            var contentId = pageId + 1;
            pageIds.Add(pageId);

            objects.Add(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(PageWidth)} {F(PageHeight)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>"));

            var length = Encoding.Latin1.GetByteCount(content);
            objects.Add($"<< /Length {length} >>\nstream\n{content}endstream");
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>";

        using var output = new MemoryStream();
        void Emit(string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            output.Write(bytes, 0, bytes.Length);
        }

        Emit("%PDF-1.4\n%âãÏÓ\n");

        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Emit($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = output.Position;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Emit(table.ToString());

        return output.ToArray();
    }
}
