using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PlcApi.Services;

namespace PlcApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CyclesController : ControllerBase
{
    private readonly ICyclesService _service;
    private readonly ILogger<CyclesController> _logger;

    public CyclesController(ICyclesService service, ILogger<CyclesController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    /// <summary>
    /// Most recent completed blast cycle (blast_start, blast_end) from plc_cycles.
    /// Used as the default time window for the amps history graph.
    /// </summary>
    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest()
    {
        try
        {
            var cycle = await _service.GetLatestAsync();
            if (cycle is null) return NotFound(new { error = "No completed cycles found" });
            return Ok(cycle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch latest cycle");
            return StatusCode(500, new { error = "Failed to fetch latest cycle" });
        }
    }
}
