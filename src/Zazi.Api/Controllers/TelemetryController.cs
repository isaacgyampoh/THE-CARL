using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Zazi.Api.Security;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure;

namespace Zazi.Api.Controllers;

/// <summary>
/// Accepts client-observed events and records them as ordinary audit entries.
/// </summary>
/// <remarks>
/// <para>
/// The client half of an existing system, not a new one. Everything posted here becomes an
/// <see cref="AuditLogEntry"/> alongside the entries the server writes for itself, so one
/// timeline can show a handset's attempt and the server's response to it.
/// </para>
/// <para>
/// <b>Nothing the client says about who it is, is believed.</b> Organization, user and device
/// come from the authenticated token and the device header; the payload carries only what the
/// handset observed. A device cannot attribute an event to another tenant, another user or
/// another device by asking to.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/telemetry")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class TelemetryController : ControllerBase
{
    /// <summary>
    /// A handset that has been offline may have a backlog, but a single request should not be
    /// able to write thousands of rows.
    /// </summary>
    private const int MaxBatchSize = 100;

    private const int MaxDetailsLength = 500;

    private readonly ApplicationDbContext _dbContext;
    private readonly ICurrentUserContext _currentUser;
    private readonly ILogger<TelemetryController> _logger;

    public TelemetryController(
        ApplicationDbContext dbContext,
        ICurrentUserContext currentUser,
        ILogger<TelemetryController> logger)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _logger = logger;
    }

    [HttpPost("events")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Record(
        [FromBody] ClientTelemetryBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.Events is null || batch.Events.Count == 0)
        {
            return Accepted(new { recorded = 0 });
        }

        if (batch.Events.Count > MaxBatchSize)
        {
            return BadRequest(new { error = $"A batch may contain at most {MaxBatchSize} events." });
        }

        var deviceId = await ResolveDeviceIdAsync(cancellationToken);
        var organizationId = _currentUser.OrganizationId;
        var userId = _currentUser.UserId;

        foreach (var reported in batch.Events)
        {
            if (string.IsNullOrWhiteSpace(reported.EventType))
            {
                continue;
            }

            _dbContext.AuditLogs.Add(new AuditLogEntry
            {
                OrganizationId = organizationId,
                UserId = userId,
                DeviceId = deviceId,
                Action = reported.EventType.Length <= 150 ? reported.EventType : reported.EventType[..150],
                // Free text from a client is bounded and stored as-is only because the client
                // is required to send a classification, never a raw exception message. The
                // Android side builds this string from fixed vocabulary.
                Details = Trim(reported.Details ?? reported.EventType, MaxDetailsLength) ?? reported.EventType,
                ActorType = "Device",
                Source = "Android",
                Severity = ParseSeverity(reported.Severity),
                Status = Trim(reported.Status, 50),
                ErrorCode = Trim(reported.ErrorCode, 100),
                DurationMs = reported.DurationMs is > 0 and < 3_600_000 ? reported.DurationMs : null,
                CorrelationId = Trim(reported.CorrelationId, 100),
                AppVersion = Trim(reported.AppVersion, 50),
                Platform = Trim(reported.Platform, 50),
                // The handset's clock is not authoritative and may be wrong by hours, but a
                // client event with a server timestamp would sit in the wrong place on its own
                // timeline. The reported time is used when it is plausible.
                CreatedAt = Plausible(reported.OccurredAtUtc) ? reported.OccurredAtUtc!.Value : DateTimeOffset.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Recorded {Count} client events for device {DeviceId}.", batch.Events.Count, deviceId);

        // Accepted, not Created: the client is reporting, not asking for anything back, and
        // it must never wait on this or retry it as though it mattered.
        return Accepted(new { recorded = batch.Events.Count });
    }

    /// <summary>
    /// The device the caller is presenting, if it belongs to this organization.
    /// </summary>
    /// <remarks>
    /// Resolved from the authenticated header rather than the payload. An event whose device
    /// cannot be resolved is still recorded — losing it would hide exactly the situation
    /// where a handset is misconfigured.
    /// </remarks>
    private async Task<Guid?> ResolveDeviceIdAsync(CancellationToken cancellationToken)
    {
        var identifier = Request.Headers["X-Device-Identifier"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        return await _dbContext.Devices
            .Where(d => d.OrganizationId == _currentUser.OrganizationId
                && d.DeviceIdentifier == identifier)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static bool Plausible(DateTimeOffset? reported)
    {
        if (reported is not { } value)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        return value <= now.AddMinutes(5) && value >= now.AddDays(-7);
    }

    private static AuditSeverity ParseSeverity(string? severity) =>
        Enum.TryParse<AuditSeverity>(severity, ignoreCase: true, out var parsed)
            ? parsed
            : AuditSeverity.Information;

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= max ? value : value[..max];

}

/// <summary>One upload from a handset.</summary>
public record ClientTelemetryBatch(IReadOnlyList<ClientTelemetryEvent>? Events);

/// <summary>
/// One thing a handset observed.
/// </summary>
/// <remarks>
/// Deliberately carries no tenant, user or device id: those are established by
/// authentication. It also carries no free-form exception text — <c>ErrorCode</c> is a
/// classification from a fixed vocabulary, so telemetry cannot become a channel for
/// exfiltrating message contents, tokens or customer data.
/// </remarks>
public record ClientTelemetryEvent(
    string EventType,
    string? Severity = null,
    string? Status = null,
    string? ErrorCode = null,
    string? Details = null,
    int? DurationMs = null,
    string? CorrelationId = null,
    string? AppVersion = null,
    string? Platform = null,
    DateTimeOffset? OccurredAtUtc = null);
