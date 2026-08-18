using LibreHardwareMonitor.Hardware;
using OverlayDataBridge.Models;

namespace OverlayDataBridge.Services;

/// <summary>
/// Aggregates total system power consumption.
/// Prefers a real PSU sensor if detected by LibreHardwareMonitor;
/// otherwise estimates: CPU power + GPU power + 40W flat overhead.
/// </summary>
public sealed class PowerAggregatorService
{
    private readonly HardwareMonitorService _hwService;
    private readonly AppLogger _logger;
    private bool _hasPsuSensor = false;
    private bool _psuChecked = false;

    public PowerAggregatorService(HardwareMonitorService hwService, AppLogger logger)
    {
        _hwService = hwService;
        _logger    = logger;
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

        // Fallback to estimation
        var cpu = _hwService.GetCpuData();
        var gpu = _hwService.GetGpuData();
        double cpuW = cpu.Power ?? 0;
        double gpuW = gpu.Power ?? 0;
        double estimated = cpuW + gpuW + 40.0; // +40W mobo/RAM/storage/fans flat overhead

        return new PowerData
        {
            TotalW     = Math.Round(estimated, 1),
            IsEstimate = true
        };
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
