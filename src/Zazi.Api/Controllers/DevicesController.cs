using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Zazi.Api.Models;
using Zazi.Api.Security;
using Zazi.Application;
using Zazi.Application.Security;

namespace Zazi.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
[EnableRateLimiting(RateLimitPolicies.Tenant)]
public class DevicesController : ControllerBase
{
    private readonly IDeviceService _deviceService;
    private readonly IDeviceEnrollmentService _enrollmentService;
    private readonly ICurrentUserContext _currentUser;
    private readonly ITenantGuard _tenantGuard;

    public DevicesController(
        IDeviceService deviceService,
        IDeviceEnrollmentService enrollmentService,
        ICurrentUserContext currentUser,
        ITenantGuard tenantGuard)
    {
        _deviceService = deviceService;
        _enrollmentService = enrollmentService;
        _currentUser = currentUser;
        _tenantGuard = tenantGuard;
    }

    [HttpGet]
    [Authorize(Policy = ZaziPolicies.DeviceRead)]
    [ProducesResponseType(typeof(IReadOnlyList<DeviceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DeviceDto>>> GetAll(CancellationToken cancellationToken)
    {
        var devices = await _deviceService.GetDevicesAsync(_currentUser.OrganizationId, cancellationToken);

        if (!_currentUser.HasOrganizationWideScope)
        {
            var assigned = _currentUser.BranchId;
            devices = devices.Where(d => d.BranchId == assigned).ToList();
        }

        return Ok(devices);
    }

    [HttpPost]
    [Authorize(Policy = ZaziPolicies.DeviceManage)]
    [ProducesResponseType(typeof(DeviceDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<DeviceDto>> Register(
        [FromBody] RegisterDeviceApiRequest request,
        CancellationToken cancellationToken)
    {
        var branchId = await _tenantGuard.ResolveWritableBranchAsync(request.BranchId, cancellationToken);

        var device = await _deviceService.RegisterDeviceAsync(
            new CreateDeviceRequest(
                _currentUser.OrganizationId,
                branchId,
                request.Name,
                request.DeviceIdentifier,
                request.Platform,
                request.Network,
                request.Role,
                request.AppVersion,
                request.OsVersion),
            cancellationToken);

        return CreatedAtAction(nameof(GetAll), null, device);
    }

    // ─── Enrolment ───────────────────────────────────────────────────────────

    /// <summary>Issues a short-lived, single-use device enrolment code.</summary>
    /// <remarks>
    /// Requires <c>device.manage</c>. The scope of the resulting device — organization,
    /// branch and role — is fixed here by the issuer, so a leaked code cannot be redeemed
    /// into a different branch or with a wider role.
    /// <para>The plaintext code is returned exactly once and cannot be recovered.</para>
    /// </remarks>
    [HttpPost("enrollment-codes")]
    [Authorize(Policy = ZaziPolicies.DeviceManage)]
    [ProducesResponseType(typeof(EnrollmentCodeIssuedDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<EnrollmentCodeIssuedDto>> IssueEnrollmentCode(
        [FromBody] IssueEnrollmentCodeApiRequest request,
        CancellationToken cancellationToken)
    {
        var branchId = await _tenantGuard.ResolveWritableBranchAsync(request.BranchId, cancellationToken);

        var issued = await _enrollmentService.IssueCodeAsync(
            new IssueEnrollmentCodeRequest(
                branchId, request.DeviceRole, request.IntendedUserId, request.LifetimeHours, request.Label),
            _currentUser.OrganizationId,
            branchId,
            _currentUser.UserId,
            cancellationToken);

        return CreatedAtAction(nameof(GetEnrollmentCodes), null, issued);
    }

    /// <summary>Lists enrolment codes. Carries no secret material — prefixes only.</summary>
    [HttpGet("enrollment-codes")]
    [Authorize(Policy = ZaziPolicies.DeviceManage)]
    [ProducesResponseType(typeof(IReadOnlyList<EnrollmentCodeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<EnrollmentCodeDto>>> GetEnrollmentCodes(
        CancellationToken cancellationToken)
    {
        // Branch-scoped managers see only their own branch's codes.
        var branchId = _currentUser.HasOrganizationWideScope ? null : _currentUser.BranchId;

        var codes = await _enrollmentService.GetCodesAsync(
            _currentUser.OrganizationId, branchId, cancellationToken);

        return Ok(codes);
    }

    /// <summary>Withdraws an unredeemed code.</summary>
    [HttpPost("enrollment-codes/{codeId:guid}/revoke")]
    [Authorize(Policy = ZaziPolicies.DeviceManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RevokeEnrollmentCode(Guid codeId, CancellationToken cancellationToken)
    {
        await _enrollmentService.RevokeCodeAsync(
            _currentUser.OrganizationId, codeId, _currentUser.UserId, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Redeems an enrolment code, creating this device.
    /// </summary>
    /// <remarks>
    /// Authenticated but deliberately <b>not</b> gated on <c>device.manage</c>: enrolment is
    /// precisely the case where the caller does not hold that capability. An agent can bring
    /// a handset online with a code an administrator issued, without the administrator
    /// handling the phone.
    /// <para>
    /// Rate limited under the authentication policy rather than the tenant policy, because
    /// this endpoint accepts a secret and is therefore a guessing target.
    /// </para>
    /// </remarks>
    [HttpPost("enrol")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Authentication)]
    [ProducesResponseType(typeof(DeviceEnrolledDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeviceEnrolledDto>> Enrol(
        [FromBody] RedeemEnrollmentCodeApiRequest request,
        CancellationToken cancellationToken)
    {
        var enrolled = await _enrollmentService.RedeemAsync(
            new RedeemEnrollmentCodeRequest(
                request.Code,
                request.DeviceIdentifier,
                request.Name,
                request.Platform ?? "Android",
                request.Network ?? "MTN",
                request.AppVersion ?? "unknown",
                request.OsVersion ?? "unknown"),
            _currentUser.OrganizationId,
            _currentUser.UserId,
            cancellationToken);

        return CreatedAtAction(nameof(GetDeviceSelf), null, enrolled);
    }

    /// <summary>
    /// Reports this device's own live state.
    /// </summary>
    /// <remarks>
    /// Lets a handset discover revocation directly rather than inferring it from a failed
    /// sync. <c>isRevoked</c> and <c>capabilities</c> are machine-readable so the client can
    /// clear local credentials deterministically instead of parsing a message.
    /// <para>
    /// The device is identified by the <c>X-Device-Identifier</c> header and always resolved
    /// within the caller's own organization, so one tenant cannot probe another's devices.
    /// </para>
    /// </remarks>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(DeviceSelfDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceSelfDto>> GetDeviceSelf(
        [FromHeader(Name = "X-Device-Identifier")] string? deviceIdentifier,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceIdentifier))
        {
            return BadRequest(new { error = "The X-Device-Identifier header is required." });
        }

        var device = await _enrollmentService.GetDeviceSelfAsync(
            _currentUser.OrganizationId, deviceIdentifier, cancellationToken);

        // A device in another tenant is indistinguishable from one that does not exist.
        return device is null ? NotFound() : Ok(device);
    }
}
