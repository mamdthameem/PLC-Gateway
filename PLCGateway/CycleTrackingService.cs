using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Detects completed blast cycles by watching for falling edges on "Blast ON/OFF"
/// in plc_historical_data. On each cycle end it reads casting metal tags and tonnage
/// from plc_current_values (Tier 1), then writes one row to plc_cycles.
///
/// Watermark: starts from MAX(blast_end) in plc_cycles so it survives restarts
/// without reprocessing old cycles.
/// </summary>
public class CycleTrackingService : BackgroundService
{
    private readonly ILogger<CycleTrackingService> _logger;
    private readonly DatabaseService _db;

    private const string TAG_BLAST = "Blast ON/OFF";

    private static readonly string[] CastingMetalNameTags =
    {
        "Casting metal 1 name", "Casting metal 2 name",
        "Casting metal 3 name", "Casting metal 4 name"
    };

    private static readonly string[] CastingMetalWeightTags =
    {
        "Casting metal 1 weight", "Casting metal 2 weight",
        "Casting metal 3 weight", "Casting metal 4 weight"
    };

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    // A cycle's duration counts only the time the gateway was recording (decided 2026-10-28): if the
    // laptop slept for an hour in the middle of a blast, that hour is not run time. "Blast ON/OFF" is a
    // heartbeat tag, so a recording gateway writes a row for it at least every heartbeat; a longer step
    // between rows is a recording gap, of which only MaxStepSeconds counts (same rule and figure as the
    // daily rollup's MaxOnSegmentSeconds). Null when the tag is not a heartbeat tag: then the rows are
    // change-only, gaps cannot be told from a long blast, and the duration is simply end − start.
    private readonly double? _maxStepSeconds;

    public CycleTrackingService(ILogger<CycleTrackingService> logger, DatabaseService db, IConfiguration config)
    {
        _logger = logger;
        _db     = db;

        var heartbeatTags = config.GetSection("DataCollection:HeartbeatTags").Get<string[]>() ?? Array.Empty<string>();
        int heartbeatSec  = config.GetValue<int?>("DataCollection:PeriodicHeartbeatSeconds") ?? 60;
        _maxStepSeconds   = heartbeatTags.Contains(TAG_BLAST) ? Math.Max(300, 5 * heartbeatSec) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CycleTrackingService starting.");

        // Watermark: resume from last recorded cycle to avoid re-processing on restart.
        DateTime watermark = (await _db.GetMaxCycleBlastEndAsync()) ?? new DateTime(2000, 1, 1);
        _logger.LogInformation("CycleTrackingService watermark: {wm}", watermark);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now     = DateTime.Now;
                var records = await _db.GetStateChangesAsync(TAG_BLAST, watermark, now);

                foreach (var record in records.OrderBy(r => r.Timestamp))
                {
                    bool isFallingEdge = IsFalse(record.Value) && IsTrue(record.PreviousValue);
                    if (!isFallingEdge) continue;

                    DateTime blastEnd = record.Timestamp;

                    // Find when this blast started: the beginning of the unbroken ON run that ends
                    // here, NOT the newest ON row — on a live gateway that row is a 60 s heartbeat.
                    DateTime? blastStart = await _db.GetRunStartTimestampBeforeAsync(TAG_BLAST, blastEnd);
                    if (blastStart == null)
                    {
                        _logger.LogWarning("Could not find blast_start for cycle ending at {end} — skipping.", blastEnd);
                        watermark = blastEnd;
                        continue;
                    }

                    double spanSec     = (blastEnd - blastStart.Value).TotalSeconds;
                    double durationSec = _maxStepSeconds is { } maxStep
                        ? await _db.GetRecordedSecondsAsync(TAG_BLAST, blastStart.Value, blastEnd, maxStep)
                        : spanSec;
                    if (spanSec - durationSec > 1)
                        _logger.LogWarning(
                            "Cycle ending {end}: {gap:F0} s of its {span:F0} s span was not recorded (gateway asleep or off); counted {sec:F0} s.",
                            blastEnd, spanSec - durationSec, spanSec, durationSec);

                    // Read casting metal names and weights from Tier 1 (current values at cycle end)
                    string?[] metalNames   = new string?[4];
                    double?[] metalWeights = new double?[4];

                    for (int i = 0; i < 4; i++)
                    {
                        var nameRec   = await _db.GetCurrentValueByNameAsync(CastingMetalNameTags[i]);
                        var weightRec = await _db.GetCurrentValueByNameAsync(CastingMetalWeightTags[i]);

                        string? rawName = nameRec?.Value?.Trim();
                        metalNames[i] = string.IsNullOrEmpty(rawName) ? null : rawName;

                        if (weightRec?.Value != null &&
                            double.TryParse(weightRec.Value,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double w) && w > 0)
                            metalWeights[i] = w;
                    }

                    // Read accumulated tonnage from Tier 1
                    var tonnageRec = await _db.GetCurrentValueByNameAsync("Tonnage");
                    double? tonnage = null;
                    if (tonnageRec?.Value != null &&
                        double.TryParse(tonnageRec.Value,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double t))
                        tonnage = t;

                    int cycleNumber = await _db.InsertCycleAsync(
                        blastStart.Value, blastEnd, durationSec,
                        metalNames[0], metalWeights[0],
                        metalNames[1], metalWeights[1],
                        metalNames[2], metalWeights[2],
                        metalNames[3], metalWeights[3],
                        tonnage);

                    _logger.LogInformation(
                        "Cycle {num} recorded: {start} → {end} ({sec:F0}s)",
                        cycleNumber, blastStart.Value, blastEnd, durationSec);

                    watermark = blastEnd;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CycleTrackingService loop.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("CycleTrackingService stopping.");
    }

    private static bool IsTrue(string? v)  => v == "1" || string.Equals(v, "true",  StringComparison.OrdinalIgnoreCase);
    private static bool IsFalse(string? v) => v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
}
