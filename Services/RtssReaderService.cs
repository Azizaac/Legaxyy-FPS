using System.IO.MemoryMappedFiles;
using OverlayDataBridge.Models;

namespace OverlayDataBridge.Services;

/// <summary>
/// Reads real-time FPS and frametime from RTSS (RivaTuner Statistics Server) shared memory.
///
/// RTSSSharedMemoryV2 AppEntry offsets:
///   0x000 dwProcessID      DWORD
///   0x004 szName[260]      CHAR
///   0x108 dwFlags          DWORD
///   0x10C dwTime0          DWORD  (ms period start)
///   0x110 dwTime1          DWORD  (ms period end)
///   0x114 dwFrames         DWORD  (frames rendered during dwTime1 - dwTime0)
///   0x118 dwFrameTime      DWORD  (last frame time in MICROSECONDS)
///   0x11C dwStatFlags      DWORD
///   0x120 dwStatTime0      DWORD
///   0x124 dwStatTime1      DWORD
///   0x128 dwStatFrames     DWORD
///   0x12C dwStatCount      DWORD
///   0x130 dwStatFramerateMin DWORD
///   0x134 dwStatFramerateAvg DWORD
///   0x138 dwStatFramerateMax DWORD
/// </summary>
public sealed class RtssReaderService : IDisposable
{
    private const string SharedMemoryName = "RTSSSharedMemoryV2";
    private const uint   RTSS_SIG         = 0x52545353; // 'RTSS'

    // Header offsets
    private const long H_SIG   = 0x00;
    private const long H_VER   = 0x04;
    private const long H_ESIZ  = 0x08;
    private const long H_AOFF  = 0x0C;
    private const long H_ACNT  = 0x10;

    // AppEntry field offsets
    private const long F_PID   = 0x000;
    private const long F_NAME  = 0x004;
    private const long F_FLAGS = 0x108;
    private const long F_T0    = 0x10C;
    private const long F_T1    = 0x110;
    private const long F_FRM   = 0x114;
    private const long F_FT    = 0x118; // dwFrameTime (μs)
    private const long F_S_AVG = 0x134;

    // ─── Circular Buffer for 1% Lows ──────────────────────────────────────────
    private const int BUF = 500;
    private readonly float[] _ftBuf = new float[BUF];
    private int _head = 0, _count = 0;

    // ─── State ────────────────────────────────────────────────────────────────
    private readonly int       _intervalMs;
    private readonly AppLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object    _lock = new();
    private FpsData _data     = new();
    private Task?   _loopTask;

    private uint    _lastPid       = 0;
    private uint    _lastTime1     = 0;
    private uint    _lastFrameTime = 0;
    private double? _smoothedFps   = null;
    private int     _staleCount    = 0;
    private int     _logCooldown   = 0;

    // ─── ctor ────────────────────────────────────────────────────────────────
    public RtssReaderService(int intervalMs, AppLogger logger)
    {
        _intervalMs = intervalMs;
        _logger = logger;
    }

    public void Start()
    {
        _loopTask = Task.Run(() => Loop(_cts.Token));
        _logger.Info("RtssReaderService: started.");
    }

    public FpsData GetFpsData()
    {
        lock (_lock)
            return new FpsData
            {
                Current     = _data.Current,
                Low1Pct     = _data.Low1Pct,
                Low01Pct    = _data.Low01Pct,
                FrametimeMs = _data.FrametimeMs
            };
    }

    // ─── background loop ─────────────────────────────────────────────────────
    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try   { TryRead(); }
            catch (Exception ex) { _logger.Error($"RtssReader: {ex.GetType().Name}: {ex.Message}"); }
            try { await Task.Delay(_intervalMs, ct); } catch (TaskCanceledException) { break; }
        }
    }

    // ─── main reader ─────────────────────────────────────────────────────────
    private void TryRead()
    {
        using var mmf = Open(SharedMemoryName);
        if (mmf == null)
        {
            _logCooldown = 0;
            _smoothedFps = null;
            lock (_lock) _data = new FpsData();
            return;
        }

        using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        long cap = acc.Capacity;
        if (cap < 0x20) return;

        uint sig  = acc.ReadUInt32(H_SIG);
        uint ver  = acc.ReadUInt32(H_VER);
        uint esiz = acc.ReadUInt32(H_ESIZ);
        uint aoff = acc.ReadUInt32(H_AOFF);
        uint acnt = acc.ReadUInt32(H_ACNT);

        if (_logCooldown == 0)
        {
            _logger.Info($"RtssReader: sig=0x{sig:X8} ver=0x{ver:X8} entrySize={esiz} arrOffset=0x{aoff:X} arrCount={acnt}");
        }

        if (sig != RTSS_SIG || esiz == 0 || acnt == 0)
        {
            lock (_lock) _data = new FpsData();
            return;
        }

        // ── Find the best active 3D game entry ────────────────────────────────
        uint bestPid = 0, bestT0 = 0, bestT1 = 0, bestFrames = 0, bestFt = 0;
        bool found = false;

        for (uint i = 0; i < Math.Min(acnt, 256u); i++)
        {
            long @base = aoff + (long)(i * esiz);
            if (@base + F_FT + 4 > cap) break;

            uint pid = acc.ReadUInt32(@base + F_PID);
            if (pid == 0) continue;

            uint t0     = acc.ReadUInt32(@base + F_T0);
            uint t1     = acc.ReadUInt32(@base + F_T1);
            uint frames = acc.ReadUInt32(@base + F_FRM);
            uint ft     = acc.ReadUInt32(@base + F_FT);

            if (_logCooldown == 0)
            {
                _logger.Info($"RtssReader: slot[{i}] PID={pid} t0={t0} t1={t1} frames={frames} ft={ft}μs");
            }

            // An active 3D game has non-zero frame time or frames or time1
            if (ft > 0 || frames > 0 || t1 > 0)
            {
                // Prefer slot with newest timestamp / highest frame activity
                if (!found || t1 >= bestT1 || (t1 == bestT1 && frames > bestFrames))
                {
                    bestPid    = pid;
                    bestT0     = t0;
                    bestT1     = t1;
                    bestFrames = frames;
                    bestFt     = ft;
                    found      = true;
                }
            }
        }

        _logCooldown = (_logCooldown + 1) % 50;

        if (!found)
        {
            _lastPid     = 0;
            _smoothedFps = null;
            lock (_lock) { _head = 0; _count = 0; _data = new FpsData(); }
            return;
        }

        // Reset buffers on game change
        if (bestPid != _lastPid)
        {
            _lastPid       = bestPid;
            _lastTime1     = bestT1;
            _lastFrameTime = bestFt;
            _smoothedFps   = null;
            _staleCount    = 0;
            lock (_lock) { _head = 0; _count = 0; }
            _logger.Info($"RtssReader: Active game detected PID={bestPid}");
        }

        // ── Calculate FPS ─────────────────────────────────────────────────────
        double instantFps = 0;
        double frameTimeMs = 0;

        // 1. Primary: dwFrameTime (instantaneous microsecond frametime)
        if (bestFt > 500 && bestFt < 2_000_000) // between 0.5 FPS and 2000 FPS
        {
            instantFps  = 1_000_000.0 / bestFt;
            frameTimeMs = bestFt / 1000.0;
        }
        // 2. Fallback: dwFrames / (dwTime1 - dwTime0) (period average)
        else if (bestT1 > bestT0 && bestFrames > 0)
        {
            uint dt = bestT1 - bestT0;
            instantFps  = (1000.0 * bestFrames) / dt;
            frameTimeMs = 1000.0 / instantFps;
        }

        if (instantFps is >= 1 and <= 1000)
        {
            _staleCount = 0;
            if (_smoothedFps == null)
                _smoothedFps = instantFps;
            else
                _smoothedFps = (_smoothedFps.Value * 0.3) + (instantFps * 0.7);

            // Record into 1% low buffer
            lock (_lock)
            {
                _ftBuf[_head] = (float)frameTimeMs;
                _head = (_head + 1) % BUF;
                if (_count < BUF) _count++;
            }
        }
        else
        {
            _staleCount++;
            if (_staleCount > 10)
            {
                _smoothedFps = null;
            }
        }

        // ── Compute 1% & 0.1% Lows ────────────────────────────────────────────
        lock (_lock)
        {
            if (!_smoothedFps.HasValue)
            {
                _data = new FpsData();
                return;
            }

            double? curFps = Math.Round(_smoothedFps.Value, 0);
            double? ft     = Math.Round(frameTimeMs > 0 ? frameTimeMs : (1000.0 / _smoothedFps.Value), 1);
            double? low1   = null;
            double? low01  = null;

            if (_count >= 10)
            {
                var samples = new float[_count];
                for (int i = 0; i < _count; i++)
                    samples[i] = _ftBuf[((_head - 1 - i) + BUF) % BUF];

                Array.Sort(samples); // ascending (fast frames first, slow at end)

                int n1   = Math.Max(1, (int)Math.Ceiling(_count * 0.01));
                int n001 = Math.Max(1, (int)Math.Ceiling(_count * 0.001));
                double s1 = 0, s001 = 0;
                for (int i = _count - n1;   i < _count; i++) s1   += samples[i];
                for (int i = _count - n001; i < _count; i++) s001 += samples[i];
                s1 /= n1; s001 /= n001;
                if (s1   > 0) low1  = Math.Round(1000.0 / s1,   0);
                if (s001 > 0) low01 = Math.Round(1000.0 / s001, 0);
            }

            _data = new FpsData
            {
                Current     = curFps,
                FrametimeMs = ft,
                Low1Pct     = low1,
                Low01Pct    = low01
            };
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────
    private static MemoryMappedFile? Open(string name)
    {
        try { return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read); }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _loopTask?.Wait(2000); } catch { }
        try { _cts.Dispose(); } catch { }
        _logger.Info("RtssReaderService: disposed.");
    }
}
