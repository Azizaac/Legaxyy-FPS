namespace OverlayDataBridge.Services;

/// <summary>
/// Simple file logger writing to logs/app.log (relative to exe).
/// Thread-safe via lock. Log files rotate if they exceed ~5 MB.
/// </summary>
public sealed class AppLogger : IDisposable
{
    private static readonly string LogDir  = Path.Combine(AppContext.BaseDirectory, "logs");
    private static readonly string LogFile = Path.Combine(LogDir, "app.log");
    private const long MaxLogSizeBytes = 5 * 1024 * 1024; // 5 MB

    private readonly object _lock = new();
    private StreamWriter? _writer;

    public AppLogger()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            RotateIfNeeded();
            _writer = new StreamWriter(LogFile, append: true, encoding: System.Text.Encoding.UTF8)
            {
                AutoFlush = true
            };
            Info("=== OverlayDataBridge started ===");
        }
        catch
        {
            // If we can't open log file just swallow — logging is best-effort
            _writer = null;
        }
    }

    public void Info(string message)  => Write("INFO ", message);
    public void Error(string message) => Write("ERROR", message);
    public void Warn(string message)  => Write("WARN ", message);

    private void Write(string level, string message)
    {
        if (_writer == null) return;
        lock (_lock)
        {
            try
            {
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
            }
            catch { /* best-effort */ }
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogFile)) return;
        var info = new FileInfo(LogFile);
        if (info.Length > MaxLogSizeBytes)
        {
            string archived = Path.Combine(LogDir, $"app.{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.Move(LogFile, archived, overwrite: true);

            // Keep only 5 archived logs
            var archives = Directory.GetFiles(LogDir, "app.*.log")
                                    .OrderByDescending(f => f)
                                    .Skip(5)
                                    .ToList();
            foreach (var old in archives) try { File.Delete(old); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                Info("=== OverlayDataBridge stopping ===");
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
            catch { }
        }
    }
}
