namespace PlcApi.Models;

public class ShotsBreakdownDto
{
    /// <summary>The refill that CLOSED this interval. plc_shots_breakdown is keyed on it.</summary>
    public DateTime RefillTimestamp { get; set; }

    /// <summary>
    /// The refill that OPENED this interval — i.e. the previous refill.
    ///
    /// Not stored: plc_shots_breakdown keeps one timestamp per row, the closing one, so for every
    /// row but the first the opening refill is simply the previous row. The FIRST row's opener is
    /// not in the table at all (the engine sets PrevRefillChangeTs on the first refill without
    /// emitting a row for it), so it is recovered from the refill events in Tier 2. Without this
    /// the earliest bar could not name its own window while every other bar could.
    ///
    /// Null only if no earlier refill change event exists at all.
    /// </summary>
    public DateTime? IntervalStartTimestamp { get; set; }

    public int BlastCount { get; set; }
}
