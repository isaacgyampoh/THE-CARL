using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
    }
    else
    {
        options.UseInMemoryDatabase("ZaziDb");
    }
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
builder.Services.AddScoped<ITenantGuard, TenantGuard>();
builder.Services.AddScoped<IIdentityRevocationService, IdentityRevocationService>();
builder.Services.AddScoped<ISyncTransactionService, SyncTransactionService>();
builder.Services.AddScoped<IDeviceEnrollmentService, DeviceEnrollmentService>();
builder.Services.AddScoped<ITransactionEvidenceSource, ManualEntryEvidenceSource>();

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
