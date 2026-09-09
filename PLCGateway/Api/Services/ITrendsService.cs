using PlcApi.Models;

namespace PlcApi.Services;

/// <summary>
/// A trend series plus the granularity it was actually built at.
///
/// The bucket has to travel back with the rows because "auto" resolves it server-side: the
/// dashboard cannot label an axis "Day" or "Month" from the rows alone without re-deriving the
/// decision, and re-deriving it in the browser is exactly the second implementation the
/// server-side-math rule exists to prevent.
/// </summary>
public sealed record TrendSeries(string Bucket, List<DailyTrendDto> Rows);

public interface ITrendsService
{
    /// <summary>
    /// Trend buckets for the dashboard graphs.
    ///
    /// bucket "day"/"month" are served from the plc_daily_trends rollup (cheap at any history
    /// size, so all-time requests are fine — pass null bounds for the full range). bucket "hour"
    /// is computed live from Tier 2 for finer detail and therefore requires both bounds; the
    /// scan is limited to the requested window. bucket "auto" picks between them from the span of
    /// history actually present, which is the only correct choice for an all-time request.
    ///
    /// Every series is gap-filled: a bucket with no underlying rows comes back as zeros rather
    /// than being omitted, so the caller can plot it on a real time axis.
    /// </summary>
    Task<List<DailyTrendDto>> GetTrendsAsync(DateTime? from, DateTime? to, string bucket);

    /// <summary>Same query, but also reports which bucket "auto" resolved to.</summary>
    Task<TrendSeries> GetSeriesAsync(DateTime? from, DateTime? to, string bucket);
}
