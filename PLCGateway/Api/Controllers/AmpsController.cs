using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PlcApi.Services;

namespace PlcApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AmpsController : ControllerBase
{
    private readonly IAmpsService _service;
    private readonly ILogger<AmpsController> _logger;

    public AmpsController(IAmpsService service, ILogger<AmpsController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    /// <summary>
    /// Returns live amp readings for all 10 impellers from plc_current_values.
    /// Poll every 5 seconds for near-real-time display.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetImpellerAmps()
    {
        try
        {
            var data = await _service.GetImpellerAmpsAsync();
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch impeller amps");
            return StatusCode(500, new { error = "Failed to fetch impeller amps" });
        }
    }
}
