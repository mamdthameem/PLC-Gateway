using PlcApi.Models;

namespace PlcApi.Services;

public interface ITrendsService
{
    /// <summary>
    /// Trend buckets for the dashboard graphs.
    ///
    /// bucket "day"/"month" are served from the plc_daily_trends rollup (cheap at any history
    /// size, so all-time requests are fine — pass null bounds for the full range). bucket "hour"
    /// is computed live from Tier 2 for finer detail and therefore requires both bounds; the
    /// scan is limited to the requested window.
    /// </summary>
    Task<List<DailyTrendDto>> GetTrendsAsync(DateTime? from, DateTime? to, string bucket);
}
