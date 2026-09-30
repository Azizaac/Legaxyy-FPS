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

    // ─── settings ────────────────────────────────────────────────────────────
    private UserSettings _settings;
    private readonly int _httpPort;

    // ─── tray UI ─────────────────────────────────────────────────────────────
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _modeGamerItem;
    private readonly ToolStripMenuItem _modeStreamerItem;
    private readonly ToolStripMenuItem _rtssOsdToggleItem;
    private readonly ToolStripMenuItem _rtssStyleAllInOne;
    private readonly ToolStripMenuItem _rtssStyleHorizontal;
    private readonly ToolStripMenuItem _rtssStyleStacked;
    private readonly ToolStripMenuItem _rtssStyleMinimal;
    private readonly ToolStripMenuItem _showDashboardItem;
    private readonly ToolStripMenuItem _startupItem;
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

        _settings = LoadUserSettings();

        // Build services
        _logger        = new AppLogger();
        _hwService     = new HardwareMonitorService(hwInterval, _logger);
        _rtssService   = new RtssReaderService(fpsInterval, _logger);
        _powerService  = new PowerAggregatorService(_hwService, _logger);
        _rtssOsdWriter = new RtssOsdWriterService(_hwService, _rtssService, _powerService, config, _logger);
        _wsServer      = new WsBroadcastServer(wsPort, broadcastInterval, _hwService, _rtssService, _powerService, _logger);
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

        // Build context menu
        _statusItem = new ToolStripMenuItem("Status: Starting…") { Enabled = false };

        // 2 Modes: Gamer (OSD Only) vs Streamer (OSD + Dashboard)
        _modeGamerItem = new ToolStripMenuItem("🎮 Mode Gamer (Hanya OSD In-Game)", null, OnModeGamerClicked);
        _modeStreamerItem = new ToolStripMenuItem("🎥 Mode Streamer (OSD + Dashboard Window)", null, OnModeStreamerClicked);

        // RTSS In-Game OSD Injection controls
        _rtssOsdToggleItem = new ToolStripMenuItem("📊 In-Game OSD (RivaTuner / RTSS)", null, OnToggleRtssOsdClicked)
        {
            Checked = _rtssOsdWriter.IsEnabled
        };
        
        var rtssStyleMenu = new ToolStripMenuItem("🎨 Gaya Tampilan OSD In-Game");
        _rtssStyleAllInOne   = new ToolStripMenuItem("Lengkap + Hotspot + VRAM + FT (All-In-One)", null, (_, _) => SetRtssStyle(RtssOsdStyle.FullAllInOne));
        _rtssStyleHorizontal = new ToolStripMenuItem("Baris Horizontal (Cyberpunk)", null, (_, _) => SetRtssStyle(RtssOsdStyle.HorizontalBar));
        _rtssStyleStacked    = new ToolStripMenuItem("Kotak Bertumpuk (2 Baris)", null, (_, _) => SetRtssStyle(RtssOsdStyle.StackedBlock));
        _rtssStyleMinimal    = new ToolStripMenuItem("Minimalis Ringkas", null, (_, _) => SetRtssStyle(RtssOsdStyle.Minimal));
        
        rtssStyleMenu.DropDownItems.Add(_rtssStyleAllInOne);
        rtssStyleMenu.DropDownItems.Add(_rtssStyleHorizontal);
        rtssStyleMenu.DropDownItems.Add(_rtssStyleStacked);
        rtssStyleMenu.DropDownItems.Add(_rtssStyleMinimal);
        UpdateRtssStyleMenuChecks(_rtssOsdWriter.Style);

        // Dashboard Window controls
        _showDashboardItem = new ToolStripMenuItem("🖥️ Buka Jendela Dashboard (F11)", null, OnToggleDashboardClicked)
        {
            Checked = false
        };

        _startupItem = new ToolStripMenuItem("Run on Startup", null, OnStartupToggleClicked) { Checked = IsStartupTaskEnabled() };
        var checkUpdateItem = new ToolStripMenuItem("Periksa Pembaruan...", null, OnCheckUpdateClicked);
        var restartItem = new ToolStripMenuItem("Restart WebSocket Server", null, OnRestartClicked);
        var exitItem    = new ToolStripMenuItem("Exit", null, OnExitClicked);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_modeGamerItem);
        contextMenu.Items.Add(_modeStreamerItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_rtssOsdToggleItem);
        contextMenu.Items.Add(rtssStyleMenu);
        contextMenu.Items.Add(_showDashboardItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_startupItem);
        contextMenu.Items.Add(checkUpdateItem);
        contextMenu.Items.Add(restartItem);
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
        _rtssOsdWriter.Start();
        _wsServer.Start();
        _httpServer.Start();
        _statusTimer.Start();

        RefreshStatus();
        _logger.Info($"TrayApp: Initialized. WS port={wsPort}, HTTP port={_httpPort}");

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
    private void OnModeGamerClicked(object? sender, EventArgs e)
    {
        ApplyMode("Gamer", notifyUser: true);
    }

    private void OnModeStreamerClicked(object? sender, EventArgs e)
    {
        ApplyMode("Streamer", notifyUser: true);
    }

    private void ApplyMode(string mode, bool notifyUser)
    {
        _settings.Mode = mode;
        SaveUserSettings();

        bool isStreamer = string.Equals(mode, "Streamer", StringComparison.OrdinalIgnoreCase);
        _modeStreamerItem.Checked = isStreamer;
        _modeGamerItem.Checked    = !isStreamer;

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
                    "Mode Streamer AKTIF: OSD In-Game & Jendela Dashboard aktif untuk OBS / Layar Kedua. Tekan F11 untuk sembunyikan dashboard kapan saja.",
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
                    "Mode Gamer AKTIF: Hanya OSD In-Game yang aktif. Jendela Dashboard ditutup agar performa game maksimal (hemat CPU/GPU/RAM).",
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

    // ─── RTSS callbacks ──────────────────────────────────────────────────────
    private void OnToggleRtssOsdClicked(object? sender, EventArgs e)
    {
        _rtssOsdWriter.IsEnabled = !_rtssOsdWriter.IsEnabled;
        _rtssOsdToggleItem.Checked = _rtssOsdWriter.IsEnabled;
        _settings.RtssOsdEnabled = _rtssOsdWriter.IsEnabled;
        SaveUserSettings();

        _trayIcon.ShowBalloonTip(
            1500,
            "LegaxyyFPS RTSS In-Game OSD",
            _rtssOsdWriter.IsEnabled ? "OSD In-Game (RivaTuner) AKTIF" : "OSD In-Game NONAKTIF",
            ToolTipIcon.Info
        );
    }

    private void SetRtssStyle(RtssOsdStyle style)
    {
        _rtssOsdWriter.Style = style;
        _settings.RtssStyle = style.ToString();
        SaveUserSettings();
        UpdateRtssStyleMenuChecks(style);
    }

    private void UpdateRtssStyleMenuChecks(RtssOsdStyle style)
    {
        _rtssStyleAllInOne.Checked   = (style == RtssOsdStyle.FullAllInOne);
        _rtssStyleHorizontal.Checked = (style == RtssOsdStyle.HorizontalBar);
        _rtssStyleStacked.Checked    = (style == RtssOsdStyle.StackedBlock);
        _rtssStyleMinimal.Checked    = (style == RtssOsdStyle.Minimal);
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
        bool enable = !_startupItem.Checked;
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
                _startupItem.Checked = enable;
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
