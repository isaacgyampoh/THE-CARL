using Zazi.Application;
using Zazi.Infrastructure.Services;

namespace Zazi.UnitTests;

/// <summary>
/// Which network the server attributes a message to.
/// </summary>
/// <remarks>
/// The mirror of the Android test of the same name. Both sides read the same fixture file and
/// must agree, so a weakness fixed on the handset and left on the server would reappear the
/// moment a message took the server path.
/// </remarks>
public class NetworkAttributionTests
{
    private static readonly ISmsTransactionParser[] Parsers =
    {
        new MtnSmsParser(), new TelecelSmsParser(), new AirtelTigoSmsParser(), new GenericSmsParser()
    };

    /// <summary>The provider whose parser claims a message, as the service would select it.</summary>
    private static string Claimed(string senderIdentity, string body) =>
        Parsers.First(p => p.CanHandle(senderIdentity, body)).ProviderName;

    [Theory]
    [InlineData("MTN", "Cash In of GHS 500.00 from 0241000001. Ref: MP240815.1201", "MTN")]
    [InlineData("TelecelCash", "Telecel Cash: Deposit of GHS 1,250.00 from 0201000001", "TELECEL")]
    [InlineData("AirtelTigo", "AirtelTigo Money: Cash Out GHS 420.00 to 0271000002", "AIRTELTIGO")]
    [InlineData("VodafoneCash", "Telecel Cash: Deposit of GHS 90.00", "TELECEL")]
    public void EachNetworksOwnMessageIsAttributedToIt(string sender, string body, string expected) =>
        Assert.Equal(expected, Claimed(sender, body));

    [Theory]
    // "MoMo" is generic for mobile money in Ghana. Matching it claimed every one of these
    // for MTN, putting other networks' takings — and a bank's — under MTN's figures.
    [InlineData("TelecelCash", "Telecel Cash: Cash Out GHS 300.00. Momo balance GHS 1,200.00", "TELECEL")]
    [InlineData("AirtelTigo", "AirtelTigo Money: Cash In GHS 150.00. Your momo wallet is now GHS 800.00", "AIRTELTIGO")]
    public void OneNetworksMessageIsNotClaimedByAnotherBecauseItSaysMomo(string sender, string body, string expected) =>
        Assert.Equal(expected, Claimed(sender, body));

    [Fact]
    public void ABankMessageMentioningMomoIsNotAttributedToMtn() =>
        Assert.NotEqual("MTN", Claimed("SomeBank", "SomeBank: GHS 100.00 sent to your MoMo wallet"));

    [Theory]
    // "ATL" is three letters that occur inside ordinary words, so it claimed messages that had
    // nothing to do with AirtelTigo.
    [InlineData("AtlanticBank", "AtlanticBank: GHS 60.00 received")]
    [InlineData("TelecelCash", "Telecel Cash: settlement batch ATL-9921 completed")]
    public void AirtelTigoDoesNotClaimAMessageForThreeIncidentalLetters(string sender, string body) =>
        Assert.NotEqual("AIRTELTIGO", Claimed(sender, body));

    [Fact]
    public void AnUnattributableMessageIsReportedUnknownRatherThanGuessed() =>
        Assert.Equal("UNKNOWN", Claimed("RandomCo", "You have received GHS 75.00. Ref: XY123"));

    // ─── Direction: deposit versus cash-out ──────────────────────────────────

    private static string TypeOf(string sender, string body) =>
        Parsers.First(p => p.CanHandle(sender, body)).Parse(body, sender, null, null, null, Guid.NewGuid(), null).TransactionType;

    [Theory]
    [InlineData("MTN", "Cash In of GHS 500.00 from 0241000001", "CASH_IN")]
    [InlineData("MTN", "Cash Out of GHS 250.00 to 0241000002", "CASH_OUT")]
    [InlineData("TelecelCash", "Telecel Cash: Deposit of GHS 1,250.00 from 0201000001", "CASH_IN")]
    [InlineData("TelecelCash", "Telecel Cash: Withdrawal of GHS 300.00 to 0201000003", "CASH_OUT")]
    [InlineData("AirtelTigo", "AirtelTigo Money: Cash In GHS 150.00 from 0271000004", "CASH_IN")]
    [InlineData("AirtelTigo", "AirtelTigo Money: Cash Out GHS 420.00 to 0271000002", "CASH_OUT")]
    public void DirectionIsReadCorrectlyForEachNetwork(string sender, string body, string expected) =>
        Assert.Equal(expected, TypeOf(sender, body));

    [Theory]
    // A balance reminder must not outvote the transaction. Cash-in and cash-out move cash and
    // float in opposite directions, so getting this wrong is a balance wrong by twice the
    // amount — not a wrong label.
    [InlineData("MTN", "Cash Out of GHS 250.00 to 0241000002. Your cash in hand is now GHS 1,750.00", "CASH_OUT")]
    [InlineData("TelecelCash", "Telecel Cash: Withdrawal of GHS 300.00. Deposit balance GHS 900.00", "CASH_OUT")]
    [InlineData("AirtelTigo", "AirtelTigo Money: Cash Out GHS 80.00. Today's deposits GHS 4,200.00", "CASH_OUT")]
    public void ABalanceReminderDoesNotReverseTheDirection(string sender, string body, string expected) =>
        Assert.Equal(expected, TypeOf(sender, body));
}
