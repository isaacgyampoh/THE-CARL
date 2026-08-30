using Zazi.Domain;

namespace Zazi.UnitTests;

/// <summary>Fingerprint determinism and client-id collision resistance.</summary>
public class EvidenceFingerprintTests
{
    private static readonly Guid Org = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Occurred = new(2026, 8, 15, 9, 30, 0, TimeSpan.Zero);

    private static string Compute(
        Guid? org = null,
        string provider = "MTN",
        TransactionType type = TransactionType.CashIn,
        decimal amount = 500m,
        string? reference = "ABC123",
        string? phone = "0241234567",
        DateTimeOffset? occurred = null) =>
        EvidenceFingerprint.Compute(
            org ?? Org, provider, type, amount, reference, phone, occurred ?? Occurred);

    [Fact]
    public void SameEventProducesSameFingerprint()
    {
        Assert.Equal(Compute(), Compute());
    }

    [Fact]
    public void FingerprintIsScopedToTheOrganization()
    {
        // Two tenants may legitimately receive byte-identical provider messages.
        Assert.NotEqual(Compute(), Compute(org: Guid.NewGuid()));
    }

    [Theory]
    [InlineData("0241234567")]
    [InlineData("+233241234567")]
    [InlineData("233241234567")]
    [InlineData("024 123 4567")]
    public void MsisdnFormattingDoesNotChangeTheFingerprint(string phone)
    {
        Assert.Equal(Compute(), Compute(phone: phone));
    }

    [Theory]
    [InlineData("ABC123")]
    [InlineData("abc123")]
    [InlineData("ABC-123")]
    [InlineData("ABC 123")]
    public void ReferenceFormattingDoesNotChangeTheFingerprint(string reference)
    {
        Assert.Equal(Compute(), Compute(reference: reference));
    }

    [Theory]
    [InlineData("500")]
    [InlineData("500.0")]
    [InlineData("500.00")]
    [InlineData("500.0000")]
    public void AmountScaleDoesNotChangeTheFingerprint(string literal)
    {
        // Passed as strings because C# collapses 500.0 and 500.00 to the same literal,
        // whereas decimal.Parse preserves the trailing-zero scale that is under test.
        var amount = decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(Compute(), Compute(amount: amount));
    }

    [Fact]
    public void SecondLevelClockDriftDoesNotChangeTheFingerprint()
    {
        // Providers report seconds inconsistently between the message body and delivery
        // metadata; a one-second difference must not defeat duplicate detection.
        Assert.Equal(Compute(), Compute(occurred: Occurred.AddSeconds(45)));
    }

    [Fact]
    public void DifferentMinuteProducesADifferentFingerprint()
    {
        Assert.NotEqual(Compute(), Compute(occurred: Occurred.AddMinutes(1)));
    }

    [Fact]
    public void DifferentAmountProducesADifferentFingerprint()
    {
        Assert.NotEqual(Compute(), Compute(amount: 500.01m));
    }

    [Fact]
    public void DifferentTypeProducesADifferentFingerprint()
    {
        // A cash-in and a cash-out of the same value are opposite events.
        Assert.NotEqual(Compute(), Compute(type: TransactionType.CashOut));
    }

    [Fact]
    public void RawHashIgnoresCosmeticWhitespace()
    {
        var a = EvidenceFingerprint.ComputeRawHash("MTN MOMO: Deposit of GHS 75.50");
        var b = EvidenceFingerprint.ComputeRawHash("  MTN  MOMO:\r\n Deposit of  GHS 75.50  ");

        Assert.Equal(a, b);
    }

    [Fact]
    public void FingerprintIsHexSha256()
    {
        var fingerprint = Compute();

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }
}

public class ClientTransactionIdTests
{
    private const string DeviceA = "device-installation-a";
    private const string DeviceB = "device-installation-b";

    [Fact]
    public void GeneratedIdsAreWellFormed()
    {
        var id = ClientTransactionId.Create(DeviceA);

        Assert.True(ClientTransactionId.IsWellFormed(id));
        Assert.Equal(ClientTransactionId.TotalLength, id.Length);
        Assert.StartsWith("CTX-", id);
    }

    [Fact]
    public void TenThousandIdsFromOneDeviceAreUnique()
    {
        var ids = Enumerable.Range(0, 10_000)
            .Select(_ => ClientTransactionId.Create(DeviceA))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(10_000, ids.Count);
    }

    [Fact]
    public void IdsGeneratedInTheSameMillisecondAreStillUnique()
    {
        // A timestamp alone would collide here. The 80 random bits are what prevent it.
        var instant = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 1_000)
            .Select(_ => ClientTransactionId.Create(DeviceA, instant))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1_000, ids.Count);
    }

    [Fact]
    public void DifferentDevicesInTheSameMillisecondDoNotCollide()
    {
        var instant = DateTimeOffset.UtcNow;

        var a = ClientTransactionId.Create(DeviceA, instant);
        var b = ClientTransactionId.Create(DeviceB, instant);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeviceTagIsStableAcrossRestarts()
    {
        // Derived from the installation id, not from process state, so a reboot mid-outbox
        // does not change how a device tags its transactions.
        Assert.Equal(ClientTransactionId.DeviceTag(DeviceA), ClientTransactionId.DeviceTag(DeviceA));
        Assert.NotEqual(ClientTransactionId.DeviceTag(DeviceA), ClientTransactionId.DeviceTag(DeviceB));
    }

    [Fact]
    public void DeviceTagDoesNotDiscloseTheInstallationId()
    {
        var tag = ClientTransactionId.DeviceTag(DeviceA);

        Assert.DoesNotContain(DeviceA, tag, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ClientTransactionId.DeviceTagLength, tag.Length);
    }

    [Fact]
    public void IdsAreLexicographicallyOrderedByCreationTime()
    {
        var earlier = ClientTransactionId.Create(DeviceA, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var later = ClientTransactionId.Create(DeviceA, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        // Same device tag, so the ULID component decides the ordering.
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void ClockGoingBackwardsDoesNotProduceDuplicates()
    {
        var now = DateTimeOffset.UtcNow;
        var a = ClientTransactionId.Create(DeviceA, now);
        var b = ClientTransactionId.Create(DeviceA, now.AddHours(-3));

        Assert.NotEqual(a, b);
        Assert.True(ClientTransactionId.IsWellFormed(b));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-id")]
    [InlineData("CTX-XYZ-01HQ8Z7K3M4N5P6Q7R8S9T0V1W")]
    [InlineData("CTX-9f3a1c07-SHORT")]
    public void MalformedIdsAreRejected(string candidate)
    {
        Assert.False(ClientTransactionId.IsWellFormed(candidate));
    }

    [Fact]
    public void GenerationDoesNotRequireADeviceIdentifier_ToBeAbsent()
    {
        Assert.Throws<ArgumentException>(() => ClientTransactionId.Create("  "));
    }
}
