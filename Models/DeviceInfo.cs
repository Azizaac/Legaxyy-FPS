namespace OverlayDataBridge.Models;

public class DeviceInfo
{
    public string? CpuName { get; set; }
    public string? GpuName { get; set; }
    public string? RamLabel { get; set; }   // e.g. "16 GB"
    public List<string> StorageNames { get; set; } = new(); // detected drives
}
