using Newtonsoft.Json;

namespace OverlayDataBridge.Services;

/// <summary>
/// Tracks real-time accumulative electricity consumption.
/// Samples power draw every second, accumulates kWh, and persists to JSON.
/// Auto-saves every 60 seconds and on dispose to prevent data loss.
/// </summary>
public sealed class EnergyTrackerService : IDisposable
{
    private readonly PowerAggregatorService _powerService;
    private readonly AppLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private Task? _loopTask;
    private Task? _saveTask;
    private bool _disposed;

    // ─── Accumulative state ──────────────────────────────────────────────────
    private double _todayKwh;
    private double _monthKwh;
    private DateTime _todayDate;       // tracks current day
    private int _billingCycleStartDay; // e.g., 1 = 1st of month
    private DateTime _billingPeriodStart;

    // ─── Persisted data model ────────────────────────────────────────────────
    private class EnergyLogData
    {
        public double TodayKwh { get; set; }
        public double MonthKwh { get; set; }
        public string TodayDate { get; set; } = "";
        public string BillingPeriodStart { get; set; } = "";
        public int BillingCycleStartDay { get; set; } = 1;
        public string LastSaved { get; set; } = "";
    }

    private static string GetLogFilePath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegaxyyFPS");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "energy_log.json");
    }

    public EnergyTrackerService(PowerAggregatorService powerService, AppLogger logger, int billingCycleStartDay = 1)
    {
        _powerService = powerService;
        _logger = logger;
        _billingCycleStartDay = Math.Clamp(billingCycleStartDay, 1, 28);
        LoadState();
    }

    public void Start()
    {
        _loopTask = Task.Run(() => SampleLoopAsync(_cts.Token));
        _saveTask = Task.Run(() => AutoSaveLoopAsync(_cts.Token));
        _logger.Info("EnergyTrackerService: Started accumulative energy tracking.");
    }

    // ─── Public getters (thread-safe) ────────────────────────────────────────
    public double GetTodayKwh()  { lock (_lock) return _todayKwh; }
    public double GetMonthKwh()  { lock (_lock) return _monthKwh; }
    public DateTime GetBillingPeriodStart() { lock (_lock) return _billingPeriodStart; }

    public void UpdateBillingCycleStartDay(int day)
    {
        lock (_lock)
        {
            _billingCycleStartDay = Math.Clamp(day, 1, 28);
            RecalcBillingPeriod(DateTime.Now);
        }
        SaveState();
    }

    // ─── Core sampling loop (runs every 1 second) ────────────────────────────
    private async Task SampleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var power = _powerService.GetPowerData();
                double watts = power.TotalW ?? 0;

                if (watts > 0)
                {
                    // kWh for 1 second interval: (W * 1s) / (3600 * 1000)
                    double kwhSample = (watts * 1.0) / (3600.0 * 1000.0);

                    lock (_lock)
                    {
                        var now = DateTime.Now;

                        // Check if day rolled over
                        if (now.Date != _todayDate.Date)
                        {
                            _logger.Info($"EnergyTrackerService: Day rolled over. Yesterday kWh={_todayKwh:F4}");
                            _todayKwh = 0;
                            _todayDate = now.Date;
                        }

                        // Check if billing period rolled over
                        if (now >= GetNextBillingCycleDate())
                        {
                            _logger.Info($"EnergyTrackerService: Billing period rolled over. Period kWh={_monthKwh:F4}");
                            _monthKwh = 0;
                            RecalcBillingPeriod(now);
                        }

                        _todayKwh += kwhSample;
                        _monthKwh += kwhSample;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"EnergyTrackerService: Sample error: {ex.Message}");
            }

            try { await Task.Delay(1000, ct); } catch (TaskCanceledException) { break; }
        }
    }

    // ─── Auto-save loop (every 60 seconds) ───────────────────────────────────
    private async Task AutoSaveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(60_000, ct); } catch (TaskCanceledException) { break; }
            SaveState();
        }
    }

    // ─── Billing period helpers ──────────────────────────────────────────────
    private void RecalcBillingPeriod(DateTime now)
    {
        int day = Math.Min(_billingCycleStartDay, DateTime.DaysInMonth(now.Year, now.Month));
        var cycleStart = new DateTime(now.Year, now.Month, day);
        if (now < cycleStart)
            cycleStart = cycleStart.AddMonths(-1);
        _billingPeriodStart = cycleStart;
    }

    private DateTime GetNextBillingCycleDate()
    {
        var next = _billingPeriodStart.AddMonths(1);
        int day = Math.Min(_billingCycleStartDay, DateTime.DaysInMonth(next.Year, next.Month));
        return new DateTime(next.Year, next.Month, day);
    }

    // ─── Persistence ─────────────────────────────────────────────────────────
    private void LoadState()
    {
        try
        {
            string path = GetLogFilePath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var data = JsonConvert.DeserializeObject<EnergyLogData>(json);
                if (data != null)
                {
                    _billingCycleStartDay = Math.Clamp(data.BillingCycleStartDay, 1, 28);

                    var savedToday = DateTime.TryParse(data.TodayDate, out var td) ? td.Date : DateTime.Now.Date;
                    _todayDate = DateTime.Now.Date;

                    // Only restore today's kWh if date matches
                    _todayKwh = (savedToday == _todayDate) ? data.TodayKwh : 0;

                    // Restore billing period
                    var now = DateTime.Now;
                    RecalcBillingPeriod(now);

                    if (DateTime.TryParse(data.BillingPeriodStart, out var bps))
                    {
                        // If the saved billing period matches current, restore month kWh
                        if (bps.Date == _billingPeriodStart.Date)
                            _monthKwh = data.MonthKwh;
                        else
                            _monthKwh = (savedToday == _todayDate) ? data.TodayKwh : 0;
                    }
                    else
                    {
                        _monthKwh = _todayKwh;
                    }

                    _logger.Info($"EnergyTrackerService: Loaded state — today={_todayKwh:F4} kWh, month={_monthKwh:F4} kWh");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"EnergyTrackerService: Failed to load state: {ex.Message}");
        }

        // First run defaults
        _todayDate = DateTime.Now.Date;
        RecalcBillingPeriod(DateTime.Now);
        _todayKwh = 0;
        _monthKwh = 0;
    }

    private void SaveState()
    {
        try
        {
            EnergyLogData data;
            lock (_lock)
            {
                data = new EnergyLogData
                {
                    TodayKwh = Math.Round(_todayKwh, 6),
                    MonthKwh = Math.Round(_monthKwh, 6),
                    TodayDate = _todayDate.ToString("yyyy-MM-dd"),
                    BillingPeriodStart = _billingPeriodStart.ToString("yyyy-MM-dd"),
                    BillingCycleStartDay = _billingCycleStartDay,
                    LastSaved = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };
            }
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(GetLogFilePath(), json);
        }
        catch (Exception ex)
        {
            _logger.Error($"EnergyTrackerService: Failed to save state: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _loopTask?.Wait(2000); } catch { }
        try { _saveTask?.Wait(2000); } catch { }
        SaveState(); // Final save on shutdown
        _logger.Info("EnergyTrackerService: Disposed. Final state saved.");
    }
}
