using Zazi.Application.Security;
using Zazi.Application.Statements;

namespace Zazi.Web.Endpoints;

/// <summary>
/// "Download statement" in the portal: a file, not a page.
/// </summary>
/// <remarks>
/// A plain GET that returns the file, so it works as an ordinary link — on a phone that means
/// the browser's own download, which lands where the owner expects and can be shared straight
/// to WhatsApp or email.
/// </remarks>
public static class StatementEndpoints
{
    public static void MapStatementEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/statements/download", async (
            HttpContext context,
            IStatementService statements,
            ICurrentUserContext currentUser,
            StatementPeriod period,
            DateOnly? from,
            DateOnly? to,
            Guid? agent,
            string? customer,
            StatementFormat? format,
            CancellationToken cancellationToken) =>
        {
            // Scope from the signed-in identity. A branch manager's statement is their branch
            // whatever the link says; only organisation-wide roles see every branch.
            var request = new StatementRequest(
                currentUser.OrganizationId,
                period,
                from,
                to,
                BranchId: currentUser.HasOrganizationWideScope ? null : currentUser.BranchId,
                AgentId: agent,
                CustomerPhone: customer);

            try
            {
                var file = await statements.RenderAsync(request, format ?? StatementFormat.Pdf, cancellationToken);
                return Results.File(file.Content, file.ContentType, file.FileName);
            }
            catch (ArgumentException problem)
            {
                // A custom range with the dates the wrong way round, or longer than two years.
                // Said in words, because this is followed from a link, not submitted by a form.
                return Results.Text(problem.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        })
        // Customer numbers and every transaction amount: the same people who can read
        // transactions, and nobody else.
        .RequireAuthorization(ZaziPolicies.TransactionRead);
    }
}
