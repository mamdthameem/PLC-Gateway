using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PlcApi.Services;

namespace PlcApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MachineStatusController : ControllerBase
{
    private readonly IMachineStatusService _service;
    private readonly ILogger<MachineStatusController> _logger;

    public MachineStatusController(IMachineStatusService service, ILogger<MachineStatusController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    /// <summary>
    /// Live machine status from plc_current_values (address = DB60.DBB0). Poll every 5 s.
    /// value='0' â†’ Stopped; any other value â†’ Running.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            var status = await _service.GetStatusAsync();
            if (status is null) return NotFound(new { error = "Machine status row not found" });
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch machine status");
            return StatusCode(500, new { error = "Failed to fetch machine status" });
        }
    }
}
