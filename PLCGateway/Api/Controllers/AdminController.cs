using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace PlcApi.Controllers;

// Cloud pull endpoints (Part E4). Not JWT-protected — access is gated by AdminGuardMiddleware
// (IP allowlist + X-Api-Key). Reads local PostgreSQL only; returns JSON.
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly string _connectionString;
    private readonly ILogger<AdminController> _logger;

    public AdminController(IConfiguration config, ILogger<AdminController> logger)
    {
        _connectionString = config.GetValue<string>("PostgreSQL:ConnectionString")
            ?? config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("Connection string is required.");
        _logger = logger;
    }

    // Live snapshot: PLC link state, lifetime parameters, and active spare alerts.
    [HttpGet("live")]
    public async Task<IActionResult> Live()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            bool plcConnected = false; DateTime? lastScan = null, changedAt = null;
            await using (var cmd = new NpgsqlCommand("SELECT plc_connected, last_scan_at, changed_at FROM gateway_status WHERE id = 1", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                if (await r.ReadAsync())
                {
                    plcConnected = r.GetBoolean(0);
                    lastScan  = r.IsDBNull(1) ? null : r.GetDateTime(1);
                    changedAt = r.IsDBNull(2) ? null : r.GetDateTime(2);
                }

            var lifetime = new List<object>();
            await using (var cmd = new NpgsqlCommand("SELECT parameter_name, value, updated_at FROM plc_lifetime_parameters ORDER BY parameter_name", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    lifetime.Add(new { parameter = r.GetString(0), value = r.IsDBNull(1) ? null : (decimal?)r.GetDecimal(1), updatedAt = r.GetDateTime(2) });

            var alerts = new List<object>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT impeller_num, spare_index, spare_name, current_run_hours, threshold_hours FROM plc_spare_status WHERE trigger_active = TRUE AND threshold_hours > 0 ORDER BY impeller_num, spare_index", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    alerts.Add(new { impeller = r.GetInt32(0), spareIndex = r.GetInt32(1), spareName = r.GetString(2), runHours = r.GetDecimal(3), thresholdHours = r.GetDecimal(4) });

            return Ok(new { plcConnected, lastScanAt = lastScan, changedAt, lifetime, spareAlerts = alerts });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/live failed");
            return StatusCode(500, new { error = "live snapshot failed" });
        }
    }

    // Historical time-series for one metric (tag), row-capped and paged.
    [HttpGet("history")]
    public async Task<IActionResult> History(
        [FromQuery] string metric,
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] int limit = 5000,
        [FromQuery] int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(metric))
            return BadRequest(new { error = "metric is required" });
        limit = Math.Clamp(limit, 1, 20000);
        offset = Math.Max(0, offset);

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            string sql = $@"
                SELECT {SqlExpressions.TypedValue()}, timestamp, storage_reason
                FROM plc_historical_data
                WHERE parameter_name = @metric AND timestamp >= @from AND timestamp <= @to
                ORDER BY timestamp ASC
                LIMIT @limit OFFSET @offset";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("metric", metric);
            cmd.Parameters.AddWithValue("from", from);
            cmd.Parameters.AddWithValue("to", to);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);

            var points = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                points.Add(new { value = r.IsDBNull(0) ? null : r.GetString(0), timestamp = r.GetDateTime(1), reason = r.GetString(2) });

            return Ok(new { metric, from, to, count = points.Count, limit, offset, points });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/history failed for {metric}", metric);
            return StatusCode(500, new { error = "history query failed" });
        }
    }
}
