namespace PLCGateway.DemoSeeder;

public sealed class CyclePlan
{
    public DateTime BlastStart;
    public DateTime BlastEnd;
    public bool IsReblast;

    /// Declared casting slots (up to 4). A reblast declares nothing: the load was already
    /// declared and discharged, so its weight tags read 0 by the time it goes back in.
    public string?[] SlotNames = new string?[4];
    public int[] SlotKg = new int[4];

    /// What the machine actually measured for this load — the Tonnage accumulator advances by
    /// this, while the slots declare the nominal weight. The small, deliberate gap between the
    /// two is what makes Section 1 (measured) and Section 2 (declared) legitimately differ.
    public int MeasuredKg;

    /// Tonnage accumulator value at blast end.
    public long TonnageAfterKg;

    public double DurationSeconds => (BlastEnd - BlastStart).TotalSeconds;
    public int DeclaredKg => SlotKg.Sum();
}

public sealed class ShiftBlock
{
    public DateTime MachineOn;
    public DateTime MachineOff;
    public List<CyclePlan> Cycles = new();
}

public sealed record RefillEvent(DateTime At, int WeightKg);

public sealed class PlantSchedule
{
    public DateTime WindowStart;
    public DateTime WindowEnd;
    public List<ShiftBlock> Shifts = new();
    public List<RefillEvent> Refills = new();
    public List<DateTime> PoorDays = new();
    public List<DateTime> FaultDays = new();
    public DateTime MaintenanceDay;

    public IEnumerable<CyclePlan> AllCycles => Shifts.SelectMany(s => s.Cycles);
}

/// <summary>
/// Builds the month's timeline: which shifts ran, what each cycle blasted and declared, when the
/// hopper was refilled. Produces no database rows and no KPI values — it is a description of what
/// the machine did, which RawSeeder then renders as raw PLC tag readings.
/// </summary>
public static class PlantModel
{
    public static PlantSchedule Build(Random rng, DateTime endOfWindow)
    {
        var schedule = new PlantSchedule
        {
            WindowEnd   = endOfWindow,
            WindowStart = endOfWindow.Date.AddDays(-(DemoProfile.WindowDays - 1)),
        };

        var workDays = new List<DateTime>();
        for (var d = schedule.WindowStart; d.Date <= endOfWindow.Date; d = d.AddDays(1))
            if (d.DayOfWeek != DemoProfile.WeeklyOff) workDays.Add(d.Date);

        // One planned maintenance half-day: a mid-month Wednesday, morning shift dark.
        var wednesdays = workDays.Where(d => d.DayOfWeek == DayOfWeek.Wednesday).ToList();
        schedule.MaintenanceDay = wednesdays.Count > 2 ? wednesdays[2] : workDays[workDays.Count / 2];

        var candidates = workDays.Where(d => d != schedule.MaintenanceDay).ToList();
        schedule.PoorDays  = PickDistinct(rng, candidates, DemoProfile.PoorDayCount);
        schedule.FaultDays = PickDistinct(rng, candidates, DemoProfile.FaultEventCount);

        long tonnage = 0;                       // the PLC own running accumulator, starts new
        double shotDebtKg = 0;                  // shot consumed since the last refill
        double hopperTrip = Lerp(rng, DemoProfile.HopperTripKgMin, DemoProfile.HopperTripKgMax);
        int lastRefillKg = 0;

        foreach (var day in workDays)
        {
            bool poor = schedule.PoorDays.Contains(day);
            var dayItems = PickDayItems(rng);
            double blastDrift = (rng.NextDouble() * 2 - 1) * DemoProfile.DayBlastDriftMinutes;

            // A fault day gets exactly one fault, in one randomly chosen shift.
            int faultShift = schedule.FaultDays.Contains(day) ? rng.Next(DemoProfile.Shifts.Length) : -1;

            for (int si = 0; si < DemoProfile.Shifts.Length; si++)
            {
                if (day == schedule.MaintenanceDay && si == 0) continue;   // maintenance half-day

                var (startHour, endHour) = DemoProfile.Shifts[si];
                var shiftOpen  = day.AddHours(startHour);
                var shiftClose = day.AddHours(endHour);
                if (shiftClose > schedule.WindowEnd) break;                // never write the future

                int targetCycles = Math.Clamp(
                    (int)Math.Round(Gauss(rng, DemoProfile.CyclesPerShiftMean, DemoProfile.CyclesPerShiftStdDev)),
                    DemoProfile.CyclesPerShiftMin, DemoProfile.CyclesPerShiftMax);

                var block = new ShiftBlock
                {
                    MachineOn = shiftOpen.AddMinutes(
                        Lerp(rng, DemoProfile.WarmUpMinutesMin, DemoProfile.WarmUpMinutesMax)),
                };

                var t = block.MachineOn;
                int faultAt = faultShift == si ? rng.Next(4, Math.Max(5, targetCycles - 4)) : -1;

                for (int c = 0; c < targetCycles; c++)
                {
                    double blastMin = Lerp(rng, DemoProfile.BlastMinutesMin, DemoProfile.BlastMinutesMax) + blastDrift;
                    if (t.AddMinutes(blastMin + 2) > shiftClose) break;

                    var cycle = BuildCycle(rng, t, blastMin, dayItems);
                    tonnage += cycle.MeasuredKg;
                    cycle.TonnageAfterKg = tonnage;
                    block.Cycles.Add(cycle);
                    t = cycle.BlastEnd;

                    shotDebtKg += cycle.MeasuredKg / 1000.0 *
                                  Lerp(rng, DemoProfile.ShotKgPerTonneMin, DemoProfile.ShotKgPerTonneMax);

                    // Reblast: straight back in, no new declaration, no tonnage.
                    if (rng.NextDouble() < DemoProfile.ReblastProbability)
                    {
                        var pause = Lerp(rng, DemoProfile.ReblastPauseMinutesMin, DemoProfile.ReblastPauseMinutesMax);
                        var rbMin = Lerp(rng, DemoProfile.ReblastMinutesMin, DemoProfile.ReblastMinutesMax);
                        var rbStart = t.AddMinutes(pause);
                        if (rbStart.AddMinutes(rbMin) <= shiftClose)
                        {
                            var reblast = new CyclePlan
                            {
                                BlastStart = rbStart,
                                BlastEnd = rbStart.AddMinutes(rbMin),
                                IsReblast = true,
                                MeasuredKg = 0,
                                TonnageAfterKg = tonnage,
                            };
                            block.Cycles.Add(reblast);
                            t = reblast.BlastEnd;
                        }
                    }

                    double idle = poor
                        ? Lerp(rng, DemoProfile.PoorDayIdleMin, DemoProfile.PoorDayIdleMax)
                        : Lerp(rng, DemoProfile.IdleMinutesMin, DemoProfile.IdleMinutesMax);
                    if (c == faultAt)
                        idle += Lerp(rng, DemoProfile.FaultMinutesMin, DemoProfile.FaultMinutesMax);

                    // Hopper low, so the operator tops up during this idle.
                    if (shotDebtKg >= hopperTrip)
                    {
                        int kg = NextRefillWeight(rng, lastRefillKg);
                        var refillAt = t.AddMinutes(Lerp(rng, 0.5, 1.5));
                        if (refillAt < shiftClose)
                        {
                            schedule.Refills.Add(new RefillEvent(refillAt, kg));
                            lastRefillKg = kg;
                            shotDebtKg -= kg;
                            hopperTrip = Lerp(rng, DemoProfile.HopperTripKgMin, DemoProfile.HopperTripKgMax);
                            idle += Lerp(rng, 2.0, 4.0);   // topping up takes a few minutes
                        }
                    }

                    t = t.AddMinutes(idle);
                }

                if (block.Cycles.Count == 0) continue;

                block.MachineOff = block.Cycles[^1].BlastEnd
                    .AddMinutes(Lerp(rng, DemoProfile.CoolDownMinutesMin, DemoProfile.CoolDownMinutesMax));
                if (block.MachineOff > shiftClose) block.MachineOff = shiftClose;
                schedule.Shifts.Add(block);
            }
        }

        return schedule;
    }

    private static CyclePlan BuildCycle(Random rng, DateTime start, double blastMinutes, CastingItem[] dayItems)
    {
        var cycle = new CyclePlan
        {
            BlastStart = start,
            BlastEnd = start.AddMinutes(blastMinutes),
        };

        var primary = dayItems[rng.Next(dayItems.Length)];
        cycle.SlotNames[0] = primary.Name;
        cycle.SlotKg[0] = primary.NominalKg;

        // Some loads carry two part types, which exercises the 4-slot schema and the item split.
        if (dayItems.Length > 1 && rng.NextDouble() < DemoProfile.TwoSlotCycleProbability)
        {
            var second = dayItems[rng.Next(dayItems.Length)];
            if (second.Name != primary.Name)
            {
                cycle.SlotNames[1] = second.Name;
                cycle.SlotKg[1] = second.NominalKg;
            }
        }

        double spread = 1.0 + (rng.NextDouble() * 2 - 1) * DemoProfile.MeasuredWeightSpread;
        cycle.MeasuredKg = (int)Math.Round(cycle.DeclaredKg * spread);
        return cycle;
    }

    private static CastingItem[] PickDayItems(Random rng)
    {
        if (rng.NextDouble() < DemoProfile.SingleItemDayProbability)
            return new[] { DemoProfile.Items[rng.Next(DemoProfile.Items.Length)] };

        int n = 2 + rng.Next(3);   // 2 to 4 items
        return DemoProfile.Items.OrderBy(_ => rng.Next()).Take(n).ToArray();
    }

    /// Guarantees consecutive refills differ by at least MinRefillWeightDeltaKg. Without this a
    /// repeat weight produces no COV row and the refill vanishes from the record entirely.
    private static int NextRefillWeight(Random rng, int previousKg)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            int kg = rng.Next(DemoProfile.RefillKgMin, DemoProfile.RefillKgMax + 1);
            if (previousKg == 0 || Math.Abs(kg - previousKg) >= DemoProfile.MinRefillWeightDeltaKg)
                return kg;
        }

        // Fallback: step away from the previous weight, staying inside the band.
        int stepped = previousKg + DemoProfile.MinRefillWeightDeltaKg;
        return stepped > DemoProfile.RefillKgMax
            ? previousKg - DemoProfile.MinRefillWeightDeltaKg
            : stepped;
    }

    private static List<DateTime> PickDistinct(Random rng, List<DateTime> pool, int count) =>
        pool.OrderBy(_ => rng.Next()).Take(Math.Min(count, pool.Count)).ToList();

    private static double Lerp(Random rng, double min, double max) => min + rng.NextDouble() * (max - min);

    private static double Gauss(Random rng, double mean, double stdDev)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return mean + stdDev * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
