using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Runs every minute.
/// Updates Section 1 (plc_lifetime_parameters) by calling ComputeLifetimeParametersAsync.
/// Also tracks shift boundaries for logging purposes.
/// </summary>
public class AggregationService : BackgroundService
{
    private readonly ILogger<AggregationService> _logger;
    private readonly CalculationService _calculationService;
    private readonly DatabaseService _dbService;

    private bool _machineWasOn = false;
    private DateTime _shiftStartTime = DateTime.MinValue;

    public AggregationService(
        ILogger<AggregationService> logger,
        CalculationService calculationService,
        DatabaseService dbService)
    {
        _logger             = logger;
        _calculationService = calculationService;
        _dbService          = dbService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AggregationService starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Detect shift boundaries for logging
                var machineStatusRecord = await _dbService.GetCurrentValueAsync("DB60.DBB0");
                bool machineIsOn = machineStatusRecord?.Value != null && machineStatusRecord.Value != "0";

                if (!_machineWasOn && machineIsOn)
                {
                    _shiftStartTime = DateTime.Now;
                    _logger.LogInformation("Machine ON — shift started at {time}", _shiftStartTime);
                }
                else if (_machineWasOn && !machineIsOn && _shiftStartTime != DateTime.MinValue)
                {
                    _logger.LogInformation("Machine OFF — shift ended at {time} (started {start})", DateTime.Now, _shiftStartTime);
                    _shiftStartTime = DateTime.MinValue;
                }

                _machineWasOn = machineIsOn;

                // Update Section 1 lifetime parameters
                await _calculationService.ComputeLifetimeParametersAsync();
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
