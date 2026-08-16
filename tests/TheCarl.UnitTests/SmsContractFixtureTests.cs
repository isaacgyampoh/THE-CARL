using System.Globalization;
using System.Text.Json;
using TheCarl.Application;
using TheCarl.Domain;
using TheCarl.Infrastructure.Services;

namespace TheCarl.UnitTests;

/// <summary>
/// Drives the .NET parser from <c>contracts/sms-contract-fixtures.json</c>, the same corpus
/// the Android suite reads.
/// </summary>
/// <remarks>
/// The two parsers cannot share code, so this shared corpus is what stops them diverging.
/// They already had: Android required a provider reference before auto-posting and the
/// server did not, so a truncated message was held on one platform and posted on the other.
/// </remarks>
public class SmsContractFixtureTests
{
    private static readonly SmsContractCorpus Corpus = SmsContractCorpus.Load();

    private static readonly IReadOnlyList<ISmsTransactionParser> Parsers =
    [
        new MtnSmsParser(),
        new TelecelSmsParser(),
        new AirtelTigoSmsParser(),
        new GenericSmsParser()
    ];

    public static TheoryData<string> FixtureIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var fixture in Corpus.Fixtures)
            {
                data.Add(fixture.Id);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void ServerParserMatchesTheSharedContract(string fixtureId)
    {
        var fixture = Corpus.FixtureById(fixtureId);
        var parsed = Parse(fixture);
        var expected = fixture.Expect;

        if (expected.TransactionType is { } expectedType)
        {
            Assert.Equal(expectedType, Normalize(parsed.TransactionType));
        }

        if (expected.AmountSpecified)
        {
            var actual = parsed.Amount == 0m ? (decimal?)null : parsed.Amount;
            Assert.Equal(expected.Amount, actual);
        }

        if (expected.ReferenceSpecified)
        {
            var actual = string.IsNullOrWhiteSpace(parsed.ProviderReference) ? null : parsed.ProviderReference;
            Assert.Equal(expected.Reference, actual);
        }

        if (expected.CustomerPhone is { } expectedPhone)
        {
            Assert.Equal(expectedPhone, parsed.CustomerPhoneNumber);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void AutoPostDecisionMatchesTheSharedContract(string fixtureId)
    {
        var fixture = Corpus.FixtureById(fixtureId);
        var parsed = Parse(fixture);

        var observedType = MapType(parsed.TransactionType);

        // Both gates, exactly as SmsProcessingService applies them: evidence quality first,
        // then the type-based rule that keeps reversals and adjustments off the auto path.
        var autoPost = SmsEvidencePolicy.MeetsAutoPostBar(parsed.ConfidenceScore, parsed.Amount, observedType)
            && LedgerPolicy.CanPostAutomatically(observedType);

        Assert.Equal(fixture.Expect.AutoPostAllowed, autoPost);
    }

    [Fact]
    public void TruncatedEvidenceNeverPosts()
    {
        // Named separately from the theory so the regression is visible in a test list and
        // cannot be silently dropped by editing the corpus.
        var fixture = Corpus.FixtureById("truncated-loses-reference");
        var parsed = Parse(fixture);

        Assert.False(fixture.Expect.AutoPostAllowed);
        Assert.True(parsed.ConfidenceScore < SmsEvidencePolicy.MinimumAutoPostConfidence);
        Assert.Null(parsed.ProviderReference);
        Assert.False(parsed.IsValid);
    }

    [Fact]
    public void ThousandsSeparatorsAreNotTruncated()
    {
        // Regression: "GHS 1,250,000.75" previously parsed as 1.25.
        var parsed = Parse(Corpus.FixtureById("thousands-separator"));

        Assert.Equal(1_250_000.75m, parsed.Amount);
    }

    [Fact]
    public void EveryFixtureThatMayNotPostIsProvablyBelowTheBarOrAnExcludedType()
    {
        foreach (var fixture in Corpus.Fixtures.Where(f => !f.Expect.AutoPostAllowed))
        {
            var parsed = Parse(fixture);
            var observedType = MapType(parsed.TransactionType);

            var blockedByConfidence = parsed.ConfidenceScore < SmsEvidencePolicy.MinimumAutoPostConfidence;
            var blockedByAmount = parsed.Amount < LedgerPolicy.MinimumAmount;
            var blockedByType = !LedgerPolicy.CanPostAutomatically(observedType);

            Assert.True(
                blockedByConfidence || blockedByAmount || blockedByType,
                $"Fixture '{fixture.Id}' must not auto-post, but nothing blocks it.");
        }
    }

    // ─── Fingerprint contract ────────────────────────────────────────────────

    [Fact]
    public void FingerprintsAgreeWithTheSharedContract()
    {
        var canonical = ComputeFingerprint(Corpus.FingerprintCaseById("canonical"));

        foreach (var testCase in Corpus.FingerprintCases)
        {
            var fingerprint = ComputeFingerprint(testCase);

            if (testCase.SameFingerprintAs is not null)
            {
                Assert.Equal(canonical, fingerprint);
            }

            if (testCase.DifferentFingerprintFrom is not null)
            {
                Assert.NotEqual(canonical, fingerprint);
            }
        }
    }

    [Fact]
    public void FingerprintIsLowercaseHexSha256()
    {
        var fingerprint = ComputeFingerprint(Corpus.FingerprintCaseById("canonical"));

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.True(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string ComputeFingerprint(SmsContractCorpus.FingerprintCase testCase) =>
        EvidenceFingerprint.Compute(
            Corpus.OrganizationId,
            testCase.Provider,
            MapType(testCase.TransactionType),
            decimal.Parse(testCase.Amount, CultureInfo.InvariantCulture),
            testCase.Reference,
            testCase.CustomerPhone,
            testCase.OccurredAtUtc);

    private static SmsParsedTransaction Parse(SmsContractCorpus.Fixture fixture)
    {
        var provider = fixture.Sender ?? string.Empty;
        var parser = Parsers.First(p => p.CanHandle(provider, fixture.Body));

        return parser.Parse(
            fixture.Body, provider, null, null, null, Corpus.OrganizationId, null);
    }

    /// <summary>Normalises a parser token to the corpus vocabulary (CASH_IN, CASH_OUT, …).</summary>
    private static string Normalize(string parserToken) => parserToken.ToUpperInvariant() switch
    {
        "DEPOSIT" or "CASHIN" or "CASH_IN" => "CASH_IN",
        "WITHDRAWAL" or "CASHOUT" or "CASH_OUT" => "CASH_OUT",
        _ => parserToken.ToUpperInvariant()
    };

    private static TransactionType MapType(string parserToken) => Normalize(parserToken) switch
    {
        "CASH_IN" => TransactionType.CashIn,
        "CASH_OUT" => TransactionType.CashOut,
        "TRANSFER" => TransactionType.Transfer,
        "COMMISSION" => TransactionType.Commission,
        "REVERSAL" => TransactionType.Reversal,
        _ => TransactionType.Unknown
    };
}

/// <summary>Reader for the shared cross-platform fixture corpus.</summary>
public sealed class SmsContractCorpus
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public required Guid OrganizationId { get; init; }
    public required IReadOnlyList<Fixture> Fixtures { get; init; }
    public required IReadOnlyList<FingerprintCase> FingerprintCases { get; init; }

    public Fixture FixtureById(string id) =>
        Fixtures.FirstOrDefault(f => f.Id == id)
        ?? throw new InvalidOperationException($"Fixture '{id}' is missing from the shared corpus.");

    public FingerprintCase FingerprintCaseById(string id) =>
        FingerprintCases.FirstOrDefault(f => f.Id == id)
        ?? throw new InvalidOperationException($"Fingerprint case '{id}' is missing from the shared corpus.");

    public static SmsContractCorpus Load()
    {
        var path = Locate();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var fixtures = new List<Fixture>();
        foreach (var element in root.GetProperty("fixtures").EnumerateArray())
        {
            var expect = element.GetProperty("expect");

            fixtures.Add(new Fixture
            {
                Id = element.GetProperty("id").GetString()!,
                Sender = element.TryGetProperty("sender", out var s) ? s.GetString() : null,
                Body = element.GetProperty("body").GetString()!,
                Expect = new Expectation
                {
                    TransactionType = expect.TryGetProperty("transactionType", out var t) ? t.GetString() : null,
                    Provider = expect.TryGetProperty("provider", out var p) ? p.GetString() : null,
                    AmountSpecified = expect.TryGetProperty("amount", out var a),
                    Amount = expect.TryGetProperty("amount", out var a2) && a2.ValueKind != JsonValueKind.Null
                        ? decimal.Parse(a2.GetString()!, CultureInfo.InvariantCulture)
                        : null,
                    ReferenceSpecified = expect.TryGetProperty("reference", out _),
                    Reference = expect.TryGetProperty("reference", out var r) && r.ValueKind != JsonValueKind.Null
                        ? r.GetString()
                        : null,
                    CustomerPhone = expect.TryGetProperty("customerPhone", out var cp) ? cp.GetString() : null,
                    AutoPostAllowed = expect.GetProperty("autoPostAllowed").GetBoolean()
                }
            });
        }

        var fingerprintCases = new List<FingerprintCase>();
        foreach (var element in root.GetProperty("fingerprintCases").EnumerateArray())
        {
            fingerprintCases.Add(new FingerprintCase
            {
                Id = element.GetProperty("id").GetString()!,
                Provider = element.GetProperty("provider").GetString()!,
                TransactionType = element.GetProperty("transactionType").GetString()!,
                Amount = element.GetProperty("amount").GetString()!,
                Reference = element.TryGetProperty("reference", out var r) ? r.GetString() : null,
                CustomerPhone = element.TryGetProperty("customerPhone", out var cp) ? cp.GetString() : null,
                OccurredAtUtc = element.GetProperty("occurredAtUtc").GetDateTimeOffset(),
                SameFingerprintAs = element.TryGetProperty("sameFingerprintAs", out var same) ? same.GetString() : null,
                DifferentFingerprintFrom = element.TryGetProperty("differentFingerprintFrom", out var diff)
                    ? diff.GetString()
                    : null
            });
        }

        return new SmsContractCorpus
        {
            OrganizationId = Guid.Parse(root.GetProperty("organizationId").GetString()!),
            Fixtures = fixtures,
            FingerprintCases = fingerprintCases
        };
    }

    /// <summary>
    /// Walks up from the test binary to find the repository's <c>contracts</c> directory.
    /// Located rather than copied so both platforms read the same file on disk — a copied
    /// fixture could drift from the original, defeating the point.
    /// </summary>
    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "contracts", "sms-contract-fixtures.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "contracts/sms-contract-fixtures.json was not found above the test output directory.");
    }

    public sealed class Fixture
    {
        public required string Id { get; init; }
        public string? Sender { get; init; }
        public required string Body { get; init; }
        public required Expectation Expect { get; init; }
    }

    public sealed class Expectation
    {
        public string? TransactionType { get; init; }
        public string? Provider { get; init; }
        public bool AmountSpecified { get; init; }
        public decimal? Amount { get; init; }
        public bool ReferenceSpecified { get; init; }
        public string? Reference { get; init; }
        public string? CustomerPhone { get; init; }
        public required bool AutoPostAllowed { get; init; }
    }

    public sealed class FingerprintCase
    {
        public required string Id { get; init; }
        public required string Provider { get; init; }
        public required string TransactionType { get; init; }
        public required string Amount { get; init; }
        public string? Reference { get; init; }
        public string? CustomerPhone { get; init; }
        public required DateTimeOffset OccurredAtUtc { get; init; }
        public string? SameFingerprintAs { get; init; }
        public string? DifferentFingerprintFrom { get; init; }
    }
}
