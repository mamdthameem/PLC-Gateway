using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

public class AggregationService : BackgroundService
{
    private readonly ILogger<AggregationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly CalculationService _calculationService;

    public AggregationService(
        ILogger<AggregationService> logger,
        IConfiguration configuration,
        CalculationService calculationService)
    {
        _logger = logger;
        _configuration = configuration;
        _calculationService = calculationService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AggregationService starting.");

        // Get aggregation schedule from config
        var hourlyEnabled = _configuration.GetValue<bool>("Aggregation:AggregationSchedule:Hourly", true);
        var shiftEnabled = _configuration.GetValue<bool>("Aggregation:AggregationSchedule:Shift", true);
        var dailyEnabled = _configuration.GetValue<bool>("Aggregation:AggregationSchedule:Daily", true);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;

                // Hourly aggregation (run at top of each hour)
                if (hourlyEnabled && now.Minute == 0 && now.Second < 10)
                {
                    var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
                    var hourEnd = hourStart.AddHours(1);
                    await _calculationService.CalculateAggregatedMetricsAsync("hour", hourStart, hourEnd);
                }

                // Shift aggregation (run at shift change - every 8 hours)
                var shiftDurationHours = _configuration.GetValue<int>("Aggregation:ShiftDurationHours", 8);
                if (shiftEnabled && now.Hour % shiftDurationHours == 0 && now.Minute == 0 && now.Second < 10)
                {
                    var shiftStart = new DateTime(now.Year, now.Month, now.Day, (now.Hour / shiftDurationHours) * shiftDurationHours, 0, 0);
                    var shiftEnd = shiftStart.AddHours(shiftDurationHours);
                    await _calculationService.CalculateAggregatedMetricsAsync("shift", shiftStart, shiftEnd);
                }

                // Daily aggregation (run at midnight)
                if (dailyEnabled && now.Hour == 0 && now.Minute == 0 && now.Second < 10)
                {
                    var dayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0);
                    var dayEnd = dayStart.AddDays(1);
                    await _calculationService.CalculateAggregatedMetricsAsync("day", dayStart, dayEnd);
                }

                // Weekly aggregation (run on Monday at midnight)
                if (now.DayOfWeek == DayOfWeek.Monday && now.Hour == 0 && now.Minute == 0 && now.Second < 10)
                {
                    var weekStart = now.AddDays(-(int)now.DayOfWeek);
                    weekStart = new DateTime(weekStart.Year, weekStart.Month, weekStart.Day, 0, 0, 0);
                    var weekEnd = weekStart.AddDays(7);
                    await _calculationService.CalculateAggregatedMetricsAsync("week", weekStart, weekEnd);
                }

                // Monthly aggregation (run on 1st of month at midnight)
                if (now.Day == 1 && now.Hour == 0 && now.Minute == 0 && now.Second < 10)
                {
                    var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0);
                    var monthEnd = monthStart.AddMonths(1);
                    await _calculationService.CalculateAggregatedMetricsAsync("month", monthStart, monthEnd);
                }

                // Yearly aggregation (run on Jan 1st at midnight)
                if (now.Month == 1 && now.Day == 1 && now.Hour == 0 && now.Minute == 0 && now.Second < 10)
                {
                    var yearStart = new DateTime(now.Year, 1, 1, 0, 0, 0);
                    var yearEnd = yearStart.AddYears(1);
                    await _calculationService.CalculateAggregatedMetricsAsync("year", yearStart, yearEnd);
                }

                // Check every minute
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in aggregation service loop");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        _logger.LogInformation("AggregationService stopping.");
    }
}
