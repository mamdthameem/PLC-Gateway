namespace PLCGateway.DemoSeeder;

/// <summary>
/// The plant profile the generated month is drawn from. Every number here is a tuning knob;
/// nothing downstream hard-codes any of it.
///
/// The shape it describes: a shot-blasting machine in its FIRST month of recording, so the
/// lifetime figures and the "all-time" graphs cover exactly this window. That is deliberate —
/// it makes effective_shots_usage_kg_per_ton honest (shot refills and the Tonnage accumulator
/// both start at zero together, so the commissioning-baseline distortion documented in the
/// README simply does not arise).
/// </summary>
public static class DemoProfile
{
    // ── Calendar ────────────────────────────────────────────────────────────────────────────
    public const int WindowDays = 30;          // calendar days, ending on the seed date
    public const DayOfWeek WeeklyOff = DayOfWeek.Sunday;

    // Two 8-hour shifts. Cycles are only ever scheduled inside these windows.
    public static readonly (int StartHour, int EndHour)[] Shifts = { (6, 14), (14, 22) };

    // ── Work rate ───────────────────────────────────────────────────────────────────────────
    public const int CyclesPerShiftMin = 25;
    public const int CyclesPerShiftMax = 32;
    public const double CyclesPerShiftMean = 28.5;
    public const double CyclesPerShiftStdDev = 2.0;

    public const double BlastMinutesMin = 8.0;
    public const double BlastMinutesMax = 12.0;
    public const double DayBlastDriftMinutes = 0.6;   // ± per-day drift, so no two days repeat

    // Load/unload/idle between cycles. This is the utility dial: utility ≈ blast ÷ (blast + idle),
    // so ~3.4 min against a ~10 min blast lands around 75%.
    public const double IdleMinutesMin = 2.4;
    public const double IdleMinutesMax = 3.7;
    public const double PoorDayIdleMin = 4.6;         // the 2–3 bad days
    public const double PoorDayIdleMax = 5.4;
    public const int PoorDayCount = 3;

    // Machine powered up before the first cycle and left running briefly after the last.
    public const double WarmUpMinutesMin = 4.0;
    public const double WarmUpMinutesMax = 12.0;
    public const double CoolDownMinutesMin = 3.0;
    public const double CoolDownMinutesMax = 8.0;

    // ── Disruptions ─────────────────────────────────────────────────────────────────────────
    // A fault leaves the machine POWERED but not blasting, so it costs utility — which is what a
    // real fault does. A fault modelled as a power-down would be invisible in the ratio.
    public const int FaultEventCount = 4;
    public const double FaultMinutesMin = 10.0;
    public const double FaultMinutesMax = 40.0;

    // Reblast: the load goes back in for a short second pass. It produces a genuine second cycle
    // (its own rising edge) that consumes energy and declares nothing, so production_kg is 0.
    public const double ReblastProbability = 0.04;
    public const double ReblastPauseMinutesMin = 0.5;
    public const double ReblastPauseMinutesMax = 1.0;
    public const double ReblastMinutesMin = 2.0;
    public const double ReblastMinutesMax = 4.0;

    // ── Castings ────────────────────────────────────────────────────────────────────────────
    // Real parts cleaned in a shot-blasting machine; the weight is the declared load weight.
    public static readonly CastingItem[] Items =
    {
        new("Brake Drum",   120),
        new("Pump Casing",  180),
        new("Gear Housing", 260),
        new("Flywheel",     340),
    };

    public const double SingleItemDayProbability = 0.30;   // gives the item filter clean days
    public const double TwoSlotCycleProbability  = 0.10;   // a load carrying two part types
    public const double MeasuredWeightSpread     = 0.03;   // Tonnage advances by nominal ±3%

    // ── Shot consumption ────────────────────────────────────────────────────────────────────
    // Drives effective_shots_usage_kg_per_ton. Consumption per tonne cast; a refill is triggered
    // when accumulated consumption crosses the hopper's low mark, never on a fixed schedule.
    public const double ShotKgPerTonneMin = 4.2;
    public const double ShotKgPerTonneMax = 6.0;
    public const double HopperTripKgMin = 110;
    public const double HopperTripKgMax = 190;
    public const int RefillKgMin = 100;
    public const int RefillKgMax = 180;

    // 'Refil shots weight' carries the weight of the LATEST refill and is stored on change only
    // (absolute 1 kg deadband). Two consecutive refills of the same weight would therefore emit
    // NO row at all — an invisible refill that silently corrupts both refill parameters and the
    // shots-breakdown chart. Consecutive refills are forced at least this far apart.
    public const int MinRefillWeightDeltaKg = 2;

    // ── Impeller current ────────────────────────────────────────────────────────────────────
    public const int ImpellerCount = 10;
    public const double AmpsBaseMin = 20.0;
    public const double AmpsBaseMax = 35.0;
    public const double AmpsIdle = 0.35;              // near zero, not zero
    public const double AmpsNoise = 0.6;
    public const double AmpsStartupSeconds = 20.0;    // ramp at blast start
    public const double AmpsLoadFactor = 0.06;        // heavier load draws slightly more

    // Blade wear: current climbs as the blades erode, and steps back down on replacement.
    public const double AmpsWearPerBladeHour = 0.0006;   // +6 % across a 100 h blade life

    // Sampling interval for Current_imp_N rows during blast. The poller writes these at 1 Hz;
    // every consumer of them is an AVG() (per-cycle energy, the Section 2 amps panel), so the
    // sample rate changes only how dense the per-cycle amps trace is drawn — 5 s still gives
    // ~120 points across a 10-minute blast. 1 Hz would be 8.6 M rows for the month instead of
    // 1.7 M. Override with --amp-interval.
    public const int AmpSampleSecondsDefault = 5;

    // ── Spares ──────────────────────────────────────────────────────────────────────────────
    // Run hours accumulate with blast time, identically for every spare on an impeller. On a
    // first month (~240 blast hours) only the 100 h blade can cross its threshold — which it
    // does 2–3 times. Everything else legitimately sits below its limit.
    public const double BladeStartHoursSpread = 18.0;  // commissioning trials, staggers the waves
    public const int BladeSpareIndex = 0;

    // Impellers whose final blade crossing is deliberately left unreplaced, so the spare table
    // has live triggers on screen instead of an all-green board.
    public const int UnreplacedFinalTriggers = 3;

    public const int PeriodicHeartbeatSeconds = 60;
}

public sealed record CastingItem(string Name, int NominalKg);
