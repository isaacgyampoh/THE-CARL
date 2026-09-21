namespace Zazi.Domain;

/// <summary>
/// The mobile money networks a Ghanaian agent works, and the one spelling of each.
/// </summary>
/// <remarks>
/// <para>
/// Balances are matched by network name, so spelling is not cosmetic: "Telecel", "telecel" and
/// "TELECEL " reaching the database as three strings would split one agent's float into three
/// wallets holding a fraction each. Every value that will be compared goes through
/// <see cref="Normalise"/>.
/// </para>
/// <para>
/// There is deliberately no "default network" here. MTN was used as one in several places and
/// was wrong in each: it put a Telecel agent's opening float on an MTN balance, and it showed
/// an owner three MTN handsets for the ordinary setup of one phone per network. Where a
/// network genuinely is not known, <see cref="Unspecified"/> says so.
/// </para>
/// </remarks>
public static class Networks
{
    public const string Mtn = "MTN";
    public const string Telecel = "TELECEL";
    public const string AirtelTigo = "AIRTELTIGO";

    /// <summary>
    /// Stands in where a network is genuinely unknown and nothing depends on knowing it.
    /// </summary>
    /// <remarks>
    /// Safe on a device record, which is descriptive. Never safe on a float balance: no
    /// transaction posts to this network, so money filed under it leaves the agent's books
    /// entirely, which is why an opening float with no network is refused rather than given
    /// this value.
    /// </remarks>
    public const string Unspecified = "UNSPECIFIED";

    /// <summary>All three networks, in no significant order.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Mtn, Telecel, AirtelTigo };

    /// <summary>
    /// Trims and upper-cases a network name, returning <see cref="Unspecified"/> for nothing.
    /// </summary>
    /// <remarks>
    /// Names that are not one of the three are passed through rather than rejected. A network
    /// this build has never heard of is a labelling problem; refusing the record that carries
    /// it would lose the transaction, which is a money problem.
    /// </remarks>
    public static string Normalise(string? network) =>
        string.IsNullOrWhiteSpace(network) ? Unspecified : network.Trim().ToUpperInvariant();

    /// <summary>Whether this is one of the three networks Zazi knows how to parse.</summary>
    public static bool IsKnown(string? network) =>
        All.Contains(Normalise(network), StringComparer.Ordinal);
}
