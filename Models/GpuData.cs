namespace OverlayDataBridge.Models;

public class GpuData
{
    public double? Temp { get; set; }
    public double? HotSpotTemp { get; set; }
    public double? MemTemp { get; set; }
    public double? Load { get; set; }
    public double? CoreClock { get; set; }
    public double? MemClock { get; set; }
    public double? FanRpm { get; set; }
    public double? VramUsedGb { get; set; }
    public double? VramTotalGb { get; set; }
    public double? Power { get; set; }
}
