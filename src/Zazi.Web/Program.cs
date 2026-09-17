using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Services;
using Zazi.Web.Components;
using Zazi.Web.Endpoints;
using Zazi.Web.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// ─── Data ────────────────────────────────────────────────────────────────────
// The same database and the same DbContext the API uses. The web application is another
// caller of the existing domain, not a second system with its own store.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    // Refused rather than silently falling back to an in-memory store. A dashboard quietly
    // reporting an empty branch is worse than one that will not start.
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is required. Zazi.Web reads the same PostgreSQL " +
        "database as the API and will not start against an in-memory substitute.");
}

builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));

// ─── Data protection ─────────────────────────────────────────────────────────
// Data Protection encrypts the authentication cookie and the antiforgery tokens. Without a
// persisted key ring ASP.NET generates one under the process user's home directory, which is
// exactly the thing a container does not keep: every deploy or restart mints new keys, every
// agent's cookie becomes unreadable and they are all signed out mid-shift. Two instances
// behind a load balancer never agree at all, so sign-in appears to work and then randomly
// does not.
//
// It is required rather than defaulted because the failing case looks like it is working.
// The application name is pinned so the keys stay readable across deployments.
var keyRingPath = builder.Configuration["Zazi:DataProtectionKeyPath"];

if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(Directory.CreateDirectory(keyRingPath))
        .SetApplicationName("zazi-web");
}
else if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Zazi:DataProtectionKeyPath is required outside Development. Point it at a directory " +
        "that survives restarts and is shared by every instance, or sessions will be dropped " +
        "whenever this process restarts.");
}

// ─── Authentication ──────────────────────────────────────────────────────────
// A cookie carries the browser session; the credentials behind it are verified by the
// existing IAuthService against the existing user store. No second set of passwords, no
// second hashing scheme, and the same roles.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/sign-in";
        options.LogoutPath = "/auth/sign-out";
        options.AccessDeniedPath = "/denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "zazi.web";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

        // Revocation must reach an open browser session, not wait for the cookie to expire.
        options.Events.OnValidatePrincipal = RevokedSessionValidator.ValidateAsync;
    });

// Mirrors the API's credential policy. Account lockout already blunts a targeted guess; this
// blunts a spray across many accounts from one source, which lockout alone does not.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(WebRateLimitPolicies.Authentication, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddAuthorization(options =>
{
    // Deny by default, exactly as the API does: a page must opt in to being anonymous.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // The same role-to-policy map the API enforces, from the same source. Restating the
    // roles here would let the dashboard drift into showing a page the API would refuse to
    // serve the data for.
    foreach (var (policyName, roles) in ZaziPolicies.RolesByPolicy)
    {
        options.AddPolicy(policyName, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ZaziClaimTypes.OrganizationId)
            .RequireRole(roles));
    }
});

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

// ─── Application services ────────────────────────────────────────────────────
// Bound and validated at startup rather than discovered on the first sign-in. IAuthService
// issues tokens as part of verifying a password, so a missing key does not surface as a
// configuration error — it surfaces as every login failing for no visible reason.
var jwtOptions = new JwtOptions();
builder.Configuration.GetSection(JwtOptions.SectionName).Bind(jwtOptions);

if (string.IsNullOrWhiteSpace(jwtOptions.Key))
{
    jwtOptions.Key = Environment.GetEnvironmentVariable("ZAZI_JWT_KEY") ?? string.Empty;
}

if (string.IsNullOrWhiteSpace(jwtOptions.Key) && builder.Environment.IsDevelopment())
{
    jwtOptions.Key = Convert.ToBase64String(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
}

jwtOptions.Validate();
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(jwtOptions));
builder.Services.AddScoped<ICurrentUserContext, WebCurrentUserContext>();
builder.Services.AddScoped<ITenantGuard, TenantGuard>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IObservabilityService, ObservabilityService>();
builder.Services.AddScoped<IReconciliationService, ReconciliationService>();
builder.Services.AddScoped<IOrganizationService, OrganizationService>();
builder.Services.AddScoped<IDeviceService, DeviceService>();
builder.Services.AddScoped<ILedgerService, LedgerService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    // The same explicit decision the API makes. UseHttpsRedirection silently does nothing
    // without a configured HTTPS port, and the session cookie is marked Secure here — so a
    // plain-HTTP deployment would not leak the cookie, it would simply never keep anyone
    // signed in, which is a confusing way to discover a transport mistake.
    var behindTlsProxy = builder.Configuration.GetValue<bool>("Zazi:BehindTlsProxy");

    var servesHttps = (builder.Configuration["ASPNETCORE_URLS"] ?? string.Empty)
        .Contains("https://", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_HTTPS_PORT"])
        || !string.IsNullOrWhiteSpace(builder.Configuration["HTTPS_PORT"]);

    // Only a real network listener can expose cleartext. An in-memory test host has no
    // socket and nothing to protect, so the rule there would assert something that cannot
    // be true rather than catch a misconfiguration.
    //
    // This deliberately identifies the *exception* and treats everything else as a real
    // listener, so an unrecognised server still gets the check. Testing the other way round
    // — asking whether the server is Kestrel — fails open: the registered implementation is
    // the internal KestrelServerImpl, not the public KestrelServer, so the type test is
    // never true and the guard silently stops protecting anything.
    var serverType = app.Services.GetService<IServer>()?.GetType().FullName ?? string.Empty;
    var bindsNetworkTransport = !serverType.StartsWith("Microsoft.AspNetCore.TestHost.", StringComparison.Ordinal);

    if (bindsNetworkTransport && !servesHttps && !behindTlsProxy)
    {
        throw new InvalidOperationException(
            "Refusing to serve plain HTTP outside Development. Either configure HTTPS on this " +
            "process, or set Zazi:BehindTlsProxy=true if TLS is terminated by a proxy in front " +
            "of it.");
    }

    if (bindsNetworkTransport && behindTlsProxy)
    {
        // So the cookie's Secure policy and the sign-in rate limiter see the client's scheme
        // and address rather than the proxy's.
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });
    }

    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();

    // A password posted over plain HTTP is a password disclosed. Development is exempt so
    // the app remains reachable on a loopback address without a certificate.
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Same split, and the same paths, as the API: /health is liveness, /ready proves this
// instance can actually serve.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.MapGet("/ready", async (ApplicationDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        return await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "ready" })
            : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception)
    {
        return Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

app.MapAuthEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
