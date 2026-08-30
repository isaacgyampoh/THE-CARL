using Zazi.Api.Security;

namespace Zazi.Api.Middleware;

/// <summary>
/// Assigns a correlation id to every request and echoes it on the response, so a client
/// report, an audit row and a log line can be tied to the same request.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ILogger<CorrelationIdMiddleware> logger)
    {
        var correlationId = ResolveCorrelationId(context);

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HttpCurrentUserContext.CorrelationHeader] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["RequestPath"] = context.Request.Path.Value ?? string.Empty
        }))
        {
            await _next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        var inbound = context.Request.Headers[HttpCurrentUserContext.CorrelationHeader].FirstOrDefault();

        // Client-supplied ids are echoed for traceability but constrained in length and
        // character set: they end up in logs, and unbounded caller-controlled strings in
        // logs are a log-injection vector.
        if (!string.IsNullOrWhiteSpace(inbound)
            && inbound.Length <= 64
            && inbound.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            return inbound;
        }

        return Guid.NewGuid().ToString("N");
    }
}
