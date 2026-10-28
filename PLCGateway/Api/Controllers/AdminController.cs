using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PlcApi.Hosting;
using PlcApi.Models;
using PlcApi.Services;

namespace PlcApi.Controllers;

// Cloud pull endpoints (Part E4). Not JWT-protected — access is gated by AdminGuardMiddleware
// (X-Api-Key only; no IP allowlist). Reads local PostgreSQL only; returns JSON.
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
    private readonly ImpellerSelection _impellers;
    private readonly IConfiguration _config;
    private readonly PlcService _plc;
    private readonly SiteProfileState _siteProfile;

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
        CalculationService calculation,
        ImpellerSelection impellers,
        PlcService plc,
        SiteProfileState siteProfile)
    {
        _config = config;
        _plc = plc;
        _siteProfile = siteProfile;
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? config.GetValue<string>("PostgreSQL:ConnectionString")
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
        _impellers = impellers;
    }

    // DB timestamps are wall-clock local time (TIMESTAMP without tz, written via DateTime.Now).
    // The admin API emits UTC ISO 8601, so convert on the way out; a value already marked Utc
    // (DTO fallbacks use DateTime.UtcNow) must not be shifted twice.
    private static DateTime ToUtc(DateTime ts) =>
        ts.Kind == DateTimeKind.Utc ? ts : DateTime.SpecifyKind(ts, DateTimeKind.Local).ToUniversalTime();

    private static DateTime? ToUtc(DateTime? ts) => ts.HasValue ? ToUtc(ts.Value) : null;

    // Shared by POST /api/admin/filter's synchronous result and GET /api/admin/filter/{id} - same
    // shape either way, since both are just "the state of one calculation_requests row plus its
    // child tables."
    private async Task<object> BuildSection2Async(
        int requestId, string filterBy, DateTime filterStart, DateTime filterEnd,
        string? periodLabel, int? filterCycleFrom, int? filterCycleTo, string? filterMetalName,
        DateTime? processedAt, string[]? selectedParameters)
    {
        var results     = await _filter.GetResultsAsync(requestId);
        var cycles      = await _filter.GetCycleDataAsync(requestId);
        var metals      = await _filter.GetMetalProductionAsync(requestId);
        var amps        = await _filter.GetAmpsDataAsync(requestId);

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
            // What the request asked for; null = everything. Tells the cloud which tables and
            // panels exist for a request it did not submit itself (the local dashboard decides the
            // same way).
            selectedParameters = selectedParameters is { Length: > 0 } ? selectedParameters : null,
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
                durationSec    = Math.Round(c.DurationSec, 1),
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
            }),
            // The "Impeller Current (Filtered)" tiles: one duration-weighted average per selected
            // impeller. The per-cycle points behind each tile's chart are served separately by
            // GET filter/{id}/amps — about 14 000 of them over all history, too many for every
            // Live() poll. Empty when impeller_current was not selected.
            amps = amps.Select(a => new
            {
                impellerNumber = a.ImpellerNumber,
                overallAvgAmps = a.OverallAvgAmps
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

    // Live snapshot: everything the local Section 1 dashboard renders. All values are the dashboard
    // services' outputs. No Section 2 here: filtered results belong to the viewer who asked for them.
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
            var lastCycle = await _amps.GetLastCycleAveragesAsync();
            var spareGrid = await _spares.GetAllAsync();
            var alerts    = await _spares.GetAlertsAsync();

            // BREAKING (2026-10-12, see CONTRACT-admin-api.md): /live no longer carries section2,
            // the "latest completed filter from anyone". Every viewer polling /live was handed
            // whatever filter someone else had just applied. A viewer now sees only its own result:
            // the response of its own POST /api/admin/filter, or GET /api/admin/filter/{id} later.

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
                    refillTimestamp        = ToUtc(s.RefillTimestamp),
                    // The refill that OPENED the interval (refillTimestamp closed it). Null only
                    // for an interval with no earlier refill on record.
                    intervalStartTimestamp = ToUtc(s.IntervalStartTimestamp),
                    blastCount             = s.BlastCount
                }),
                // The site's impeller selection (gateway_settings): amps, spareGrid and spareAlerts
                // hold only these impellers, and every energy figure counts only these. available =
                // the impellers the tag file has tags for, the only ones the cloud may offer.
                impellers = new { selected = _impellers.Snapshot(), available = _impellers.Available() },
                amps = amps.Select(a => new
                {
                    parameterName = a.ParameterName,
                    value         = a.Value,
                    lastUpdated   = ToUtc(a.LastUpdated)
                }),
                // Each selected impeller's average current over the last completed cycle — the
                // "ran at N A" line under an idle tile. An impeller with no sample in that cycle
                // is absent, so match entries to amps by parameterName, never by position.
                ampsLastCycle = lastCycle.Select(a => new
                {
                    parameterName = a.ParameterName,
                    value         = a.Value,
                    lastUpdated   = ToUtc(a.LastUpdated)
                }),
                spareGrid   = spareGrid.Select(ToSpareJson),
                spareAlerts = alerts.Select(ToSpareJson)
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
    //
    // bucket=auto lets the gateway choose, exactly as the local dashboard does: only the gateway
    // knows how much history exists. The choice comes back in X-Trend-Bucket. The default stays
    // "day" so callers written before auto existed are unaffected.
    [HttpGet("trends")]
    public async Task<IActionResult> Trends(
        [FromQuery] DateTimeOffset? start,
        [FromQuery] DateTimeOffset? end,
        [FromQuery] string bucket = "day")
    {
        if (start.HasValue && end.HasValue && start >= end)
            return BadRequest(new { error = "start must be before end" });

        if (bucket is not ("auto" or "hour" or "day" or "month"))
            return BadRequest(new { error = "bucket must be 'auto', 'hour', 'day' or 'month'" });

        if (bucket == "hour" && (!start.HasValue || !end.HasValue))
            return BadRequest(new { error = "hourly trends require both start and end" });

        try
        {
            // Storage is gateway-local wall time — same conversion TrendsController applies.
            var series = await _trends.GetSeriesAsync(start?.LocalDateTime, end?.LocalDateTime, bucket);
            Response.Headers["X-Trend-Bucket"] = series.Bucket;
            return Ok(series.Rows.Select(d => new
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

    // Average current per completed cycle for one impeller, across all recorded cycles — the chart
    // behind a live impeller tile. Same service as the local /api/amps/by-cycle.
    [HttpGet("amps/by-cycle")]
    public async Task<IActionResult> AmpsByCycle([FromQuery] int impeller)
    {
        if (impeller < 1 || impeller > 10)
            return BadRequest(new { error = "impeller must be between 1 and 10" });

        try
        {
            var points = await _amps.GetPerCycleAveragesAsync(impeller);
            return Ok(points.Select(p => new
            {
                cycleNumber = p.CycleNumber,
                blastEnd    = ToUtc(p.BlastEnd),
                avgAmps     = p.AvgAmps
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/amps/by-cycle failed for impeller {imp}", impeller);
            return StatusCode(500, new { error = "per-cycle amps query failed" });
        }
    }

    // One filtered calculation's impeller current: the tile average plus the per-cycle points
    // behind each tile's chart. Same service as the local /api/filter/{id}/amps. Works for any
    // request id - one returned by POST filter, or reopened with GET filter/{id}.
    [HttpGet("filter/{id:int}/amps")]
    public async Task<IActionResult> FilterAmps(int id)
    {
        try
        {
            var data = await _filter.GetAmpsDataAsync(id);
            return Ok(data.Select(a => new
            {
                impellerNumber = a.ImpellerNumber,
                overallAvgAmps = a.OverallAvgAmps,
                cycles = a.Cycles.Select(c => new
                {
                    cycleNumber = c.CycleNumber,
                    blastEnd    = ToUtc(c.BlastEnd),
                    avgAmps     = c.AvgAmps
                })
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/filter/{id}/amps failed", id);
            return StatusCode(500, new { error = "filtered amps query failed" });
        }
    }

    // One saved filtered calculation by id, in the same shape as the POST filter response. For a
    // viewer reopening its own result (e.g. after a page refresh) without recomputing it. 404 when
    // there is no such id; 409 with the status while it is still pending, processing or failed.
    [HttpGet("filter/{id:int}")]
    public async Task<IActionResult> GetFilterResult(int id)
    {
        try
        {
            var r = await _filter.GetRequestAsync(id);
            if (r is null) return NotFound(new { error = $"request {id} not found" });
            if (r.Status != "done") return Conflict(new { error = "request is not complete", requestId = id, status = r.Status });

            return Ok(await BuildSection2Async(
                r.RequestId, r.FilterBy, r.FilterStart, r.FilterEnd, r.PeriodLabel,
                r.FilterCycleFrom, r.FilterCycleTo, r.FilterMetalName, r.ProcessedAt, r.SelectedParameters));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/filter/{id} failed", id);
            return StatusCode(500, new { error = "filter result query failed" });
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
    // It reuses the calculation_requests table the local dashboard's async flow uses (the DB is the
    // Section 2 message bus), but nothing reads "the latest" request any more: the caller gets its
    // own result in the response and can fetch it again by id.
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
                input.FilterMetalName, DateTime.UtcNow, input.SelectedParameters);
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

    // PUT /api/admin/settings/impellers — the ONLY way to change which impellers the site includes
    // (decided 2026-10-13: it is chosen from the cloud, never on the gateway dashboard). Same save
    // as before: gateway_settings, every cycle's energy re-read from Tier 2, the daily rollup and the
    // lifetime totals, in one transaction, finished BEFORE the 200 — so the response means the new
    // figures are already in place. Body: { "selected": [1, 2, 3] }.
    [HttpPut("settings/impellers")]
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

        // Only impellers the tag file has: a machine with one impeller has nothing to show for 2–10.
        var missing = selected.Except(_impellers.Available()).ToArray();
        if (missing.Length > 0)
            return BadRequest(new
            {
                error = $"This site's tag file has no tags for impeller {string.Join(", ", missing)}. " +
                        $"It has: {(_impellers.Available() is { Length: > 0 } a ? string.Join(", ", a) : "none")}."
            });

        try
        {
            await _calculation.ApplyImpellerSelectionAsync(selected, "cloud");
            return Ok(new ImpellerSelectionDto
            {
                Selected     = _impellers.Snapshot(),
                Available    = _impellers.Available(),
                MaxImpellers = ImpellerSelection.MaxImpellers
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/settings/impellers failed");
            return StatusCode(500, new { error = "Failed to save the impeller selection" });
        }
    }

    // GET /api/admin/tags — the cloud's Tag check page (office only, 2026-10-26): every tag in this
    // site's tag file, in the file's order, with its latest reading. value = null means the gateway
    // has never stored a reading for that address (a write-only tag, or one outside what the PLC
    // returns). Served only behind the admin API key: the tag list is private to the office.
    [HttpGet("tags")]
    public async Task<IActionResult> Tags()
    {
        try
        {
            var readings = await _db.GetCurrentValuesByAddressAsync();
            var (plcConnected, lastScan) = await ReadPlcLinkAsync();
            var tags = _config.GetSection("Tags").GetChildren().Select(t =>
            {
                string address = t["Address"] ?? "";
                bool read = readings.TryGetValue(address, out var r);
                return new
                {
                    name        = t["Name"] ?? "",
                    address,
                    dataType    = t["DataType"] ?? "",
                    mode        = t["Mode"] ?? "",
                    value       = read ? r.Value : null,
                    lastUpdated = read ? ToUtc(r.LastUpdated) : null,
                    isStale     = read && r.IsStale,
                };
            }).ToList();

            return Ok(new
            {
                generatedAtUtc = DateTime.UtcNow,
                plcConnected,
                lastScanAt = ToUtc(lastScan),
                plc = PlcView(_plc.Endpoint),
                tagFileVersion = _siteProfile.Version,
                tags,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/tags failed");
            return StatusCode(500, new { error = "tag check failed" });
        }
    }

    // GET /api/admin/settings/plc — where the gateway reads the PLC, where that address came from, and
    // whether the PLC answers there.
    [HttpGet("settings/plc")]
    public async Task<IActionResult> GetPlcSettings()
    {
        try
        {
            var saved = await _db.GetSavedPlcEndpointAsync();
            var (plcConnected, lastScan) = await ReadPlcLinkAsync();
            return Ok(new
            {
                plc = PlcView(_plc.Endpoint),
                source = saved is null ? "installer" : "cloud",
                connected = _plc.IsConnected && plcConnected,
                lastScanAt = ToUtc(lastScan),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/settings/plc read failed");
            return StatusCode(500, new { error = "Failed to read the PLC settings" });
        }
    }

    // PUT /api/admin/settings/plc — sets the PLC's address from the cloud (2026-10-26). Saved in
    // gateway_settings, where it wins over the settings file from then on, and switched to at once:
    // the scan loop picks it up before its next read. Waits up to ~10 s to say whether the PLC
    // answered there. Body: { "ipAddress": "192.168.1.11", "rack": 0, "slot": 1 }.
    [HttpPut("settings/plc")]
    public async Task<IActionResult> SavePlcSettings([FromBody] PlcSettingsRequest request)
    {
        if (PlcEndpoint.Problem(request.IpAddress, request.Rack, request.Slot) is { } problem)
            return BadRequest(new { error = problem });

        var endpoint = new PlcEndpoint(request.IpAddress!.Trim(), (short)request.Rack, (short)request.Slot);
        try
        {
            await _db.SavePlcEndpointAsync(endpoint, "cloud");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "admin/settings/plc save failed");
            return StatusCode(500, new { error = "Failed to save the PLC address" });
        }
        _plc.RequestEndpoint(endpoint);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !(_plc.Endpoint == endpoint && _plc.IsConnected))
            await Task.Delay(500, HttpContext.RequestAborted);

        return Ok(new
        {
            plc = PlcView(endpoint),
            source = "cloud",
            applied = _plc.Endpoint == endpoint,
            connected = _plc.Endpoint == endpoint && _plc.IsConnected,
        });
    }

    private static object PlcView(PlcEndpoint e) => new { ipAddress = e.IpAddress, rack = e.Rack, slot = e.Slot };

    private async Task<(bool Connected, DateTime? LastScan)> ReadPlcLinkAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT plc_connected, last_scan_at FROM gateway_status WHERE id = 1", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync()
            ? (r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetDateTime(1))
            : (false, null);
    }
}

public class PlcSettingsRequest
{
    public string? IpAddress { get; set; }
    public int Rack { get; set; }
    public int Slot { get; set; } = 1;
}
