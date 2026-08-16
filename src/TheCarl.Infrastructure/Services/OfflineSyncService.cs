using Microsoft.EntityFrameworkCore;
using TheCarl.Application;
using TheCarl.Domain;

namespace TheCarl.Infrastructure.Services;

public class OfflineSyncService : IOfflineSyncService
{
    private readonly ApplicationDbContext _dbContext;

    public OfflineSyncService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SyncQueueItemDto> QueueEventAsync(QueueSyncEventRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.EntityType))
        {
            throw new ArgumentException("Entity type is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.EventType))
        {
            throw new ArgumentException("Event type is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Payload))
        {
            throw new ArgumentException("Payload is required.", nameof(request));
        }

        var item = new SyncQueueEntry
        {
            OrganizationId = request.OrganizationId,
            BranchId = request.BranchId,
            DeviceId = request.DeviceId,
            EntityId = request.EntityId,
            EntityType = request.EntityType,
            EventType = request.EventType,
            Payload = request.Payload,
            Status = SyncStatus.Pending,
            RetryCount = 0,
            NextAttemptAtUtc = DateTimeOffset.UtcNow,
            IsManual = request.IsManual
        };

        _dbContext.SyncQueue.Add(item);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Map(item);
    }

    public async Task<IReadOnlyList<SyncQueueItemDto>> GetPendingAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.SyncQueue
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status != SyncStatus.Synced && x.Status != SyncStatus.DeadLetter)
            .OrderBy(x => x.NextAttemptAtUtc ?? x.CreatedAt)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToList();
    }

    public async Task<int> ProcessPendingAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var pending = await _dbContext.SyncQueue
            .Where(x => x.OrganizationId == organizationId && x.Status == SyncStatus.Pending)
            .OrderBy(x => x.NextAttemptAtUtc ?? x.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var item in pending)
        {
            item.Status = SyncStatus.InFlight;
            item.NextAttemptAtUtc = DateTimeOffset.UtcNow.AddMinutes(1);
            _dbContext.SyncAttempts.Add(new SyncAttempt
            {
                OrganizationId = item.OrganizationId,
                BranchId = item.BranchId,
                DeviceId = item.DeviceId,
                TransactionId = item.EntityId,
                EntityType = item.EntityType,
                AttemptNumber = item.RetryCount + 1,
                Status = TransactionSyncStatus.InFlight,
                AttemptedAtUtc = DateTimeOffset.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var item in pending)
        {
            item.Status = SyncStatus.Synced;
            item.NextAttemptAtUtc = DateTimeOffset.UtcNow;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            item.RetryCount = Math.Max(item.RetryCount, 1);
            _dbContext.SyncAttempts.Add(new SyncAttempt
            {
                OrganizationId = item.OrganizationId,
                BranchId = item.BranchId,
                DeviceId = item.DeviceId,
                TransactionId = item.EntityId,
                EntityType = item.EntityType,
                AttemptNumber = item.RetryCount,
                Status = TransactionSyncStatus.Synced,
                AttemptedAtUtc = DateTimeOffset.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return pending.Count;
    }

    public async Task<SyncQueueItemDto> MarkDeadLetterAsync(Guid organizationId, Guid itemId, string reason, CancellationToken cancellationToken = default)
    {
        var item = await _dbContext.SyncQueue
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == itemId, cancellationToken);

        if (item is null)
        {
            throw new InvalidOperationException("Sync queue item was not found.");
        }

        item.Status = SyncStatus.DeadLetter;
        item.ErrorMessage = reason;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.NextAttemptAtUtc = null;

        _dbContext.DeadLetterTransactions.Add(new DeadLetterTransaction
        {
            OrganizationId = organizationId,
            BranchId = item.BranchId,
            DeviceId = item.DeviceId,
            TransactionId = item.EntityId,
            EntityType = item.EntityType,
            Payload = item.Payload,
            Reason = reason,
            RetryCount = item.RetryCount
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Map(item);
    }

    private static SyncQueueItemDto Map(SyncQueueEntry item)
    {
        return new SyncQueueItemDto(
            item.Id,
            item.OrganizationId,
            item.BranchId,
            item.DeviceId,
            item.EntityId,
            item.EntityType,
            item.EventType,
            item.Payload,
            item.Status,
            item.RetryCount,
            item.NextAttemptAtUtc,
            item.ErrorMessage,
            item.IsManual,
            item.CreatedAt);
    }
}
