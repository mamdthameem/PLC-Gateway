using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using PLCGateway.Models;

public class CovDetectionService
{
    private readonly DatabaseService _dbService;
    private readonly ILogger<CovDetectionService> _logger;
    private readonly double _covDeadbandPercent;
    private readonly int _periodicHeartbeatSeconds;

    public CovDetectionService(
        DatabaseService dbService,
        ILogger<CovDetectionService> logger,
        IConfiguration configuration)
    {
        _dbService = dbService;
        _logger = logger;
        _covDeadbandPercent = configuration.GetValue<double>("DataCollection:CovDeadbandPercent", 2.0);
        _periodicHeartbeatSeconds = configuration.GetValue<int>("DataCollection:PeriodicHeartbeatSeconds", 60);
    }

    /// <summary>
    /// Check if value should be stored in Tier 2 (historical) based on COV, state change, or periodic heartbeat.
    /// Returns storage reason if should store, null otherwise.
    /// </summary>
    public async Task<string?> ShouldStoreInHistoricalAsync(string address, string parameterName, object? currentValue, string dataType)
    {
        // Get last value from Tier 1
        var currentValueRecord = await _dbService.GetCurrentValueAsync(address);

        // If no previous value exists, store it (first time)
        if (currentValueRecord == null || string.IsNullOrEmpty(currentValueRecord.Value))
        {
            return "INITIAL";
        }

        object? lastValue = currentValueRecord.Value;
        DateTime? lastStoredHistorical = currentValueRecord.LastStoredHistorical;
        DateTime? lastHeartbeat = currentValueRecord.LastHeartbeat;

        // Type-specific storage logic
        return dataType.ToUpper() switch
        {
            "BOOL" or "BOOLEAN" => ShouldStoreBool(currentValue, lastValue),
            "STRING" or "CHAR" or "VARCHAR" => ShouldStoreString(currentValue, lastValue),
            _ => await ShouldStoreNumericAsync(currentValue, lastValue, lastStoredHistorical, lastHeartbeat)
        };
    }

    /// <summary>
    /// Check if numeric value should be stored (COV with 2% deadband OR periodic heartbeat).
    /// </summary>
    private async Task<string?> ShouldStoreNumericAsync(object? currentValue, object? lastValue, DateTime? lastStoredHistorical, DateTime? lastHeartbeat)
    {
        if (currentValue == null || lastValue == null) return "INITIAL";

        try
        {
            // Try to convert to double for numeric comparison
            double current = Convert.ToDouble(currentValue);
            double last = Convert.ToDouble(lastValue);

            // Check for COV (Change of Value) - 2% deadband
            if (last != 0)
            {
                double changePercent = Math.Abs((current - last) / last) * 100;
                if (changePercent >= _covDeadbandPercent)
                {
                    return "COV";
                }
            }
            else if (current != 0)
            {
                // If last value was 0 and current is not 0, it's a significant change
                return "COV";
            }

            // Check for periodic heartbeat (every 60 seconds)
            DateTime now = DateTime.Now;
            if (lastStoredHistorical == null || (now - lastStoredHistorical.Value).TotalSeconds >= _periodicHeartbeatSeconds)
            {
                return "PERIODIC";
            }

            // Also check last heartbeat timestamp
            if (lastHeartbeat == null || (now - lastHeartbeat.Value).TotalSeconds >= _periodicHeartbeatSeconds)
            {
                return "PERIODIC";
            }

            return null; // Don't store
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking numeric COV for value comparison");
            // If conversion fails, store it anyway to be safe
            return "COV";
        }
    }

    /// <summary>
    /// Check if boolean value should be stored (only on state change).
    /// </summary>
    private string? ShouldStoreBool(object? currentValue, object? lastValue)
    {
        if (currentValue == null || lastValue == null) return "INITIAL";

        try
        {
            bool current = Convert.ToBoolean(currentValue);
            bool last = Convert.ToBoolean(lastValue);

            if (current != last)
            {
                return "STATE_CHANGE";
            }

            return null; // Don't store if state hasn't changed
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking boolean state change");
            return "COV"; // Store if conversion fails
        }
    }

    /// <summary>
    /// Check if string value should be stored (only on value change).
    /// </summary>
    private string? ShouldStoreString(object? currentValue, object? lastValue)
    {
        if (currentValue == null || lastValue == null) return "INITIAL";

        string currentStr = currentValue.ToString() ?? "";
        string lastStr = lastValue.ToString() ?? "";

        if (!currentStr.Equals(lastStr, StringComparison.Ordinal))
        {
            return "VALUE_CHANGE";
        }

        return null; // Don't store if value hasn't changed
    }

    /// <summary>
    /// Get previous value for a tag (for storing in historical data).
    /// </summary>
    public async Task<string?> GetPreviousValueAsync(string address)
    {
        var currentValue = await _dbService.GetCurrentValueAsync(address);
        return currentValue?.Value;
    }
}
