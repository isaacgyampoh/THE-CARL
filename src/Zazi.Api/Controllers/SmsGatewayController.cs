using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Zazi.Application.Keypad;

namespace Zazi.Api.Controllers;

/// <summary>
/// Where the SMS gateway delivers texts sent to Zazi's number by agents on keypad phones.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous, because the gateway has no Zazi account — but not open. Gateways such as Africa's
/// Talking cannot sign their callbacks, so the callback URL carries a secret, and a request
/// without it is refused before anything is read. Without that, anyone could post a fake "MoMo
/// message" in an agent's name.
/// </para>
/// <para>
/// The sender is taken from the gateway's own field, which is the number the text really came
/// from. That number is the agent's identity here, the way a signed-in token is elsewhere: an
/// unlinked number can do nothing except link itself with a valid activation code.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/sms-gateway")]
[AllowAnonymous]
public class SmsGatewayController : ControllerBase
{
    private readonly IKeypadSmsService _keypad;
    private readonly SmsGatewayOptions _options;
    private readonly IWebHostEnvironment _environment;

    public SmsGatewayController(IKeypadSmsService keypad, IOptions<SmsGatewayOptions> options, IWebHostEnvironment environment)
    {
        _keypad = keypad;
        _options = options.Value;
        _environment = environment;
    }

    /// <summary>A text arrived. Africa's Talking posts it as a form: from, to, text, date, id.</summary>
    [HttpPost("inbound")]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<IActionResult> Inbound([FromQuery] string? key, [FromForm] GatewayForm form, CancellationToken cancellationToken)
    {
        if (!KeyMatches(key))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(form.From) || string.IsNullOrWhiteSpace(form.Text))
        {
            // Acknowledged all the same: a gateway retries anything but a 200, and retrying an
            // empty message helps nobody.
            return Ok();
        }

        await _keypad.HandleAsync(
            new InboundSms(form.From, form.Text, ParseDate(form.Date), form.Id),
            cancellationToken);

        return Ok();
    }

    /// <summary>
    /// Development only: send a text as if from a phone and see the reply, with no gateway.
    /// </summary>
    [HttpPost("simulate")]
    public async Task<ActionResult<KeypadReply>> Simulate([FromBody] SimulatedSms body, CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        return Ok(await _keypad.HandleAsync(
            new InboundSms(body.From, body.Text, DateTimeOffset.UtcNow, body.Id), cancellationToken));
    }

    private bool KeyMatches(string? key)
    {
        // No secret configured means the endpoint is off, not open.
        if (string.IsNullOrWhiteSpace(_options.InboundSecret) || string.IsNullOrEmpty(key))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(_options.InboundSecret));
    }

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : DateTimeOffset.UtcNow;

    public sealed class GatewayForm
    {
        [FromForm(Name = "from")] public string? From { get; set; }
        [FromForm(Name = "to")] public string? To { get; set; }
        [FromForm(Name = "text")] public string? Text { get; set; }
        [FromForm(Name = "date")] public string? Date { get; set; }
        [FromForm(Name = "id")] public string? Id { get; set; }
    }

    public sealed record SimulatedSms(string From, string Text, string? Id = null);
}
