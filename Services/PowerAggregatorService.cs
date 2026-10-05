using LibreHardwareMonitor.Hardware;
using OverlayDataBridge.Models;

namespace OverlayDataBridge.Services;

/// <summary>
/// Aggregates total system power consumption.
/// Prefers a real PSU sensor when present; otherwise estimates using:
///   measured CPU/GPU power when available, or a TDP database lookup
///   (hardware_power.json) scaled by load, plus a platform overhead that
///   differs between desktop and laptop.
/// </summary>
public sealed class PowerAggregatorService
{
    private const string DbFileName = "hardware_power.json";

    private readonly HardwareMonitorService _hwService;
    private readonly AppLogger _logger;
    private bool _hasPsuSensor = false;
    private bool _psuChecked = false;

    // "Auto" | "Desktop" | "Laptop" (manual override)
    private string _deviceTypeOverride = "Auto";
    private bool? _isLaptop;

    // ─── database model ──────────────────────────────────────────────────────
    private sealed class DbEntry
    {
        public string Match { get; set; } = "";
        public double Tdp { get; set; }
    }

    private sealed class PowerDefaults
    {
        public double OverheadDesktopW { get; set; } = 45;
        public double OverheadLaptopW { get; set; } = 18;
        public double CpuIdleFrac { get; set; } = 0.25;
        public double GpuIdleFrac { get; set; } = 0.20;
        public double CpuFallbackTdpW { get; set; } = 65;
        public double GpuFallbackTdpW { get; set; } = 120;
        public double CpuFallbackTdpLaptopW { get; set; } = 28;
        public double GpuFallbackTdpLaptopW { get; set; } = 15;
        public double RamPerGbW { get; set; } = 0.25;
        public double SsdNvmeW { get; set; } = 6;
        public double SsdSataW { get; set; } = 3;
        public double HddW { get; set; } = 7;
        public double StorageUnknownW { get; set; } = 4;
        public double PlatformDesktopW { get; set; } = 25;
        public double PlatformLaptopW { get; set; } = 6;
        public double OtherDesktopW { get; set; } = 10;
        public double OtherLaptopW { get; set; } = 2.5;
        public double DisplayLaptopW { get; set; } = 5;
    }

    private sealed class PowerDb
    {
        public PowerDefaults Defaults { get; set; } = new();
        public List<DbEntry> Cpus { get; set; } = new();
        public List<DbEntry> Gpus { get; set; } = new();
    }

    private PowerDb _db = new();
    private List<(string Pattern, double Tdp)> _cpuIndex = new();
    private List<(string Pattern, double Tdp)> _gpuIndex = new();

    public PowerAggregatorService(HardwareMonitorService hwService, AppLogger logger)
    {
        _hwService = hwService;
        _logger    = logger;
        LoadDatabase();
    }

    /// <summary>Manual override from settings: "Auto", "Desktop" or "Laptop".</summary>
    public void SetDeviceType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) type = "Auto";
        if (!string.Equals(type, _deviceTypeOverride, StringComparison.OrdinalIgnoreCase))
        {
            _deviceTypeOverride = type;
            _isLaptop = null; // re-evaluate if "Auto"
            _logger.Info($"PowerAggregatorService: Device type override -> '{type}'.");
        }
    }

    public PowerData GetPowerData()
    {
        // On first call, check for a PSU power sensor once
        if (!_psuChecked) CheckForPsuSensor();

        if (_hasPsuSensor)
        {
            double? psuW = TryReadPsuSensor();
            if (psuW.HasValue)
                return new PowerData { TotalW = psuW, IsEstimate = false };
        }

        // ── Estimate ─────────────────────────────────────────────────────────
        var cpu = _hwService.GetCpuData();
        var gpu = _hwService.GetGpuData();
        var dev = _hwService.GetDeviceInfo();

        bool laptop = IsLaptop();

        double cpuFallback = laptop ? _db.Defaults.CpuFallbackTdpLaptopW : _db.Defaults.CpuFallbackTdpW;
        double gpuFallback = laptop ? _db.Defaults.GpuFallbackTdpLaptopW : _db.Defaults.GpuFallbackTdpW;

        double cpuLoadFrac = Math.Clamp((cpu.Load ?? 0) / 100.0, 0.0, 1.0);
        double gpuLoadFrac = Math.Clamp((gpu.Load ?? 0) / 100.0, 0.0, 1.0);

        double cpuW;
        if (cpu.Power.HasValue && cpu.Power.Value > 0)
        {
            cpuW = cpu.Power.Value;
        }
        else
        {
            double tdp = Lookup(_cpuIndex, dev.CpuName, cpuFallback);
            cpuW = tdp * (_db.Defaults.CpuIdleFrac + (1.0 - _db.Defaults.CpuIdleFrac) * cpuLoadFrac);
        }

        double gpuW;
        if (gpu.Power.HasValue && gpu.Power.Value > 0)
        {
            gpuW = gpu.Power.Value;
        }
        else
        {
            double tdp = Lookup(_gpuIndex, dev.GpuName, gpuFallback);
            gpuW = tdp * (_db.Defaults.GpuIdleFrac + (1.0 - _db.Defaults.GpuIdleFrac) * gpuLoadFrac);
        }

        // ── Auto component breakdown ─────────────────────────────────────────
        var mem = _hwService.GetMemData();
        double ramW = (mem.TotalGb ?? 0) * _db.Defaults.RamPerGbW;

        double storageW = 0;
        foreach (var sname in dev.StorageNames)
        {
            string n = (sname ?? "").ToUpperInvariant();
            if (n.Contains("NVME")) storageW += _db.Defaults.SsdNvmeW;
            else if (n.Contains("SSD") || n.Contains("M.2")) storageW += _db.Defaults.SsdSataW;
            else if (n.Contains("HDD") || n.Contains("HARD") || n.Contains("7200") || n.Contains("5400")) storageW += _db.Defaults.HddW;
            else storageW += _db.Defaults.StorageUnknownW; // modern drives usually SSD/NVMe
        }

        double platformW = laptop ? _db.Defaults.PlatformLaptopW : _db.Defaults.PlatformDesktopW;
        double displayW  = laptop ? _db.Defaults.DisplayLaptopW : 0;
        double otherW    = laptop ? _db.Defaults.OtherLaptopW : _db.Defaults.OtherDesktopW;

        double estimated = cpuW + gpuW + ramW + storageW + platformW + displayW + otherW;

        return new PowerData
        {
            TotalW     = Math.Round(estimated, 1),
            IsEstimate = true,
            Breakdown  = new PowerBreakdown
            {
                CpuW      = Math.Round(cpuW, 1),
                GpuW      = Math.Round(gpuW, 1),
                RamW      = Math.Round(ramW, 1),
                StorageW  = Math.Round(storageW, 1),
                PlatformW = Math.Round(platformW, 1),
                DisplayW  = Math.Round(displayW, 1),
                OtherW    = Math.Round(otherW, 1)
            }
        };
    }

    // ─── device type detection ───────────────────────────────────────────────
    private bool IsLaptop()
    {
        if (_isLaptop.HasValue) return _isLaptop.Value;

        bool result;
        if (string.Equals(_deviceTypeOverride, "Laptop", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
        }
        else if (string.Equals(_deviceTypeOverride, "Desktop", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
        }
        else
        {
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                result = ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;
            }
            catch { result = false; }
        }

        _isLaptop = result;
        _logger.Info($"PowerAggregatorService: Device type = {(result ? "Laptop" : "Desktop")} (override '{_deviceTypeOverride}').");
        return result;
    }

    // ─── database loading ────────────────────────────────────────────────────
    private void LoadDatabase()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, DbFileName);
            if (File.Exists(path))
            {
                var db = Newtonsoft.Json.JsonConvert.DeserializeObject<PowerDb>(File.ReadAllText(path));
                if (db != null)
                {
                    _db = db;
                    _cpuIndex = BuildIndex(db.Cpus);
                    _gpuIndex = BuildIndex(db.Gpus);
                    _logger.Info($"PowerAggregatorService: Loaded {DbFileName} ({_cpuIndex.Count} CPUs, {_gpuIndex.Count} GPUs).");
                    return;
                }
            }
            _logger.Warn($"PowerAggregatorService: {DbFileName} not found/invalid — using built-in fallbacks.");
        }
        catch (Exception ex)
        {
            _logger.Error($"PowerAggregatorService: Failed to load {DbFileName}: {ex.Message}");
        }
    }

    private static List<(string Pattern, double Tdp)> BuildIndex(List<DbEntry> entries)
    {
        return entries
            .Select(e => (Pattern: Normalize(e.Match), Tdp: e.Tdp))
            .Where(t => t.Pattern.Length > 0 && t.Tdp > 0)
            .OrderByDescending(t => t.Pattern.Length) // longest = most specific match wins
            .ToList();
    }

    private static double Lookup(List<(string Pattern, double Tdp)> index, string? name, double fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        string n = Normalize(name);
        if (n.Length == 0) return fallback;
        foreach (var (pattern, tdp) in index)
            if (n.Contains(pattern)) return tdp;
        return fallback;
    }

    private static readonly string[] VendorWords =
    {
        "INTEL", "AMD", "NVIDIA", "GEFORCE", "RADEON", "CORE", "RYZEN",
        "PROCESSOR", "GRAPHICS", "CPU", "GPU", "APU", "WITH", "SERIES"
    };

    private static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.ToUpperInvariant()
             .Replace("(R)", "").Replace("(TM)", "").Replace("(C)", "")
             .Replace("@", " ");
        foreach (var w in VendorWords)
            s = s.Replace(w, " ");
        s = s.Replace(" ", "").Replace("_", "").Replace("/", "").Replace("-", "");
        return s.Trim();
    }

    // ─── PSU sensor detection ────────────────────────────────────────────────
    private void CheckForPsuSensor()
    {
        _psuChecked = true;

        foreach (var hw in _hwService.GetAllHardware())
        {
            // LHM exposes PSU as HardwareType.Psu on some boards (e.g., ASUS ROG with SuperI/O PSU monitoring)
            if (hw.HardwareType == HardwareType.Psu)
            {
                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType == SensorType.Power &&
                        sensor.Name.Contains("Input", StringComparison.OrdinalIgnoreCase))
                    {
                        _hasPsuSensor = true;
                        _logger.Info($"PowerAggregatorService: PSU sensor found on [{hw.Name}] — [{sensor.Name}]");
                        return;
                    }
                }
            }

            // Some boards report PSU wattage under Motherboard as a "Power Supply" sensor
            if (hw.HardwareType == HardwareType.Motherboard)
            {
                foreach (var sub in hw.SubHardware)
                {
                    foreach (var sensor in sub.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Power &&
                            (sensor.Name.Contains("Power Supply", StringComparison.OrdinalIgnoreCase) ||
                             sensor.Name.Contains("PSU", StringComparison.OrdinalIgnoreCase)))
                        {
                            _hasPsuSensor = true;
                            _logger.Info($"PowerAggregatorService: PSU sensor found under Motherboard [{sensor.Name}]");
                            return;
                        }
                    }
                }
            }
        }

        _logger.Info("PowerAggregatorService: No PSU sensor found — will use estimated power.");
    }

    private double? TryReadPsuSensor()
    {
        foreach (var hw in _hwService.GetAllHardware())
        {
            if (hw.HardwareType == HardwareType.Psu)
            {
                hw.Update();
                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType == SensorType.Power &&
                        sensor.Name.Contains("Input", StringComparison.OrdinalIgnoreCase) &&
                        sensor.Value.HasValue)
                        return Math.Round(sensor.Value.Value, 1);
                }
            }

            if (hw.HardwareType == HardwareType.Motherboard)
            {
                foreach (var sub in hw.SubHardware)
                {
                    sub.Update();
                    foreach (var sensor in sub.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Power &&
                            (sensor.Name.Contains("Power Supply", StringComparison.OrdinalIgnoreCase) ||
                             sensor.Name.Contains("PSU", StringComparison.OrdinalIgnoreCase)) &&
                            sensor.Value.HasValue)
                            return Math.Round(sensor.Value.Value, 1);
                    }
                }
            }
        }
        return null;
    }
}
