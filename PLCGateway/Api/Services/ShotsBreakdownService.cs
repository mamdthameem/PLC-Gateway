using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public class ShotsBreakdownService : IShotsBreakdownService
{
    private const string RefillTag = "Refil shots weight";

    // Mirrors CalculationService.RefillChangeReasons: the reasons that count as an actual refill.
    private static readonly string[] ChangeReasons = { "COV", "VALUE_CHANGE", "STATE_CHANGE" };

    private readonly string _connectionString;
    private readonly ILogger<ShotsBreakdownService> _logger;

    public ShotsBreakdownService(IConfiguration config, ILogger<ShotsBreakdownService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<List<ShotsBreakdownDto>> GetAllAsync()
    {
        var results = new List<ShotsBreakdownDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // interval_start is the PREVIOUS refill. For all rows but the first that is the previous
            // row; the first row's opener never got a row of its own (CalculationService.FoldRefillAsync
            // seeds PrevRefillChangeTs on the first refill and only emits from the second onwards), so
            // it is looked up from the Tier 2 refill events. COALESCE short-circuits in Postgres, so
            // the subquery runs for the first row only.
            const string sql = @"
                WITH b AS (
                    SELECT refill_timestamp, blast_count,
                           LAG(refill_timestamp) OVER (ORDER BY refill_timestamp) AS prev_row
                    FROM plc_shots_breakdown
                )
                SELECT b.refill_timestamp,
                       COALESCE(
                           b.prev_row,
                           (SELECT MAX(h.timestamp)
                            FROM plc_historical_data h
                            WHERE h.parameter_name = @refill_tag
                              AND h.storage_reason = ANY(@change_reasons)
                              AND h.timestamp < b.refill_timestamp)
                       ) AS interval_start,
                       b.blast_count
                FROM b
                ORDER BY b.refill_timestamp ASC;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("refill_tag", RefillTag);
            // Must match CalculationService.RefillChangeReasons — an INITIAL snapshot is not a refill.
            cmd.Parameters.AddWithValue("change_reasons", ChangeReasons);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                results.Add(new ShotsBreakdownDto
                {
                    RefillTimestamp        = reader.GetDateTime(0),
                    IntervalStartTimestamp = reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                    BlastCount             = reader.GetInt32(2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read plc_shots_breakdown");
            throw;
        }
        return results;
    }
}
