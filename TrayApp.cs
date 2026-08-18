using Microsoft.Extensions.Configuration;
using OverlayDataBridge.Services;

namespace OverlayDataBridge;

/// <summary>
/// System tray application host.
/// Owns all services and exposes right-click context menu for status/restart/exit.
/// </summary>
public sealed class TrayApp : IDisposable
{
    // ─── services ────────────────────────────────────────────────────────────
    private readonly AppLogger _logger;
    private readonly HardwareMonitorService _hwService;
    private readonly RtssReaderService _rtssService;
    private readonly PowerAggregatorService _powerService;
    private readonly WsBroadcastServer _wsServer;
    private readonly HttpServerService _httpServer;

    // ─── tray UI ─────────────────────────────────────────────────────────────
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    
    private OverlayWindow? _overlayWindow;

    // ─── Task Scheduler config ───────────────────────────────────────────────
    private const string TaskName = "LegaxyyFPSStartup";

    // ─── ctor ────────────────────────────────────────────────────────────────
    private bool _disposed = false;

    public TrayApp(IConfiguration config)
    {
        static int Cfg(IConfiguration c, string key, int def)
            => int.TryParse(c[key], out var v) ? v : def;

        int wsPort            = Cfg(config, "WebSocketPort",          8765);
        int httpPort          = Cfg(config, "HttpPort",               8766);
        int hwInterval        = Cfg(config, "HardwareUpdateIntervalMs", 1000);
        int fpsInterval       = Cfg(config, "FpsUpdateIntervalMs",     300);
        int broadcastInterval = Cfg(config, "BroadcastIntervalMs",    500);

        // Build services
        _logger       = new AppLogger();
        _hwService    = new HardwareMonitorService(hwInterval, _logger);
        _rtssService  = new RtssReaderService(fpsInterval, _logger);
        _powerService = new PowerAggregatorService(_hwService, _logger);
        _wsServer     = new WsBroadcastServer(wsPort, broadcastInterval, _hwService, _rtssService, _powerService, _logger);
        _httpServer   = new HttpServerService(httpPort, wsPort, _logger);

        // Build context menu
        _statusItem = new ToolStripMenuItem("Status: Starting…") { Enabled = false };
        _startupItem = new ToolStripMenuItem("Run on Startup", null, OnStartupToggleClicked) { Checked = IsStartupTaskEnabled() };
        var showOverlayItem = new ToolStripMenuItem("Buka Layar Overlay (Native)", null, OnShowOverlayClicked);
        var restartItem = new ToolStripMenuItem("Restart WebSocket Server", null, OnRestartClicked);
        var exitItem    = new ToolStripMenuItem("Exit", null, OnExitClicked);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(showOverlayItem);
        contextMenu.Items.Add(_startupItem);
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
        _trayIcon.DoubleClick += (_, _) => ShowBalloonStatus();

        // Status refresh timer (every 2s)
        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();

        // Start services
        _hwService.Start();
        _rtssService.Start();
        _wsServer.Start();
        _httpServer.Start();
        _statusTimer.Start();

        RefreshStatus();
        _logger.Info($"TrayApp: Initialized. WS port={wsPort}, HTTP port={httpPort}");

        // Automatically open the Overlay Window when application starts
        OnShowOverlayClicked(null, EventArgs.Empty);
    }

    // ─── tray callbacks ──────────────────────────────────────────────────────
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

    private void OnShowOverlayClicked(object? sender, EventArgs e)
    {
        if (_overlayWindow == null || _overlayWindow.IsDisposed)
        {
            _overlayWindow = new OverlayWindow();
            _overlayWindow.FormClosed += delegate { _overlayWindow = null; };
            _overlayWindow.Show();
        }
        else
        {
            if (_overlayWindow.WindowState == FormWindowState.Minimized)
                _overlayWindow.WindowState = FormWindowState.Normal;
            _overlayWindow.Activate();
        }
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
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "powershell.exe";
            process.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction Stop\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
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

    private void ShowBalloonStatus()
    {
        _trayIcon.ShowBalloonTip(3000,
            "LegaxyyFPS",
            _wsServer.IsRunning
                ? $"Running — {_wsServer.ClientCount} client(s)\nhttp://localhost:8766"
                : "WebSocket server stopped. Right-click → Restart.",
            ToolTipIcon.Info);
    }

    // ─── icon helper ─────────────────────────────────────────────────────────
    private static Icon GetAppIcon()
    {
        // Try to load from executable's resources first, fall back to system icon
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

        // Fallback: Windows information icon
        return SystemIcons.Application;
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────
    public void Dispose()
    {
        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Dispose();
        _httpServer.Dispose();
        _wsServer.Dispose();
        _rtssService.Dispose();
        _hwService.Dispose();
        _logger.Dispose();
        _trayIcon.Dispose();
        
        if (_overlayWindow != null && !_overlayWindow.IsDisposed)
            _overlayWindow.Dispose();
    }
}
