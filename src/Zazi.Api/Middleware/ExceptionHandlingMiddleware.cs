using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Zazi.Application.Security;
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
