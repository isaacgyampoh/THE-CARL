namespace Zazi.Domain;

/// <summary>
/// Canonical alert type identifiers.
/// </summary>
/// <remarks>
/// These values are persisted and queried. Using literals at each site let the writer and
/// the reader drift apart — the dashboard counted "LowFloat" while nothing produced that
/// exact string after a rename, so the low-float tile silently read zero.
/// </remarks>
public static class AlertTypes
{
    public const string LowFloat = "LOW_FLOAT";
    public const string HighTransactionVolume = "HIGH_TRANSACTION_VOLUME";
    public const string UnusualTransaction = "UNUSUAL_TRANSACTION";
    public const string ReconciliationDifference = "RECONCILIATION_DIFFERENCE";
    public const string DeviceOffline = "DEVICE_OFFLINE";
    public const string SyncFailure = "SYNC_FAILURE";
    public const string ParserFailure = "PARSER_FAILURE";
    public const string MultipleFailedLogins = "MULTIPLE_FAILED_LOGINS";
    public const string DeviceRevoked = "DEVICE_REVOKED";
    public const string SessionNotClosed = "SESSION_NOT_CLOSED";
}
