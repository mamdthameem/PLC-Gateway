using PlcApi.Models;

namespace PlcApi.Services;

public interface IAmpsService
{
    Task<List<AmpReadingDto>> GetImpellerAmpsAsync();

    /// <summary>
    /// Average current per impeller over the LAST COMPLETED blast cycle.
    ///
    /// The live reading is the truth, but between cycles the truth is zero — the impellers really
    /// are stopped. A panel of ten zeroes tells a reader nothing about the machine, so each tile
    /// also carries the figure the impeller last ran at, clearly labelled as the last cycle rather
    /// than as a live value.
    /// </summary>
    Task<List<AmpReadingDto>> GetLastCycleAveragesAsync();

    /// <summary>
    /// Average current for one impeller in EVERY completed cycle, oldest first.
    ///
    /// This is the whole-history view of the amps chart. Per-second detail cannot cover a month —
    /// one impeller alone carries ~178 000 samples — so the full range is served at one point per
    /// cycle instead. The per-sample trace stays available for a bounded number of recent cycles,
    /// where the spin-up and wind-down are actually resolvable.
    /// </summary>
    Task<List<CycleAmpDto>> GetPerCycleAveragesAsync(int impellerNumber);
}
