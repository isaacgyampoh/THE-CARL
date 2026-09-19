using System.Net;

namespace Zazi.Application.Email;

/// <summary>
/// The shell every Zazi email is rendered into.
/// </summary>
/// <remarks>
/// <para>
/// Extracted when password reset needed the same layout as the onboarding messages. Plain HTML
/// with inline styles, no template engine, no external images, no tracking pixel: mail clients
/// strip most of what a designer would want, and a message that renders as a blank box in
/// Outlook is a customer who cannot get into their account.
/// </para>
/// <para>
/// Every caller-supplied value passed in here must already be encoded. The helpers below do it,
/// which is the reason to go through them rather than assembling HTML at each call site.
/// </para>
/// </remarks>
public static class EmailLayout
{
    private const string Ink = "#1c1917";
    private const string Muted = "#57534e";
    private const string Faint = "#78716c";
    private const string Line = "#e7e5e4";
    private const string Page = "#f5f5f4";

    /// <summary>Wraps body markup in the standard card.</summary>
    public static string Page_(string bodyHtml) => $"""
        <!DOCTYPE html>
        <html lang="en"><body style="margin:0;padding:24px;background:{Page};font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:{Ink};">
          <div style="max-width:520px;margin:0 auto;background:#ffffff;border:1px solid {Line};border-radius:12px;padding:32px;">
            <p style="margin:0 0 24px;font-size:20px;font-weight:600;letter-spacing:-0.02em;">Zazi</p>
        {bodyHtml}
          </div>
        </body></html>
        """;

    public static string Paragraph(string html) =>
        $"""    <p style="margin:0 0 16px;font-size:16px;line-height:1.5;">{html}</p>""";

    public static string Small(string html) =>
        $"""    <p style="margin:0 0 8px;font-size:14px;line-height:1.5;color:{Muted};">{html}</p>""";

    public static string Footnote(string html) => $"""
            <hr style="border:none;border-top:1px solid {Line};margin:24px 0;">
            <p style="margin:0;font-size:13px;line-height:1.5;color:{Faint};">{html}</p>
        """;

    /// <summary>A call-to-action button, plus the raw URL for clients that strip the link.</summary>
    public static string Action(string label, string url)
    {
        var safeUrl = Encode(url);
        return $"""
                <p style="margin:0 0 24px;">
                  <a href="{safeUrl}" style="display:inline-block;background:{Ink};color:#ffffff;text-decoration:none;padding:12px 20px;border-radius:8px;font-size:15px;font-weight:500;">{Encode(label)}</a>
                </p>
                <p style="margin:0 0 24px;font-size:14px;line-height:1.5;color:{Muted};">If the button does not work, paste this into your browser:<br><span style="word-break:break-all;">{safeUrl}</span></p>
            """;
    }

    /// <summary>
    /// HTML-encodes a caller-supplied value.
    /// </summary>
    /// <remarks>
    /// A business name is chosen by whoever signed up, and it lands in HTML sent to an address
    /// they also chose.
    /// </remarks>
    public static string Encode(string value) => WebUtility.HtmlEncode(value);
}
