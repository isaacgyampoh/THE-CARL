using Microsoft.Extensions.Logging.Abstractions;
using Zazi.Application.Email;
using Zazi.Infrastructure.Email;

namespace Zazi.IntegrationTests;

/// <summary>
/// Sends a real message through the real Resend API.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about the email transport is tested against a stub, which proves the code is
/// right and proves nothing about the account: whether the key works, whether it has the scope it
/// needs, and above all whether <c>getzazi.com</c> is verified. That last one is the failure this
/// deployment is most likely to hit, it cannot be discovered locally, and it presents as signup
/// appearing to work while nobody receives anything.
/// </para>
/// <para>
/// Skipped unless <b>both</b> environment variables are set, so a normal test run never sends
/// anything. Two rather than one because <c>RESEND_API_KEY</c> will legitimately be present on a
/// machine configured for real use, and that alone should not start sending mail from a test run.
/// </para>
/// <para>
/// Run it after issuing or rotating a key:
/// <code>
/// ZAZI_EMAIL_LIVE_TEST=1 RESEND_API_KEY="$(cat /path/to/key)" \
///   scripts/dotnet.sh test tests/Zazi.IntegrationTests/Zazi.IntegrationTests.csproj \
///   --filter FullyQualifiedName~ResendLiveSmokeTest
/// </code>
/// </para>
/// </remarks>
public class ResendLiveSmokeTest
{
    /// <summary>
    /// Resend's own sink address. It accepts and discards, so this verifies the whole path —
    /// credential, scope, sender domain, request shape — without putting a test message in
    /// front of a real person.
    /// </summary>
    private const string ResendTestRecipient = "delivered@resend.dev";

    [SkippableFact]
    public async Task TheConfiguredKeyAndSenderDomainActuallyWork()
    {
        Skip.If(
            Environment.GetEnvironmentVariable("ZAZI_EMAIL_LIVE_TEST") != "1",
            "Live email test is opt-in. Set ZAZI_EMAIL_LIVE_TEST=1 to run it.");

        var credential = ResendCredential.FromEnvironment();
        Skip.IfNot(
            credential.IsPresent,
            $"{EmailOptions.ApiKeyEnvironmentVariable} is not set in this process's environment.");

        var options = new EmailOptions
        {
            Provider = EmailProvider.Resend,
            FromAddress = "no-reply@getzazi.com",
            FromName = "Zazi",
            TimeoutSeconds = 20
        };
        options.Validate();

        using var http = new HttpClient
        {
            BaseAddress = new Uri("https://api.resend.com/"),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };

        var sender = new ResendEmailSender(
            http,
            Microsoft.Extensions.Options.Options.Create(options),
            credential,
            NullLogger<ResendEmailSender>.Instance);

        var result = await sender.SendAsync(new EmailMessage(
            ToAddress: ResendTestRecipient,
            Subject: "Zazi transport check",
            HtmlBody: "<p>Automated check that the Zazi email transport is configured. No action needed.</p>",
            TextBody: "Automated check that the Zazi email transport is configured. No action needed.")
        {
            // Repeated runs within Resend's idempotency window collapse into one send rather
            // than filling the account's history with identical checks.
            IdempotencyKey = $"zazi-transport-check-{DateTime.UtcNow:yyyy-MM-dd-HH}"
        });

        // The failure reason is deliberately coarse and carries no provider detail, so asserting
        // on it is not useful. What is useful is that it sent.
        Assert.True(
            result.Sent,
            $"Live send failed: {result.FailureReason}. "
            + "A 403 here almost always means getzazi.com is not yet verified at "
            + "https://resend.com/domains; a 401 means the key is wrong or lacks Sending access.");
    }
}
