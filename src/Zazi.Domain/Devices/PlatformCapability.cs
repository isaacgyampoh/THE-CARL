namespace Zazi.Domain;

/// <summary>
/// A technical capability a platform either has or does not have.
/// </summary>
/// <remarks>
/// This is <b>not</b> permission. <see cref="SmsCapture"/> says the operating system can
/// surface incoming messages to an application; it says nothing about whether this user is
/// allowed to record transactions. Permission remains the authorization policies' business.
/// </remarks>
public enum PlatformCapability
{
    /// <summary>The OS can deliver incoming SMS to the application.</summary>
    SmsCapture = 0,

    /// <summary>A person can key a transaction in. Universally available.</summary>
    ManualTransactionCapture = 1,

    OfflineStorage = 2,
    BackgroundSync = 3,
    PushNotifications = 4,
    BiometricAuthentication = 5,
    Camera = 6,
    Location = 7,
    EncryptedLocalStorage = 8,
    BackgroundExecution = 9,
    NetworkSync = 10,
    SessionManagement = 11,
    DashboardAccess = 12,
    OwnerManagement = 13,
    BranchManagement = 14,
    DeviceManagement = 15
}

/// <summary>
/// The single authority for which capabilities a platform genuinely has.
/// </summary>
/// <remarks>
/// <para>
/// Centralised so no controller, service or client ever writes
/// <c>if (platform == "Android")</c>. Clients query the server rather than assuming, which
/// is what lets iOS and Web be first-class without an Android-shaped codebase.
/// </para>
/// <para>
/// <b>iOS and SMS.</b> iOS grants third-party applications no access to arbitrary incoming
/// SMS. There is no entitlement, no permission prompt, and no supported workaround.
/// <see cref="SmsCapture"/> is therefore absent from both iOS entries and must stay absent.
/// An iPhone is not a degraded Android: it captures manually and gets every other capability
/// its hardware supports.
/// </para>
/// <para>
/// <b>Android and SMS.</b> <see cref="PlatformCapability.SmsCapture"/> means the platform
/// <i>can</i> support it. Whether a given handset actually does still depends on the user
/// granting <c>RECEIVE_SMS</c> and on OEM behaviour — a runtime fact the server cannot know.
/// The client must treat this as permission-to-ask, not a guarantee.
/// </para>
/// </remarks>
public static class PlatformCapabilityPolicy
{
    /// <summary>Capabilities every platform has, including unrecognised ones.</summary>
    private static readonly PlatformCapability[] Universal =
    [
        PlatformCapability.ManualTransactionCapture,
        PlatformCapability.NetworkSync,
        PlatformCapability.SessionManagement,
        PlatformCapability.DashboardAccess
    ];

    private static readonly IReadOnlyDictionary<DeviceType, PlatformCapability[]> ByDeviceType =
        new Dictionary<DeviceType, PlatformCapability[]>
        {
            // Android: the only platform that can observe SMS.
            [DeviceType.AndroidPhone] =
            [
                PlatformCapability.SmsCapture,
                PlatformCapability.OfflineStorage,
                PlatformCapability.BackgroundSync,
                PlatformCapability.BackgroundExecution,
                PlatformCapability.EncryptedLocalStorage,
                PlatformCapability.PushNotifications,
                PlatformCapability.BiometricAuthentication,
                PlatformCapability.Camera,
                PlatformCapability.Location,
                PlatformCapability.BranchManagement,
                PlatformCapability.DeviceManagement
            ],

            // Tablets are frequently Wi-Fi-only with no SIM, so SMS is not assumed. A
            // SIM-equipped tablet is enrolled as AndroidPhone if it is used for capture.
            [DeviceType.AndroidTablet] =
            [
                PlatformCapability.OfflineStorage,
                PlatformCapability.BackgroundSync,
                PlatformCapability.BackgroundExecution,
                PlatformCapability.EncryptedLocalStorage,
                PlatformCapability.PushNotifications,
                PlatformCapability.BiometricAuthentication,
                PlatformCapability.Camera,
                PlatformCapability.Location,
                PlatformCapability.OwnerManagement,
                PlatformCapability.BranchManagement,
                PlatformCapability.DeviceManagement
            ],

            // iOS: no SmsCapture — see the remarks above. Everything else is first-class.
            // BackgroundExecution is absent because iOS schedules background work at its own
            // discretion; promising it would make the sync engine's contract a lie.
            [DeviceType.iPhone] =
            [
                PlatformCapability.OfflineStorage,
                PlatformCapability.BackgroundSync,
                PlatformCapability.EncryptedLocalStorage,
                PlatformCapability.PushNotifications,
                PlatformCapability.BiometricAuthentication,
                PlatformCapability.Camera,
                PlatformCapability.Location,
                PlatformCapability.BranchManagement,
                PlatformCapability.DeviceManagement
            ],

            [DeviceType.iPad] =
            [
                PlatformCapability.OfflineStorage,
                PlatformCapability.BackgroundSync,
                PlatformCapability.EncryptedLocalStorage,
                PlatformCapability.PushNotifications,
                PlatformCapability.BiometricAuthentication,
                PlatformCapability.Camera,
                PlatformCapability.Location,
                PlatformCapability.OwnerManagement,
                PlatformCapability.BranchManagement,
                PlatformCapability.DeviceManagement
            ],

            // Web: online-first administration. No offline store and no background work,
            // because a browser tab cannot be relied on to hold either.
            [DeviceType.WebBrowser] =
            [
                PlatformCapability.PushNotifications,
                PlatformCapability.OwnerManagement,
                PlatformCapability.BranchManagement,
                PlatformCapability.DeviceManagement
            ],

            // A gateway forwards provider messages. It has no operator, so it gets no
            // dashboard, session or management capabilities.
            [DeviceType.GsmGateway] =
            [
                PlatformCapability.SmsCapture,
                PlatformCapability.OfflineStorage,
                PlatformCapability.BackgroundSync,
                PlatformCapability.BackgroundExecution,
                PlatformCapability.EncryptedLocalStorage
            ],

            // Unrecognised platform: manual capture and sync only. Narrow on purpose.
            [DeviceType.Other] = []
        };

    /// <summary>
    /// Capabilities for a device type.
    /// </summary>
    /// <param name="deviceType">Platform and form factor.</param>
    /// <param name="isRevoked">
    /// A revoked device gets nothing at all. Revocation is absolute: capabilities describe
    /// what a trusted device may do, and a revoked device is not trusted.
    /// </param>
    public static IReadOnlyList<PlatformCapability> For(DeviceType deviceType, bool isRevoked = false)
    {
        if (isRevoked)
        {
            return [];
        }

        var specific = ByDeviceType.TryGetValue(deviceType, out var found) ? found : [];

        // A gateway has no human operator, so the universal operator capabilities do not
        // apply to it.
        if (deviceType == DeviceType.GsmGateway)
        {
            return [.. specific];
        }

        return [.. Universal.Concat(specific).Distinct().OrderBy(c => (int)c)];
    }

    public static bool Supports(DeviceType deviceType, PlatformCapability capability, bool isRevoked = false) =>
        For(deviceType, isRevoked).Contains(capability);

    /// <summary>
    /// Whether this platform can observe SMS at all.
    /// </summary>
    /// <remarks>
    /// Exposed as a named check because it is the one capability that differs on a product
    /// level rather than a technical one — it decides whether the client offers automatic
    /// capture or explains that manual entry is the available method.
    /// </remarks>
    public static bool CanCaptureSms(DeviceType deviceType, bool isRevoked = false) =>
        Supports(deviceType, PlatformCapability.SmsCapture, isRevoked);
}
