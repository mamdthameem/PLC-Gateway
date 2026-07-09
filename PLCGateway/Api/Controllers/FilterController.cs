using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PlcApi.Models;
using PlcApi.Services;

namespace PlcApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FilterController : ControllerBase
{
    private readonly IFilterService _service;
    private readonly ILogger<FilterController> _logger;

    public FilterController(IFilterService service, ILogger<FilterController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    /// <summary>
    /// Submit a filter request. Supports filter_by = "time" | "cycle" | "metal".
    /// Returns the request id. Poll GET /api/filter/{id}/status every 2â€“3 s until done.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] FilterRequestInput input)
    {
        try
        {
            if (input.FilterStart >= input.FilterEnd && input.FilterBy == "time")
                return BadRequest(new { error = "filter_start must be before filter_end for time-based filters" });

            var id = await _service.SubmitRequestAsync(input);
            return Ok(new { requestId = id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit filter request");
            return StatusCode(500, new { error = "Failed to submit filter request" });
        }
    }

    /// <summary>
    /// Poll the status of a calculation request.
    /// Status: pending â†’ processing â†’ done (or error). Stop polling on done or error.
    /// </summary>
    [HttpGet("{id}/status")]
    public async Task<IActionResult> GetStatus(int id)
    {
        try
        {
            var status = await _service.GetStatusAsync(id);
            if (status is null)
                return NotFound(new { error = $"Request {id} not found" });

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch status for request {Id}", id);
            return StatusCode(500, new { error = "Failed to fetch request status" });
        }
    }

    /// <summary>
    /// Scalar results from plc_filtered_parameters once status is 'done'.
    /// </summary>
    [HttpGet("{id}/results")]
    public async Task<IActionResult> GetResults(int id)
    {
        try
        {
            var results = await _service.GetResultsAsync(id);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch results for request {Id}", id);
            return StatusCode(500, new { error = "Failed to fetch results" });
        }
    }

    /// <summary>
    /// Per-cycle breakdown from plc_filtered_cycle_data once status is 'done'.
    /// </summary>
    [HttpGet("{id}/cycles")]
    public async Task<IActionResult> GetCycles(int id)
    {
        try
        {
            var data = await _service.GetCycleDataAsync(id);
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch cycle data for request {Id}", id);
            return StatusCode(500, new { error = "Failed to fetch cycle data" });
        }
    }

    /// <summary>
    /// Shots breakdown from plc_filtered_shots_breakdown once status is 'done'.
    /// </summary>
    [HttpGet("{id}/shots")]
    public async Task<IActionResult> GetShots(int id)
    {
        try
        {
            var data = await _service.GetShotsBreakdownAsync(id);
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch shots breakdown for request {Id}", id);
            return StatusCode(500, new { error = "Failed to fetch shots breakdown" });
        }
    }
}
