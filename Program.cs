using Microsoft.Extensions.Configuration;
using OverlayDataBridge;
using System.Runtime.InteropServices;

namespace OverlayDataBridge;

internal static class Program
{
    // ─── Hide console window (WinExe output type alone isn't always sufficient) ──
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    const int SW_HIDE = 0;

    [STAThread]
    static void Main(string[] args)
    {
        var consoleWindow = GetConsoleWindow();
        if (consoleWindow != IntPtr.Zero)
            ShowWindow(consoleWindow, SW_HIDE);

// ─── Load configuration ───────────────────────────────────────────────────────
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();

// ─── Windows Forms application setup ─────────────────────────────────────────
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

// Handle unhandled exceptions gracefully — log and show balloon, don't crash
Application.ThreadException += (sender, args) =>
{
    try
    {
        // Best-effort log
        var logDir  = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        File.AppendAllText(
            Path.Combine(logDir, "app.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [FATAL] Unhandled ThreadException: {args.Exception}\r\n");
    }
    catch { }

    MessageBox.Show(
        $"Unhandled error:\n{args.Exception.Message}\n\nSee logs/app.log for details.",
        "Overlay Data Bridge — Error",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
};

AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
{
    try
    {
        var logDir  = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        File.AppendAllText(
            Path.Combine(logDir, "app.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [FATAL] UnhandledException: {args.ExceptionObject}\r\n");
    }
    catch { }
};

        // ─── Run tray application ────────────────────────────────────────────────────
        using var app = new TrayApp(config);

        // Keep the message loop running — Application.Run() without a Form keeps
        // the NotifyIcon alive and processes Windows messages (required for tray icon).
        Application.Run();
    }
}
