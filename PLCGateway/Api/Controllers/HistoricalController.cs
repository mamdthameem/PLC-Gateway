using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PlcApi.Services;

namespace PlcApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class HistoricalController : ControllerBase
{
    private readonly IHistoricalService _service;
    private readonly ILogger<HistoricalController> _logger;

    public HistoricalController(IHistoricalService service, ILogger<HistoricalController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    /// <summary>
    /// Time-series records for a single parameter from plc_historical_data.
    /// Returns all recorded samples (COV/STATE_CHANGE/VALUE_CHANGE/BLAST_ON/INITIAL/HEARTBEAT),
    /// including the periodic 60s heartbeats; only synthetic DISCONNECT events are excluded.
    /// Query: ?name=Blast+ON%2FOFF&start=2026-05-12T00:00:00Z&end=2026-05-13T00:00:00Z
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string name,
        [FromQuery] DateTimeOffset start,
        [FromQuery] DateTimeOffset end)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "name query parameter is required" });
        if (start >= end)
            return BadRequest(new { error = "start must be before end" });

        try
        {
            var data = await _service.GetPointsAsync(name, start.UtcDateTime, end.UtcDateTime);
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch historical data for {Name}", name);
            return StatusCode(500, new { error = "Failed to fetch historical data" });
        }
    }
}
