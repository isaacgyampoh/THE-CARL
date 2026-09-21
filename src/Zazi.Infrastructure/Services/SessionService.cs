using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

public class SessionService : ISessionService
{
    private readonly ApplicationDbContext _dbContext;

    public SessionService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SessionDto> OpenSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        // Rounded once, here, and every row below is written from these. Letting each table
        // round on insert is how a session's opening cash and its own cash balance came to
        // disagree: the balance column was numeric(18,4) and the session's was not.
        var openingCash = LedgerPolicy.RoundToCurrency(request.OpeningCash);
        var openingFloat = LedgerPolicy.RoundToCurrency(request.OpeningFloat);

        var session = new Session
        {
            OrganizationId = request.OrganizationId,
            BranchId = request.BranchId,
            UserId = request.UserId,
            DeviceId = request.DeviceId,
            OpeningCash = openingCash,
            OpeningFloat = openingFloat,
            IsClosed = false,
            OpenedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Sessions.Add(session);
        _dbContext.CashBalances.Add(new CashBalance
        {
            OrganizationId = request.OrganizationId,
            BranchId = request.BranchId,
            AgentId = request.UserId,
            OpeningCash = openingCash,
            CurrentCash = openingCash
        });
        _dbContext.FloatBalances.Add(new FloatBalance
        {
            OrganizationId = request.OrganizationId,
            BranchId = request.BranchId,
            AgentId = request.UserId,
            // The caller may now say which network the opening float is on, which a Telecel or
            // AirtelTigo agent needs — previously it was hardcoded to MTN and their opening
            // balance sat on a row their own transactions never touched.
            //
            // It still falls back to MTN rather than to UNKNOWN when unspecified, and that is
            // deliberate: a float balance on a network no transaction will ever post to is an
            // orphaned figure, which is worse than a wrong label because the opening amount
            // simply vanishes from the agent's books. Until every caller supplies the network,
            // the wrong-label failure is the recoverable one. See docs for the remaining gap.
            Network = string.IsNullOrWhiteSpace(request.Network) ? "MTN" : request.Network.ToUpperInvariant(),
            OpeningFloat = openingFloat,
            CurrentFloat = openingFloat,
            Threshold = 0m
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new SessionDto(
            session.Id,
            session.OrganizationId,
            session.BranchId,
            session.UserId,
            session.DeviceId,
            session.OpenedAt,
            session.ClosedAt,
            session.IsClosed,
            session.OpeningCash,
            session.OpeningFloat);
    }

    public async Task<SessionDto?> CloseSessionAsync(CloseSessionRequest request, CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.Sessions
            .SingleOrDefaultAsync(x => x.Id == request.SessionId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        if (session.UserId != request.UserId)
        {
            throw new InvalidOperationException("Only the session owner may close the session.");
        }

        session.IsClosed = true;
        session.ClosedAt = DateTimeOffset.UtcNow;
        session.UpdatedAt = DateTimeOffset.UtcNow;

        _dbContext.AuditLogs.Add(new AuditLogEntry
        {
            OrganizationId = session.OrganizationId,
            UserId = session.UserId,
            DeviceId = session.DeviceId,
            Action = "SessionClosed",
            Details = $"Session closed for branch {session.BranchId} with opening cash {session.OpeningCash} and opening float {session.OpeningFloat}.",
            ActorType = "User"
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new SessionDto(
            session.Id,
            session.OrganizationId,
            session.BranchId,
            session.UserId,
            session.DeviceId,
            session.OpenedAt,
            session.ClosedAt,
            session.IsClosed,
            session.OpeningCash,
            session.OpeningFloat);
    }

    public async Task<SessionDto?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.Sessions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        return new SessionDto(
            session.Id,
            session.OrganizationId,
            session.BranchId,
            session.UserId,
            session.DeviceId,
            session.OpenedAt,
            session.ClosedAt,
            session.IsClosed,
            session.OpeningCash,
            session.OpeningFloat);
    }
}
