using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Zazi.IntegrationTests;

/// <summary>
/// The credential limiter must tell a refused caller when to come back.
/// </summary>
/// <remarks>
/// This is a contract the Android client already relies on: the sync engine reads Retry-After
/// and prefers it to its own backoff curve, on the grounds that the server knows when the
/// window reopens and a guess does not. For a long time the header was simply never sent, so
/// that path was dead and every handset fell back to guessing — a failure that is invisible
/// from either side on its own, because the client works and the server answers correctly.
/// </remarks>
public sealed class RateLimitRetryAfterTests : IClassFixture<ZaziApiFactory>
{
    private readonly ZaziApiFactory _factory;

    public RateLimitRetryAfterTests(ZaziApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ARefusedLoginSaysHowLongToWait()
    {
        var client = _factory.CreateClient();

        // The limiter permits ten credential attempts per minute per caller. These are all
        // wrong on purpose: being refused for bad credentials and being refused for rate is
        // the same shape of request, and it is the second one under test.
        HttpResponseMessage? limited = null;

        for (var attempt = 0; attempt < 30 && limited is null; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { email = "nobody@zazi.test", password = "not-the-password" });

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
        }

        Assert.NotNull(limited);

        var retryAfter = limited!.Headers.RetryAfter;
        Assert.NotNull(retryAfter);

        // Seconds rather than a date, because the client honours only the seconds form.
        Assert.NotNull(retryAfter!.Delta);
        Assert.InRange(retryAfter.Delta!.Value.TotalSeconds, 1, 60);
    }
}
