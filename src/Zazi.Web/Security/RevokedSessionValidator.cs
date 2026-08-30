using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Zazi.Application.Security;
using Zazi.Infrastructure;

namespace Zazi.Web.Security;

/// <summary>
/// Re-checks on every request that the signed-in account is still permitted to be signed in.
/// </summary>
/// <remarks>
/// <para>
/// The API's access tokens live for an hour, so revoking a user takes effect within one token
/// lifetime when the refresh is refused. A cookie has no such natural expiry — this session
/// slides for eight hours — so without this check, revoking or deactivating an account would
/// leave its browser session working for the rest of the day. That is a weaker guarantee than
/// the API gives, for the same act of revocation.
/// </para>
/// <para>
/// The comparison is against the security stamp the existing revocation service rotates. No
/// second revocation mechanism is introduced; this reads the one that already exists.
/// </para>
/// </remarks>
public static class RevokedSessionValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;

        if (!ClaimsTenantIdentity.IsAuthenticated(principal))
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var database = services.GetRequiredService<ApplicationDbContext>();

        Guid userId;
        try
        {
            userId = ClaimsTenantIdentity.UserId(principal);
        }
        catch (TenantAccessDeniedException)
        {
            await RejectAsync(context);
            return;
        }

        var account = await database.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, u.SecurityStamp })
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

        // A deleted account, a deactivated one, or a rotated stamp all mean this cookie
        // outlived the authority it was issued under.
        if (account is null ||
            !account.IsActive ||
            !string.Equals(account.SecurityStamp, ClaimsTenantIdentity.SecurityStamp(principal), StringComparison.Ordinal))
        {
            await RejectAsync(context);
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
