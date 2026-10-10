using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(ICloudSettingsService cloudSettingsService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<EffectiveSettingsResponse>> Get(CancellationToken cancellationToken)
    {
        var response = await cloudSettingsService.GetEffectiveSettingsAsync(cancellationToken);
        return Ok(response);
    }

    [HttpPut]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<SettingsUpdateResponse>> Put(
        [FromBody] UpdateEffectiveSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var response = await cloudSettingsService.SaveEffectiveSettingsAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpGet("print-layout")]
    public async Task<ActionResult<PrintLayoutResponse>> GetPrintLayout(CancellationToken cancellationToken)
    {
        var response = await cloudSettingsService.GetPrintLayoutAsync(cancellationToken);
        return Ok(response);
    }

    [HttpPut("print-layout")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<PrintLayoutResponse>> UpdatePrintLayout(
        [FromBody] UpdatePrintLayoutRequest request,
        CancellationToken cancellationToken)
    {
        var response = await cloudSettingsService.UpdatePrintLayoutAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("company/select")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<SettingsUpdateResponse>> SelectCompany(
        [FromBody] SelectActiveCompanyRequest request,
        CancellationToken cancellationToken)
    {
        var response = await cloudSettingsService.SelectActiveCompanyAsync(request.CompanyName, cancellationToken);
        return Ok(response);
    }
}
