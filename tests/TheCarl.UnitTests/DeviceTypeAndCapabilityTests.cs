using TheCarl.Domain;

namespace TheCarl.UnitTests;

/// <summary>
/// <see cref="DeviceType"/> is platform and form factor only. It must never absorb business
/// role or permission, both of which have their own model.
/// </summary>
public class DeviceTypeTests
{
    [Fact]
    public void OrdinalsArePinned()
    {
        // Persisted as integers, so these values are the storage contract. Renumbering
        // reclassifies existing hardware and silently changes its capabilities.
        Assert.Equal(0, (int)DeviceType.Other);
        Assert.Equal(1, (int)DeviceType.AndroidPhone);
        Assert.Equal(2, (int)DeviceType.AndroidTablet);
        Assert.Equal(3, (int)DeviceType.iPhone);
        Assert.Equal(4, (int)DeviceType.iPad);
        Assert.Equal(5, (int)DeviceType.WebBrowser);
        Assert.Equal(6, (int)DeviceType.GsmGateway);
    }

    [Fact]
    public void OtherIsZeroSoADefaultedRowReadsAsUnknown()
    {
        // A row defaulted by a migration must not accidentally claim to be a real platform,
        // because capabilities are granted from this value.
        Assert.Equal(default, DeviceType.Other);
    }

    [Fact]
    public void DeviceTypeDoesNotDuplicateDeviceRole()
    {
        // Guards the architectural rule. A value like "AndroidOwner" would fold business
        // responsibility into platform identity and create a second source of truth for
        // "is this an owner's device" that can disagree with DeviceRole.OwnerDevice.
        var typeNames = Enum.GetNames<DeviceType>();
        var roleNames = Enum.GetNames<DeviceRole>();

        foreach (var roleName in roleNames)
        {
            var roleWord = roleName.Replace("Device", string.Empty);
            Assert.DoesNotContain(typeNames, t => t.Contains(roleWord, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("Android", DeviceType.AndroidPhone)]
    [InlineData("android", DeviceType.AndroidPhone)]
    [InlineData("Android Phone", DeviceType.AndroidPhone)]
    [InlineData("android-phone", DeviceType.AndroidPhone)]
    [InlineData("ANDROID_PHONE", DeviceType.AndroidPhone)]
    [InlineData("Android 14", DeviceType.AndroidPhone)]
    [InlineData("Android Tablet", DeviceType.AndroidTablet)]
    [InlineData("android-tablet", DeviceType.AndroidTablet)]
    [InlineData("iPhone", DeviceType.iPhone)]
    [InlineData("iphone 15 pro", DeviceType.iPhone)]
    [InlineData("iOS", DeviceType.iPhone)]
    [InlineData("iOS 17", DeviceType.iPhone)]
    [InlineData("iPad", DeviceType.iPad)]
    [InlineData("ipad-pro", DeviceType.iPad)]
    [InlineData("WebBrowser", DeviceType.WebBrowser)]
    [InlineData("web", DeviceType.WebBrowser)]
    [InlineData("browser", DeviceType.WebBrowser)]
    [InlineData("GsmGateway", DeviceType.GsmGateway)]
    [InlineData("gsm-gateway", DeviceType.GsmGateway)]
    public void PlatformStringsMapDeterministically(string platform, DeviceType expected)
    {
        Assert.Equal(expected, DeviceTypeMapping.FromPlatformString(platform));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("Symbian")]
    // KaiOS is a real feature-phone OS. It normalises to "kaios", which contains "ios" —
    // a substring match would classify it as an iPhone and grant it iOS capabilities.
    [InlineData("KaiOS")]
    [InlineData("KaiOS 3.0")]
    [InlineData("something we have never seen")]
    public void UnrecognisedPlatformsBecomeOtherRatherThanBeingGuessed(string? platform)
    {
        // Guessing would grant capabilities — including SMS capture — to hardware that may
        // not support them. A wrong capability claim is worse than an absent one.
        Assert.Equal(DeviceType.Other, DeviceTypeMapping.FromPlatformString(platform));
    }

    [Fact]
    public void TabletIsTestedBeforePhone()
    {
        // "androidtablet" also contains "android"; testing the broader term first would
        // classify every tablet as a phone and hand it SmsCapture.
        Assert.Equal(DeviceType.AndroidTablet, DeviceTypeMapping.FromPlatformString("Android Tablet"));
        Assert.NotEqual(DeviceType.AndroidPhone, DeviceTypeMapping.FromPlatformString("Android Tablet"));
    }

    [Fact]
    public void MappingIsDeterministic()
    {
        foreach (var platform in new[] { "Android", "iPhone", "iPad", "web", "gateway", "nonsense" })
        {
            var first = DeviceTypeMapping.FromPlatformString(platform);
            for (var i = 0; i < 20; i++)
            {
                Assert.Equal(first, DeviceTypeMapping.FromPlatformString(platform));
            }
        }
    }
}

/// <summary>
/// Capabilities describe what a platform can technically do — never what a principal is
/// permitted to do.
/// </summary>
public class PlatformCapabilityPolicyTests
{
    [Fact]
    public void AndroidPhoneCanCaptureSms()
    {
        Assert.True(PlatformCapabilityPolicy.CanCaptureSms(DeviceType.AndroidPhone));
        Assert.Contains(PlatformCapability.SmsCapture, PlatformCapabilityPolicy.For(DeviceType.AndroidPhone));
    }

    [Theory]
    [InlineData(DeviceType.iPhone)]
    [InlineData(DeviceType.iPad)]
    [InlineData(DeviceType.WebBrowser)]
    [InlineData(DeviceType.AndroidTablet)]
    [InlineData(DeviceType.Other)]
    public void OnlyAndroidPhonesAndGatewaysMayClaimSmsCapture(DeviceType deviceType)
    {
        // iOS grants third-party apps no access to arbitrary incoming SMS. There is no
        // entitlement and no supported workaround, so claiming the capability would be a lie
        // the client would then act on.
        Assert.False(PlatformCapabilityPolicy.CanCaptureSms(deviceType));
        Assert.DoesNotContain(PlatformCapability.SmsCapture, PlatformCapabilityPolicy.For(deviceType));
    }

    [Theory]
    [InlineData(DeviceType.Other)]
    [InlineData(DeviceType.AndroidPhone)]
    [InlineData(DeviceType.AndroidTablet)]
    [InlineData(DeviceType.iPhone)]
    [InlineData(DeviceType.iPad)]
    [InlineData(DeviceType.WebBrowser)]
    public void EveryOperatorPlatformCanCaptureManually(DeviceType deviceType)
    {
        // The guarantee that no platform is ever unable to record a transaction. This is
        // what makes iOS a first-class client rather than a degraded Android.
        var capabilities = PlatformCapabilityPolicy.For(deviceType);

        Assert.Contains(PlatformCapability.ManualTransactionCapture, capabilities);
        Assert.Contains(PlatformCapability.NetworkSync, capabilities);
        Assert.Contains(PlatformCapability.SessionManagement, capabilities);
        Assert.Contains(PlatformCapability.DashboardAccess, capabilities);
    }

    [Fact]
    public void IosIsFullyCapableApartFromSms()
    {
        var iphone = PlatformCapabilityPolicy.For(DeviceType.iPhone);

        Assert.Contains(PlatformCapability.ManualTransactionCapture, iphone);
        Assert.Contains(PlatformCapability.OfflineStorage, iphone);
        Assert.Contains(PlatformCapability.EncryptedLocalStorage, iphone);
        Assert.Contains(PlatformCapability.BackgroundSync, iphone);
        Assert.Contains(PlatformCapability.BiometricAuthentication, iphone);
        Assert.Contains(PlatformCapability.DashboardAccess, iphone);
        Assert.DoesNotContain(PlatformCapability.SmsCapture, iphone);
    }

    [Fact]
    public void IosDoesNotPromiseGuaranteedBackgroundExecution()
    {
        // iOS schedules background work at its own discretion. Promising it would make the
        // sync engine's contract a lie the client cannot honour.
        Assert.DoesNotContain(PlatformCapability.BackgroundExecution,
            PlatformCapabilityPolicy.For(DeviceType.iPhone));
        Assert.Contains(PlatformCapability.BackgroundExecution,
            PlatformCapabilityPolicy.For(DeviceType.AndroidPhone));
    }

    [Fact]
    public void WebHasNoOfflineOrBackgroundCapabilities()
    {
        var web = PlatformCapabilityPolicy.For(DeviceType.WebBrowser);

        // A browser tab cannot be relied on to hold an outbox or run background work.
        Assert.DoesNotContain(PlatformCapability.OfflineStorage, web);
        Assert.DoesNotContain(PlatformCapability.BackgroundExecution, web);
        Assert.Contains(PlatformCapability.ManualTransactionCapture, web);
        Assert.Contains(PlatformCapability.OwnerManagement, web);
    }

    [Fact]
    public void AGatewayHasNoOperatorCapabilities()
    {
        var gateway = PlatformCapabilityPolicy.For(DeviceType.GsmGateway);

        // A gateway forwards messages; nobody is sitting in front of it.
        Assert.Contains(PlatformCapability.SmsCapture, gateway);
        Assert.DoesNotContain(PlatformCapability.DashboardAccess, gateway);
        Assert.DoesNotContain(PlatformCapability.SessionManagement, gateway);
        Assert.DoesNotContain(PlatformCapability.ManualTransactionCapture, gateway);
    }

    [Fact]
    public void OtherGetsTheNarrowestCapabilitySet()
    {
        var other = PlatformCapabilityPolicy.For(DeviceType.Other);

        Assert.Contains(PlatformCapability.ManualTransactionCapture, other);
        Assert.DoesNotContain(PlatformCapability.SmsCapture, other);
        Assert.DoesNotContain(PlatformCapability.OfflineStorage, other);
    }

    [Theory]
    [InlineData(DeviceType.AndroidPhone)]
    [InlineData(DeviceType.iPhone)]
    [InlineData(DeviceType.WebBrowser)]
    [InlineData(DeviceType.GsmGateway)]
    public void ARevokedDeviceGetsNothingAtAll(DeviceType deviceType)
    {
        // Revocation is absolute. Capabilities describe what a trusted device may do, and a
        // revoked device is not trusted.
        Assert.Empty(PlatformCapabilityPolicy.For(deviceType, isRevoked: true));
        Assert.False(PlatformCapabilityPolicy.CanCaptureSms(deviceType, isRevoked: true));
    }

    [Fact]
    public void EveryDeviceTypeHasAnExplicitCapabilitySet()
    {
        // Guards against adding a platform without deciding what it can do — which would
        // otherwise silently inherit an empty set.
        foreach (var deviceType in Enum.GetValues<DeviceType>())
        {
            var capabilities = PlatformCapabilityPolicy.For(deviceType);

            if (deviceType == DeviceType.GsmGateway)
            {
                Assert.NotEmpty(capabilities);
                continue;
            }

            Assert.Contains(PlatformCapability.ManualTransactionCapture, capabilities);
        }
    }

    [Fact]
    public void CapabilitiesAreDistinctAndStablyOrdered()
    {
        var first = PlatformCapabilityPolicy.For(DeviceType.AndroidPhone);
        var second = PlatformCapabilityPolicy.For(DeviceType.AndroidPhone);

        Assert.Equal(first, second);
        Assert.Equal(first.Distinct().Count(), first.Count);
    }
}
