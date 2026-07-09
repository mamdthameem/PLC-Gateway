using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

public class CovDetectionService
{
    private readonly ILogger<CovDetectionService> _logger;
    private readonly double _covDeadbandPercent;

    public CovDetectionService(
        ILogger<CovDetectionService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _covDeadbandPercent = configuration.GetValue<double>("DataCollection:CovDeadbandPercent", 2.0);
    }

    // Returns a storage reason if the value should be written to Tier 2, null otherwise.
    // previousValue is the last value known from the in-memory scan cache.
    // "INITIAL" → first ever record for this tag (previousValue is null/empty)
    // "COV"     → numeric change beyond the tag's deadband
    // "STATE_CHANGE" → BOOL flipped
    // "VALUE_CHANGE" → STRING changed
    //
    // covMode selects the numeric deadband style:
    //   "relative" (default) → change ≥ CovDeadbandPercent % of the last value (for analogs)
    //   "absolute"           → |change| ≥ covDeadband in engineering units (for accumulators
    //                          such as tonnage / run-hours, whose relative deadband would grow
    //                          without bound as the running total climbs).
    public string? ShouldStoreInHistorical(
        string currentValue,
        string dataType,
        string? previousValue,
        string covMode = "relative",
        double? covDeadband = null)
    {
        if (string.IsNullOrEmpty(previousValue))
            return "INITIAL";

        return dataType.ToUpper() switch
        {
            "BOOL" or "BOOLEAN"             => ShouldStoreBool(currentValue, previousValue),
            "STRING" or "CHAR" or "VARCHAR" => ShouldStoreString(currentValue, previousValue),
            _                               => ShouldStoreNumeric(currentValue, previousValue, covMode, covDeadband)
        };
    }

    private string? ShouldStoreNumeric(string currentValue, string? lastValue, string covMode, double? covDeadband)
    {
        if (string.IsNullOrEmpty(currentValue) || string.IsNullOrEmpty(lastValue)) return "INITIAL";

        try
        {
            double current = Convert.ToDouble(currentValue, System.Globalization.CultureInfo.InvariantCulture);
            double last    = Convert.ToDouble(lastValue,    System.Globalization.CultureInfo.InvariantCulture);

            if (string.Equals(covMode, "absolute", StringComparison.OrdinalIgnoreCase) && covDeadband.HasValue)
            {
                if (Math.Abs(current - last) >= covDeadband.Value) return "COV";
                return null;
            }

            // Relative deadband (default).
            if (last != 0)
            {
                double changePercent = Math.Abs((current - last) / last) * 100;
                if (changePercent >= _covDeadbandPercent) return "COV";
            }
            else if (current != 0)
            {
                return "COV";
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing numeric value for COV check");
            return "COV";
        }
    }

    private string? ShouldStoreBool(string currentValue, string? lastValue)
    {
        if (string.IsNullOrEmpty(currentValue) || string.IsNullOrEmpty(lastValue)) return "INITIAL";

        try
        {
            bool current = currentValue == "1" || currentValue.ToLower() == "true";
            bool last    = lastValue    == "1" || lastValue.ToLower()    == "true";
            return current != last ? "STATE_CHANGE" : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing BOOL value for state-change check");
            return "COV";
        }
    }

    private string? ShouldStoreString(string currentValue, string? lastValue)
    {
        if (string.IsNullOrEmpty(currentValue) || string.IsNullOrEmpty(lastValue)) return "INITIAL";
        return !currentValue.Equals(lastValue, StringComparison.Ordinal) ? "VALUE_CHANGE" : null;
    }
}
