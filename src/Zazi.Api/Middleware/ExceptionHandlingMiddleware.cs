using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure;
using Zazi.Infrastructure.Services;

namespace Zazi.Api.Middleware;

/// <summary>
/// Translates exceptions into RFC 7807 problem responses. Clients receive a stable code
/// and a safe message; the exception detail is logged server-side against the correlation
/// id and never written to the response body.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            await HandleAsync(context, exception);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        var (status, title, logLevel) = Classify(exception);

        await RecordAsync(context, exception, status, title);

        // The full exception (including message) is logged, not returned. Tenant-denial and
        // authentication failures are logged at Warning so they are alertable.
        _logger.Log(
            logLevel,
            exception,
            "Request {Method} {Path} failed with {StatusCode} (correlation {CorrelationId})",
            context.Request.Method,
            context.Request.Path.Value,
            status,
            context.TraceIdentifier);

        if (context.Response.HasStarted)
        {
            // Nothing safe can be done once the response is on the wire.
            return;
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://zazi.app/problems/{status}",
            Instance = context.Request.Path
        };
        problem.Extensions["correlationId"] = context.TraceIdentifier;

        if (exception is SyncBatchTooLargeException tooLarge)
        {
            // Actionable detail: the client needs the limit to re-chunk its outbox. This is
            // configuration, not internal state, so exposing it discloses nothing sensitive.
            problem.Extensions["maxBatchSize"] = tooLarge.Maximum;
            problem.Extensions["submitted"] = tooLarge.Submitted;
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, SerializerOptions));
    }

    /// <summary>
    /// Records the failure where an operator can find it, not only in the log stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stored detail is the classified title and status, never the exception message.
    /// An exception can carry a connection string, a row of customer data or a fragment of a
    /// request body, and this table is readable from the dashboard — so it gets the same safe
    /// summary the client receives, plus the correlation id that leads to the full server-side
    /// log for anyone with access to it.
    /// </para>
    /// <para>
    /// Only recorded for authenticated callers, because an audit row belongs to an
    /// organization and an anonymous request has none. Those failures remain in the log.
    /// </para>
    /// <para>
    /// Every failure here is swallowed. Telemetry that can fail a request would turn a
    /// logging outage into a financial one.
    /// </para>
    /// </remarks>
    private async Task RecordAsync(HttpContext context, Exception exception, int status, string title)
    {
        try
        {
            var currentUser = context.RequestServices.GetService<ICurrentUserContext>();
            if (currentUser is null || !currentUser.IsAuthenticated)
            {
                return;
            }

            var database = context.RequestServices.GetService<ApplicationDbContext>();
            if (database is null)
            {
                return;
            }

            database.AuditLogs.Add(new AuditLogEntry
            {
                OrganizationId = currentUser.OrganizationId,
                UserId = currentUser.UserId,
                Action = $"{context.Request.Method} {context.Request.Path}",
                Details = title,
                ActorType = "Api",
                Severity = status >= StatusCodes.Status500InternalServerError
                    ? AuditSeverity.Error
                    : AuditSeverity.Warning,
                Source = "Api",
                Status = "Failed",
                ErrorCode = ErrorCodeFor(exception),
                CorrelationId = context.TraceIdentifier
            });

            await database.SaveChangesAsync(context.RequestAborted);
        }
        catch (Exception recordingFailure)
        {
            _logger.LogWarning(
                recordingFailure,
                "Could not record a failure for correlation {CorrelationId}. The request outcome is unaffected.",
                context.TraceIdentifier);
        }
    }

    /// <summary>
    /// A stable, non-sensitive classification used to group recurring failures.
    /// </summary>
    /// <remarks>
    /// Derived from the exception type rather than its message, so rewording an error does not
    /// split one long-running problem into many apparently new ones.
    /// </remarks>
    private static string ErrorCodeFor(Exception exception) => exception switch
    {
        NotAuthenticatedException => "auth.not_authenticated",
        UnauthorizedAccessException => "auth.failed",
        TenantAccessDeniedException => "auth.tenant_denied",
        ConflictException => "request.conflict",
        SyncBatchTooLargeException => "sync.batch_too_large",
        KeyNotFoundException => "request.not_found",
        ArgumentException => "request.invalid",
        InvalidOperationException => "request.conflict",
        OperationCanceledException => "request.cancelled",
        _ => "internal.unexpected"
    };

    private static (int Status, string Title, LogLevel Level) Classify(Exception exception) => exception switch
    {
        NotAuthenticatedException => (StatusCodes.Status401Unauthorized, "Authentication is required.", LogLevel.Warning),
        UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Authentication failed.", LogLevel.Warning),
        TenantAccessDeniedException => (StatusCodes.Status403Forbidden, "Access to the requested resource is denied.", LogLevel.Warning),
        ConflictException => (StatusCodes.Status409Conflict, "The request conflicts with the current state.", LogLevel.Information),
        SyncBatchTooLargeException => (StatusCodes.Status413PayloadTooLarge, "The synchronisation batch is too large.", LogLevel.Information),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "The requested resource was not found.", LogLevel.Information),
        ArgumentException => (StatusCodes.Status400BadRequest, "The request is not valid.", LogLevel.Information),
        InvalidOperationException => (StatusCodes.Status409Conflict, "The request conflicts with the current state.", LogLevel.Information),
        OperationCanceledException => (ClientClosedRequest, "The request was cancelled.", LogLevel.Debug),
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", LogLevel.Error)
    };

    /// <summary>Nginx's client-closed-request code; ASP.NET Core does not define it.</summary>
    private const int ClientClosedRequest = 499;
}
