using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using OverlayDataBridge.Services;

namespace OverlayDataBridge;

public class UserSettings
{
    public string Mode { get; set; } = "Streamer"; // "Streamer" or "Gamer"
    public bool RtssOsdEnabled { get; set; } = true;
    public string RtssStyle { get; set; } = "FullAllInOne";
    public double PlnRate { get; set; } = 1352.0;
    public int PlnHours { get; set; } = 8;
    public int PlnDays { get; set; } = 30;
    public string PlnTier { get; set; } = "900_nonsubsidi";
    public string PlnLabel { get; set; } = "900 VA";
    public int BillingCycleStartDay { get; set; } = 1;
    public string DeviceType { get; set; } = "Auto"; // Auto | Desktop | Laptop
}

/// <summary>
/// System tray application host.
/// Owns all services and exposes right-click context menu for modes/status/restart/exit.
/// </summary>
public sealed class TrayApp : IDisposable
{
    // ─── services ────────────────────────────────────────────────────────────
    private readonly AppLogger _logger;
    private readonly HardwareMonitorService _hwService;
    private readonly RtssReaderService _rtssService;
    private readonly PowerAggregatorService _powerService;
    private readonly RtssOsdWriterService _rtssOsdWriter;
    private readonly WsBroadcastServer _wsServer;
    private readonly HttpServerService _httpServer;
    private readonly UpdateService _updateService;
    private readonly EnergyTrackerService _energyService;

    // ─── settings ────────────────────────────────────────────────────────────
    private UserSettings _settings;
    private readonly int _httpPort;

    // ─── tray UI ─────────────────────────────────────────────────────────────
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _showDashboardItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    
    private OverlayWindow? _overlayWindow;

    // ─── Task Scheduler config ───────────────────────────────────────────────
    private const string TaskName = "LegaxyyFPSStartup";

    private bool _disposed = false;

    public TrayApp(IConfiguration config)
    {
        static int Cfg(IConfiguration c, string key, int def)
            => int.TryParse(c[key], out var v) ? v : def;

        int wsPort            = Cfg(config, "WebSocketPort",          8765);
        _httpPort             = Cfg(config, "HttpPort",               8766);
        int hwInterval        = Cfg(config, "HardwareUpdateIntervalMs", 1000);
        int fpsInterval       = Cfg(config, "FpsUpdateIntervalMs",     300);
        int broadcastInterval = Cfg(config, "BroadcastIntervalMs",    500);
        string wsBindAddress  = string.IsNullOrWhiteSpace(config["WsBindAddress"]) ? "127.0.0.1" : config["WsBindAddress"]!;

        _settings = LoadUserSettings();

        // Build services
        _logger        = new AppLogger();
        _hwService     = new HardwareMonitorService(hwInterval, _logger);
        _rtssService   = new RtssReaderService(fpsInterval, _logger);
        _powerService  = new PowerAggregatorService(_hwService, _logger);
        _powerService.SetDeviceType(_settings.DeviceType);
        _energyService = new EnergyTrackerService(_powerService, _logger, _settings.BillingCycleStartDay);
        _rtssOsdWriter = new RtssOsdWriterService(_hwService, _rtssService, _powerService, _energyService, config, _logger);
        _wsServer      = new WsBroadcastServer(wsPort, wsBindAddress, broadcastInterval, _hwService, _rtssService, _powerService, _energyService, _logger);
        _httpServer    = new HttpServerService(_httpPort, wsPort, _logger);
        _updateService = new UpdateService(config, _logger);

        // Sync PLN settings between dashboard and in-game RTSS OSD
        _httpServer.SetInitialPlnConfig(_settings.PlnRate, _settings.PlnHours, _settings.PlnDays, _settings.PlnTier, _settings.PlnLabel);
        _httpServer.PlnConfigChanged += (rate, hours, days, tier, label) =>
        {
            _rtssOsdWriter.UpdatePlnConfig(rate, hours, days);
            _settings.PlnRate = rate;
            _settings.PlnHours = hours;
            _settings.PlnDays = days;
            _settings.PlnTier = tier;
            _settings.PlnLabel = label;
            SaveUserSettings();
        };

        // ─── Dashboard Settings UI → backend event handlers ──────────────────────
        _httpServer.ModeChanged += (mode) =>
        {
            if (this._disposed) return;
            // Must run on UI thread since it manipulates Forms
            _trayIcon.ContextMenuStrip?.Invoke((MethodInvoker)(() => ApplyMode(mode, notifyUser: false)));
        };

        _httpServer.RtssOsdToggled += (enabled) =>
        {
            if (this._disposed) return;
            _rtssOsdWriter.IsEnabled = enabled;
            _settings.RtssOsdEnabled = enabled;
            SaveUserSettings();
            _httpServer.UpdateRtssState(enabled, _settings.RtssStyle);
        };

        _httpServer.RtssStyleChanged += (styleName) =>
        {
            if (this._disposed) return;
            if (Enum.TryParse<RtssOsdStyle>(styleName, out var style))
            {
                _rtssOsdWriter.Style = style;
                _settings.RtssStyle = style.ToString();
                SaveUserSettings();
                _httpServer.UpdateRtssState(_settings.RtssOsdEnabled, _settings.RtssStyle);
            }
        };

        _httpServer.StartupToggled += (enable) =>
        {
            if (this._disposed) return;
            _trayIcon.ContextMenuStrip?.Invoke((MethodInvoker)(() => ToggleStartup(enable)));
        };

        _httpServer.RestartWsRequested += () =>
        {
            if (this._disposed) return;
            _logger.Info("TrayApp: Dashboard requested WebSocket server restart.");
            _wsServer.Restart();
        };

        _httpServer.CheckUpdateRequested += () =>
        {
            if (this._disposed) return;
            _ = Task.Run(async () => await _updateService.CheckForUpdatesAsync(isManualCheck: true));
        };

        _httpServer.BillingCycleStartDayChanged += (day) =>
        {
            if (this._disposed) return;
            _settings.BillingCycleStartDay = day;
            SaveUserSettings();
            _energyService.UpdateBillingCycleStartDay(day);
        };

        _httpServer.DeviceTypeChanged += (type) =>
        {
            if (this._disposed) return;
            _settings.DeviceType = type;
            SaveUserSettings();
            _powerService.SetDeviceType(type);
        };

        if (_settings.PlnRate > 0)
        {
            _rtssOsdWriter.UpdatePlnConfig(_settings.PlnRate, _settings.PlnHours, _settings.PlnDays);
        }

        // Apply saved settings
        _rtssOsdWriter.IsEnabled = _settings.RtssOsdEnabled;
        if (Enum.TryParse<RtssOsdStyle>(_settings.RtssStyle, out var parsedStyle))
            _rtssOsdWriter.Style = parsedStyle;
        else
            _rtssOsdWriter.Style = RtssOsdStyle.FullAllInOne;

        // Build simplified context menu (all settings live in the Dashboard UI)
        _statusItem = new ToolStripMenuItem("Status: Starting…") { Enabled = false };
        _showDashboardItem = new ToolStripMenuItem("Buka Dashboard", null, OnToggleDashboardClicked);
        var exitItem = new ToolStripMenuItem("Keluar", null, OnExitClicked);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_showDashboardItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        // Tray icon — use built-in Windows application icon
        _trayIcon = new NotifyIcon
        {
            Text            = "LegaxyyFPS",
            Icon            = GetAppIcon(),
            ContextMenuStrip = contextMenu,
            Visible         = true
        };
        _trayIcon.DoubleClick += (_, _) => OnToggleDashboardClicked(null, EventArgs.Empty);

        // Status refresh timer (every 2s)
        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();

        // Start services
        _hwService.Start();
        _rtssService.Start();
        _energyService.Start();
        _rtssOsdWriter.Start();
        _wsServer.Start();
        _httpServer.Start();
        _statusTimer.Start();

        // Push initial settings state to HTTP server for dashboard API
        _httpServer.SetSettingsState(
            _settings.Mode,
            _settings.RtssOsdEnabled,
            _settings.RtssStyle,
            IsStartupTaskEnabled(),
            _settings.BillingCycleStartDay,
            _wsServer.ClientCount
        );
        _httpServer.UpdateDeviceType(_settings.DeviceType);

        RefreshStatus();
        _logger.Info($"TrayApp: Initialized. WS {wsBindAddress}:{wsPort}, HTTP port={_httpPort}");

        // Apply initial mode
        ApplyMode(_settings.Mode, notifyUser: false);

        // Check for updates silently in the background after startup
        _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            await _updateService.CheckForUpdatesAsync(isManualCheck: false);
        });
    }

    // ─── Mode handling ───────────────────────────────────────────────────────
    private void ApplyMode(string mode, bool notifyUser)
    {
        _settings.Mode = mode;
        SaveUserSettings();
        _httpServer.UpdateMode(mode);

        bool isStreamer = string.Equals(mode, "Streamer", StringComparison.OrdinalIgnoreCase);

        if (isStreamer)
        {
            // Streamer Mode: OSD is active + Dashboard Window is shown
            EnsureOverlayWindow();
            if (_overlayWindow != null && !_overlayWindow.Visible)
            {
                _overlayWindow.Show();
                if (_overlayWindow.WindowState == FormWindowState.Minimized)
                    _overlayWindow.WindowState = FormWindowState.Normal;
                _overlayWindow.Activate();
            }
            _showDashboardItem.Checked = true;

            if (notifyUser)
            {
                _trayIcon.ShowBalloonTip(
                    2000,
                    "LegaxyyFPS - Mode Streamer",
                    "Mode Streamer AKTIF: OSD In-Game & Jendela Dashboard aktif untuk OBS / Layar Kedua.",
                    ToolTipIcon.Info
                );
            }
        }
        else
        {
            // Gamer Mode: OSD is active, Dashboard Window stays closed/hidden to save resources
            if (_overlayWindow != null && _overlayWindow.Visible)
            {
                _overlayWindow.Hide();
            }
            _showDashboardItem.Checked = false;

            if (notifyUser)
            {
                _trayIcon.ShowBalloonTip(
                    2000,
                    "LegaxyyFPS - Mode Gamer",
                    "Mode Gamer AKTIF: Hanya OSD In-Game yang aktif. Dashboard ditutup agar performa game maksimal.",
                    ToolTipIcon.Info
                );
            }
        }
    }

    private void EnsureOverlayWindow()
    {
        if (_overlayWindow == null || _overlayWindow.IsDisposed)
        {
            _overlayWindow = new OverlayWindow(_httpPort);
            _overlayWindow.FormClosed += delegate
            {
                _overlayWindow = null;
                if (_showDashboardItem != null) _showDashboardItem.Checked = false;
            };
            _overlayWindow.VisibilityChanged += visible =>
            {
                if (_showDashboardItem != null) _showDashboardItem.Checked = visible;
            };
        }
    }

    private void OnToggleDashboardClicked(object? sender, EventArgs e)
    {
        EnsureOverlayWindow();
        _overlayWindow?.ToggleVisibility();
    }

    // ─── Settings persistence ────────────────────────────────────────────────
    private static string GetSettingsFilePath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegaxyyFPS");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "user_settings.json");
    }

    private static UserSettings LoadUserSettings()
    {
        try
        {
            string path = GetSettingsFilePath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var loaded = JsonConvert.DeserializeObject<UserSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch { }
        return new UserSettings();
    }

    private void SaveUserSettings()
    {
        try
        {
            string path = GetSettingsFilePath();
            string json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    // ─── Other callbacks ─────────────────────────────────────────────────────
    private async void OnCheckUpdateClicked(object? sender, EventArgs e)
    {
        await _updateService.CheckForUpdatesAsync(isManualCheck: true);
    }

    private void OnStartupToggleClicked(object? sender, EventArgs e)
    {
        ToggleStartup(!IsStartupTaskEnabled());
    }

    private void ToggleStartup(bool enable)
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null) throw new Exception("Executable path not found.");
            var dir = System.IO.Path.GetDirectoryName(exePath);

            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "powershell.exe";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            if (enable)
            {
                string psScript = $@"
                    $action = New-ScheduledTaskAction -Execute '{exePath}' -WorkingDirectory '{dir}';
                    $trigger = New-ScheduledTaskTrigger -AtLogOn;
                    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0;
                    Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Trigger $trigger -RunLevel Highest -Settings $settings -Force;
                ".Replace("\r\n", " ").Replace("\n", " ");
                process.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"";
            }
            else
            {
                process.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false\"";
            }
            
            process.Start();
            process.WaitForExit();
            
            if (process.ExitCode == 0)
            {
                _httpServer.UpdateStartupState(enable);
                _logger.Info($"TrayApp: Run on startup set to {enable}");
            }
            else
            {
                throw new Exception($"powershell exited with code {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"TrayApp: Failed to toggle startup: {ex.Message}");
            MessageBox.Show("Gagal mengatur auto-startup.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnRestartClicked(object? sender, EventArgs e)
    {
        _logger.Info("TrayApp: User requested WebSocket server restart.");
        _statusItem.Text = "Status: Restarting…";
        _wsServer.Restart();
        RefreshStatus();
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _logger.Info("TrayApp: User requested exit.");
        _trayIcon.Visible = false;
        Dispose();
        Application.Exit();
    }

    // ─── startup helpers ─────────────────────────────────────────────────────
    private bool IsStartupTaskEnabled()
    {
        try
        {
            var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (currentExe == null) return false;

            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "powershell.exe";
            process.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"" +
                $"$t = Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue; " +
                $"if ($t -eq $null) {{ exit 1 }}; " +
                $"$exe = ($t.Actions | Select-Object -First 1).Execute -replace '\\\"',''; " +
                $"if ($exe -ieq '{currentExe.Replace("'", "''")}') {{ exit 0 }} else {{ exit 2 }}\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.Start();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    // ─── status helpers ──────────────────────────────────────────────────────
    private void RefreshStatus()
    {
        if (_disposed) return;
        var status = _wsServer.IsRunning
            ? $"Status: Running — {_wsServer.ClientCount} client(s)"
            : "Status: Stopped";
        _statusItem.Text = status;
        _trayIcon.Text   = $"LegaxyyFPS\n{status}";
        _httpServer.UpdateWsClientCount(_wsServer.ClientCount);
    }

    // ─── icon helper ─────────────────────────────────────────────────────────
    private static Icon GetAppIcon()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath != null)
            {
                var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon != null) return icon;
            }
        }
        catch { }

        return SystemIcons.Application;
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────
    public void Dispose()
    {
        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _rtssOsdWriter.Dispose();
        _energyService.Dispose();
        _httpServer.Dispose();
        _wsServer.Dispose();
        _rtssService.Dispose();
        _hwService.Dispose();
        _logger.Dispose();
        _trayIcon.Dispose();
        
        if (_overlayWindow != null && !_overlayWindow.IsDisposed)
        {
            _overlayWindow.ForceClose();
            _overlayWindow.Dispose();
        }
    }
}
