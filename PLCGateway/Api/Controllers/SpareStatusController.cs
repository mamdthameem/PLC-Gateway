using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PlcApi.Services;

namespace PlcApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SpareStatusController : ControllerBase
{
    private readonly ISpareStatusService _service;
    private readonly ILogger<SpareStatusController> _logger;

    public SpareStatusController(ISpareStatusService service, ILogger<SpareStatusController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    /// <summary>
    /// Returns all 140 rows from plc_spare_status (10 impellers Ã— 14 spares each).
    /// Poll every 10 seconds.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        try
        {
            var data = await _service.GetAllAsync();
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch spare status");
            return StatusCode(500, new { error = "Failed to fetch spare status" });
        }
    }

    /// <summary>
    /// Returns only spares with trigger_active = TRUE AND threshold_hours > 0.
    /// Use this to drive alert banners in the UI.
    /// </summary>
    [HttpGet("alerts")]
    public async Task<IActionResult> GetAlerts()
    {
        try
        {
            var data = await _service.GetAlertsAsync();
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch spare alerts");
            return StatusCode(500, new { error = "Failed to fetch spare alerts" });
        }
    }
}
