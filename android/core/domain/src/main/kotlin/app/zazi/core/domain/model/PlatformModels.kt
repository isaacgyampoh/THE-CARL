package app.zazi.core.domain.model

/**
 * Physical platform and form factor. Mirrors the backend enum ordinals exactly, since those
 * values travel over the wire.
 *
 * <p>Deliberately excludes business role — that is DeviceRole's job on the server. Folding
 * the two together would create two sources of truth that can disagree.</p>
 */
enum class DeviceType(val wireValue: Int) {
    OTHER(0),
    ANDROID_PHONE(1),
    ANDROID_TABLET(2),
    IPHONE(3),
    IPAD(4),
    WEB_BROWSER(5),
    GSM_GATEWAY(6);

    companion object {
        /** Unknown values degrade to [OTHER] so a newer server cannot crash an older client. */
        fun fromWire(value: Int): DeviceType =
            entries.firstOrNull { it.wireValue == value } ?: OTHER
    }
}

/**
 * A technical capability the server says this platform has.
 *
 * <p>Not a permission. [SMS_CAPTURE] means the operating system <i>can</i> surface messages;
 * whether the user granted the runtime permission is a separate question the server cannot
 * answer.</p>
 */
enum class PlatformCapability(val wireValue: Int) {
    SMS_CAPTURE(0),
    MANUAL_TRANSACTION_CAPTURE(1),
    OFFLINE_STORAGE(2),
    BACKGROUND_SYNC(3),
    PUSH_NOTIFICATIONS(4),
    BIOMETRIC_AUTHENTICATION(5),
    CAMERA(6),
    LOCATION(7),
    ENCRYPTED_LOCAL_STORAGE(8),
    BACKGROUND_EXECUTION(9),
    NETWORK_SYNC(10),
    SESSION_MANAGEMENT(11),
    DASHBOARD_ACCESS(12),
    OWNER_MANAGEMENT(13),
    BRANCH_MANAGEMENT(14),
    DEVICE_MANAGEMENT(15);

    companion object {
        /** Unrecognised capabilities are dropped rather than guessed at. */
        fun fromWire(value: Int): PlatformCapability? =
            entries.firstOrNull { it.wireValue == value }
    }
}
