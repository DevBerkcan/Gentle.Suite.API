using System.Security.Claims;
using GentleSuite.Infrastructure.Ai;
using GentleSuite.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GentleSuite.API.Controllers;

/// <summary>Vertrags-Agent: proposal only, nothing is written except the run log. The contract itself is created by the
/// normal wizard endpoint (POST api/agencycontracts/from-wizard) after the user reviewed the proposal.</summary>
[ApiController, Route("api/contract-ai"), Authorize]
public class ContractAiController(ContractAiService svc) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<ContractAiStatusDto>> Status(CancellationToken ct) => Ok(await svc.GetStatusAsync(ct));

    [HttpPost("draft"), EnableRateLimiting("contract-ai")]
    public async Task<ActionResult<ContractAiDraftDto>> Draft(ContractAiDraftRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await svc.DraftAsync(req, User.FindFirstValue(ClaimTypes.Email), ct));
        }
        catch (AiNotConfiguredException e) { return StatusCode(503, new { error = e.Message }); }
        catch (ContractAiException e) { return UnprocessableEntity(new { error = e.Message }); }
        catch (KeyNotFoundException e) { return NotFound(new { error = e.Message }); }
    }
}
