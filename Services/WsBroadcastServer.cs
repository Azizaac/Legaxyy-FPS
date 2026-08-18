using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using OverlayDataBridge.Models;

namespace OverlayDataBridge.Services;

/// <summary>
/// WebSocket broadcast server using Fleck.
/// Manages client connections and broadcasts JSON payloads to all connected clients
/// at a throttled rate (BroadcastIntervalMs), collecting latest data on each tick.
/// </summary>
public sealed class WsBroadcastServer : IDisposable
{
    // ─── dependencies ────────────────────────────────────────────────────────
    private readonly HardwareMonitorService _hwService;
    private readonly RtssReaderService _rtssService;
    private readonly PowerAggregatorService _powerService;
    private readonly AppLogger _logger;
    private readonly int _port;
    private readonly int _broadcastIntervalMs;

    // ─── Fleck state ─────────────────────────────────────────────────────────
    private WebSocketServer? _server;
    private readonly List<IWebSocketConnection> _clients = new();
    private readonly object _clientsLock = new();

    // ─── broadcast loop ───────────────────────────────────────────────────────
    private CancellationTokenSource _cts = new();
    private Task? _broadcastTask;

    // ─── JSON serializer settings (camelCase) ────────────────────────────────
    private static readonly JsonSerializerSettings _jsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Include,
        Formatting = Formatting.None
    };

    // ─── ctor ────────────────────────────────────────────────────────────────
    public WsBroadcastServer(
        int port,
        int broadcastIntervalMs,
        HardwareMonitorService hwService,
        RtssReaderService rtssService,
        PowerAggregatorService powerService,
        AppLogger logger)
    {
        _port                = port;
        _broadcastIntervalMs = broadcastIntervalMs;
        _hwService           = hwService;
        _rtssService         = rtssService;
        _powerService        = powerService;
        _logger              = logger;
    }

    // ─── public API ──────────────────────────────────────────────────────────
    public bool IsRunning => _server != null;

    public int ClientCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    public void Start()
    {
        if (_server != null) return;

        try
        {
            // Fleck suppresses its own console output; set log action to our logger
            FleckLog.LogAction = (level, message, ex) =>
            {
                var msg = ex != null ? $"{message} — {ex.Message}" : message;
                if (level == LogLevel.Error || level == LogLevel.Warn)
                    _logger.Error($"[Fleck/{level}] {msg}");
                else
                    _logger.Info($"[Fleck/{level}] {msg}");
            };

            _server = new WebSocketServer($"ws://0.0.0.0:{_port}");
            _server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    lock (_clientsLock) _clients.Add(socket);
                    _logger.Info($"WsBroadcastServer: Client connected [{socket.ConnectionInfo.ClientIpAddress}]. Total: {ClientCount}");
                };

                socket.OnClose = () =>
                {
                    lock (_clientsLock) _clients.Remove(socket);
                    _logger.Info($"WsBroadcastServer: Client disconnected. Total: {ClientCount}");
                };

                socket.OnError = ex =>
                {
                    _logger.Error($"WsBroadcastServer: Socket error — {ex.Message}");
                    lock (_clientsLock) _clients.Remove(socket);
                };
            });

            _logger.Info($"WsBroadcastServer: Listening on ws://localhost:{_port}");
        }
        catch (Exception ex)
        {
            _logger.Error($"WsBroadcastServer: Failed to start server. {ex.Message}");
            _server = null;
            return;
        }

        // Start broadcast loop
        _cts = new CancellationTokenSource();
        _broadcastTask = Task.Run(() => BroadcastLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _broadcastTask?.Wait(3000); } catch { /* swallow */ }

        lock (_clientsLock)
        {
            foreach (var client in _clients)
            {
                try { client.Close(); } catch { /* swallow */ }
            }
            _clients.Clear();
        }

        try { _server?.Dispose(); } catch { /* swallow */ }
        _server = null;
        _logger.Info("WsBroadcastServer: Stopped.");
    }

    public void Restart()
    {
        _logger.Info("WsBroadcastServer: Restarting...");
        Stop();
        Thread.Sleep(500); // give OS time to release port
        Start();
    }

    // ─── broadcast loop ───────────────────────────────────────────────────────
    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                BroadcastSnapshot();
            }
            catch (Exception ex)
            {
                _logger.Error($"WsBroadcastServer: Broadcast error. {ex.Message}");
            }

            try { await Task.Delay(_broadcastIntervalMs, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private void BroadcastSnapshot()
    {
        List<IWebSocketConnection> snapshot;
        lock (_clientsLock)
        {
            if (_clients.Count == 0) return;
            snapshot = new List<IWebSocketConnection>(_clients);
        }

        var payload = BuildPayload();
        var json    = JsonConvert.SerializeObject(payload, _jsonSettings);

        var toRemove = new List<IWebSocketConnection>();
        foreach (var client in snapshot)
        {
            try
            {
                if (client.IsAvailable)
                    client.Send(json);
                else
                    toRemove.Add(client);
            }
            catch
            {
                toRemove.Add(client);
            }
        }

        if (toRemove.Count > 0)
        {
            lock (_clientsLock)
            {
                foreach (var dead in toRemove) _clients.Remove(dead);
            }
        }
    }

    private object BuildPayload()
    {
        var cpu    = _hwService.GetCpuData();
        var gpu    = _hwService.GetGpuData();
        var mem    = _hwService.GetMemData();
        var fps    = _rtssService.GetFpsData();
        var power  = _powerService.GetPowerData();
        var device = _hwService.GetDeviceInfo();

        return new
        {
            device = new
            {
                cpuName  = device.CpuName,
                gpuName  = device.GpuName,
                ramLabel = device.RamLabel
            },
            cpu = new
            {
                temp  = cpu.Temp,
                load  = cpu.Load,
                clock = cpu.Clock,
                power = cpu.Power
            },
            mem = new
            {
                load    = mem.Load,
                usedGb  = mem.UsedGb,
                totalGb = mem.TotalGb
            },
            gpu = new
            {
                temp        = gpu.Temp,
                hotSpotTemp = gpu.HotSpotTemp,
                memTemp     = gpu.MemTemp,
                load        = gpu.Load,
                coreClock   = gpu.CoreClock,
                memClock    = gpu.MemClock,
                fanRpm      = gpu.FanRpm,
                vramUsedGb  = gpu.VramUsedGb,
                vramTotalGb = gpu.VramTotalGb,
                vramPct     = (gpu.VramUsedGb.HasValue && gpu.VramTotalGb.HasValue && gpu.VramTotalGb > 0)
                                  ? Math.Round(gpu.VramUsedGb.Value / gpu.VramTotalGb.Value * 100.0, 1)
                                  : (double?)null,
                power       = gpu.Power
            },
            fps = new
            {
                current     = fps.Current,
                low1pct     = fps.Low1Pct,
                low01pct    = fps.Low01Pct,
                frametimeMs = fps.FrametimeMs
            },
            power = new
            {
                totalW     = power.TotalW,
                isEstimate = power.IsEstimate
            }
        };
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────
    public void Dispose() => Stop();
}
