using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PlcApi.Models;

namespace PlcApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly ImpellerSelection _impellers;
    private readonly CalculationService _calculation;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        ImpellerSelection impellers,
        CalculationService calculation,
        ILogger<SettingsController> logger)
    {
        _impellers   = impellers;
        _calculation = calculation;
        _logger      = logger;
    }

    /// <summary>
    /// The impellers every panel shows and every calculation counts, plus the most the machine
    /// can have.
    /// </summary>
    [HttpGet("impellers")]
    public IActionResult GetImpellers() => Ok(Current());

    /// <summary>
    /// Saves a new selection for every viewer. Any signed-in user may change it (a site decision).
    /// Recalculates the energy of every recorded cycle, the daily rollup and the lifetime totals
    /// BEFORE returning, so a 200 means the new figures are already in place.
    /// </summary>
    [HttpPut("impellers")]
    public async Task<IActionResult> SaveImpellers([FromBody] ImpellerSelectionDto request)
    {
        int[] selected;
        try
        {
            selected = ImpellerSelection.Normalize(request.Selected ?? []);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        try
        {
            string? user = User.Identity?.Name ?? User.FindFirst("unique_name")?.Value;
            await _calculation.ApplyImpellerSelectionAsync(selected, user);
            return Ok(Current());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save the impeller selection");
            return StatusCode(500, new { error = "Failed to save the impeller selection" });
        }
    }

    private ImpellerSelectionDto Current() => new()
    {
        Selected     = _impellers.Snapshot(),
        MaxImpellers = ImpellerSelection.MaxImpellers
    };
}
