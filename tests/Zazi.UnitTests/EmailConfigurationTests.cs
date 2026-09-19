using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zazi.Application.Email;
using Zazi.Infrastructure.Email;

namespace Zazi.UnitTests;

/// <summary>
/// How a deployment gets wired: where the credential may come from, and what an environment
/// that has not been given one ends up with.
/// </summary>
/// <remarks>
/// Serialised into its own collection because these tests write a process-wide environment
/// variable. xUnit runs separate collections in parallel by default, and two of these running at
/// once would read each other's value.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public class EmailConfigurationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static IEmailSender Resolve(IConfiguration configuration, bool isDevelopment)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddZaziEmail(configuration, isDevelopment);
        return services.BuildServiceProvider().GetRequiredService<IEmailSender>();
    }

    // ─── The credential comes from the environment, and only from there ──────

    [Fact]
    public void A_key_in_configuration_is_not_picked_up()
    {
        // The whole point of reading the environment directly. If configuration were consulted,
        // this key — which is exactly what someone would write into appsettings.json — would
        // work, and then it would be committed.
        using var _ = EnvironmentVariable.Cleared(EmailOptions.ApiKeyEnvironmentVariable);

        var configuration = Config(
            ("Email:Provider", "Resend"),
            ("Email:ApiKey", "re_from_appsettings_0123456789"),
            ("RESEND_API_KEY", "re_from_appsettings_0123456789"));

        var error = Assert.Throws<InvalidOperationException>(
            () => Resolve(configuration, isDevelopment: false));

        Assert.Contains(EmailOptions.ApiKeyEnvironmentVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_for_Resend_without_a_key_fails_at_startup()
    {
        using var _ = EnvironmentVariable.Cleared(EmailOptions.ApiKeyEnvironmentVariable);

        var error = Assert.Throws<InvalidOperationException>(
            () => Resolve(Config(("Email:Provider", "Resend")), isDevelopment: false));

        // Failing here is the point: a deployment configured to send but unable to would
        // otherwise present a working signup page that silently delivers nothing.
        Assert.Contains(EmailOptions.ApiKeyEnvironmentVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_for_Resend_with_a_key_in_the_environment_gets_the_Resend_sender()
    {
        using var _ = EnvironmentVariable.Set(
            EmailOptions.ApiKeyEnvironmentVariable, "re_TestKey_0123456789abcdef");

        Assert.IsType<ResendEmailSender>(Resolve(Config(("Email:Provider", "Resend")), isDevelopment: false));
    }

    // ─── What an unconfigured environment gets ───────────────────────────────

    [Fact]
    public void Production_without_a_provider_refuses_to_send_rather_than_logging_the_message()
    {
        // Never LoggingEmailSender here. It writes the body, and the body of the message this
        // system most needs to send is a one-time verification link.
        using var _ = EnvironmentVariable.Cleared(EmailOptions.ApiKeyEnvironmentVariable);

        Assert.IsType<DisabledEmailSender>(Resolve(Config(), isDevelopment: false));
    }

    [Fact]
    public void Development_without_a_provider_writes_the_message_to_the_log()
    {
        using var _ = EnvironmentVariable.Cleared(EmailOptions.ApiKeyEnvironmentVariable);

        Assert.IsType<LoggingEmailSender>(Resolve(Config(), isDevelopment: true));
    }

    [Fact]
    public async Task The_disabled_sender_reports_failure_rather_than_pretending()
    {
        using var _ = EnvironmentVariable.Cleared(EmailOptions.ApiKeyEnvironmentVariable);

        var result = await Resolve(Config(), isDevelopment: false)
            .SendAsync(new EmailMessage("a@example.com", "s", "<p>h</p>", "t"));

        // Reporting success would leave the caller believing a person had been emailed, and that
        // person waiting for a message nobody is going to send.
        Assert.False(result.Sent);
    }

    // ─── The credential object itself ────────────────────────────────────────

    [Fact]
    public void The_credential_never_prints_itself()
    {
        var credential = new ResendCredential("re_TestKey_0123456789abcdef");

        Assert.DoesNotContain("re_TestKey", credential.ToString(), StringComparison.Ordinal);
        Assert.True(credential.IsPresent);
        Assert.False(new ResendCredential("   ").IsPresent);
    }

    [Fact]
    public void The_credential_reads_the_environment_and_not_configuration()
    {
        using var _ = EnvironmentVariable.Set(
            EmailOptions.ApiKeyEnvironmentVariable, "re_FromEnvironment_0123456789");

        Assert.Equal("re_FromEnvironment_0123456789", ResendCredential.FromEnvironment().ApiKey);
    }

    // ─── Options validation ──────────────────────────────────────────────────

    [Fact]
    public void A_sender_address_is_required_and_must_be_one()
    {
        Assert.Throws<InvalidOperationException>(() => new EmailOptions { FromAddress = "" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new EmailOptions { FromAddress = "getzazi.com" }.Validate());
    }

    [Theory]
    [InlineData("Zazi\r\nBcc: attacker@example.com")]
    [InlineData("Zazi <spoofed@example.com>")]
    public void A_sender_name_cannot_smuggle_headers(string fromName)
    {
        // FormattedFrom builds "Name <address>" by concatenation. A newline or a bracket in
        // either half is how that becomes a second header.
        Assert.Throws<InvalidOperationException>(
            () => new EmailOptions { FromName = fromName }.Validate());
    }

    [Fact]
    public void The_default_sender_is_the_production_identity()
    {
        var options = new EmailOptions();
        options.Validate();

        Assert.Equal("Zazi <no-reply@getzazi.com>", options.FormattedFrom);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(121)]
    public void An_unreasonable_timeout_is_refused(int seconds) =>
        Assert.Throws<InvalidOperationException>(
            () => new EmailOptions { TimeoutSeconds = seconds }.Validate());

    /// <summary>Sets an environment variable for the duration of a test, then puts it back.</summary>
    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        private EnvironmentVariable(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public static EnvironmentVariable Set(string name, string value) => new(name, value);

        public static EnvironmentVariable Cleared(string name) => new(name, null);

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}

/// <summary>Serialises every test that touches a process-wide environment variable.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class EnvironmentCollection
{
    public const string Name = "environment-variables";
}
