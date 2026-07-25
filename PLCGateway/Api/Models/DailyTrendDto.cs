namespace PlcApi.Models;

// One bucket of the trend series served to the Section 1 all-time graphs. Read from the
// plc_daily_trends rollup (never from raw Tier 2), optionally grouped up to months.
//
// Every derived figure is computed server-side so the dashboard renders values verbatim — the
// same rule the cloud mirror follows. This is why the frontend no longer carries its own
// utility math.
public class DailyTrendDto
{
    // Bucket start: the calendar day, or the first day of the month when bucket=month.
    public DateTime Day { get; set; }

    public double MachineOnSec { get; set; }
    public double BlastOnSec { get; set; }

    // blast_on_sec / machine_on_sec * 100, capped at 100. Zero when the machine never ran.
    public double UtilityPct { get; set; }

    public int CycleCount { get; set; }

    // Production recorded in the bucket (sum of per-cycle production_kg).
    public double ProductionKg { get; set; }

    // The PLC's running Tonnage accumulator at the end of the bucket, carried forward across
    // buckets that have no reading. Null only before the first-ever Tonnage reading.
    public double? TonnageEnd { get; set; }

    public double EnergyKwh { get; set; }

    // EnergyKwh / ProductionKg for the bucket; zero when nothing was produced.
    public double EfficiencyKwhPerKg { get; set; }
}
