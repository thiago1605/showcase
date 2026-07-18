using FellowCore.Api.Auth;
using FellowCore.Application.Modules.Settlements.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/settlements")]
[EnableRateLimiting("fixed")]
public class SettlementController(ISettlementService settlementService) : ControllerBase
{
    // H6: Settlement trigger must NOT be accessible by regular tenants.
    // Requires the platform master key (X-Master-Key header) so only operators can trigger it.
    [HttpPost("process-daily")]
    [MasterKeyAuth]
    public async Task<IActionResult> ProcessDailySettlements()
    {
        await settlementService.ProcessDailySettlementsAsync();

        return Ok(new { Message = "Liquidação diária processada com sucesso (ou ignorada se não havia saldo)." });
    }
}