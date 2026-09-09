namespace PLCGateway.DemoSeeder;

/// <summary>
/// Mirrors what the spare tags say at any point in the month: accumulated run hours, live
/// triggers, and the last replacement timestamp per spare.
///
/// It exists because SpareMonitoringService cannot build plc_spare_status here — it early-returns
/// while PlcConnectionState reports no PLC, and with no PLC attached it never runs at all. The
/// seeder therefore feeds the same values into the same production write method
/// (DatabaseService.UpsertSpareStatusAsync); only the polling loop is replaced.
///
/// Thresholds come from MaintenanceThresholds in appsettings.json — the same array the real
/// service reads — so a threshold change there is picked up here automatically.
/// </summary>
public sealed class SpareState
{
    public const int SpareCount = 14;

    private readonly double[,] _runHours = new double[DemoProfile.ImpellerCount + 1, SpareCount];
    private readonly bool[,] _trigger = new bool[DemoProfile.ImpellerCount + 1, SpareCount];
    private readonly DateTime?[,] _replacedAt = new DateTime?[DemoProfile.ImpellerCount + 1, SpareCount];

    public string[] Names { get; set; } = Array.Empty<string>();
    public double[] Thresholds { get; set; } = Array.Empty<double>();

    public double BladeThresholdHours =>
        DemoProfile.BladeSpareIndex < Thresholds.Length ? Thresholds[DemoProfile.BladeSpareIndex] : 100;

    public double RunHours(int impeller, int index) => _runHours[impeller, index];
    public bool TriggerActive(int impeller, int index) => _trigger[impeller, index];
    public DateTime? ReplacedAt(int impeller, int index) => _replacedAt[impeller, index];

    public string Name(int index) => index < Names.Length ? Names[index] : $"Spare_{index}";
    public double Threshold(int index) => index < Thresholds.Length ? Thresholds[index] : 0;

    public void SetRunHours(int impeller, int index, double hours) => _runHours[impeller, index] = hours;
    public void SetTrigger(int impeller, int index, bool active) => _trigger[impeller, index] = active;
    public void RecordReplacement(int impeller, int index, DateTime at) => _replacedAt[impeller, index] = at;

    /// Impellers whose blade has tripped and is still waiting to be changed.
    public IEnumerable<int> PendingBladeReplacements(int spareIndex)
    {
        for (int imp = 1; imp <= DemoProfile.ImpellerCount; imp++)
            if (_trigger[imp, spareIndex]) yield return imp;
    }

    public IEnumerable<(int Impeller, int Index)> All()
    {
        for (int imp = 1; imp <= DemoProfile.ImpellerCount; imp++)
            for (int idx = 0; idx < SpareCount; idx++)
                yield return (imp, idx);
    }
}
