namespace PLCGateway.Models;

// Single-row running state for the incremental Section 1 aggregation engine (plc_aggregation_state).
// Each pass processes only Tier 2 rows with id > LastHistId, folds them into these accumulators,
// and persists the row back.
public sealed class AggregationState
{
    public long LastHistId { get; set; }

    // Blast ON/OFF on-time + rising-edge (cycle) count
    public bool BlastSeeded { get; set; }
    public bool BlastOn { get; set; }
    public DateTime? BlastSegStart { get; set; }
    public double BlastClosedSec { get; set; }
    public DateTime? FirstBlastTs { get; set; }
    public long CycleCount { get; set; }

    // Machine status on-time (accumulated only from FirstBlastTs onward, as before)
    public bool MachineSeeded { get; set; }
    public bool MachineOn { get; set; }
    public DateTime? MachineSegStart { get; set; }
    public double MachineClosedSec { get; set; }

    // Refill weight change tracking
    public long RefillCount { get; set; }
    public DateTime? FirstRefillChangeTs { get; set; }
    public DateTime? PrevRefillChangeTs { get; set; }
    public DateTime? LastRefillAnyTs { get; set; }

    // Energy running total (Σ plc_cycles.energy_kwh) + cycle watermark
    public decimal EnergyTotal { get; set; }
    public int LastCycleNumber { get; set; }
}

// One Tier 2 event fed to the incremental fold.
public sealed class AggEvent
{
    public long Id { get; set; }
    public string ParameterName { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool? ValueBool { get; set; }
    public string? PreviousValue { get; set; }
    public string StorageReason { get; set; } = string.Empty;
}
