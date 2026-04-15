using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Runs every minute.
/// - Triggers ComputeAndStoreAllMetrics (hour/day/week/month/year + realtime + cumulative).
/// - Detects shift boundaries (machine status 0 → non-zero = shift start,
///   non-zero → 0 = shift end) and triggers shift metric computation.
/// </summary>
public class AggregationService : BackgroundService
{
    private readonly ILogger<AggregationService> _logger;
    private readonly CalculationService _calculationService;
    private readonly DatabaseService _dbService;

    // Shift state tracking
    private bool _machineWasOn = false;
    private DateTime _shiftStartTime = DateTime.MinValue;

    public AggregationService(
        ILogger<AggregationService> logger,
        CalculationService calculationService,
        DatabaseService dbService)
    {
        _logger = logger;
        _calculationService = calculationService;
        _dbService = dbService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AggregationService starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // ── Shift boundary detection ──────────────────────────────
                var machineStatusRecord = await _dbService.GetCurrentValueAsync("DB60.DBB0");
                bool machineIsOn = machineStatusRecord != null
                    && machineStatusRecord.Value != null
                    && machineStatusRecord.Value != "0";

                if (!_machineWasOn && machineIsOn)
                {
                    // Machine just turned ON → shift start
                    _shiftStartTime = DateTime.Now;
                    _logger.LogInformation("Shift started at {time}", _shiftStartTime);
                }
                else if (_machineWasOn && !machineIsOn && _shiftStartTime != DateTime.MinValue)
                {
                    // Machine just turned OFF → shift end → compute shift metrics
                    var shiftEnd = DateTime.Now;
                    _logger.LogInformation("Shift ended at {time}. Computing shift metrics.", shiftEnd);
                    await _calculationService.ComputeShiftMetricsAsync(_shiftStartTime, shiftEnd);
                    _shiftStartTime = DateTime.MinValue;
                }

                _machineWasOn = machineIsOn;

                // ── Compute all period metrics (upserts all filters) ──────
                await _calculationService.ComputeAndStoreAllMetricsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AggregationService loop");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }

        _logger.LogInformation("AggregationService stopping.");
    }
}
