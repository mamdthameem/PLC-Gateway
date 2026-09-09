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

    /// <summary>
    /// Average current per impeller across the last completed blast cycle. The tiles show this
    /// beside the live reading so a stopped machine still tells you what it runs at.
    /// </summary>
    [HttpGet("last-cycle")]
    public async Task<IActionResult> GetLastCycle()
    {
        try
        {
            return Ok(await _service.GetLastCycleAveragesAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch last-cycle impeller averages");
            return StatusCode(500, new { error = "Failed to fetch last-cycle impeller averages" });
        }
    }

    /// <summary>
    /// Average current per completed cycle for one impeller, across the whole recorded history.
    /// Backs the amps chart's "all cycles" range.
    /// </summary>
    [HttpGet("by-cycle")]
    public async Task<IActionResult> GetByCycle([FromQuery] int impeller)
    {
        if (impeller < 1 || impeller > 10)
            return BadRequest(new { error = "impeller must be between 1 and 10" });

        try
        {
            return Ok(await _service.GetPerCycleAveragesAsync(impeller));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch per-cycle amps");
            return StatusCode(500, new { error = "Failed to fetch per-cycle amps" });
        }
    }
}
