namespace PlcApi.Models;

// Section 2 production for one casting metal within a filter's scope.
//
// ProductionKg is the sum of the DECLARED 'Casting metal N weight' values recorded against that
// metal name across the in-scope cycles — not a share of the Tonnage accumulator. Section 1
// reports production from Tonnage; Section 2 reports it per declared casting metal.
public class FilteredMetalProductionDto
{
    public string MetalName { get; set; } = string.Empty;
    public double ProductionKg { get; set; }
}
