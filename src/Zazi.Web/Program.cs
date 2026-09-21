using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Infrastructure.Float;
using Zazi.Application.Float;
using Zazi.Infrastructure.Onboarding;
using Zazi.Application.Onboarding;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Email;
using Zazi.Infrastructure.Security;
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

    // A bare 429 tells a client it was refused but not when to come back, so every client
    // has to guess. The Android sync engine already reads Retry-After and prefers it to its
    // own backoff curve — the header was simply never sent, which left that path dead and
    // the handsets guessing. The limiter knows exactly when the window reopens, so it says so.
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            // Seconds, not an HTTP date: both are legal, and the client honours the seconds
            // form only. Rounded up, because rounding down invites a retry that is refused
            // again a fraction of a second early.
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.HttpContext.Response.Headers.RetryAfter =
                seconds.ToString(CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };

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
// DeviceService revokes sessions as well as marking the device, so it needs this. The portal
// failed to start without it — registered in the API but not here, which no test caught
// because every test builds the API's container.
builder.Services.AddScoped<IIdentityRevocationService, IdentityRevocationService>();
// The Team page issues and revokes activation codes.
builder.Services.AddScoped<IDeviceEnrollmentService, DeviceEnrollmentService>();
builder.Services.AddScoped<ILedgerService, LedgerService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
// Recording what an owner hands an agent, and reading back what they hold.
builder.Services.AddScoped<IFloatService, FloatService>();
// Transactional email. Registered in both hosts from one place so the dashboard cannot start
// without something the API has — the mistake already made once with IIdentityRevocationService.
// The Resend credential is read from the RESEND_API_KEY environment variable inside this call
// and is never bound from configuration, so it cannot arrive from a committed appsettings file.
builder.Services.AddZaziEmail(builder.Configuration, builder.Environment.IsDevelopment());

// Where a failed request is re-executed, outside Development. Declared here rather than as an
// argument to UseExceptionHandler so there is one copy of the path and it is readable from the
// container.
//
// The page it names MUST be anonymous. The portal denies by default, so an error page behind
// the fallback policy turns every error an anonymous visitor hits into a redirect to /sign-in
// — which errors the same way, forever. PortalErrorPageTests holds the two ends together.
builder.Services.Configure<ExceptionHandlerOptions>(options =>
{
    options.ExceptionHandlingPath = "/error";
    options.CreateScopeForErrors = true;
});

// ─── Self-service onboarding ─────────────────────────────────────────────────
// Validated at startup like everything else that can be half-configured: signup switched on
// without a public base URL sends verification links that go nowhere, and the person who finds
// out is a customer who cannot open the account they just created.
// How the portal is reached from outside. Every link Zazi emails is built from this and
// never from the request's Host header — those links activate accounts and change passwords,
// so a URL the caller chooses is a URL pointing wherever they like.
var portalOptions = new PortalOptions();
builder.Configuration.GetSection(PortalOptions.SectionName).Bind(portalOptions);
portalOptions.Validate();
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(portalOptions));

var signUpOptions = new SignUpOptions();
builder.Configuration.GetSection(SignUpOptions.SectionName).Bind(signUpOptions);
signUpOptions.Validate(portalOptions);
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(signUpOptions));
builder.Services.AddScoped<ISignUpService, SignUpService>();

// Password reset. Unlike signup there is no switch: an account holder who has forgotten their
// password needs a way back in whether or not the deployment accepts new signups. It refuses
// at the page when Portal:PublicBaseUrl is absent, rather than stopping the portal starting,
// so adding this setting does not take an existing deployment down.
var passwordResetOptions = new PasswordResetOptions();
builder.Configuration.GetSection(PasswordResetOptions.SectionName).Bind(passwordResetOptions);
passwordResetOptions.Validate();
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(passwordResetOptions));
builder.Services.AddScoped<IPasswordResetService, PasswordResetService>();


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
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };

        // Without these two lines the options above do nothing.
        //
        // ASP.NET only honours X-Forwarded-* from a proxy it already trusts, and the default
        // trust list is loopback alone. Behind Caddy on the same host that was satisfied, so
        // this was never noticed. On a managed platform the proxy is a different machine, the
        // headers are silently discarded, and the symptoms are quiet: no HSTS header is sent
        // because the request looks like plain HTTP, and the per-IP rate limiter buckets every
        // visitor under the proxy's address — so one person hammering sign-in locks out
        // unrelated agents.
        //
        // Clearing the lists trusts whatever forwards to us, which is safe here and only here:
        // the process is never directly reachable. Kestrel binds loopback behind Caddy, and on
        // a managed platform the container is only addressable through the platform's own
        // edge. If Zazi is ever exposed directly, this must be narrowed to known proxy
        // addresses first — a caller that can reach Kestrel can otherwise choose its own
        // apparent IP.
        // Which header carries the real client address.
        //
        // X-Forwarded-For is a chain, and ASP.NET reads its rightmost entry — the nearest
        // proxy. Behind a CDN that entry is an internal address which changes between
        // requests, so a per-IP rate limiter ends up with a fresh bucket every time and never
        // limits anything. Cloudflare publishes the true client in a single-valued header of
        // its own, which has no chain to misread.
        //
        // Configured rather than hard-coded: naming Cloudflare in the source would make the
        // application wrong the day it sits behind something else. Unset means the standard
        // header, which is correct when the only proxy is one we run ourselves.
        var clientIpHeader = builder.Configuration["Zazi:ClientIpHeader"];
        if (!string.IsNullOrWhiteSpace(clientIpHeader))
        {
            forwarded.ForwardedForHeaderName = clientIpHeader;
        }

        forwarded.KnownNetworks.Clear();
        forwarded.KnownProxies.Clear();

        app.UseForwardedHeaders(forwarded);
    }

    // Parameterless, so the path comes from the options configured above rather than being
    // written a second time here. The string overload builds its own options object and leaves
    // IOptions<ExceptionHandlerOptions> empty, which means nothing else — including a test —
    // can ask the application where it sends failed requests.
    app.UseExceptionHandler();
    app.UseHsts();

    // A password posted over plain HTTP is a password disclosed. Development is exempt so
    // the app remains reachable on a loopback address without a certificate.
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// After authentication, not before it. An antiforgery token is bound to the user it was
// issued to, so the middleware that validates one needs an identity to validate it against;
// running it first means it decides on every request that nobody is signed in. The framework
// does not complain, and the sign-in POST works either way because it is genuinely anonymous
// — which is what makes the wrong order easy to keep.
app.UseAntiforgery();

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

// Checked against the endpoints that were actually built, after everything is mapped.
//
// These two paths carry the whole dashboard. If either stops being anonymous, every visitor
// is redirected to a page that redirects them again — and the logs look like a healthy portal
// serving signed-out users the entire time. A startup failure naming the page is worth far
// more than discovering it from a customer.
app.AssertAnonymouslyReachable(
    "/sign-in",
    // Both halves of onboarding. A verification link that lands on a page requiring sign-in
    // cannot be followed by the one person it was sent to — who, by definition, cannot sign in
    // until they have followed it.
    "/sign-up",
    "/verify-email",
    // Both halves of password reset. Somebody using these cannot sign in by definition, so a
    // page here that demanded authentication would be unreachable by exactly the people it is
    // for — and the redirect would look like ordinary sign-in traffic in the logs.
    "/forgot-password",
    "/reset-password",
    app.Services.GetRequiredService<IOptions<ExceptionHandlerOptions>>()
        .Value.ExceptionHandlingPath.Value ?? "/error");

app.Run();

// Test-visible entry point, so the portal's own dependency graph can be built in a test.
// It was not, and the portal stopped starting when a service it newly depended on was
// registered in the API but not here — a failure that only appeared when someone ran it.
public partial class Program { }
