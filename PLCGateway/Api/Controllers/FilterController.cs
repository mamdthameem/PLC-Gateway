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

            // Omitted/null/[] means "all parameters". Unknown keys are rejected rather than
            // dropped, so a stale dashboard build fails loudly instead of silently computing less.
            if (input.SelectedParameters is { Length: > 0 })
            {
                var unknown = input.SelectedParameters
                    .Except(CalculationService.Section2ParameterKeys, StringComparer.Ordinal)
                    .ToArray();
                if (unknown.Length > 0)
                    return BadRequest(new
                    {
                        error = "unknown selectedParameters",
                        unknown,
                        supported = CalculationService.Section2ParameterKeys
                    });
            }

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
    /// One saved request: what was asked for (mode, window or cycles or item, the parameter
    /// toggles) and its status. The dashboard calls this when a page opens with ?result=&lt;id&gt; in
    /// its URL, to show that saved result again without recomputing it.
    ///
    /// Signed-in users only (the controller is [Authorize]): a result id copied from a URL is useless
    /// to anyone who cannot log in to this gateway. Times carry the gateway's UTC offset
    /// (e.g. +05:30), because they are stored as plant wall-clock time and a browser elsewhere would
    /// otherwise read them as its own local time.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetRequest(int id)
    {
        try
        {
            var r = await _service.GetRequestAsync(id);
            if (r is null)
                return NotFound(new { error = $"Request {id} not found" });

            return Ok(new
            {
                requestId          = r.RequestId,
                status             = r.Status,
                filterBy           = r.FilterBy,
                filterStart        = PlantTime(r.FilterStart),
                filterEnd          = PlantTime(r.FilterEnd),
                periodLabel        = r.PeriodLabel,
                filterCycleFrom    = r.FilterCycleFrom,
                filterCycleTo      = r.FilterCycleTo,
                filterMetalName    = r.FilterMetalName,
                selectedParameters = r.SelectedParameters,
                processedAt        = r.ProcessedAt is { } p ? PlantTime(p) : (DateTimeOffset?)null,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch request {Id}", id);
            return StatusCode(500, new { error = "Failed to fetch request" });
        }
    }

    private static DateTimeOffset PlantTime(DateTime stored) =>
        new(DateTime.SpecifyKind(stored, DateTimeKind.Local));

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
    /// Production per casting metal from plc_filtered_metal_production once status is 'done'.
    /// Values are summed declared casting-metal weights — this is how Section 2 reports
    /// production (Section 1 reports it from the Tonnage accumulator instead).
    /// </summary>
    [HttpGet("{id}/metals")]
    public async Task<IActionResult> GetMetals(int id)
    {
        try
        {
            var data = await _service.GetMetalProductionAsync(id);
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch metal production for request {Id}", id);
            return StatusCode(500, new { error = "Failed to fetch metal production" });
        }
    }

    /// <summary>
    /// Per-impeller current from plc_filtered_amps_data once status is 'done'. Per impeller:
    /// a duration-weighted overall average for the filter (tile headline value) plus a per-cycle
    /// series (tile-click chart), mirroring the Section 1 Amps tile/graph but scoped to this filter.
    /// </summary>
    [HttpGet("{id}/amps")]
    public async Task<IActionResult> GetAmps(int id)
    {
        try
        {
            var data = await _service.GetAmpsDataAsync(id);
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch filtered amps data for request {Id}", id);
            return StatusCode(500, new { error = "Failed to fetch filtered amps data" });
        }
    }

    // GET {id}/shots was removed: the shots breakdown is Section 1 only now (it does not respond
    // to a filter — a refill interval spans whatever cycles fall in it, mixing casting items), so
    // the dashboard reads it from /api/shotsbreakdown above the filter bar instead.
}
