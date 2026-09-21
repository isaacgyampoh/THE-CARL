using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Zazi.Infrastructure;
using Zazi.Web.Security;
using Zazi.Application;
using Zazi.Application.Security;

namespace Zazi.Web.Endpoints;

/// <summary>
/// Sign-in and sign-out for the browser session.
/// </summary>
/// <remarks>
/// <para>
/// These are plain HTTP endpoints rather than interactive components because a cookie can
/// only be written while a real response is being composed — a Blazor circuit has already
/// sent its headers by the time a button is clicked.
/// </para>
/// <para>
/// Credential verification is delegated to <see cref="IAuthService"/>: the same user store,
/// the same PBKDF2 verification, the same lockout counters and the same roles the API uses.
/// The cookie records who was verified; it never becomes a second way to prove identity.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/auth/sign-in", async (
            HttpContext context,
            IAuthService auth,
            IAntiforgery antiforgery,
            ApplicationDbContext database,
            CancellationToken cancellationToken) =>
        {
            // Without this, any site could post a login form to this endpoint and silently
            // sign a manager into an account the attacker controls — after which the manager
            // reviews the attacker's figures believing they are their own branch's.
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Redirect("/sign-in?error=expired");
            }

            var form = await context.Request.ReadFormAsync(cancellationToken);
            var email = form["email"].ToString();
            var password = form["password"].ToString();

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                return Results.Redirect("/sign-in?error=missing");
            }

            AuthTokenResult result;
            try
            {
                // The password never reaches this application's own storage, logs or cookie.
                result = await auth.LoginAsync(new LoginRequest(email, password), cancellationToken);
            }
            catch (SignInRefusedException refused)
            {
                // Only reachable with the correct password, so naming the reason discloses
                // nothing — and not naming it is what had people who had just signed up
                // retyping a right password until the lockout caught them. The address goes
                // back with it so the page can offer to resend the confirmation link.
                var code = refused.Reason switch
                {
                    SignInRefusal.EmailNotVerified => "unverified",
                    SignInRefusal.TemporarilyLocked => "locked",
                    _ => "disabled"
                };

                return Results.Redirect(
                    $"/sign-in?error={code}&email={Uri.EscapeDataString(email.Trim())}");
            }
            catch (Exception)
            {
                // Unknown address and wrong password stay indistinguishable: telling a caller
                // whether the address exists turns the sign-in form into an account
                // enumeration oracle.
                return Results.Redirect(
                    $"/sign-in?error=invalid&email={Uri.EscapeDataString(email.Trim())}");
            }

            // Captured so every later request can check it is still current. Without it a
            // revoked account keeps a working browser session until the cookie expires.
            var securityStamp = await database.Users
                .Where(u => u.Id == result.User.Id)
                .Select(u => u.SecurityStamp)
                .SingleOrDefaultAsync(cancellationToken);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, result.User.Id.ToString()),
                new(ClaimTypes.Name, result.User.FullName),
                new(ZaziClaimTypes.OrganizationId, result.User.OrganizationId.ToString()),
                new(ZaziClaimTypes.SecurityStamp, securityStamp ?? string.Empty)
            };

            // Only present when the account has an address. Portal sign-in is password-based,
            // so in practice it always does; the guard is here so an activation-only identity
            // can never produce a blank email claim on a cookie.
            if (!string.IsNullOrWhiteSpace(result.User.Email))
            {
                claims.Add(new Claim(ClaimTypes.Email, result.User.Email));
            }

            if (result.User.BranchId is { } branchId)
            {
                claims.Add(new Claim(ZaziClaimTypes.BranchId, branchId.ToString()));
            }

            foreach (var role in result.User.Roles)
            {
                claims.Add(new Claim(ZaziClaimTypes.Role, role));
            }

            // The role claim type is stated explicitly. Roles are written as ZaziClaimTypes.Role
            // to match the bearer path's vocabulary, and without naming it here RequireRole
            // would look for ClaimTypes.Role, find nothing, and refuse every policy-protected
            // page to a user who genuinely holds the role.
            var identity = new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme,
                ClaimTypes.Name,
                ZaziClaimTypes.Role);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));

            return Results.Redirect("/");
        }).AllowAnonymous()
          // Antiforgery is validated explicitly above rather than by the filter, because the
          // failure must render a readable message instead of a bare 400.
          .DisableAntiforgery()
          .RequireRateLimiting(WebRateLimitPolicies.Authentication);

        routes.MapPost("/auth/sign-out", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            // Also token-checked: a forced sign-out is a nuisance an attacker should not be
            // able to trigger from another site.
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Redirect("/");
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/sign-in");
        }).DisableAntiforgery();
    }
}
