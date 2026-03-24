using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using S7.Net;

public class GatewayWorker : BackgroundService
{
    private readonly ILogger<GatewayWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly DatabaseService _dbService;
    private readonly PlcService _plcService;
    private readonly CovDetectionService _covService;
    private List<PlcAddress> _tags = new List<PlcAddress>();
    private int _scanMs = 1000;
    // Track last written values to avoid writing the same value repeatedly
    private Dictionary<string, object> _lastWrittenValues = new Dictionary<string, object>();
    // Track addresses that consistently fail to avoid repeated error logging
    private HashSet<string> _failedAddresses = new HashSet<string>();
    private Dictionary<string, int> _failureCount = new Dictionary<string, int>();
    private const int MAX_FAILURE_COUNT = 2; // Stop logging after 2 consecutive failures (reduced for faster suppression)

    public GatewayWorker(
        ILogger<GatewayWorker> logger,
        IConfiguration configuration,
        DatabaseService dbService,
        PlcService plcService,
        CovDetectionService covService)
    {
        _logger = logger;
        _configuration = configuration;
        _dbService = dbService;
        _plcService = plcService;
        _covService = covService;

        // read scan interval from config (fallback 1000ms)
        _scanMs = _configuration.GetValue<int?>("ScanIntervalMs") ?? 1000;

        LoadTagsFromConfig();
    }

    private void LoadTagsFromConfig()
    {
        try
        {
            var tagsSection = _configuration.GetSection("Tags");
            if (!tagsSection.Exists())
            {
                _logger.LogWarning("No Tags found in configuration (appsettings.json).");
                return;
            }

            _tags = tagsSection.Get<List<PlcAddress>>() ?? new List<PlcAddress>();
            _logger.LogInformation("Loaded {count} tags from configuration.", _tags.Count);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to load Tags from configuration.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GatewayWorker starting with two-tier architecture.");

        // Attempt initial PLC connect
        try
        {
            _plcService.Connect();
            _logger.LogInformation("Connected to PLC.");
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "Initial PLC connection failed; worker will retry in loop.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ensure PLC is connected; Connect() method is idempotent
                try
                {
                    _plcService.Connect();
                }
                catch (System.Exception ex)
                {
                    _logger.LogWarning(ex, "PLC connect attempt failed in loop.");
                }

                if (_plcService.IsConnected)
                {
                    foreach (var tag in _tags)
                    {
                        // Case-insensitive mode check
                        var mode = tag.Mode?.ToLower() ?? "";
                        
                        try
                        {
                            // Skip addresses that have consistently failed
                            if (_failedAddresses.Contains(tag.Address))
                            {
                                continue; // Skip this address silently
                            }

                            // Handle Read operations with two-tier storage
                            if (mode == "read" || mode == "readwrite")
                            {
                                object? rawVal = null;
                                try
                                {
                                    rawVal = _plcService.Read(tag.Address);
                                    
                                    // Reset failure count on successful read
                                    if (_failureCount.ContainsKey(tag.Address))
                                    {
                                        _failureCount.Remove(tag.Address);
                                    }
                                }
                                catch (S7.Net.PlcException plcEx)
                                {
                                    // Handle S7.Net specific errors
                                    int currentFailures = _failureCount.GetValueOrDefault(tag.Address, 0) + 1;
                                    _failureCount[tag.Address] = currentFailures;

                                    // Log only first few failures, then suppress
                                    if (currentFailures <= MAX_FAILURE_COUNT)
                                    {
                                        if (currentFailures == MAX_FAILURE_COUNT)
                                        {
                                            _logger.LogWarning(
                                                "Address {addr} ({name}) has failed {count} times. Suppressing further errors. " +
                                                "This address may be out of range or invalid in the PLC.",
                                                tag.Address, tag.Name, currentFailures);
                                            _failedAddresses.Add(tag.Address);
                                        }
                                        else
                                        {
                                            _logger.LogWarning(
                                                "Failed to read tag {name} ({addr}): {error}",
                                                tag.Name, tag.Address, plcEx.Message);
                                        }
                                    }
                                    continue; // Skip to next tag
                                }
                                
                                // Get data type from tag config or default
                                string dataType = tag.DataType ?? "UNKNOWN";
                                
                                // Convert raw PLC value to proper string representation
                                string convertedValue = ValueConverter.ConvertToString(rawVal, dataType);
                                
                                // TIER 1: Always update current values table
                                await _dbService.UpsertCurrentValueAsync(
                                    tag.Address, 
                                    tag.Name, 
                                    convertedValue, 
                                    dataType
                                );

                                // TIER 2: Conditionally store in historical based on COV detection
                                // Use converted value for comparison
                                var storageReason = await _covService.ShouldStoreInHistoricalAsync(
                                    tag.Address, 
                                    tag.Name, 
                                    convertedValue, 
                                    dataType
                                );

                                if (storageReason != null)
                                {
                                    // Get previous value for historical record
                                    var previousValue = await _covService.GetPreviousValueAsync(tag.Address);

                                    // Store in Tier 2 (historical)
                                    await _dbService.InsertHistoricalDataAsync(
                                        tag.Address,
                                        tag.Name,
                                        convertedValue,
                                        dataType,
                                        storageReason,
                                        previousValue
                                    );

                                    // Update last stored historical timestamp in Tier 1
                                    await _dbService.UpdateLastStoredHistoricalAsync(tag.Address);

                                    // If periodic heartbeat, also update heartbeat timestamp
                                    if (storageReason == "PERIODIC")
                                    {
                                        await _dbService.UpdateLastHeartbeatAsync(tag.Address);
                                    }

                                    _logger.LogDebug(
                                        "Stored {name} ({addr}) = {val} to Tier 2. Reason: {reason}", 
                                        tag.Name, 
                                        tag.Address, 
                                        convertedValue, 
                                        storageReason
                                    );
                                }

                                _logger.LogInformation("Read {name} ({addr}) = {val} (raw: {raw})", 
                                    tag.Name, tag.Address, convertedValue, rawVal);
                            }
                            
                            // Handle Write operations
                            if (mode == "write" || mode == "readwrite")
                            {
                                // Priority: 1) WriteValue from config, 2) Last write value from database (pending write)
                                object writeValue = tag.WriteValue;
                                bool isFromConfig = writeValue != null;
                                
                                if (writeValue == null)
                                {
                                    // Get the most recent write value from database that hasn't been written yet
                                    var currentValue = await _dbService.GetCurrentValueAsync(tag.Address);
                                    if (currentValue != null)
                                    {
                                        writeValue = currentValue.Value;
                                    }
                                }
                                
                                if (writeValue != null)
                                {
                                    // Check if this is a new value (different from last written)
                                    bool isNewValue = !_lastWrittenValues.ContainsKey(tag.Address) || 
                                                     !Equals(_lastWrittenValues[tag.Address], writeValue);
                                    
                                    if (isNewValue || isFromConfig)
                                    {
                                        try
                                        {
                                            _plcService.Write(tag.Address, writeValue);
                                            _logger.LogInformation("Write {name} ({addr}) = {val}", tag.Name, tag.Address, writeValue);
                                            
                                            // Update last written value
                                            _lastWrittenValues[tag.Address] = writeValue;
                                        }
                                        catch (System.Exception ex)
                                        {
                                            _logger.LogError(ex, "Failed to write to PLC for tag {name} ({addr})", tag.Name, tag.Address);
                                        }
                                    }
                                }
                            }
                        }
                        catch (System.Exception ex)
                        {
                            // Only log if not already in failed addresses list
                            if (!_failedAddresses.Contains(tag.Address))
                            {
                                int currentFailures = _failureCount.GetValueOrDefault(tag.Address, 0) + 1;
                                _failureCount[tag.Address] = currentFailures;

                                if (currentFailures <= MAX_FAILURE_COUNT)
                                {
                                    if (currentFailures == MAX_FAILURE_COUNT)
                                    {
                                        _logger.LogWarning(
                                            "Address {addr} ({name}) has failed {count} times. Suppressing further errors.",
                                            tag.Address, tag.Name, currentFailures);
                                        _failedAddresses.Add(tag.Address);
                                    }
                                    else
                                    {
                                        _logger.LogWarning(ex, "Failed to read/write tag {name} ({addr})", tag.Name, tag.Address);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("PLC not connected this cycle.");
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in worker loop.");
            }

            await Task.Delay(_scanMs, stoppingToken);
        }

        _logger.LogInformation("GatewayWorker stopping.");
    }
}
