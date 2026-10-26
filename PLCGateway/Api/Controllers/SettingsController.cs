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

    public SettingsController(ImpellerSelection impellers)
    {
        _impellers = impellers;
    }

    /// <summary>
    /// The impellers every panel shows and every calculation counts, the ones the tag file has, and
    /// the most a machine can have. Read-only here: the selection is changed only from the cloud
    /// (PUT /api/admin/settings/impellers, decided 2026-10-13).
    /// </summary>
    [HttpGet("impellers")]
    public IActionResult GetImpellers() => Ok(new ImpellerSelectionDto
    {
        Selected     = _impellers.Snapshot(),
        Available    = _impellers.Available(),
        MaxImpellers = ImpellerSelection.MaxImpellers
    });
}
