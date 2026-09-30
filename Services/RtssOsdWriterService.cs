using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using OverlayDataBridge.Models;

namespace OverlayDataBridge.Services
{
    public enum RtssOsdStyle
    {
        HorizontalBar, // Cyberpunk Sleek Line (Default - FPS, 1% Low, Power, PLN)
        StackedBlock,  // 2-Row Compact Block
        FullAllInOne,  // Complete Stats (CPU + GPU + FPS + 1% Low + PWR + PLN)
        Minimal        // Compact Minimalist Dots
    }

    /// <summary>
    /// Injects real-time telemetry (FPS, 1% Lows, Total Power Watts, PLN Electricity Cost)
    /// directly into RTSS (RivaTuner Statistics Server) in-game On-Screen Display.
    /// Uses native RTSS tags (<C=RRGGBB>...<C>) and font scaling tags (<S=...><S>).
    /// </summary>
    public sealed class RtssOsdWriterService : IDisposable
    {
        private const string SharedMemoryName = "RTSSSharedMemoryV2";
        private const uint RTSS_SIG = 0x52545353;
        private const string OsdOwner = "LegaxyyFPS";

        private readonly HardwareMonitorService _hwService;
        private readonly RtssReaderService _rtssReader;
        private readonly PowerAggregatorService _powerService;
        private readonly AppLogger _logger;
        private readonly IConfiguration _config;

        private readonly CancellationTokenSource _cts = new();
        private Task? _loopTask;
        private bool _isEnabled = true;
        private RtssOsdStyle _style = RtssOsdStyle.FullAllInOne;

        // PLN configuration
        private double _tariffPerKwh = 1352.0;
        private int _hoursPerDay = 8;
        private int _daysPerMonth = 30;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                if (!_isEnabled) ClearOsd();
            }
        }

        public RtssOsdStyle Style
        {
            get => _style;
            set => _style = value;
        }

        public RtssOsdWriterService(
            HardwareMonitorService hwService,
            RtssReaderService rtssReader,
            PowerAggregatorService powerService,
            IConfiguration config,
            AppLogger logger)
        {
            _hwService = hwService;
            _rtssReader = rtssReader;
            _powerService = powerService;
            _config = config;
            _logger = logger;

            LoadPlnConfig();
        }

        public void LoadPlnConfig()
        {
            try
            {
                var sec = _config.GetSection("Electricity");
                if (sec.Exists())
                {
                    if (double.TryParse(sec["TariffPerKwh"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r))
                        _tariffPerKwh = r;
                    if (int.TryParse(sec["HoursPerDay"], out var h))
                        _hoursPerDay = h;
                    if (int.TryParse(sec["DaysPerMonth"], out var d))
                        _daysPerMonth = d;
                }
            }
            catch { }
        }

        public void UpdatePlnConfig(double tariffPerKwh, int hoursPerDay, int daysPerMonth)
        {
            if (tariffPerKwh > 0) _tariffPerKwh = tariffPerKwh;
            if (hoursPerDay > 0)  _hoursPerDay  = hoursPerDay;
            if (daysPerMonth > 0) _daysPerMonth = daysPerMonth;
            _logger.Info($"RtssOsdWriterService: Synchronized PLN -> Rate=Rp {_tariffPerKwh}, Hours={_hoursPerDay}h, Days={_daysPerMonth}d");
        }

        public void Start()
        {
            _loopTask = Task.Run(() => UpdateLoopAsync(_cts.Token));
            _logger.Info("RtssOsdWriterService: Started RTSS OSD injection loop.");
        }

        private async Task UpdateLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_isEnabled)
                    {
                        WriteToRtss();
                    }
                }
                catch
                {
                    // RTSS may not be running or shared memory not yet initialized
                }

                try
                {
                    await Task.Delay(300, ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
            ClearOsd();
        }

        private void WriteToRtss()
        {
            using var mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.ReadWrite);
            using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

            uint sig = acc.ReadUInt32(0x00);
            if (sig != RTSS_SIG) return;

            uint ver = acc.ReadUInt32(0x04);
            uint osdEntrySize = acc.ReadUInt32(0x14);
            uint osdArrOffset = acc.ReadUInt32(0x18);
            uint osdArrSize   = acc.ReadUInt32(0x1C);

            if (osdEntrySize == 0 || osdArrSize == 0) return;

            // Find existing slot owned by LegaxyyFPS or find first empty slot
            long targetOffset = -1;
            long emptyOffset  = -1;

            for (uint i = 0; i < Math.Min(osdArrSize, 8u); i++)
            {
                long entryOffset = osdArrOffset + (i * osdEntrySize);
                byte[] ownerBuf = new byte[256];
                acc.ReadArray(entryOffset + 256, ownerBuf, 0, 256);
                string owner = Encoding.Latin1.GetString(ownerBuf).TrimEnd('\0');

                if (owner == OsdOwner)
                {
                    if (targetOffset == -1)
                    {
                        targetOffset = entryOffset;
                    }
                    else
                    {
                        // Clean any duplicate leftover slot owned by us
                        byte[] blank = new byte[Math.Min(osdEntrySize, 4608u)];
                        acc.WriteArray(entryOffset, blank, 0, blank.Length);
                    }
                }
                else if (emptyOffset == -1 && string.IsNullOrEmpty(owner))
                {
                    emptyOffset = entryOffset;
                }
            }

            long writeOffset = targetOffset != -1 ? targetOffset : emptyOffset;
            if (writeOffset == -1) return; // No slot available

            // Prepare beautiful telemetry string
            string osdText = BuildOsdText();

            byte[] ownerBytes  = new byte[256];
            byte[] textBytes   = new byte[256];
            byte[] textExBytes = new byte[4096];

            Encoding.Latin1.GetBytes(OsdOwner, 0, Math.Min(OsdOwner.Length, 255), ownerBytes, 0);

            // Version 2.14+ concurrency lock (dwBusy at offset 0x24)
            if (ver >= 0x0002000E)
            {
                acc.Write(0x24, (int)1);
            }

            try
            {
                if (osdEntrySize > 512 && ver >= 0x00020007)
                {
                    // Modern RTSS: Write exclusively into szOSDEx at offset 512, keeping legacy szOSD empty!
                    // This prevents RTSS from rendering duplicate rows.
                    Encoding.Latin1.GetBytes(osdText, 0, Math.Min(osdText.Length, 4095), textExBytes, 0);

                    acc.WriteArray(writeOffset, textBytes, 0, 256); // zeroes
                    acc.WriteArray(writeOffset + 256, ownerBytes, 0, 256);
                    acc.WriteArray(writeOffset + 512, textExBytes, 0, 4096);
                }
                else
                {
                    // Legacy RTSS: Write to szOSD (256 bytes)
                    Encoding.Latin1.GetBytes(osdText, 0, Math.Min(osdText.Length, 255), textBytes, 0);
                    acc.WriteArray(writeOffset, textBytes, 0, 256);
                    acc.WriteArray(writeOffset + 256, ownerBytes, 0, 256);
                }
            }
            finally
            {
                if (ver >= 0x0002000E)
                {
                    acc.Write(0x24, (int)0);
                }
            }

            // Increment dwOSDFrame (offset 0x20) to signal RTSS to re-render OSD
            uint frame = acc.ReadUInt32(0x20);
            acc.Write(0x20, frame + 1);
        }

        private string BuildOsdText()
        {
            var pwr = _powerService.GetPowerData();
            var fps = _rtssReader.GetFpsData();
            var cpu = _hwService.GetCpuData();
            var gpu = _hwService.GetGpuData();

            double watts = pwr.TotalW ?? 0;
            double cost = (watts / 1000.0) * _hoursPerDay * _daysPerMonth * _tariffPerKwh;
            string costStr = Math.Round(cost).ToString("#,##0", new System.Globalization.CultureInfo("id-ID"));

            string pwrFormatted = watts > 0 ? $"{watts:F0}" : "—";
            string fpsStr   = fps.Current.HasValue ? $"{Math.Round(fps.Current.Value)}" : "—";
            string low1Str  = fps.Low1Pct.HasValue ? $"{Math.Round(fps.Low1Pct.Value)}" : "—";
            string low01Str = fps.Low01Pct.HasValue ? $"{Math.Round(fps.Low01Pct.Value)}" : "—";
            string ftStr    = (fps.FrametimeMs.HasValue && fps.FrametimeMs.Value > 0) ? $"{fps.FrametimeMs.Value:F1}ms" : "";

            string cpuT = cpu.Temp.HasValue ? $"{cpu.Temp:F0}°C" : "—";
            string cpuL = cpu.Load.HasValue ? $"{cpu.Load:F0}%" : "—";
            string gpuT = gpu.Temp.HasValue ? $"{gpu.Temp:F0}°C" : "—";
            string gpuL = gpu.Load.HasValue ? $"{gpu.Load:F0}%" : "—";

            string hsPart = "";
            if (gpu.HotSpotTemp.HasValue)
            {
                hsPart = $" <C=475569>|<C> <C=F43F5E>HS<C> <C=FFFFFF>{gpu.HotSpotTemp:F0}°C<C>";
            }

            string vramPart = "";
            if (gpu.VramUsedGb.HasValue)
            {
                string vStr = gpu.VramTotalGb.HasValue
                    ? $"{gpu.VramUsedGb:F1}/{gpu.VramTotalGb:F0}GB"
                    : $"{gpu.VramUsedGb:F1}GB";
                vramPart = $" <C=475569>|<C> <C=818CF8>VRAM<C> <C=FFFFFF>{vStr}<C>";
            }

            string ftPart = !string.IsNullOrEmpty(ftStr)
                ? $"<S=75><C=94A3B8> ({ftStr})<C><S>"
                : "";

            switch (_style)
            {
                case RtssOsdStyle.StackedBlock:
                    // 2-Row Clean Box
                    return $"<C=22D3EE>FPS<C> <C=FFFFFF>{fpsStr}<C>{ftPart}  <C=FB923C>1% LOW<C> <C=FFFFFF>{low1Str}<C><S=75><C=FB923C> FPS<C><S>{hsPart}{vramPart}\n" +
                           $"<C=F472B6>POWER<C> <C=FFFFFF>{pwrFormatted} W<C>  <C=475569>•<C>  <C=4ADE80>PLN<C> <C=FFFFFF>Rp {costStr}<C><S=75><C=86EFAC>/bln<C><S>";

                case RtssOsdStyle.FullAllInOne:
                    // Complete stats: CPU + GPU + Hotspot + VRAM + FPS + Frametime + 1% Low + Power + PLN
                    return $"<C=38BDF8>CPU<C> <C=FFFFFF>{cpuT}<C><S=75><C=94A3B8> {cpuL}<C><S> <C=475569>|<C> <C=C084FC>GPU<C> <C=FFFFFF>{gpuT}<C><S=75><C=94A3B8> {gpuL}<C><S>{hsPart}{vramPart}\n" +
                           $"<C=22D3EE>FPS<C> <C=FFFFFF>{fpsStr}<C>{ftPart} <C=475569>|<C> <C=FB923C>1%<C> <C=FFFFFF>{low1Str}<C> <C=475569>|<C> <C=F472B6>PWR<C> <C=FFFFFF>{pwrFormatted}W<C> <C=475569>|<C> <C=4ADE80>PLN<C> <C=FFFFFF>Rp {costStr}<C><S=75><C=86EFAC>/bln<C><S>";

                case RtssOsdStyle.Minimal:
                    return $"<C=22D3EE>{fpsStr} FPS<C>{ftPart} <C=475569>•<C> <C=FB923C>1% {low1Str}<C>{hsPart}{vramPart} <C=475569>•<C> <C=F472B6>{pwrFormatted}W<C> <C=475569>•<C> <C=4ADE80>Rp {costStr}<C>";

                case RtssOsdStyle.HorizontalBar:
                default:
                    // Cyberpunk Pro Single-Line Bar (FPS, Frametime, 1% Low, GPU, HS, VRAM, Power, PLN)
                    return $"<C=22D3EE>FPS<C> <C=FFFFFF>{fpsStr}<C>{ftPart} <C=475569>|<C> " +
                           $"<C=FB923C>1% LOW<C> <C=FFFFFF>{low1Str}<C><S=75><C=FB923C> FPS<C><S> <C=475569>|<C> " +
                           $"<C=C084FC>GPU<C> <C=FFFFFF>{gpuT}<C>{hsPart}{vramPart} <C=475569>|<C> " +
                           $"<C=F472B6>PWR<C> <C=FFFFFF>{pwrFormatted}<C><S=75><C=F472B6>W<C><S> <C=475569>|<C> " +
                           $"<C=4ADE80>PLN<C> <C=FFFFFF>Rp {costStr}<C><S=75><C=86EFAC>/bln<C><S>";
            }
        }

        public void ClearOsd()
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.ReadWrite);
                using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                uint osdEntrySize = acc.ReadUInt32(0x14);
                uint osdArrOffset = acc.ReadUInt32(0x18);
                uint osdArrSize   = acc.ReadUInt32(0x1C);

                for (uint i = 0; i < Math.Min(osdArrSize, 8u); i++)
                {
                    long entryOffset = osdArrOffset + (i * osdEntrySize);
                    byte[] ownerBuf = new byte[256];
                    acc.ReadArray(entryOffset + 256, ownerBuf, 0, 256);
                    string owner = Encoding.Latin1.GetString(ownerBuf).TrimEnd('\0');

                    if (owner == OsdOwner)
                    {
                        byte[] empty = new byte[Math.Min(osdEntrySize, 32768u)];
                        acc.WriteArray(entryOffset, empty, 0, empty.Length);
                    }
                }

                uint frame = acc.ReadUInt32(0x20);
                acc.Write(0x20, frame + 1);
            }
            catch { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _loopTask?.Wait(1500); } catch { }
            ClearOsd();
        }
    }
}
