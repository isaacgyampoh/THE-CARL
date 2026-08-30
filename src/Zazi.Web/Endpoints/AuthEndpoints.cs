using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
            CancellationToken cancellationToken) =>
        {
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
            catch (Exception)
            {
                // Deliberately undifferentiated: telling a caller whether the address exists
                // turns the sign-in form into an account enumeration oracle.
                return Results.Redirect("/sign-in?error=invalid");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, result.User.Id.ToString()),
                new(ClaimTypes.Name, result.User.FullName),
                new(ClaimTypes.Email, result.User.Email),
                new(ZaziClaimTypes.OrganizationId, result.User.OrganizationId.ToString())
            };

            if (result.User.BranchId is { } branchId)
            {
                claims.Add(new Claim(ZaziClaimTypes.BranchId, branchId.ToString()));
            }

            foreach (var role in result.User.Roles)
            {
                claims.Add(new Claim(ZaziClaimTypes.Role, role));
            }

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));

            return Results.Redirect("/");
        }).AllowAnonymous().DisableAntiforgery();

        routes.MapPost("/auth/sign-out", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/sign-in");
        }).DisableAntiforgery();
    }
}
