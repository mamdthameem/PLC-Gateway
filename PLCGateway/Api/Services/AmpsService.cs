using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public class AmpsService : IAmpsService
{
    private readonly string _connectionString;
    private readonly ILogger<AmpsService> _logger;

    private static readonly string[] ImpellerNames =
    [
        "Current_imp_1","Current_imp_2","Current_imp_3","Current_imp_4","Current_imp_5",
        "Current_imp_6","Current_imp_7","Current_imp_8","Current_imp_9","Current_imp_10"
    ];

    public AmpsService(IConfiguration config, ILogger<AmpsService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<List<AmpReadingDto>> GetImpellerAmpsAsync()
    {
        var results = new List<AmpReadingDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            string sql = $@"
                SELECT parameter_name, {SqlExpressions.TypedValue()}, last_updated
                FROM plc_current_values
                WHERE parameter_name = ANY(@names)
                -- Sort on the trailing NUMBER, not the text. As text 'Current_imp_10' sorts
                -- immediately after 'Current_imp_1', so the panel listed 1, 10, 2, 3 …
                ORDER BY substring(parameter_name from '[0-9]+$')::int;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("names", ImpellerNames);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                results.Add(new AmpReadingDto
                {
                    ParameterName = reader.GetString(0),
                    Value         = reader.IsDBNull(1) ? "0" : reader.GetValue(1)?.ToString() ?? "0",
                    LastUpdated   = reader.IsDBNull(2) ? DateTime.UtcNow : reader.GetDateTime(2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read impeller amps from plc_current_values");
            throw;
        }
        return results;
    }

    public async Task<List<AmpReadingDto>> GetLastCycleAveragesAsync()
    {
        var results = new List<AmpReadingDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Averaged over the blast window itself, which is why the idle zero written just after
            // blast_end is placed AFTER the boundary by the recorder — it must not drag this down.
            const string sql = @"
                WITH last_cycle AS (
                    SELECT blast_start, blast_end
                    FROM plc_cycles
                    ORDER BY blast_end DESC
                    LIMIT 1
                )
                SELECT h.parameter_name, AVG(h.value_num), MAX(c.blast_end)
                FROM plc_historical_data h, last_cycle c
                WHERE h.parameter_name = ANY(@names)
                  AND h.value_num IS NOT NULL
                  AND h.timestamp >= c.blast_start
                  AND h.timestamp <= c.blast_end
                GROUP BY h.parameter_name
                ORDER BY substring(h.parameter_name from '[0-9]+$')::int;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("names", ImpellerNames);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                results.Add(new AmpReadingDto
                {
                    ParameterName = reader.GetString(0),
                    Value         = reader.IsDBNull(1) ? "0" : Math.Round(reader.GetDouble(1), 2)
                                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    LastUpdated   = reader.IsDBNull(2) ? DateTime.Now : reader.GetDateTime(2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute last-cycle impeller averages");
            throw;
        }
        return results;
    }

    public async Task<List<CycleAmpDto>> GetPerCycleAveragesAsync(int impellerNumber)
    {
        var results = new List<CycleAmpDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // LATERAL rather than a range join: one small index scan per cycle over
            // (parameter_name, timestamp), which measures ~65 ms for all 1 463 cycles. A plain join
            // across the impeller's 178 000 sample rows is far more work for the same answer.
            const string sql = @"
                SELECT c.cycle_number, c.blast_end, a.avg_amps
                FROM plc_cycles c
                CROSS JOIN LATERAL (
                    SELECT AVG(h.value_num) AS avg_amps
                    FROM plc_historical_data h
                    WHERE h.parameter_name = @name
                      AND h.value_num IS NOT NULL
                      AND h.timestamp >= c.blast_start
                      AND h.timestamp <= c.blast_end
                ) a
                ORDER BY c.cycle_number ASC;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name", $"Current_imp_{impellerNumber}");

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new CycleAmpDto
                {
                    CycleNumber = reader.GetInt32(0),
                    BlastEnd    = reader.GetDateTime(1),
                    AvgAmps     = reader.IsDBNull(2) ? null : Math.Round(reader.GetDouble(2), 2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute per-cycle averages for impeller {Imp}", impellerNumber);
            throw;
        }
        return results;
    }
}
