using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zazi.Application.Float;
using Zazi.Application.Growth;
using Zazi.Application.Keypad;
using Zazi.Application.Security;
using Zazi.Domain;
using Zazi.Infrastructure.Services;

namespace Zazi.Infrastructure.Growth;

/// <summary>
/// Agents asking for float, and owners answering — from the app, a keypad phone, the portal,
/// or the owner's own phone by SMS.
/// </summary>
public sealed class FloatRequestService : IFloatRequestService
{
    private readonly ApplicationDbContext _db;
    private readonly IFloatService _float;
    private readonly ISmsSender? _sms;
    private readonly ILogger<FloatRequestService>? _logger;
    private readonly TimeProvider _clock;

    public FloatRequestService(
        ApplicationDbContext db,
        IFloatService floatService,
        IEnumerable<ISmsSender>? sms = null,
        ILogger<FloatRequestService>? logger = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _float = floatService;
        _sms = sms?.LastOrDefault();
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<FloatRequestDto> RequestAsync(Guid organizationId, Guid branchId, Guid agentId, string network,
        decimal amount, string channel, CancellationToken cancellationToken = default)
    {
        var normalised = Networks.Normalise(network);
        if (normalised == Networks.Unspecified)
        {
            throw new FloatRequestRejectedException("Say which network: MTN, Telecel or AirtelTigo.");
        }

        if (amount < FloatRequestRules.Minimum || amount > FloatRequestRules.Maximum || decimal.Round(amount, 2) != amount)
        {
            throw new FloatRequestRejectedException("Check the amount.");
        }

        var waiting = await _db.FloatRequests.CountAsync(r => r.OrganizationId == organizationId
            && r.AgentId == agentId && r.Status == FloatRequestStatus.Pending, cancellationToken);
        if (waiting >= FloatRequestRules.MaximumPendingPerAgent)
        {
            throw new FloatRequestRejectedException("You already have requests waiting. Wait for your owner to answer them.");
        }

        var taken = await _db.FloatRequests.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.Status == FloatRequestStatus.Pending)
            .Select(r => r.Code)
            .ToListAsync(cancellationToken);
        string code;
        do
        {
            code = RandomNumberGenerator.GetInt32(1000, 10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        while (taken.Contains(code));

        // Filed under the agent's own branch where they have one.
        var agentBranch = await _db.Users.AsNoTracking()
            .Where(u => u.Id == agentId && u.OrganizationId == organizationId)
            .Select(u => u.BranchId)
            .FirstOrDefaultAsync(cancellationToken);

        var request = new FloatRequest
        {
            OrganizationId = organizationId,
            BranchId = agentBranch ?? branchId,
            AgentId = agentId,
            Network = normalised,
            Amount = amount,
            Code = code,
            Channel = channel,
            RequestedAtUtc = _clock.GetUtcNow()
        };
        _db.FloatRequests.Add(request);
        await _db.SaveChangesAsync(cancellationToken);

        var dto = await ToDtoAsync(request, cancellationToken);
        await TextOwnerAsync(organizationId,
            $"Zazi: {SmsText.Plain(dto.AgentName, 30)} asks for GHS {amount:N2} {NetworkName(normalised)} float. " +
            $"Reply OK {code} to give it, or NO {code}.",
            cancellationToken);
        return dto;
    }

    public async Task<FloatRequestDto> DecideAsync(Guid organizationId, Guid requestId, bool approve, Guid deciderUserId,
        string via, CancellationToken cancellationToken = default)
    {
        var request = await _db.FloatRequests
            .SingleOrDefaultAsync(r => r.Id == requestId && r.OrganizationId == organizationId, cancellationToken)
            ?? throw new FloatRequestRejectedException("That request was not found.");
        if (request.Status != FloatRequestStatus.Pending)
        {
            throw new FloatRequestRejectedException("That request has already been answered.");
        }

        if (approve)
        {
            // Through the float service, so the money given moves the agent's balance through the
            // same ledger entry as float given on the Cash & float page.
            await _float.RecordFloatAsync(
                new RecordFloatRequest(request.AgentId, request.Network, 0m, request.Amount, $"Float request {request.Code}"),
                organizationId,
                deciderUserId,
                cancellationToken);
        }

        request.Status = approve ? FloatRequestStatus.Approved : FloatRequestStatus.Declined;
        request.DecidedAtUtc = _clock.GetUtcNow();
        request.DecidedByUserId = deciderUserId;
        request.DecidedVia = via;
        request.UpdatedAt = request.DecidedAtUtc.Value;

        _db.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = organizationId,
            UserId = deciderUserId,
            Action = approve ? "FLOAT_REQUEST_APPROVED" : "FLOAT_REQUEST_DECLINED",
            Details = $"Float request {request.Code}: GHS {request.Amount:0.00} {request.Network}, answered by {via}.",
            ActorType = "User"
        });
        await _db.SaveChangesAsync(cancellationToken);

        await TextAgentAsync(organizationId, request.AgentId, approve
            ? $"Zazi: your {NetworkName(request.Network)} float request for GHS {request.Amount:N2} was approved and recorded."
            : $"Zazi: your {NetworkName(request.Network)} float request for GHS {request.Amount:N2} was declined. Call your owner.",
            cancellationToken);

        return await ToDtoAsync(request, cancellationToken);
    }

    public async Task<FloatRequestDto?> DecideByCodeAsync(Guid organizationId, string code, bool approve,
        CancellationToken cancellationToken = default)
    {
        var request = await _db.FloatRequests.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.Status == FloatRequestStatus.Pending && r.Code == code.Trim())
            .FirstOrDefaultAsync(cancellationToken);
        if (request is null)
        {
            return null;
        }

        // The owner answered from the business's own number; the decision is recorded as theirs.
        var owner = await _db.Users.AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.IsActive && u.Roles.Any(r => r.Name == ZaziRoles.Owner))
            .OrderBy(u => u.CreatedAt)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (owner is not { } ownerId)
        {
            return null;
        }

        return await DecideAsync(organizationId, request.Id, approve, ownerId, "SMS", cancellationToken);
    }

    public async Task<IReadOnlyList<FloatRequestDto>> PendingAsync(Guid organizationId, Guid? branchId,
        CancellationToken cancellationToken = default) =>
        await ListAsync(_db.FloatRequests.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.Status == FloatRequestStatus.Pending)
            .Where(r => branchId == null || r.BranchId == branchId)
            .OrderBy(r => r.RequestedAtUtc), cancellationToken);

    public async Task<IReadOnlyList<FloatRequestDto>> MineAsync(Guid organizationId, Guid agentId, int take = 5,
        CancellationToken cancellationToken = default) =>
        await ListAsync(_db.FloatRequests.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.AgentId == agentId)
            .OrderByDescending(r => r.RequestedAtUtc)
            .Take(Math.Clamp(take, 1, 50)), cancellationToken);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<FloatRequestDto>> ListAsync(IQueryable<FloatRequest> query, CancellationToken cancellationToken)
    {
        var requests = await query.ToListAsync(cancellationToken);
        var ids = requests.Select(r => r.AgentId).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
        return requests.Select(r => ToDto(r, names.GetValueOrDefault(r.AgentId) ?? "Unknown")).ToList();
    }

    private async Task<FloatRequestDto> ToDtoAsync(FloatRequest r, CancellationToken cancellationToken)
    {
        var name = await _db.Users.AsNoTracking().Where(u => u.Id == r.AgentId).Select(u => u.FullName)
            .FirstOrDefaultAsync(cancellationToken);
        return ToDto(r, name ?? "Unknown");
    }

    private static FloatRequestDto ToDto(FloatRequest r, string agentName) => new(
        r.Id, r.AgentId, agentName, r.Network, r.Amount, r.Code, r.Channel, r.Status, r.RequestedAtUtc, r.DecidedAtUtc);

    private async Task TextOwnerAsync(Guid organizationId, string message, CancellationToken cancellationToken)
    {
        var raw = await _db.Organizations.AsNoTracking().Where(o => o.Id == organizationId)
            .Select(o => o.PhoneNumber).FirstOrDefaultAsync(cancellationToken);
        await SendAsync(GhanaPhoneNumber.Normalise(raw), message, cancellationToken);
    }

    /// <summary>Only an agent on a keypad phone is texted; the app shows the answer itself.</summary>
    private async Task TextAgentAsync(Guid organizationId, Guid agentId, string message, CancellationToken cancellationToken) =>
        await SendAsync(await KeypadPhones.NumberForAsync(_db, organizationId, agentId, cancellationToken), message, cancellationToken);

    private async Task SendAsync(string? to, string message, CancellationToken cancellationToken)
    {
        if (_sms is null || to is null)
        {
            return;
        }

        try
        {
            await _sms.SendAsync(to, message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "A float request SMS could not be sent.");
        }
    }

    private static string NetworkName(string network) => network switch
    {
        Networks.Mtn => "MTN",
        Networks.Telecel => "Telecel",
        Networks.AirtelTigo => "AirtelTigo",
        _ => network
    };
}

/// <summary>Finding the keypad phone an agent uses.</summary>
public static class KeypadPhones
{
    /// <summary>The number of the agent's active keypad phone, or null if they have none.</summary>
    public static async Task<string?> NumberForAsync(ApplicationDbContext db, Guid organizationId, Guid agentId,
        CancellationToken cancellationToken)
    {
        var prefix = DeviceEnrollmentService.KeypadIdentifierPrefix;
        var identifier = await db.DeviceEnrollmentCodes.AsNoTracking()
            .Where(c => c.IntendedUserId == agentId && c.RedeemedByDeviceId != null)
            .Join(db.Devices.AsNoTracking(), c => c.RedeemedByDeviceId, d => (Guid?)d.Id, (c, d) => new { c.RedeemedAtUtc, d })
            .Where(x => x.d.OrganizationId == organizationId && x.d.DeviceIdentifier.StartsWith(prefix)
                && !x.d.IsRevoked && x.d.Status == DeviceStatus.Active)
            .OrderByDescending(x => x.RedeemedAtUtc)
            .Select(x => x.d.DeviceIdentifier)
            .FirstOrDefaultAsync(cancellationToken);
        return identifier?[prefix.Length..];
    }
}
