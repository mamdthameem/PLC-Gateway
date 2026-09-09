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
    private readonly ITrendsService _trends;
    private readonly DatabaseService _db;
    private readonly CalculationService _calculation;

    public AdminController(
        IConfiguration config,
        ILogger<AdminController> logger,
        IMachineStatusService machineStatus,
        ILifetimeService lifetime,
        IShotsBreakdownService shots,
        IAmpsService amps,
        ISpareStatusService spares,
        IFilterService filter,
        ITrendsService trends,
        DatabaseService db,
        CalculationService calculation)
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
        _trends = trends;
        _db = db;
        _calculation = calculation;
    }

    // DB timestamps are wall-clock local time (TIMESTAMP without tz, written via DateTime.Now).
    // The admin API emits UTC ISO 8601, so convert on the way out; a value already marked Utc
    // (DTO fallbacks use DateTime.UtcNow) must not be shifted twice.
    private static DateTime ToUtc(DateTime ts) =>
        ts.Kind == DateTimeKind.Utc ? ts : DateTime.SpecifyKind(ts, DateTimeKind.Local).ToUniversalTime();

    private static DateTime? ToUtc(DateTime? ts) => ts.HasValue ? ToUtc(ts.Value) : null;

    // Shared by Live()'s "latest completed" mirror and POST /api/admin/filter's synchronous
    // result — same shape either way, since both are just "the state of one calculation_requests
    // row plus its child tables."
    private async Task<object> BuildSection2Async(
        int requestId, string filterBy, DateTime filterStart, DateTime filterEnd,
        string? periodLabel, int? filterCycleFrom, int? filterCycleTo, string? filterMetalName,
        DateTime? processedAt)
    {
        var results     = await _filter.GetResultsAsync(requestId);
        var cycles      = await _filter.GetCycleDataAsync(requestId);
        var metals      = await _filter.GetMetalProductionAsync(requestId);

        return new
        {
            requestId,
            filterBy,
            filterStart     = ToUtc(filterStart),
            filterEnd       = ToUtc(filterEnd),
            periodLabel,
            filterCycleFrom,
            filterCycleTo,
            filterMetalName,
            processedAt = ToUtc(processedAt),
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
                energyKwh      = c.EnergyKwh
            }),
            // BREAKING (see CONTRACT-admin-api.md): section2.shotsBreakdown was removed. The shots
            // breakdown is Section 1 only now — it does not respond to a filter. Read the
            // machine-wide series from /api/admin/live's own shotsBreakdown instead.
            metals = metals.Select(m => new
            {
                metalName    = m.MetalName,
                productionKg = m.ProductionKg
            })
        };
    }

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
            // "Latest" includes requests the cloud itself submitted via POST /api/admin/filter —
            // there is one calculation_requests table, shared by both callers.
            object? section2 = null;
            var latest = await _filter.GetLatestCompletedAsync();
            if (latest is not null)
            {
                section2 = await BuildSection2Async(
                    latest.RequestId, latest.FilterBy, latest.FilterStart, latest.FilterEnd,
                    latest.PeriodLabel, latest.FilterCycleFrom, latest.FilterCycleTo,
                    latest.FilterMetalName, latest.ProcessedAt);
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

    // Whole-history graph data for the 4 graphable Section 1 lifetime parameters
    // (machine_utility_pct, production_qty_kg, energy_kwh_total, energy_per_casting_kwh_kg).
    // Same query params and rollup logic as the local dashboard's /api/trends — this just puts
    // it behind AdminGuardMiddleware instead of JWT so the cloud can reach it. A separate
    // endpoint from Live() on purpose: this data changes at most once a minute (AggregationService
    // cadence), so it doesn't belong on Live()'s poll cadence — call this once per dashboard load.
    [HttpGet("trends")]
    public async Task<IActionResult> Trends(
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
            // Storage is gateway-local wall time — same conversion TrendsController applies.
            var data = await _trends.GetTrendsAsync(start?.LocalDateTime, end?.LocalDateTime, bucket);
            return Ok(data.Select(d => new
            {
                day                = ToUtc(d.Day),
                machineOnSec       = d.MachineOnSec,
                blastOnSec         = d.BlastOnSec,
                utilityPct         = d.UtilityPct,
                cycleCount         = d.CycleCount,
                productionKg       = d.ProductionKg,
                tonnageEnd         = d.TonnageEnd,
                energyKwh          = d.EnergyKwh,
                efficiencyKwhPerKg = d.EfficiencyKwhPerKg
            }));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/trends failed");
            return StatusCode(500, new { error = "trends query failed" });
        }
    }

    // Synchronous, cloud-triggered filtered calculation — no calculation_requests polling wait.
    // Computes inline with the same CalculationService.ComputeFilteredParametersAsync the 5-second
    // background poller uses, so there is exactly one implementation of the math either way.
    //
    // The request row is inserted already claimed as 'processing' (see
    // DatabaseService.InsertClaimedRequestAsync) specifically so FilteredCalculationService's
    // poller — which only picks up 'pending' rows — can never also grab this one and double-write
    // plc_filtered_cycle_data / plc_filtered_metal_production for the same request_id.
    //
    // Side effect worth knowing: this reuses the same calculation_requests table the local
    // dashboard's async flow uses, so a cloud-triggered filter becomes the new "latest completed"
    // row and is what Live().section2 mirrors afterward, until a different filter is computed by
    // either side. That is intentional — one shared source of truth — not an oversight.
    [HttpPost("filter")]
    public async Task<IActionResult> Filter([FromBody] FilterRequestInput input)
    {
        if (input.FilterBy is not ("time" or "cycle" or "metal"))
            return BadRequest(new { error = "filterBy must be 'time', 'cycle' or 'metal'" });

        if (input.FilterBy == "time" && input.FilterStart >= input.FilterEnd)
            return BadRequest(new { error = "filterStart must be before filterEnd" });

        if (input.FilterBy == "cycle" &&
            (!input.FilterCycleFrom.HasValue || !input.FilterCycleTo.HasValue ||
             input.FilterCycleFrom > input.FilterCycleTo))
            return BadRequest(new { error = "filterCycleFrom and filterCycleTo are required, and From must be <= To" });

        if (input.FilterBy == "metal" && string.IsNullOrWhiteSpace(input.FilterMetalName))
            return BadRequest(new { error = "filterMetalName is required" });

        // selectedParameters is optional — omitting it means "all of them", which is what every
        // caller written before the per-parameter toggles existed sends. Reject unknown keys
        // rather than silently dropping them, so a typo surfaces instead of quietly shrinking the
        // response. Note machine_utility_pct is accepted here but skipped for a metal/item filter:
        // it is machine-level and not attributable to one casting item (see README).
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

        // filter_start/filter_end are NOT NULL — NOW() is the same placeholder the local
        // dashboard sends for cycle/metal filters, which have no meaningful time axis.
        DateTime filterStart = input.FilterBy == "time" ? input.FilterStart : DateTime.Now;
        DateTime filterEnd   = input.FilterBy == "time" ? input.FilterEnd   : DateTime.Now;

        int requestId;
        try
        {
            requestId = await _db.InsertClaimedRequestAsync(
                filterStart, filterEnd, input.PeriodLabel, input.FilterBy,
                input.FilterCycleFrom, input.FilterCycleTo, input.FilterMetalName,
                input.SelectedParameters);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/filter: failed to submit request");
            return StatusCode(500, new { error = "failed to submit filter request" });
        }

        try
        {
            await _calculation.ComputeFilteredParametersAsync(
                requestId, filterStart, filterEnd, input.FilterBy,
                input.FilterCycleFrom, input.FilterCycleTo, input.FilterMetalName,
                input.SelectedParameters);
            await _db.SetRequestStatusAsync(requestId, "done");
        }
        catch (Exception ex)
        {
            await _db.SetRequestStatusAsync(requestId, "error");
            _logger.LogError(ex, "admin/filter: computation failed for request {id}", requestId);
            return StatusCode(500, new { error = "filter computation failed", requestId });
        }

        try
        {
            var section2 = await BuildSection2Async(
                requestId, input.FilterBy, filterStart, filterEnd,
                input.PeriodLabel, input.FilterCycleFrom, input.FilterCycleTo,
                input.FilterMetalName, DateTime.UtcNow);
            return Ok(section2);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/filter: failed to read back results for request {id}", requestId);
            return StatusCode(500, new { error = "filter computed but result read-back failed", requestId });
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
