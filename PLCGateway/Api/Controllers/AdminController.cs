using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PlcApi.Models;
using PlcApi.Services;

namespace PlcApi.Controllers;

// Cloud pull endpoints (Part E4). Not JWT-protected — access is gated by AdminGuardMiddleware
// (IP allowlist + X-Api-Key). Reads local PostgreSQL only; returns JSON.
//
// /live mirrors the FULL local dashboard by reusing the same API services the dashboard binds
// to (single source of truth — the cloud must never recompute). Response contract:
// CONTRACT-admin-api.md at the repo root; keep it in sync with any change here.
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly string _connectionString;
    private readonly ILogger<AdminController> _logger;
    private readonly IMachineStatusService _machineStatus;
    private readonly ILifetimeService _lifetime;
    private readonly IShotsBreakdownService _shots;
    private readonly IAmpsService _amps;
    private readonly ISpareStatusService _spares;
    private readonly IFilterService _filter;

    public AdminController(
        IConfiguration config,
        ILogger<AdminController> logger,
        IMachineStatusService machineStatus,
        ILifetimeService lifetime,
        IShotsBreakdownService shots,
        IAmpsService amps,
        ISpareStatusService spares,
        IFilterService filter)
    {
        _connectionString = config.GetValue<string>("PostgreSQL:ConnectionString")
            ?? config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("Connection string is required.");
        _logger = logger;
        _machineStatus = machineStatus;
        _lifetime = lifetime;
        _shots = shots;
        _amps = amps;
        _spares = spares;
        _filter = filter;
    }

    // DB timestamps are wall-clock local time (TIMESTAMP without tz, written via DateTime.Now).
    // The admin API emits UTC ISO 8601, so convert on the way out; a value already marked Utc
    // (DTO fallbacks use DateTime.UtcNow) must not be shifted twice.
    private static DateTime ToUtc(DateTime ts) =>
        ts.Kind == DateTimeKind.Utc ? ts : DateTime.SpecifyKind(ts, DateTimeKind.Local).ToUniversalTime();

    private static DateTime? ToUtc(DateTime? ts) => ts.HasValue ? ToUtc(ts.Value) : null;

    private static object ToSpareJson(SpareStatusDto s) => new
    {
        impellerNum     = s.ImpellerNum,
        spareIndex      = s.SpareIndex,
        spareName       = s.SpareName,
        thresholdHours  = s.ThresholdHours,
        currentRunHours = s.CurrentRunHours,
        triggerActive   = s.TriggerActive,
        lastReplacedAt  = ToUtc(s.LastReplacedAt),
        lastUpdatedAt   = ToUtc(s.LastUpdatedAt)
    };

    // Live snapshot: everything the local Section 1 dashboard renders, plus the latest
    // completed Section 2 (filtered) view. All values are the dashboard services' outputs.
    [HttpGet("live")]
    public async Task<IActionResult> Live()
    {
        try
        {
            // PLC link state (gateway_status row — includes changed_at, which the tile DTO lacks)
            bool plcConnected = false; DateTime? lastScan = null, changedAt = null;
            await using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "SELECT plc_connected, last_scan_at, changed_at FROM gateway_status WHERE id = 1", conn);
                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    plcConnected = r.GetBoolean(0);
                    lastScan  = r.IsDBNull(1) ? null : r.GetDateTime(1);
                    changedAt = r.IsDBNull(2) ? null : r.GetDateTime(2);
                }
            }

            // Same service outputs the dashboard panels bind to.
            var status    = await _machineStatus.GetStatusAsync();
            var lifetime  = await _lifetime.GetAllAsync();
            var shots     = await _shots.GetAllAsync();
            var amps      = await _amps.GetImpellerAmpsAsync();
            var spareGrid = await _spares.GetAllAsync();
            var alerts    = await _spares.GetAlertsAsync();

            // Latest completed Section 2 request, mirrored with the same read paths the
            // dashboard's FilterResultsView uses. Null until the first filter completes.
            object? section2 = null;
            var latest = await _filter.GetLatestCompletedAsync();
            if (latest is not null)
            {
                var results     = await _filter.GetResultsAsync(latest.RequestId);
                var cycles      = await _filter.GetCycleDataAsync(latest.RequestId);
                var filterShots = await _filter.GetShotsBreakdownAsync(latest.RequestId);

                section2 = new
                {
                    requestId       = latest.RequestId,
                    filterBy        = latest.FilterBy,
                    filterStart     = ToUtc(latest.FilterStart),
                    filterEnd       = ToUtc(latest.FilterEnd),
                    periodLabel     = latest.PeriodLabel,
                    filterCycleFrom = latest.FilterCycleFrom,
                    filterCycleTo   = latest.FilterCycleTo,
                    filterMetalName = latest.FilterMetalName,
                    processedAt     = ToUtc(latest.ProcessedAt),
                    results = results.Select(p => new
                    {
                        parameterName = p.ParameterName,
                        value         = p.Value
                    }),
                    cycles = cycles.Select(c => new
                    {
                        cycleNumber    = c.CycleNumber,
                        blastStart     = ToUtc(c.BlastStart),
                        blastEnd       = ToUtc(c.BlastEnd),
                        metal1Name     = c.Metal1Name,
                        metal1WeightKg = c.Metal1WeightKg,
                        metal2Name     = c.Metal2Name,
                        metal2WeightKg = c.Metal2WeightKg,
                        metal3Name     = c.Metal3Name,
                        metal3WeightKg = c.Metal3WeightKg,
                        metal4Name     = c.Metal4Name,
                        metal4WeightKg = c.Metal4WeightKg,
                        productionKg   = c.ProductionKg,
                        energyKwh      = c.EnergyKwh,
                        shotsUsage     = c.ShotsUsage
                    }),
                    shotsBreakdown = filterShots.Select(s => new
                    {
                        refillTimestamp = ToUtc(s.RefillTimestamp),
                        blastCount      = s.BlastCount
                    })
                };
            }

            return Ok(new
            {
                generatedAtUtc = DateTime.UtcNow,
                plcConnected,
                lastScanAt = ToUtc(lastScan),
                changedAt  = ToUtc(changedAt),
                machineStatus = status is null ? null : new
                {
                    value       = status.Value,
                    // Same rule the local tile applies: any non-"0" byte means running.
                    running     = status.Value != "0",
                    isStale     = status.IsStale,
                    lastUpdated = ToUtc(status.LastUpdated)
                },
                lifetime = lifetime.Select(p => new
                {
                    parameterName = p.ParameterName,
                    value         = p.Value,
                    updatedAt     = ToUtc(p.UpdatedAt)
                }),
                shotsBreakdown = shots.Select(s => new
                {
                    refillTimestamp = ToUtc(s.RefillTimestamp),
                    blastCount      = s.BlastCount
                }),
                amps = amps.Select(a => new
                {
                    parameterName = a.ParameterName,
                    value         = a.Value,
                    lastUpdated   = ToUtc(a.LastUpdated)
                }),
                spareGrid   = spareGrid.Select(ToSpareJson),
                spareAlerts = alerts.Select(ToSpareJson),
                section2
            });
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
