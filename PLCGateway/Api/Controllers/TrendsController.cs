using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PlcApi.Services;

namespace PlcApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TrendsController : ControllerBase
{
    private readonly ITrendsService _service;
    private readonly ILogger<TrendsController> _logger;

    public TrendsController(ITrendsService service, ILogger<TrendsController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    /// <summary>
    /// Pre-aggregated trend buckets for the dashboard graphs (utility, production, energy,
    /// efficiency) — one row per bucket, so an all-time request stays small however much history
    /// has accumulated.
    ///
    /// bucket = "day" (default) or "month" reads the plc_daily_trends rollup; omit start/end for
    /// the full all-time range. bucket = "hour" is computed live from Tier 2 for sub-day detail
    /// and requires both bounds.
    /// Query: ?bucket=month  ·  ?bucket=hour&amp;start=2026-07-24T00:00:00Z&amp;end=2026-07-25T00:00:00Z
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTimeOffset? start,
        [FromQuery] DateTimeOffset? end,
        [FromQuery] string bucket = "day")
    {
        if (start.HasValue && end.HasValue && start >= end)
            return BadRequest(new { error = "start must be before end" });

        if (bucket is not ("hour" or "day" or "month"))
            return BadRequest(new { error = "bucket must be 'hour', 'day' or 'month'" });

        if (bucket == "hour" && (!start.HasValue || !end.HasValue))
            return BadRequest(new { error = "hourly trends require both start and end" });

        try
        {
            // Storage is gateway-local wall time (written via DateTime.Now), so incoming
            // offsets are converted to local before bucketing.
            var data = await _service.GetTrendsAsync(
                start?.LocalDateTime, end?.LocalDateTime, bucket);
            return Ok(data);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch trends");
            return StatusCode(500, new { error = "Failed to fetch trends" });
        }
    }
}
