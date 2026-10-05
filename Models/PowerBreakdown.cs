namespace OverlayDataBridge.Models;

/// <summary>
/// Per-component power estimate breakdown (all values in Watts).
/// </summary>
public class PowerBreakdown
{
    public double CpuW { get; set; }
    public double GpuW { get; set; }
    public double RamW { get; set; }
    public double StorageW { get; set; }
    public double PlatformW { get; set; }
    public double DisplayW { get; set; }
    public double OtherW { get; set; }
}
