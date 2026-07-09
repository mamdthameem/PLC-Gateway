using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Polls plc_current_values every 10 seconds for all 140 spare tags
/// (10 impellers × 14 spares each) and keeps plc_spare_status up to date.
///
/// For each spare it reads:
///   - Spares trigger impN[M]  — BOOL flag set by PLC when run hours threshold reached
///   - Spares_Runhour_impN[M]  — REAL accumulated run hours kept by PLC
///   - REPLACED_N[M]           — BOOL set by HMI when operator physically replaces the spare
///
/// When a REPLACED bit goes TRUE (rising edge detected in memory), last_replaced_at is recorded.
/// Disc Spacer (spare_index = 9, threshold = 0) is tracked but skipped for alert triggers.
/// </summary>
public class SpareMonitoringService : BackgroundService
{
    private readonly ILogger<SpareMonitoringService> _logger;
    private readonly DatabaseService _db;
    private readonly PlcConnectionState _connState;

    private readonly string[] _spareNames;
    private readonly double[] _spareThresholds;

    private const int ImpellerCount = 10;
    private const int SpareCount    = 14;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    // In-memory previous REPLACED state for rising-edge detection
    private readonly Dictionary<(int imp, int idx), bool> _prevReplaced = new();

    public SpareMonitoringService(
        ILogger<SpareMonitoringService> logger,
        DatabaseService db,
        IConfiguration configuration,
        PlcConnectionState connState)
    {
        _logger          = logger;
        _db              = db;
        _connState       = connState;
        _spareNames      = configuration.GetSection("MaintenanceThresholds:SpareNames").Get<string[]>()
                           ?? Array.Empty<string>();
        _spareThresholds = configuration.GetSection("MaintenanceThresholds:SpareLifeBlastHours").Get<double[]>()
                           ?? Array.Empty<double>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SpareMonitoringService starting ({imp} impellers × {sp} spares).",
            ImpellerCount, SpareCount);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Tier 1 spare values are stale while the PLC is disconnected — don't
            // overwrite plc_spare_status with them.
            if (!_connState.IsConnected)
            {
                await Task.Delay(PollInterval, stoppingToken);
                continue;
            }

            try
            {
                for (int imp = 1; imp <= ImpellerCount; imp++)
                {
                    for (int idx = 0; idx < SpareCount; idx++)
                    {
                        string spareName      = idx < _spareNames.Length      ? _spareNames[idx]      : $"Spare_{idx}";
                        double thresholdHours = idx < _spareThresholds.Length ? _spareThresholds[idx] : 0;

                        // Tag name conventions match appsettings.json
                        string triggerTag  = $"Spares trigger imp{imp}[{idx}]";
                        string runHourTag  = $"Spares_Runhour_imp{imp}[{idx}]";
                        string replacedTag = $"REPLACED_{imp}[{idx}]";

                        var triggerRec  = await _db.GetCurrentValueByNameAsync(triggerTag);
                        var runHourRec  = await _db.GetCurrentValueByNameAsync(runHourTag);
                        var replacedRec = await _db.GetCurrentValueByNameAsync(replacedTag);

                        bool triggerActive  = IsTrue(triggerRec?.Value);
                        double runHours     = ParseDouble(runHourRec?.Value);
                        bool replacedNow    = IsTrue(replacedRec?.Value);

                        // Detect rising edge on REPLACED bit → record replacement timestamp
                        var key = (imp, idx);
                        _prevReplaced.TryGetValue(key, out bool wasReplaced);
                        DateTime? lastReplacedAt = (!wasReplaced && replacedNow) ? DateTime.Now : null;
                        _prevReplaced[key] = replacedNow;

                        await _db.UpsertSpareStatusAsync(
                            imp, idx, spareName, thresholdHours,
                            runHours, triggerActive, lastReplacedAt);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SpareMonitoringService loop.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("SpareMonitoringService stopping.");
    }

    private static bool IsTrue(string? v) =>
        v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    private static double ParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return double.TryParse(s,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var d) ? d : 0;
    }
}
