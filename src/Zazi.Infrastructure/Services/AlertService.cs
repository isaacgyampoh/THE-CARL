using Microsoft.EntityFrameworkCore;
using Zazi.Application;
using Zazi.Domain;

namespace Zazi.Infrastructure.Services;

public class AlertService : IAlertService
{
    private readonly ApplicationDbContext _dbContext;

    public AlertService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<AlertDto>> GetAlertsAsync(Guid organizationId, Guid? branchId = null, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Alerts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId);

        if (branchId.HasValue)
        {
            query = query.Where(x => x.BranchId == branchId.Value);
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new AlertDto(
                x.Id,
                x.OrganizationId,
                x.BranchId,
                x.Type,
                x.Message,
                x.Severity,
                x.Network,
                x.Threshold,
                x.IsAcknowledged,
                x.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<AlertThresholdRequest> SetThresholdAsync(AlertThresholdRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Network))
        {
            throw new ArgumentException("Network is required.", nameof(request));
        }

        if (request.WarningThreshold < 0 || request.CriticalThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Thresholds must be non-negative.");
        }

        if (request.CriticalThreshold > request.WarningThreshold)
        {
            throw new ArgumentException("Critical threshold must be lower than or equal to the warning threshold.", nameof(request));
        }

        var existing = await _dbContext.AlertThresholds
            .SingleOrDefaultAsync(x => x.OrganizationId == request.OrganizationId
                && x.BranchId == request.BranchId
                && x.Network == request.Network,
                cancellationToken);

        if (existing is null)
        {
            existing = new AlertThreshold
            {
                OrganizationId = request.OrganizationId,
                BranchId = request.BranchId,
                Network = request.Network,
                WarningThreshold = request.WarningThreshold,
                CriticalThreshold = request.CriticalThreshold,
                IsEnabled = request.IsEnabled
            };
            _dbContext.AlertThresholds.Add(existing);
        }
        else
        {
            existing.WarningThreshold = request.WarningThreshold;
            existing.CriticalThreshold = request.CriticalThreshold;
            existing.IsEnabled = request.IsEnabled;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return request;
    }

    public async Task<int> EvaluateFloatAlertsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var balances = await _dbContext.FloatBalances
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);

        var thresholds = await _dbContext.AlertThresholds
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsEnabled)
            .ToListAsync(cancellationToken);

        var alertsCreated = 0;

        foreach (var balance in balances)
        {
            var threshold = thresholds
                .Where(x => x.BranchId == null || x.BranchId == balance.BranchId)
                .Where(x => string.Equals(x.Network, balance.Network, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CriticalThreshold)
                .FirstOrDefault();

            if (threshold is null)
            {
                continue;
            }

            if (balance.CurrentFloat <= threshold.CriticalThreshold)
            {
                await CreateAlertAsync(organizationId, balance.BranchId, balance.Network, threshold.CriticalThreshold, AlertTypes.LowFloat, "Critical", cancellationToken);
                alertsCreated++;
            }
            else if (balance.CurrentFloat <= threshold.WarningThreshold)
            {
                await CreateAlertAsync(organizationId, balance.BranchId, balance.Network, threshold.WarningThreshold, AlertTypes.LowFloat, "Warning", cancellationToken);
                alertsCreated++;
            }
        }

        return alertsCreated;
    }

    private async Task CreateAlertAsync(Guid organizationId, Guid branchId, string network, decimal threshold, string type, string severity, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.Alerts
            .AnyAsync(x => x.OrganizationId == organizationId
                && x.BranchId == branchId
                && x.Network == network
                && x.Type == type
                && x.Severity == severity
                && x.Threshold == threshold
                && !x.IsAcknowledged,
                cancellationToken);

        if (exists)
        {
            return;
        }

        _dbContext.Alerts.Add(new AlertRecord
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            Type = type,
            Message = $"{network} float is below the configured threshold ({threshold:0.00}).",
            Severity = severity,
            Network = network,
            Threshold = threshold,
            IsAcknowledged = false
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}

public class DashboardService : IDashboardService
{
    private readonly ApplicationDbContext _dbContext;

    public DashboardService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DashboardSummaryDto> GetOrganizationDashboardAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        // "Today" is the business day in Africa/Accra, not a UTC calendar day. Ghana sits at
        // UTC+0 with no daylight saving, so the two coincide today — but stating the zone
        // keeps the aggregate correct if the platform ever serves another market.
        var (dayStartUtc, dayEndUtc) = GhanaBusinessDay.CurrentUtcRange();

        var branches = await _dbContext.Branches
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .CountAsync(cancellationToken);

        // Only ledger-affecting states are counted. Rejected evidence and transactions
        // awaiting review must never appear in an operating summary as though they posted.
        // A half-open range on the raw column, never a .Date projection: Npgsql cannot
        // translate DateTimeOffset.Date against a timestamptz column (it throws at runtime),
        // and a function over the column would defeat the index even if it could.
        var todayAccepted = _dbContext.Transactions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                && x.TransactionAtUtc >= dayStartUtc
                && x.TransactionAtUtc < dayEndUtc)
            .Where(x => x.State == TransactionLifecycleState.Accepted
                || x.State == TransactionLifecycleState.Synced
                || x.State == TransactionLifecycleState.Reversed
                || x.State == TransactionLifecycleState.Adjusted);

        var todayTransactions = await todayAccepted.CountAsync(cancellationToken);

        var todayVolume = await todayAccepted
            .SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;

        var cashPosition = await _dbContext.CashBalances
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .SumAsync(x => (decimal?)x.CurrentCash, cancellationToken) ?? 0m;

        var totalNetworkFloat = await _dbContext.FloatBalances
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .SumAsync(x => (decimal?)x.CurrentFloat, cancellationToken) ?? 0m;

        var lowFloatAlerts = await _dbContext.Alerts
            .AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.Type == AlertTypes.LowFloat && !x.IsAcknowledged, cancellationToken);

        var discrepancies = await _dbContext.Reconciliations
            .AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.Status != "Balanced", cancellationToken);

        var activeSessions = await _dbContext.Sessions
            .AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && !x.IsClosed, cancellationToken);

        var activeDevices = await _dbContext.Devices
            .AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.Status == DeviceStatus.Active, cancellationToken);

        var balanceVariance = await _dbContext.Reconciliations
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .SumAsync(x => (decimal?)x.Difference, cancellationToken) ?? 0m;

        return new DashboardSummaryDto(
            branches,
            todayTransactions,
            todayVolume,
            cashPosition,
            totalNetworkFloat,
            lowFloatAlerts,
            discrepancies,
            activeSessions,
            activeDevices,
            balanceVariance);
    }
}
