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
    // previousValue must be read from Tier 1 BEFORE the Tier 1 upsert.
    // "INITIAL" → first ever record for this tag (previousValue is null/empty)
    // "COV"     → numeric change ≥ deadband %
    // "STATE_CHANGE" → BOOL flipped
    // "VALUE_CHANGE" → STRING changed
    public string? ShouldStoreInHistorical(
        string currentValue,
        string dataType,
        string? previousValue)
    {
        if (string.IsNullOrEmpty(previousValue))
            return "INITIAL";

        return dataType.ToUpper() switch
        {
            "BOOL" or "BOOLEAN"             => ShouldStoreBool(currentValue, previousValue),
            "STRING" or "CHAR" or "VARCHAR" => ShouldStoreString(currentValue, previousValue),
            _                               => ShouldStoreNumeric(currentValue, previousValue)
        };
    }

    private string? ShouldStoreNumeric(string currentValue, string? lastValue)
    {
        if (string.IsNullOrEmpty(currentValue) || string.IsNullOrEmpty(lastValue)) return "INITIAL";

        try
        {
            double current = Convert.ToDouble(currentValue, System.Globalization.CultureInfo.InvariantCulture);
            double last    = Convert.ToDouble(lastValue,    System.Globalization.CultureInfo.InvariantCulture);

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
