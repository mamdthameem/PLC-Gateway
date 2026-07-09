using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PlcApi.Services;

namespace PlcApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ShotsBreakdownController : ControllerBase
{
    private readonly IShotsBreakdownService _service;
    private readonly ILogger<ShotsBreakdownController> _logger;

    public ShotsBreakdownController(IShotsBreakdownService service, ILogger<ShotsBreakdownController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    /// <summary>
    /// Returns all rows from plc_shots_breakdown ordered by refill_timestamp ASC.
    /// Poll every 60 seconds â€” same cadence as plc_lifetime_parameters.
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
            _logger.LogError(ex, "Failed to fetch shots breakdown");
            return StatusCode(500, new { error = "Failed to fetch shots breakdown" });
        }
    }
}
