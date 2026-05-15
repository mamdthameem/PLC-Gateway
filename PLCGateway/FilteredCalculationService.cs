using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Polls calculation_requests every 5 seconds.
/// When a pending request is found: marks it processing, runs the calculation,
/// writes aggregate results to plc_filtered_parameters and per-cycle data to
/// plc_filtered_cycle_data, then marks it done (or error).
/// </summary>
public class FilteredCalculationService : BackgroundService
{
    private readonly ILogger<FilteredCalculationService> _logger;
    private readonly CalculationService _calculationService;
    private readonly DatabaseService _dbService;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public FilteredCalculationService(
        ILogger<FilteredCalculationService> logger,
        CalculationService calculationService,
        DatabaseService dbService)
    {
        _logger             = logger;
        _calculationService = calculationService;
        _dbService          = dbService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FilteredCalculationService starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var request = await _dbService.GetNextPendingRequestAsync();
                if (request != null)
                {
                    _logger.LogInformation(
                        "Processing request {id}: filter_by={by}, period={label}",
                        request.Id, request.FilterBy, request.PeriodLabel ?? "custom");

                    await _dbService.SetRequestStatusAsync(request.Id, "processing");

                    try
                    {
                        await _calculationService.ComputeFilteredParametersAsync(
                            request.Id,
                            request.FilterStart,
                            request.FilterEnd,
                            request.FilterBy,
                            request.FilterCycleFrom,
                            request.FilterCycleTo,
                            request.FilterMetalName);

                        await _dbService.SetRequestStatusAsync(request.Id, "done");
                        _logger.LogInformation("Request {id} completed successfully.", request.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Request {id} failed during calculation.", request.Id);
                        await _dbService.SetRequestStatusAsync(request.Id, "error");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in FilteredCalculationService poll loop.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("FilteredCalculationService stopping.");
    }
}
