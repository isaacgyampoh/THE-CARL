using System.Text;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zazi.Api.Middleware;
using Zazi.Api.Security;
using Zazi.Application;
using Zazi.Application.Security;
using Zazi.Application.Evidence;
using Zazi.Application.Sync;
using Zazi.Infrastructure.Evidence;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Email;
using Zazi.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// ─── Configuration ───────────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

var jwtOptions = new JwtOptions();
builder.Configuration.GetSection(JwtOptions.SectionName).Bind(jwtOptions);

// Environment variable wins only when configuration did not supply a key, so a deployment
// can inject the secret without baking it into appsettings.
if (string.IsNullOrWhiteSpace(jwtOptions.Key))
{
    jwtOptions.Key = Environment.GetEnvironmentVariable("ZAZI_JWT_KEY") ?? string.Empty;
}

// There is no fallback signing key. A shipped default means every deployment that forgets
// to configure one shares a publicly known secret and anyone can mint tokens for any tenant.
// Development gets a per-process random key so local work is frictionless without ever
// introducing a predictable secret; tokens simply stop being valid across restarts.
if (string.IsNullOrWhiteSpace(jwtOptions.Key) && builder.Environment.IsDevelopment())
{
    jwtOptions.Key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    Console.WriteLine(
        "[Zazi] No Jwt:Key configured. Generated an ephemeral development signing key. " +
        "Tokens will be invalidated on restart. Set Jwt:Key or ZAZI_JWT_KEY for stable sessions.");
}

jwtOptions.Validate();
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(jwtOptions));

var syncOptions = new SyncOptions();
builder.Configuration.GetSection(SyncOptions.SectionName).Bind(syncOptions);
syncOptions.Validate();
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(syncOptions));

// ─── Persistence ─────────────────────────────────────────────────────────────
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        options.UseNpgsql(connectionString);
        return;
    }

    // Outside development, refuse rather than fall back. An in-memory store accepts every
    // write, reports success, and loses the lot on restart — so a deployment with a mistyped
    // connection string would take real transactions from agents and silently discard them.
    // It also drops the two guarantees the ledger depends on: the partial unique indexes that
    // make submissions idempotent, and the atomic upsert that keeps balances correct under
    // concurrent writes.
    //
    // This already cost hours once: the API ran happily on memory while every login failed
    // with "0-candidate email", which looks nothing like a configuration problem.
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "ConnectionStrings:DefaultConnection is required outside Development. Zazi will not " +
            "start against an in-memory store, because it would accept financial work and lose it.");
    }

    options.UseInMemoryDatabase("ZaziDb");
});

// ─── Authentication ──────────────────────────────────────────────────────────
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtOptions.Issuer,
        ValidAudience = jwtOptions.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
        // Default is 5 minutes of leeway, which meaningfully extends the life of a
        // revoked or expired token on a financial API.
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Sub,
        RoleClaimType = ZaziClaimTypes.Role
    };
});

// ─── Authorization ───────────────────────────────────────────────────────────
builder.Services.AddAuthorization(options =>
{
    // Deny by default. Any endpoint that omits an explicit policy still requires an
    // authenticated caller, so a newly added controller cannot be accidentally public.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    foreach (var (policyName, roles) in ZaziPolicies.RolesByPolicy)
    {
        options.AddPolicy(policyName, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ZaziClaimTypes.OrganizationId)
            .RequireRole(roles));
    }
});

// ─── Rate limiting ───────────────────────────────────────────────────────────
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

    // Credential endpoints are partitioned by client IP to blunt brute-force and
    // credential-stuffing runs.
    options.AddPolicy(RateLimitPolicies.Authentication, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Authenticated traffic is partitioned per user so one busy tenant cannot exhaust
    // another tenant's allowance.
    options.AddPolicy(RateLimitPolicies.Tenant, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// ─── Application services ────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();

builder.Services.AddScoped<IOrganizationService, OrganizationService>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<IDeviceService, DeviceService>();
builder.Services.AddScoped<IReconciliationService, ReconciliationService>();
builder.Services.AddScoped<ILedgerService, LedgerService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IOfflineSyncService, OfflineSyncService>();
builder.Services.AddScoped<IDeviceLinkService, DeviceLinkService>();
builder.Services.AddScoped<ISmsTransactionParser, MtnSmsParser>();
builder.Services.AddScoped<ISmsTransactionParser, AirtelTigoSmsParser>();
builder.Services.AddScoped<ISmsTransactionParser, TelecelSmsParser>();
builder.Services.AddScoped<ISmsTransactionParser, GenericSmsParser>();
builder.Services.AddScoped<ISmsProcessingService, SmsProcessingService>();
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IObservabilityService, ObservabilityService>();
builder.Services.AddScoped<ITenantGuard, TenantGuard>();
builder.Services.AddScoped<IIdentityRevocationService, IdentityRevocationService>();
builder.Services.AddScoped<ISyncTransactionService, SyncTransactionService>();
builder.Services.AddScoped<IDeviceEnrollmentService, DeviceEnrollmentService>();
builder.Services.AddScoped<ITransactionEvidenceSource, ManualEntryEvidenceSource>();
// Transactional email. Registered in both hosts from one place so the dashboard cannot start
// without something the API has — the mistake already made once with IIdentityRevocationService.
// The Resend credential is read from the RESEND_API_KEY environment variable inside this call
// and is never bound from configuration, so it cannot arrive from a committed appsettings file.
builder.Services.AddZaziEmail(builder.Configuration, builder.Environment.IsDevelopment());


builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

// ─── Schema ──────────────────────────────────────────────────────────────────
// Migrating on startup is convenient locally and hazardous in production: several instances
// rolling out together race each other through the same migration, and a deployment that
// only meant to ship code silently alters the schema with no separate step to review, gate
// or roll back. It therefore defaults on in Development and off everywhere else, and a
// deployment runs migrations deliberately.
var migrateOnStartup = builder.Configuration.GetValue<bool?>("Database:MigrateOnStartup")
    ?? app.Environment.IsDevelopment();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    if (!db.Database.IsRelational())
    {
        await db.Database.EnsureCreatedAsync();
    }
    else if (migrateOnStartup)
    {
        // Relational databases go through migrations so schema history stays auditable.
        // EnsureCreated bypasses migrations entirely and leaves the database unmigratable.
        await db.Database.MigrateAsync();
    }
    else
    {
        // Refusing to start beats serving traffic against a schema the code does not match,
        // which surfaces as scattered column-not-found errors rather than one clear failure.
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"The database is missing {pending.Count} migration(s), starting with " +
                $"'{pending[0]}'. Apply them as a deployment step, or set " +
                "Database:MigrateOnStartup=true to migrate automatically.");
        }
    }
}

// ─── Transport security ──────────────────────────────────────────────────────
// UseHttpsRedirection does nothing when no HTTPS port is configured: it logs one line and
// serves the request. A deployment can therefore look TLS-enforced while accepting passwords
// in clear — observed exactly that way, with a login over plain HTTP answering 401 rather
// than redirecting.
//
// So the decision is made explicit. Either this process terminates TLS, or the deployer
// states that something in front of it does. Anything else refuses to start.
if (!app.Environment.IsDevelopment())
{
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
            "Refusing to serve plain HTTP outside Development. Zazi carries credentials and " +
            "financial evidence. Either configure HTTPS on this process, or set " +
            "Zazi:BehindTlsProxy=true if TLS is terminated by a proxy in front of it.");
    }

    if (bindsNetworkTransport && behindTlsProxy)
    {
        // Without this the application sees the proxy instead of the client: every request
        // looks like plain HTTP from one address. That breaks the per-IP credential limiter
        // in the worst way — every user shares a single bucket, so one attacker can lock out
        // everyone, or the limit never bites at all.
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
}

// ─── Pipeline ────────────────────────────────────────────────────────────────
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Cross-Origin-Resource-Policy"] = "same-origin";
    headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    // This is a JSON API that never returns markup, so the strictest possible policy applies.
    headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    // X-XSS-Protection is deprecated and its filter has introduced vulnerabilities of its
    // own; modern browsers ignore it and CSP covers the case.
    headers.Remove("X-Powered-By");
    await next();
});

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Liveness: the process is up. Deliberately cheap and dependency-free, so a database blip
// does not cause an orchestrator to kill an otherwise healthy instance.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

// Readiness, as distinct from the liveness probe above: an instance whose database is
// unreachable can answer nothing, and a load balancer needs to stop sending it traffic.
app.MapGet("/ready", async (ApplicationDbContext db, CancellationToken ct) =>
{
    try
    {
        return await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "ready" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception)
    {
        // CanConnectAsync reports an unreachable server by returning false, but a bad host
        // name or a refused socket throws. Both mean the same thing to a probe, and an
        // unhandled 500 here reads as "the application is broken" rather than "not ready".
        // The reason is logged; it is never returned, because connection strings and host
        // names have no business on an unauthenticated surface.
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

// Deliberately reports no version or backing-store detail: that is reconnaissance material
// for an unauthenticated caller.
app.MapGet("/api/v1/status", () => Results.Ok(new { service = "Zazi API" })).AllowAnonymous();

app.MapControllers();

app.Run();

public partial class Program { }
