using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using OverlayDataBridge.Models;

namespace OverlayDataBridge.Services;

/// <summary>
/// Reads CPU, GPU and Memory sensor data from LibreHardwareMonitor.
/// RAM total/used is read via GlobalMemoryStatusEx (kernel32) for accuracy
/// — LHM's Data sensors can report virtual/committed memory instead of physical RAM.
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    // ─── Win32 RAM API ────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;          // 0-100 %
        public ulong ullTotalPhys;          // total physical RAM bytes
        public ulong ullAvailPhys;          // available physical RAM bytes
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ─── private state ───────────────────────────────────────────────────────
    private readonly Computer _computer;
    private readonly int _intervalMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly AppLogger _logger;

    private CpuData    _cpuData    = new();
    private GpuData    _gpuData    = new();
    private MemData    _memData    = new();
    private DeviceInfo _deviceInfo = new();

    private Task? _loopTask;
    private bool _loggedGpuSensors = false;

    // ─── ctor ────────────────────────────────────────────────────────────────
    public HardwareMonitorService(int intervalMs, AppLogger logger)
    {
        _intervalMs = intervalMs;
        _logger     = logger;

        _computer = new Computer
        {
            IsCpuEnabled         = true,
            IsGpuEnabled         = true,
            IsMemoryEnabled      = true,
            IsMotherboardEnabled = false,
            IsControllerEnabled  = false,
            IsNetworkEnabled     = false,
            IsStorageEnabled     = false,
            IsPsuEnabled         = false
        };

        try
        {
            _computer.Open();
            _logger.Info("LibreHardwareMonitor: Computer opened successfully.");
        }
        catch (Exception ex)
        {
            _logger.Error($"LibreHardwareMonitor: Failed to open Computer. {ex.Message}");
        }
    }

    // ─── public API ──────────────────────────────────────────────────────────
    public void Start()
    {
        _loopTask = Task.Run(() => UpdateLoopAsync(_cts.Token));
        _logger.Info("HardwareMonitorService: Update loop started.");
    }

    public CpuData    GetCpuData()    { lock (_lock) return Clone(_cpuData); }
    public GpuData    GetGpuData()    { lock (_lock) return Clone(_gpuData); }
    public MemData    GetMemData()    { lock (_lock) return Clone(_memData); }
    public DeviceInfo GetDeviceInfo() { lock (_lock) return Clone(_deviceInfo); }
    public IEnumerable<IHardware> GetAllHardware() => _computer.Hardware;

    // ─── background loop ─────────────────────────────────────────────────────
    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { ReadAllSensors(); }
            catch (Exception ex) { _logger.Error($"HardwareMonitorService: Sensor read error. {ex.Message}"); }

            try { await Task.Delay(_intervalMs, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private void ReadAllSensors()
    {
        var cpuData  = new CpuData();
        var gpuData  = new GpuData();
        var devInfo  = new DeviceInfo();
        
        // ── Physical RAM from Win32 API (accurate physical memory, not virtual) ──
        var memData = ReadMemoryDirect();

        IHardware? discreteGpu    = null;
        IHardware? integratedGpu  = null;

        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    if (devInfo.CpuName == null) devInfo.CpuName = hw.Name;
                    ReadCpu(hw, cpuData);
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                    if (devInfo.GpuName == null) devInfo.GpuName = hw.Name;
                    discreteGpu = hw;
                    break;

                case HardwareType.GpuIntel:
                    if (discreteGpu == null)
                    {
                        integratedGpu = hw;
                        if (devInfo.GpuName == null) devInfo.GpuName = hw.Name;
                    }
                    break;
                    
                case HardwareType.Memory:
                    foreach (var s in hw.Sensors)
                    {
                        if (s.Value.HasValue && s.SensorType == SensorType.Clock && s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                            memData.Clock = Math.Round(s.Value.Value, 0);
                    }
                    break;
            }
        }

        var targetGpu = discreteGpu ?? integratedGpu;
        if (targetGpu != null) ReadGpu(targetGpu, gpuData);

        devInfo.RamLabel = memData.TotalGb.HasValue
            ? $"{(int)Math.Round(memData.TotalGb.Value)} GB"
            : null;

        lock (_lock)
        {
            _cpuData    = cpuData;
            _gpuData    = gpuData;
            _memData    = memData;
            _deviceInfo = devInfo;
        }
    }

    // ─── CPU ─────────────────────────────────────────────────────────────────
    private static void ReadCpu(IHardware hw, CpuData data)
    {
        var coreTemps  = new List<float>();
        var coreClocks = new List<float>();
        float? packageTemp  = null;
        float? totalLoad    = null;
        float? packagePower = null;

        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value == null) continue;
            float v = sensor.Value.Value;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        packageTemp = v;
                    else if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        coreTemps.Add(v);
                    break;

                case SensorType.Load:
                    if (sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name == "CPU Total")
                        totalLoad = v;
                    break;

                case SensorType.Clock:
                    if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
                        !sensor.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                        coreClocks.Add(v);
                    break;

                case SensorType.Power:
                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        packagePower = v;
                    break;
            }
        }

        if (packageTemp.HasValue)
            data.Temp = Math.Round(packageTemp.Value, 1);
        else if (coreTemps.Count > 0)
            data.Temp = Math.Round(coreTemps.Max(), 1);

        if (totalLoad.HasValue)
            data.Load = Math.Round(totalLoad.Value, 1);

        var activeCores = coreClocks.Where(c => c > 100).ToList();
        if (activeCores.Count > 0)
            data.Clock = Math.Round(activeCores.Average(), 0);

        if (packagePower.HasValue)
            data.Power = Math.Round(packagePower.Value, 1);
    }

    // ─── GPU ─────────────────────────────────────────────────────────────────
    private void ReadGpu(IHardware hw, GpuData data)
    {
        float? vramUsed  = null;
        float? vramTotal = null;
        float? vramFree  = null;
        
        // Log all GPU sensors once on first run
        if (!_loggedGpuSensors)
        {
            _logger.Info($"GPU Name: {hw.Name}");
            foreach (var s in hw.Sensors)
            {
                _logger.Info($"GPU Sensor found: Type={s.SensorType}, Name='{s.Name}', Value={s.Value}");
            }
            _loggedGpuSensors = true;
        }

        foreach (var sensor in hw.Sensors)
        {
            if (sensor.Value == null) continue;
            float v = sensor.Value.Value;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    if (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Junction", StringComparison.OrdinalIgnoreCase))
                    {
                        data.HotSpotTemp = Math.Round(v, 1);
                    }
                    else if (sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                             sensor.Name.Contains("VRAM", StringComparison.OrdinalIgnoreCase))
                    {
                        data.MemTemp = Math.Round(v, 1);
                    }
                    else if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                             sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                    {
                        data.Temp = Math.Round(v, 1);
                    }
                    break;

                case SensorType.Load:
                    if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name == "GPU Core")
                        data.Load = Math.Round(v, 1);
                    break;

                case SensorType.Clock:
                    if (sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        data.CoreClock = Math.Round(v, 0);
                    else if (sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                        data.MemClock = Math.Round(v, 0);
                    break;

                case SensorType.Fan:
                    data.FanRpm = Math.Round(v, 0);
                    break;

                case SensorType.SmallData:
                case SensorType.Data:
                    if (sensor.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Dedicated Memory Used", StringComparison.OrdinalIgnoreCase))
                    {
                        vramUsed = v;
                    }
                    else if (sensor.Name.Contains("Memory Free", StringComparison.OrdinalIgnoreCase))
                    {
                        vramFree = v;
                    }
                    else if (sensor.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase) ||
                             sensor.Name.Contains("Dedicated Memory Total", StringComparison.OrdinalIgnoreCase))
                    {
                        vramTotal = v;
                    }
                    break;

                case SensorType.Power:
                    if (sensor.Name.Contains("GPU Package", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("GPU Power", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name == "GPU Package")
                        data.Power = Math.Round(v, 1);
                    break;
            }
        }

        // If total VRAM sensor is not directly provided by driver, calculate from Used + Free
        if (!vramTotal.HasValue && vramUsed.HasValue && vramFree.HasValue)
        {
            vramTotal = vramUsed.Value + vramFree.Value;
        }

        if (vramUsed.HasValue)
        {
            double val = vramUsed.Value;
            if (val > 1_000_000) val /= (1024.0 * 1024.0 * 1024.0); // bytes -> GB
            else if (val > 100) val /= 1024.0; // MB -> GB
            data.VramUsedGb = Math.Round(val, 2);
        }

        if (vramTotal.HasValue)
        {
            double val = vramTotal.Value;
            if (val > 1_000_000) val /= (1024.0 * 1024.0 * 1024.0); // bytes -> GB
            else if (val > 100) val /= 1024.0; // MB -> GB
            data.VramTotalGb = Math.Round(val, 1);
        }

        // Fallback for GPU Temp if Core sensor wasn't matched
        if (!data.Temp.HasValue && data.HotSpotTemp.HasValue)
            data.Temp = data.HotSpotTemp;
    }

    // ─── RAM (Win32 GlobalMemoryStatusEx — accurate physical RAM) ────────────
    private static MemData ReadMemoryDirect()
    {
        try
        {
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref ms)) return new MemData();

            const double GB = 1024.0 * 1024.0 * 1024.0;
            double total = ms.ullTotalPhys / GB;
            double avail = ms.ullAvailPhys / GB;
            double used  = total - avail;
            double load  = ms.dwMemoryLoad;

            return new MemData
            {
                Load    = Math.Round(load,  1),
                UsedGb  = Math.Round(used,  2),
                TotalGb = Math.Round(total, 1)
            };
        }
        catch { return new MemData(); }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────
    private static CpuData Clone(CpuData d)       => new() { Temp = d.Temp, Load = d.Load, Clock = d.Clock, Power = d.Power };
    private static GpuData Clone(GpuData d)       => new() { Temp = d.Temp, HotSpotTemp = d.HotSpotTemp, MemTemp = d.MemTemp, Load = d.Load, CoreClock = d.CoreClock, MemClock = d.MemClock, FanRpm = d.FanRpm, VramUsedGb = d.VramUsedGb, VramTotalGb = d.VramTotalGb, Power = d.Power };
    private static MemData Clone(MemData d)       => new() { Load = d.Load, UsedGb = d.UsedGb, TotalGb = d.TotalGb, Clock = d.Clock };
    private static DeviceInfo Clone(DeviceInfo d) => new() { CpuName = d.CpuName, GpuName = d.GpuName, RamLabel = d.RamLabel };

    // ─── IDisposable ─────────────────────────────────────────────────────────
    private bool _disposed = false;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _loopTask?.Wait(2000); } catch { }
        try { _cts.Dispose(); } catch { }
        try { _computer.Close(); _logger.Info("HardwareMonitorService: Computer closed."); }
        catch (Exception ex) { _logger.Error($"HardwareMonitorService: Error closing Computer. {ex.Message}"); }
    }
}
