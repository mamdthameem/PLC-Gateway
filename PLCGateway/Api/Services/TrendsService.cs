using Npgsql;
using NpgsqlTypes;
using PlcApi.Models;

namespace PlcApi.Services;

// Single source of truth for every trend series the dashboard graphs plot.
//
// Two paths, same output shape:
//   day / month → read the plc_daily_trends rollup. Cheap at any history size, so the Section 1
//                 all-time graphs are served without ever touching raw Tier 2. That matters
//                 because plc_historical_data is append-forever and carries ~1 M rows/year for
//                 'Blast ON/OFF' + 'Machine status' alone (60 s heartbeats on both).
//   hour        → compute live from Tier 2 for sub-day detail, scanned only over the requested
//                 window. Used by the Section 2 filtered view for short windows.
//
// All arithmetic (on-seconds, utility %, efficiency) happens here rather than in the browser, so
// the dashboard renders values verbatim — the same rule the cloud mirror follows.
public class TrendsService : ITrendsService
{
    private readonly string _connectionString;
    private readonly ILogger<TrendsService> _logger;

    private const string TagBlast   = "Blast ON/OFF";
    private const string TagMachine = "Machine status";
    private const string TagTonnage = "Tonnage";

    // Must match DatabaseService.MaxOnSegmentSeconds so the hourly view and the daily rollup
    // agree about what counts as runtime versus a recording gap.
    private const int MaxOnSegmentSeconds = 300;

    public TrendsService(IConfiguration config, ILogger<TrendsService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    // Reads the pre-aggregated rollup, optionally grouping days up to months.
    //
    // tonnage_end carry-forward uses the classic gaps-and-islands fill: COUNT() ignores NULLs so
    // it only advances on days that actually carry a Tonnage reading, grouping each reading with
    // the NULL days after it. The fill runs across the whole rollup BEFORE the range filter, so a
    // sub-range that opens on a gap still inherits the right value from before it.
    private const string RollupSql = @"
        WITH filled AS (
            SELECT day, machine_on_sec, blast_on_sec, cycle_count,
                   production_kg, energy_kwh, tonnage_end,
                   COUNT(tonnage_end) OVER (ORDER BY day ROWS UNBOUNDED PRECEDING) AS tgrp
            FROM plc_daily_trends
        ),
        carried AS (
            SELECT day, machine_on_sec, blast_on_sec, cycle_count,
                   production_kg, energy_kwh,
                   MAX(tonnage_end) OVER (PARTITION BY tgrp) AS tonnage_end
            FROM filled
        ),
        scoped AS (
            SELECT CASE WHEN @monthly THEN date_trunc('month', day)::date ELSE day END AS bucket,
                   day, machine_on_sec, blast_on_sec, cycle_count,
                   production_kg, energy_kwh, tonnage_end
            FROM carried
            WHERE (@from IS NULL OR day >= @from)
              AND (@to   IS NULL OR day <= @to)
        )
        SELECT bucket::timestamp,
               SUM(machine_on_sec), SUM(blast_on_sec), SUM(cycle_count),
               SUM(production_kg),  SUM(energy_kwh),
               (ARRAY_AGG(tonnage_end ORDER BY day DESC))[1]
        FROM scoped
        GROUP BY bucket
        ORDER BY bucket ASC";

    // Live hourly aggregation over a bounded window. Mirrors the rollup's segment logic exactly
    // (see DatabaseService.UpsertDailyTrendsAsync), including both corrections that matter: each
    // segment is split at hour boundaries so a long segment cannot dump its whole duration into
    // one bucket, and a segment longer than MaxOnSegmentSeconds is treated as a recording gap
    // rather than runtime. DISCONNECT rows are intentionally not filtered out — they carry the
    // forced-OFF that makes an observed disconnect contribute zero on its own.
    private const string HourlySql = @"
        WITH blast_ev AS (
            SELECT timestamp AS ts,
                   COALESCE(value_bool, lower(value) IN ('1','true')) AS is_on,
                   COALESCE(previous_value = '1', lower(previous_value) = 'true', FALSE) AS prev_on,
                   COALESCE(LEAD(timestamp) OVER (ORDER BY timestamp),
                            LEAST(@to, LOCALTIMESTAMP)) AS seg_end
            FROM plc_historical_data
            WHERE parameter_name = @blast_tag
              AND timestamp >= @from - INTERVAL '1 day' AND timestamp < @to
        ),
        mach_ev AS (
            SELECT timestamp AS ts,
                   COALESCE(value_bool, value_num <> 0, (value IS NOT NULL AND value <> '0')) AS is_on,
                   COALESCE(LEAD(timestamp) OVER (ORDER BY timestamp),
                            LEAST(@to, LOCALTIMESTAMP)) AS seg_end
            FROM plc_historical_data
            WHERE parameter_name = @machine_tag
              AND timestamp >= @from - INTERVAL '1 day' AND timestamp < @to
        ),
        blast_sec AS (
            SELECT g AS bucket,
                   SUM(GREATEST(EXTRACT(EPOCH FROM (
                       LEAST(s.capped_end, g + INTERVAL '1 hour') - GREATEST(s.ts, g)
                   )), 0)) AS v
            FROM (
                SELECT ts, LEAST(seg_end, ts + @max_seg_sec * INTERVAL '1 second') AS capped_end
                FROM blast_ev WHERE is_on
            ) s
            CROSS JOIN LATERAL generate_series(
                date_trunc('hour', s.ts),
                date_trunc('hour', s.capped_end - INTERVAL '1 microsecond'),
                INTERVAL '1 hour') g
            WHERE g >= date_trunc('hour', @from)
            GROUP BY 1
        ),
        mach_sec AS (
            SELECT g AS bucket,
                   SUM(GREATEST(EXTRACT(EPOCH FROM (
                       LEAST(s.capped_end, g + INTERVAL '1 hour') - GREATEST(s.ts, g)
                   )), 0)) AS v
            FROM (
                SELECT ts, LEAST(seg_end, ts + @max_seg_sec * INTERVAL '1 second') AS capped_end
                FROM mach_ev WHERE is_on
            ) s
            CROSS JOIN LATERAL generate_series(
                date_trunc('hour', s.ts),
                date_trunc('hour', s.capped_end - INTERVAL '1 microsecond'),
                INTERVAL '1 hour') g
            WHERE g >= date_trunc('hour', @from)
            GROUP BY 1
        ),
        edges AS (
            SELECT date_trunc('hour', ts) AS bucket, COUNT(*) AS v
            FROM blast_ev WHERE is_on AND NOT prev_on AND ts >= @from GROUP BY 1
        ),
        cyc AS (
            SELECT date_trunc('hour', blast_end) AS bucket,
                   SUM(COALESCE(production_kg, 0)) AS prod,
                   SUM(COALESCE(energy_kwh, 0))    AS kwh
            FROM plc_cycles
            WHERE blast_end >= @from AND blast_end < @to
            GROUP BY 1
        ),
        tonn AS (
            SELECT DISTINCT ON (date_trunc('hour', timestamp))
                   date_trunc('hour', timestamp) AS bucket, value_num AS v
            FROM plc_historical_data
            WHERE parameter_name = @tonnage_tag AND value_num IS NOT NULL
              AND timestamp >= @from - INTERVAL '1 day' AND timestamp < @to
            ORDER BY date_trunc('hour', timestamp), timestamp DESC
        ),
        buckets AS (
            SELECT bucket FROM blast_sec
            UNION SELECT bucket FROM mach_sec
            UNION SELECT bucket FROM edges
            UNION SELECT bucket FROM cyc
            UNION SELECT bucket FROM tonn
        ),
        joined AS (
            SELECT k.bucket,
                   COALESCE(m.v, 0)    AS machine_on_sec,
                   COALESCE(b.v, 0)    AS blast_on_sec,
                   COALESCE(e.v, 0)    AS cycle_count,
                   COALESCE(c.prod, 0) AS production_kg,
                   COALESCE(c.kwh, 0)  AS energy_kwh,
                   t.v                 AS tonnage_end,
                   COUNT(t.v) OVER (ORDER BY k.bucket ROWS UNBOUNDED PRECEDING) AS tgrp
            FROM buckets k
            LEFT JOIN blast_sec b ON b.bucket = k.bucket
            LEFT JOIN mach_sec  m ON m.bucket = k.bucket
            LEFT JOIN edges     e ON e.bucket = k.bucket
            LEFT JOIN cyc       c ON c.bucket = k.bucket
            LEFT JOIN tonn      t ON t.bucket = k.bucket
        )
        SELECT bucket, machine_on_sec, blast_on_sec, cycle_count,
               production_kg, energy_kwh,
               MAX(tonnage_end) OVER (PARTITION BY tgrp)
        FROM joined
        WHERE bucket >= @from
        ORDER BY bucket ASC";

    public async Task<List<DailyTrendDto>> GetTrendsAsync(DateTime? from, DateTime? to, string bucket)
    {
        bool hourly  = string.Equals(bucket, "hour",  StringComparison.OrdinalIgnoreCase);
        bool monthly = string.Equals(bucket, "month", StringComparison.OrdinalIgnoreCase);

        if (hourly && (from == null || to == null))
            throw new ArgumentException("hourly trends require both start and end bounds.");

        var results = new List<DailyTrendDto>();

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(hourly ? HourlySql : RollupSql, conn);

            if (hourly)
            {
                cmd.Parameters.AddWithValue("from", from!.Value);
                cmd.Parameters.AddWithValue("to",   to!.Value);
                cmd.Parameters.AddWithValue("blast_tag",   TagBlast);
                cmd.Parameters.AddWithValue("machine_tag", TagMachine);
                cmd.Parameters.AddWithValue("tonnage_tag", TagTonnage);
                cmd.Parameters.AddWithValue("max_seg_sec", MaxOnSegmentSeconds);
            }
            else
            {
                cmd.Parameters.Add(new NpgsqlParameter("monthly", NpgsqlDbType.Boolean) { Value = monthly });
                cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date)
                    { Value = from.HasValue ? from.Value.Date : (object)DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date)
                    { Value = to.HasValue ? to.Value.Date : (object)DBNull.Value });
            }

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                double machineOn = reader.IsDBNull(1) ? 0 : Convert.ToDouble(reader.GetValue(1));
                double blastOn   = reader.IsDBNull(2) ? 0 : Convert.ToDouble(reader.GetValue(2));
                double prod      = reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4));
                double kwh       = reader.IsDBNull(5) ? 0 : Convert.ToDouble(reader.GetValue(5));

                // Ratios are rebuilt from the summed seconds/kg, never averaged from per-day
                // percentages — averaging those would weight a 20-minute day like a full one and
                // drift away from the Section 1 scalar.
                double utility = machineOn > 0 ? Math.Min(blastOn / machineOn * 100.0, 100.0) : 0;
                double eff     = prod > 0 ? kwh / prod : 0;

                results.Add(new DailyTrendDto
                {
                    Day                = reader.GetDateTime(0),
                    MachineOnSec       = Math.Round(machineOn, 1),
                    BlastOnSec         = Math.Round(blastOn, 1),
                    UtilityPct         = Math.Round(utility, 2),
                    CycleCount         = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3)),
                    ProductionKg       = Math.Round(prod, 2),
                    EnergyKwh          = Math.Round(kwh, 3),
                    EfficiencyKwhPerKg = Math.Round(eff, 4),
                    TonnageEnd         = reader.IsDBNull(6) ? null : Math.Round(Convert.ToDouble(reader.GetValue(6)), 2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read trends (bucket={Bucket})", bucket);
            throw;
        }

        return results;
    }
}
