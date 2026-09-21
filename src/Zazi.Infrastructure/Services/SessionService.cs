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

        // Normalised through the one authority on spelling: balances are matched by network
        // name, so "Telecel" and "TELECEL" must not become two wallets holding half each.
        var network = string.IsNullOrWhiteSpace(request.Network)
            ? null
            : Networks.Normalise(request.Network);

        // An opening float has to belong to a network, because that is the grain balances are
        // held at: an agent working MTN, Telecel and AirtelTigo has three float balances, and
        // an unattributed opening figure lands in one of them and is wrong in two.
        //
        // This used to default to MTN when unsaid, which put a Telecel agent's opening float on
        // a row their own transactions never touched — so the figure they were given and the
        // figure that moved were different rows, and neither was right. Defaulting to UNKNOWN
        // was tried and was worse: no transaction posts to UNKNOWN, so the opening amount left
        // the books entirely. There is no safe default, which is the argument for refusing.
        if (openingFloat > 0m && network is null)
        {
            throw new ArgumentException(
                "An opening float must say which network it is held on.", nameof(request));
        }

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
        // No network and no opening float is the ordinary case for an agent who starts the day
        // with an empty wallet, and it needs no row at all: the ledger creates a float balance
        // per network on demand, the first time a transaction posts to one. Pre-creating an
        // MTN row here is what made MTN look like every agent's default network.
        if (network is not null)
        {
            _dbContext.FloatBalances.Add(new FloatBalance
            {
                OrganizationId = request.OrganizationId,
                BranchId = request.BranchId,
                AgentId = request.UserId,
                Network = network,
                OpeningFloat = openingFloat,
                CurrentFloat = openingFloat,
                Threshold = 0m
            });
        }

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
