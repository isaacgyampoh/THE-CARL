using Zazi.Application.Growth;
using Zazi.Application.Security;

namespace Zazi.Web.Endpoints;

/// <summary>Report downloads from the portal: plain GET links that return a file.</summary>
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/reports/commission.csv", async (
            IReportService reports, ICurrentUserContext currentUser, Guid? agent, int? months, CancellationToken cancellationToken) =>
        {
            // Scope from the signed-in identity, never the link: a branch manager's report is
            // their branch's whatever the query string says.
            var report = await reports.CommissionAsync(currentUser.OrganizationId,
                currentUser.HasOrganizationWideScope ? null : currentUser.BranchId, agent, months ?? 12, cancellationToken);
            return Results.File(reports.CommissionCsv(report), "text/csv; charset=utf-8",
                $"zazi-commission-{report.ToMonth:yyyy-MM}.csv");
        })
        .RequireAuthorization(ZaziPolicies.DashboardBranch);

        routes.MapGet("/reports/trading-record.pdf", async (
            IReportService reports, ICurrentUserContext currentUser, Guid? agent, int? months, CancellationToken cancellationToken) =>
        {
            var record = await reports.TradingRecordAsync(currentUser.OrganizationId,
                currentUser.HasOrganizationWideScope ? null : currentUser.BranchId, agent, months ?? 12, cancellationToken);
            return Results.File(reports.TradingRecordPdf(record), "application/pdf",
                $"zazi-trading-record-{record.ToMonth:yyyy-MM}.pdf");
        })
        .RequireAuthorization(ZaziPolicies.DashboardBranch);
    }
}
