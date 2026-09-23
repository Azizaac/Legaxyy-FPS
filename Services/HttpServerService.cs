using System.Net;
using System.Text;

namespace OverlayDataBridge.Services;

public sealed class HttpServerService : IDisposable
{
    private readonly HttpListener _listener;
    private readonly AppLogger _logger;
    private readonly int _port;
    private readonly int _wsPort;
    private CancellationTokenSource _cts = new();
    private Task? _serverTask;

    public HttpServerService(int port, int wsPort, AppLogger logger)
    {
        _port = port;
        _wsPort = wsPort;
        _logger = logger;
        _listener = new HttpListener();
    }

    public void Start()
    {
        try
        {
            // Try wildcard first (works if admin or urlacl registered), fallback to localhost/127.0.0.1
            try
            {
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
                _logger.Info($"HttpServerService: Listening on http://localhost:{_port}/ and http://127.0.0.1:{_port}/");
            }
            catch
            {
                _listener.Close();
                _cts = new CancellationTokenSource();
                var fallbackListener = new HttpListener();
                fallbackListener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                fallbackListener.Start();
                _logger.Info($"HttpServerService: Listening on fallback http://127.0.0.1:{_port}/");
            }
            _serverTask = Task.Run(() => ServerLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            _logger.Error($"HttpServerService: Failed to start listener: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _serverTask?.Wait(3000); } catch { }
        _logger.Info("HttpServerService: Stopped.");
    }

    private async Task ServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context), ct);
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.Error($"HttpServerService: Context error - {ex.Message}");
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            string customHtmlPath = Path.Combine(AppContext.BaseDirectory, "index.html");
            string html = File.Exists(customHtmlPath)
                ? File.ReadAllText(customHtmlPath, Encoding.UTF8)
                : GetHtmlContent();
            byte[] buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentLength64 = buffer.Length;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.StatusCode = 200;
            
            using var ros = context.Response.OutputStream;
            ros.Write(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
            _logger.Error($"HttpServerService: Failed to handle request. {ex.Message}");
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private string GetHtmlContent()
    {
        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>LegaxyyFPS Overlay</title>
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=Plus+Jakarta+Sans:wght@500;600;700;800;900&family=JetBrains+Mono:wght@400;500;600;700;800;900&display=swap" rel="stylesheet">
  <style>
    :root {
      --bg: #06070a;
      --card-bg: rgba(13, 15, 22, 0.96);
      --card-border: rgba(255, 255, 255, 0.14);
      
      --cpu: #38bdf8;
      --cpu-glow: rgba(56, 189, 248, 0.35);
      
      --gpu: #c084fc;
      --gpu-glow: rgba(192, 132, 252, 0.35);
      
      --ram: #34d399;
      --ram-glow: rgba(52, 211, 153, 0.35);
      
      --vram: #818cf8;
      --vram-glow: rgba(129, 140, 248, 0.35);
      
      --fps: #fb923c;
      --fps-glow: rgba(251, 146, 60, 0.4);
      
      --pwr: #f472b6;
      --pwr-glow: rgba(244, 114, 182, 0.35);
      
      --text-main: #ffffff;
      --text-muted: #e2e8f0;
      --text-dim: #64748b;
      --hot: #f43f5e;
      --warn: #fbbf24;
    }

    *, *::before, *::after {
      box-sizing: border-box;
      margin: 0;
      padding: 0;
    }

    html {
      width: 100%;
      height: 100%;
      background: var(--bg);
      overflow: hidden;
    }

    body {
      width: 1920px;
      height: 1080px;
      background: var(--bg);
      font-family: 'Plus Jakarta Sans', system-ui, sans-serif;
      -webkit-font-smoothing: antialiased;
      user-select: none;
      overflow: hidden;
      margin: 0;
      padding: 0;
      position: absolute;
      top: 0;
      left: 0;
    }

    /* ── Main Container ── */
    .hud-stage {
      position: absolute;
      inset: 16px;
      display: flex;
      flex-direction: column;
      gap: 16px;
    }

    /* ── Tactical Card Surface ── */
    .hud-card {
      position: relative;
      background: var(--card-bg);
      border: 2px solid var(--card-border);
      border-top: 2.5px solid rgba(255, 255, 255, 0.3);
      border-radius: 24px;
      padding: 20px 28px;
      display: flex;
      flex-direction: column;
      box-shadow: 0 24px 48px -12px rgba(0, 0, 0, 0.85);
      backdrop-filter: blur(24px);
      overflow: hidden;
    }

    /* ── Card Header Badge ── */
    .hud-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 12px;
    }

    .hud-tag {
      display: inline-flex;
      align-items: center;
      gap: 10px;
      background: rgba(255, 255, 255, 0.08);
      padding: 6px 18px;
      border-radius: 12px;
      border: 1.5px solid rgba(255, 255, 255, 0.15);
    }

    .hud-dot {
      width: 14px;
      height: 14px;
      border-radius: 50%;
      box-shadow: 0 0 14px currentColor;
    }

    .hud-title {
      font-size: 28px;
      font-weight: 900;
      letter-spacing: 0.12em;
      text-transform: uppercase;
      color: var(--text-main);
    }

    .hud-device-name {
      font-size: 28px;
      font-weight: 800;
      color: #ffffff;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      max-width: 520px;
      text-shadow: 0 0 16px rgba(255,255,255,0.4);
    }

    /* ── Grid Layouts ── */
    .top-grid {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 16px;
      flex: 1.15;
    }

    .bot-grid {
      display: grid;
      grid-template-columns: 1fr 1fr 1fr 1fr;
      gap: 16px;
      flex: 1;
    }

    /* ── Temperature Hero Display ── */
    .hero-temp-box {
      display: inline-flex;
      align-items: baseline;
      gap: 10px;
      padding: 10px 28px;
      border-radius: 22px;
      margin-bottom: 12px;
      background: rgba(255, 255, 255, 0.06);
      border: 2px solid rgba(255, 255, 255, 0.15);
      box-shadow: 0 0 28px currentColor;
      width: fit-content;
    }

    .hero-temp-val {
      font-family: 'JetBrains Mono', monospace;
      font-size: 88px;
      font-weight: 900;
      letter-spacing: -0.04em;
      line-height: 1;
      font-variant-numeric: tabular-nums;
    }

    .hero-temp-unit {
      font-size: 38px;
      font-weight: 900;
      opacity: 0.85;
    }

    .hero-temp-sub {
      font-size: 20px;
      font-weight: 900;
      letter-spacing: 0.1em;
      text-transform: uppercase;
      color: #ffffff;
      background: rgba(255, 255, 255, 0.14);
      padding: 4px 12px;
      border-radius: 8px;
      margin-left: 8px;
    }

    /* ── Thermal & Load Meter ── */
    .meter-row {
      display: flex;
      align-items: center;
      gap: 14px;
      margin-bottom: 12px;
    }

    .meter-label {
      font-size: 22px;
      font-weight: 900;
      letter-spacing: 0.1em;
      color: var(--text-main);
      min-width: 60px;
    }

    .meter-track {
      flex: 1;
      height: 20px;
      background: rgba(255, 255, 255, 0.08);
      border-radius: 10px;
      overflow: hidden;
      position: relative;
    }

    .meter-fill {
      height: 100%;
      width: 0%;
      border-radius: 10px;
      transition: width 0.3s cubic-bezier(0.4, 0, 0.2, 1);
      box-shadow: 0 0 18px currentColor;
    }

    .meter-val {
      font-family: 'JetBrains Mono', monospace;
      font-size: 38px;
      font-weight: 900;
      color: var(--text-main);
      min-width: 110px;
      text-align: right;
      font-variant-numeric: tabular-nums;
    }

    /* ── Telemetry Data Rows ── */
    .stat-list {
      display: flex;
      flex-direction: column;
      gap: 4px;
      margin-top: auto;
      border-top: 2px solid rgba(255, 255, 255, 0.1);
      padding-top: 10px;
    }

    .stat-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 2px 0;
    }

    .stat-label {
      font-size: 28px;
      font-weight: 800;
      color: var(--text-muted);
    }

    .stat-value {
      font-family: 'JetBrains Mono', monospace;
      font-size: 36px;
      font-weight: 900;
      color: #ffffff;
      font-variant-numeric: tabular-nums;
    }

    .stat-value .unit {
      font-size: 22px;
      font-weight: 800;
      color: #94a3b8;
      margin-left: 6px;
    }

    /* ── Radial Gauges (RAM / VRAM) ── */
    .gauge-container {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      flex: 1;
      margin: 0;
    }

    .gauge-box {
      position: relative;
      width: 150px;
      height: 150px;
    }

    .gauge-box svg {
      width: 150px;
      height: 150px;
      transform: rotate(-90deg);
    }

    .gauge-bg {
      fill: none;
      stroke: rgba(255, 255, 255, 0.08);
      stroke-width: 16;
    }

    .gauge-progress {
      fill: none;
      stroke-width: 16;
      stroke-linecap: round;
      transition: stroke-dashoffset 0.4s ease, stroke 0.3s;
    }

    .gauge-center-text {
      position: absolute;
      inset: 0;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
    }

    .gauge-pct {
      font-family: 'JetBrains Mono', monospace;
      font-size: 52px;
      font-weight: 900;
      line-height: 1;
      font-variant-numeric: tabular-nums;
    }

    .gauge-pct-unit {
      font-size: 16px;
      font-weight: 900;
      color: var(--text-muted);
      margin-top: 2px;
    }

    .vram-temp-pill {
      font-family: 'JetBrains Mono', monospace;
      font-size: 22px;
      font-weight: 900;
      padding: 4px 14px;
      border-radius: 12px;
      background: rgba(129, 140, 248, 0.18);
      border: 2px solid rgba(129, 140, 248, 0.5);
      color: var(--vram);
    }

    /* ── FPS Tactical Center ── */
    .fps-hero {
      display: flex;
      align-items: baseline;
      gap: 10px;
      margin: 2px 0 6px;
    }

    .fps-main-num {
      font-family: 'JetBrains Mono', monospace;
      font-size: 96px;
      font-weight: 900;
      line-height: 0.9;
      letter-spacing: -0.04em;
      color: var(--fps);
      text-shadow: 0 0 32px var(--fps-glow);
      font-variant-numeric: tabular-nums;
    }

    .fps-label-text {
      font-size: 26px;
      font-weight: 900;
      color: var(--text-muted);
      text-transform: uppercase;
    }

    .fps-live-badge {
      font-size: 16px;
      font-weight: 900;
      letter-spacing: 0.1em;
      padding: 4px 12px;
      border-radius: 10px;
      background: rgba(251, 146, 60, 0.18);
      border: 2px solid rgba(251, 146, 60, 0.5);
      color: var(--fps);
    }

    .fps-frametime {
      font-family: 'JetBrains Mono', monospace;
      font-size: 24px;
      font-weight: 800;
      color: var(--text-muted);
      margin-bottom: 8px;
    }

    .fps-frametime span {
      color: #ffffff;
      font-weight: 900;
    }

    .fps-lows-grid {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 10px;
      margin-top: auto;
      background: rgba(255, 255, 255, 0.05);
      padding: 10px 14px;
      border-radius: 14px;
      border: 1px solid rgba(255, 255, 255, 0.1);
    }

    .fps-low-card {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }

    .fps-low-title {
      font-size: 16px;
      font-weight: 900;
      letter-spacing: 0.08em;
      color: var(--text-muted);
      text-transform: uppercase;
    }

    .fps-low-val {
      font-family: 'JetBrains Mono', monospace;
      font-size: 42px;
      font-weight: 900;
      color: #ffffff;
      line-height: 1;
      font-variant-numeric: tabular-nums;
    }

    /* ── Power & PLN Meter ── */
    .pwr-hero {
      display: flex;
      align-items: baseline;
      gap: 8px;
      margin: 4px 0 10px;
    }

    .pwr-main-val {
      font-family: 'JetBrains Mono', monospace;
      font-size: 84px;
      font-weight: 900;
      line-height: 0.95;
      letter-spacing: -0.03em;
      color: var(--pwr);
      text-shadow: 0 0 28px var(--pwr-glow);
      font-variant-numeric: tabular-nums;
    }

    .pwr-main-unit {
      font-size: 30px;
      font-weight: 900;
      color: var(--text-muted);
    }

    .cost-box {
      margin-top: auto;
      background: linear-gradient(145deg, rgba(74, 222, 128, 0.15), rgba(34, 197, 94, 0.05));
      border: 2px solid rgba(74, 222, 128, 0.35);
      padding: 12px 16px;
      border-radius: 16px;
      display: flex;
      flex-direction: column;
      gap: 2px;
      cursor: pointer;
      transition: all 0.2s ease;
    }

    .cost-box:hover {
      border-color: rgba(74, 222, 128, 0.7);
      box-shadow: 0 0 24px rgba(74, 222, 128, 0.3);
      transform: translateY(-2px);
    }

    .cost-main {
      font-family: 'JetBrains Mono', monospace;
      font-size: 44px;
      font-weight: 900;
      color: #4ade80;
      letter-spacing: -0.03em;
      line-height: 1;
      text-shadow: 0 0 20px rgba(74, 222, 128, 0.5);
      font-variant-numeric: tabular-nums;
    }

    .cost-sub {
      font-size: 15px;
      font-weight: 800;
      color: #86efac;
      display: flex;
      align-items: center;
      justify-content: space-between;
    }

    .cost-sub-hint {
      font-size: 12px;
      font-weight: 700;
      color: rgba(134, 239, 172, 0.7);
      background: rgba(255, 255, 255, 0.06);
      padding: 2px 8px;
      border-radius: 6px;
    }

    /* ── Settings Gear Button ── */
    .hud-settings-btn {
      background: rgba(255, 255, 255, 0.08);
      border: 1.5px solid rgba(255, 255, 255, 0.16);
      color: #e2e8f0;
      width: 38px;
      height: 38px;
      border-radius: 10px;
      font-size: 18px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      cursor: pointer;
      transition: all 0.2s ease;
    }

    .hud-settings-btn:hover {
      background: rgba(74, 222, 128, 0.2);
      border-color: #4ade80;
      color: #4ade80;
      box-shadow: 0 0 16px rgba(74, 222, 128, 0.4);
      transform: rotate(30deg);
    }

    /* ── PLN Modal Dialog ── */
    .pln-modal-overlay {
      position: fixed;
      inset: 0;
      background: rgba(4, 6, 12, 0.85);
      backdrop-filter: blur(14px);
      z-index: 1000;
      display: flex;
      align-items: center;
      justify-content: center;
      animation: modalFadeIn 0.2s ease-out;
    }

    @keyframes modalFadeIn {
      from { opacity: 0; transform: scale(0.96); }
      to { opacity: 1; transform: scale(1); }
    }

    .pln-modal-card {
      background: #0d101a;
      border: 2px solid rgba(255, 255, 255, 0.16);
      border-top: 3px solid #4ade80;
      border-radius: 24px;
      width: 580px;
      max-width: 90vw;
      box-shadow: 0 32px 64px -12px rgba(0, 0, 0, 0.95), 0 0 32px rgba(74, 222, 128, 0.15);
      padding: 26px 32px;
      display: flex;
      flex-direction: column;
      gap: 18px;
    }

    .pln-modal-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      border-bottom: 1.5px solid rgba(255, 255, 255, 0.1);
      padding-bottom: 14px;
    }

    .pln-modal-title {
      display: flex;
      align-items: center;
      gap: 12px;
    }

    .pln-modal-dot {
      width: 14px;
      height: 14px;
      border-radius: 50%;
      background: #4ade80;
      box-shadow: 0 0 14px #4ade80;
    }

    .pln-modal-title h3 {
      font-size: 22px;
      font-weight: 800;
      color: #ffffff;
      letter-spacing: -0.01em;
    }

    .pln-modal-close {
      background: transparent;
      border: none;
      color: #94a3b8;
      font-size: 32px;
      line-height: 1;
      cursor: pointer;
      padding: 0 4px;
      transition: color 0.15s;
    }

    .pln-modal-close:hover {
      color: #ffffff;
    }

    .pln-modal-body {
      display: flex;
      flex-direction: column;
      gap: 16px;
    }

    .pln-form-group {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }

    .pln-label {
      font-size: 14px;
      font-weight: 800;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      color: #cbd5e1;
    }

    .pln-select, .pln-input {
      background: rgba(255, 255, 255, 0.06);
      border: 1.5px solid rgba(255, 255, 255, 0.18);
      border-radius: 12px;
      padding: 12px 16px;
      color: #ffffff;
      font-family: inherit;
      font-size: 16px;
      font-weight: 600;
      outline: none;
      transition: all 0.2s;
    }

    .pln-select:focus, .pln-input:focus {
      border-color: #4ade80;
      box-shadow: 0 0 16px rgba(74, 222, 128, 0.35);
      background: rgba(255, 255, 255, 0.09);
    }

    .pln-select option {
      background: #111524;
      color: #ffffff;
      padding: 10px;
    }

    .pln-form-row {
      display: flex;
      gap: 16px;
    }

    .pln-input-range-wrap {
      position: relative;
      display: flex;
      align-items: center;
    }

    .pln-input-range-wrap .pln-input {
      width: 100%;
      padding-right: 56px;
    }

    .pln-input-addon {
      position: absolute;
      right: 16px;
      font-size: 14px;
      font-weight: 800;
      color: #94a3b8;
      pointer-events: none;
    }

    .pln-preview-box {
      background: linear-gradient(135deg, rgba(74, 222, 128, 0.1), rgba(34, 197, 94, 0.03));
      border: 1.5px dashed rgba(74, 222, 128, 0.35);
      border-radius: 14px;
      padding: 14px 18px;
      display: flex;
      flex-direction: column;
      gap: 4px;
    }

    .pln-preview-label {
      font-size: 12px;
      font-weight: 800;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: #86efac;
    }

    .pln-preview-val {
      font-family: 'JetBrains Mono', monospace;
      font-size: 15px;
      font-weight: 700;
      color: #ffffff;
    }

    .pln-modal-footer {
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: 12px;
      border-top: 1.5px solid rgba(255, 255, 255, 0.1);
      padding-top: 16px;
    }

    .pln-btn {
      padding: 12px 22px;
      border-radius: 12px;
      font-size: 15px;
      font-weight: 800;
      cursor: pointer;
      transition: all 0.2s ease;
    }

    .pln-btn-secondary {
      background: rgba(255, 255, 255, 0.08);
      border: 1.5px solid rgba(255, 255, 255, 0.15);
      color: #cbd5e1;
    }

    .pln-btn-secondary:hover {
      background: rgba(255, 255, 255, 0.14);
      color: #ffffff;
    }

    .pln-btn-primary {
      background: #22c55e;
      border: none;
      color: #051408;
      box-shadow: 0 0 20px rgba(34, 197, 94, 0.4);
    }

    .pln-btn-primary:hover {
      background: #4ade80;
      box-shadow: 0 0 28px rgba(74, 222, 128, 0.6);
      transform: translateY(-1px);
    }

    /* ── Live Pulse Connection Dot ── */
    .status-beacon {
      position: fixed;
      top: 14px;
      right: 14px;
      width: 14px;
      height: 14px;
      border-radius: 50%;
      background: #ef4444;
      transition: background 0.3s, box-shadow 0.3s;
      z-index: 100;
    }

    .status-beacon.active {
      background: #22c55e;
      box-shadow: 0 0 18px #22c55e;
    }

    .nil {
      color: var(--text-dim) !important;
      font-weight: 400 !important;
    }
  </style>
</head>
<body>

<div class="status-beacon" id="conn"></div>

<div class="hud-stage">

  <!-- ═══════════════ TOP ROW: CPU + GPU ═══════════════ -->
  <div class="top-grid">

    <!-- CPU Card -->
    <div class="hud-card">
      <div class="hud-header">
        <div class="hud-tag">
          <div class="hud-dot" style="background:var(--cpu); color:var(--cpu);"></div>
          <span class="hud-title">CPU</span>
        </div>
        <span class="hud-device-name" id="cpu-name">—</span>
      </div>

      <div class="hero-temp-box" id="cpu-temp-wrap" style="color:var(--cpu);">
        <span class="hero-temp-val" id="cpu-temp">—</span>
        <span class="hero-temp-unit">°C</span>
        <span class="hero-temp-sub">Package</span>
      </div>

      <div class="meter-row">
        <span class="meter-label">LOAD</span>
        <div class="meter-track">
          <div class="meter-fill" id="bar-cpu" style="background:var(--cpu); color:var(--cpu);"></div>
        </div>
        <span class="meter-val" id="cpu-load">—</span>
      </div>

      <div class="stat-list">
        <div class="stat-row">
          <span class="stat-label">Core Clock</span>
          <span class="stat-value" id="cpu-clock">—</span>
        </div>
        <div class="stat-row">
          <span class="stat-label">Package Power</span>
          <span class="stat-value" id="cpu-power">—</span>
        </div>
      </div>
    </div>

    <!-- GPU Card -->
    <div class="hud-card">
      <div class="hud-header">
        <div class="hud-tag">
          <div class="hud-dot" style="background:var(--gpu); color:var(--gpu);"></div>
          <span class="hud-title">GPU</span>
        </div>
        <span class="hud-device-name" id="gpu-name">—</span>
      </div>

      <div class="hero-temp-box" id="gpu-temp-wrap" style="color:var(--gpu);">
        <span class="hero-temp-val" id="gpu-temp">—</span>
        <span class="hero-temp-unit">°C</span>
        <span class="hero-temp-sub">Core</span>
      </div>

      <div class="meter-row">
        <span class="meter-label">LOAD</span>
        <div class="meter-track">
          <div class="meter-fill" id="bar-gpu" style="background:var(--gpu); color:var(--gpu);"></div>
        </div>
        <span class="meter-val" id="gpu-load">—</span>
      </div>

      <div class="stat-list">
        <div class="stat-row">
          <span class="stat-label">Hotspot</span>
          <span class="stat-value" id="gpu-hotspot">—</span>
        </div>
        <div class="stat-row">
          <span class="stat-label">Core Clock</span>
          <span class="stat-value" id="gpu-clock">—</span>
        </div>
        <div class="stat-row">
          <span class="stat-label">Fan Speed</span>
          <span class="stat-value" id="gpu-fan">—</span>
        </div>
        <div class="stat-row">
          <span class="stat-label">Board Power</span>
          <span class="stat-value" id="gpu-power">—</span>
        </div>
      </div>
    </div>

  </div><!-- /top-grid -->

  <!-- ═══════════════ BOTTOM ROW: RAM / VRAM / FPS / POWER ═══════════════ -->
  <div class="bot-grid">

    <!-- RAM Card -->
    <div class="hud-card">
      <div class="hud-header">
        <div class="hud-tag">
          <div class="hud-dot" style="background:var(--ram); color:var(--ram);"></div>
          <span class="hud-title">RAM</span>
        </div>
        <span id="ram-clock" class="vram-temp-pill" style="display:none; color:var(--ram); background:rgba(52,211,153,0.18); border-color:rgba(52,211,153,0.5);"></span>
      </div>

      <div class="gauge-container">
        <div class="gauge-box">
          <svg viewBox="0 0 150 150">
            <circle class="gauge-bg" cx="75" cy="75" r="60"/>
            <circle class="gauge-progress" id="g-ram" cx="75" cy="75" r="60"
              stroke="var(--ram)" stroke-dasharray="376.99" stroke-dashoffset="376.99" style="color:var(--ram); box-shadow:0 0 16px currentColor;"/>
          </svg>
          <div class="gauge-center-text">
            <span class="gauge-pct" id="ram-pct" style="color:var(--ram)">—</span>
            <span class="gauge-pct-unit">% USED</span>
          </div>
        </div>
      </div>

      <div class="stat-list">
        <div class="stat-row">
          <span class="stat-label">Used</span>
          <span class="stat-value" id="ram-used">—</span>
        </div>
        <div class="stat-row">
          <span class="stat-label">Total</span>
          <span class="stat-value" id="ram-total">—</span>
        </div>
      </div>
    </div>

    <!-- VRAM Card -->
    <div class="hud-card">
      <div class="hud-header">
        <div class="hud-tag">
          <div class="hud-dot" style="background:var(--vram); color:var(--vram);"></div>
          <span class="hud-title">VRAM</span>
        </div>
        <span class="vram-temp-pill" id="vram-temp-wrap">
          <span id="vram-temp">—</span> °C
        </span>
      </div>

      <div class="gauge-container">
        <div class="gauge-box">
          <svg viewBox="0 0 150 150">
            <circle class="gauge-bg" cx="75" cy="75" r="60"/>
            <circle class="gauge-progress" id="g-vram" cx="75" cy="75" r="60"
              stroke="var(--vram)" stroke-dasharray="376.99" stroke-dashoffset="376.99"/>
          </svg>
          <div class="gauge-center-text">
            <span class="gauge-pct" id="vram-pct" style="color:var(--vram)">—</span>
            <span class="gauge-pct-unit">% USED</span>
          </div>
        </div>
      </div>

      <div class="stat-list">
        <div class="stat-row">
          <span class="stat-label">Used</span>
          <span class="stat-value" id="vram-used">—</span>
        </div>
        <div class="stat-row">
          <span class="stat-label">Total</span>
          <span class="stat-value" id="vram-total">—</span>
        </div>
        <div class="stat-row">
          <span class="stat-label">Clock</span>
          <span class="stat-value" id="vram-clock">—</span>
        </div>
      </div>
    </div>

    <!-- FPS Card -->
    <div class="hud-card">
      <div class="hud-header">
        <div class="hud-tag">
          <div class="hud-dot" style="background:var(--fps); color:var(--fps);"></div>
          <span class="hud-title">FPS</span>
        </div>
        <span class="fps-live-badge">RTSS LIVE</span>
      </div>

      <div class="fps-hero">
        <span class="fps-main-num" id="fps-cur">—</span>
        <span class="fps-label-text">FPS</span>
      </div>

      <div class="fps-frametime">Frametime: <span id="fps-ft">—</span> ms</div>

      <div class="fps-lows-grid">
        <div class="fps-low-card">
          <span class="fps-low-title">1% Low</span>
          <span class="fps-low-val nil" id="fps-1p">—</span>
        </div>
        <div class="fps-low-card">
          <span class="fps-low-title">0.1% Low</span>
          <span class="fps-low-val nil" id="fps-01p">—</span>
        </div>
      </div>
    </div>

    <!-- Power Card -->
    <div class="hud-card">
      <div class="hud-header">
        <div class="hud-tag">
          <div class="hud-dot" style="background:var(--pwr); color:var(--pwr);"></div>
          <span class="hud-title">POWER</span>
        </div>
        <button class="hud-settings-btn" id="btn-open-pln-settings" type="button" title="Pengaturan Tarif Listrik PLN">⚙️</button>
      </div>

      <div class="pwr-hero">
        <span class="pwr-main-val" id="pwr-val">—</span>
        <span class="pwr-main-unit">W</span>
      </div>

      <div class="cost-box" id="cost-box-click" title="Klik untuk ubah tarif & jam pemakaian listrik">
        <span class="cost-main" id="pwr-cost">—</span>
        <div class="cost-sub">
          <span id="pwr-cost-sub">Est. Biaya Listrik PLN / bln</span>
          <span class="cost-sub-hint">⚙️ Ubah</span>
        </div>
      </div>
    </div>

  </div><!-- /bot-grid -->

</div><!-- /hud-stage -->

<!-- PLN Settings Modal -->
<div class="pln-modal-overlay" id="pln-modal" style="display: none;">
  <div class="pln-modal-card">
    <div class="pln-modal-header">
      <div class="pln-modal-title">
        <span class="pln-modal-dot"></span>
        <h3>Kustomisasi Biaya Listrik PLN</h3>
      </div>
      <button class="pln-modal-close" id="btn-close-pln-modal" type="button">&times;</button>
    </div>

    <div class="pln-modal-body">
      <div class="pln-form-group">
        <label class="pln-label">Golongan Daya Listrik PLN</label>
        <select class="pln-select" id="pln-select-tier">
          <option value="900_nonsubsidi" data-rate="1352" data-label="900 VA">900 VA (R-1/TR Non-Subsidi) — Rp 1.352 / kWh</option>
          <option value="1300_2200" data-rate="1444.7" data-label="1300/2200 VA">1.300 VA & 2.200 VA (R-1/TR) — Rp 1.444,70 / kWh</option>
          <option value="3500_5500" data-rate="1699.53" data-label="3500-5500 VA">3.500 VA – 5.500 VA (R-2/TR) — Rp 1.699,53 / kWh</option>
          <option value="6600_up" data-rate="1699.53" data-label="6600 VA+">6.600 VA ke atas (R-3/TR) — Rp 1.699,53 / kWh</option>
          <option value="900_subsidi" data-rate="605" data-label="900 VA Subsidi">900 VA (R-1/TR Bersubsidi) — Rp 605 / kWh</option>
          <option value="450_subsidi" data-rate="415" data-label="450 VA Subsidi">450 VA (R-1/TR Bersubsidi) — Rp 415 / kWh</option>
          <option value="custom" data-rate="0" data-label="Custom">Tarif Kustom (Input Manual Rp/kWh)</option>
        </select>
      </div>

      <div class="pln-form-group" id="pln-custom-rate-group" style="display: none;">
        <label class="pln-label">Tarif Manual (Rp per kWh)</label>
        <input type="number" class="pln-input" id="pln-input-custom-rate" min="1" step="0.01" value="1352" placeholder="Contoh: 1444.70">
      </div>

      <div class="pln-form-row">
        <div class="pln-form-group" style="flex: 1;">
          <label class="pln-label">Jam Pemakaian / Hari</label>
          <div class="pln-input-range-wrap">
            <input type="number" class="pln-input" id="pln-input-hours" min="1" max="24" value="8">
            <span class="pln-input-addon">Jam</span>
          </div>
        </div>

        <div class="pln-form-group" style="flex: 1;">
          <label class="pln-label">Hari / Bulan</label>
          <div class="pln-input-range-wrap">
            <input type="number" class="pln-input" id="pln-input-days" min="1" max="31" value="30">
            <span class="pln-input-addon">Hari</span>
          </div>
        </div>
      </div>

      <div class="pln-preview-box">
        <div class="pln-preview-label">Rumus Estimasi:</div>
        <div class="pln-preview-val" id="pln-preview-formula">(Watt / 1000) × 8 jam × 30 hari × Rp 1.352</div>
      </div>
    </div>

    <div class="pln-modal-footer">
      <button class="pln-btn pln-btn-secondary" id="btn-reset-pln" type="button">Reset Default</button>
      <button class="pln-btn pln-btn-primary" id="btn-save-pln" type="button">Simpan Pengaturan</button>
    </div>
  </div>
</div>

<script>
const $ = id => document.getElementById(id);
const CIRC = 2 * Math.PI * 60; // r=60 for circular gauges

function sv(el, val, unit = '', d = 0) {
  if (val == null) {
    el.innerHTML = '—';
    el.classList.add('nil');
  } else {
    el.innerHTML = Number(val).toFixed(d) + (unit ? `<span class="unit">${unit}</span>` : '');
    el.classList.remove('nil');
  }
}

function setMeter(id, pct, baseColor) {
  const el = $(id);
  el.style.width = Math.min(100, Math.max(0, pct || 0)) + '%';
  el.style.background = pct >= 90 ? 'var(--hot)' : pct >= 80 ? 'var(--warn)' : baseColor;
}

function setGauge(circleId, pctEl, pct, color) {
  const circle = $(circleId);
  const offset = CIRC * (1 - Math.min(100, Math.max(0, pct || 0)) / 100);
  circle.style.strokeDashoffset = offset;
  const c = pct >= 90 ? 'var(--hot)' : pct >= 80 ? 'var(--warn)' : color;
  circle.style.stroke = c;
  $(pctEl).textContent = pct != null ? Math.round(pct) : '—';
  $(pctEl).style.color = c;
}

function tempColor(t) {
  return t >= 85 ? 'var(--hot)' : t >= 75 ? 'var(--warn)' : null;
}

function shortName(name) {
  if (!name) return null;
  return name.replace(/AMD Radeon\s*/i,'').replace(/NVIDIA GeForce\s*/i,'')
             .replace(/Intel Core\s*/i,'').replace(/AMD Ryzen\s*/i,'Ryzen ')
             .trim().substring(0, 32);
}

function connect() {
  const beacon = $('conn');
  const isSecure = location.protocol === 'https:';
  const wsProtocol = isSecure ? 'wss://' : 'ws://';
  let wsUrl = '';
  
  if (isSecure) {
    wsUrl = wsProtocol + 'ws.' + location.hostname;
  } else {
    const host = location.hostname || 'localhost';
    wsUrl = wsProtocol + host + ':{{_wsPort}}';
  }

  const ws = new WebSocket(wsUrl);
  ws.onopen  = () => beacon.className = 'status-beacon active';
  ws.onclose = () => { beacon.className = 'status-beacon'; setTimeout(connect, 2000); };
  ws.onerror = () => beacon.className = 'status-beacon';

  ws.onmessage = ({ data }) => {
    let d; try { d = JSON.parse(data); } catch { return; }

    // Device Labels
    $('cpu-name').textContent = shortName(d.device?.cpuName) || '—';
    $('gpu-name').textContent = shortName(d.device?.gpuName) || '—';

    // ── CPU ──
    const ct = d.cpu?.temp;
    if (ct != null) {
      $('cpu-temp').textContent = Number(ct).toFixed(1);
      const tc = tempColor(ct);
      $('cpu-temp-wrap').style.color = tc || 'var(--cpu)';
      $('cpu-temp-wrap').style.borderColor = tc || 'rgba(56,189,248,0.3)';
    }
    sv($('cpu-load'),  d.cpu?.load,  '%',   1);
    sv($('cpu-clock'), d.cpu?.clock, 'MHz', 0);
    sv($('cpu-power'), d.cpu?.power, 'W',   1);
    if (d.cpu?.load != null) setMeter('bar-cpu', d.cpu.load, 'var(--cpu)');

    // ── GPU ──
    const gt = d.gpu?.temp;
    if (gt != null) {
      $('gpu-temp').textContent = Number(gt).toFixed(1);
      const tc = tempColor(gt);
      $('gpu-temp-wrap').style.color = tc || 'var(--gpu)';
      $('gpu-temp-wrap').style.borderColor = tc || 'rgba(192,132,252,0.3)';
    }
    sv($('gpu-load'),    d.gpu?.load,        '%',   1);
    sv($('gpu-hotspot'), d.gpu?.hotSpotTemp, '°C',  1);
    sv($('gpu-clock'),   d.gpu?.coreClock,   'MHz', 0);
    sv($('gpu-fan'),     d.gpu?.fanRpm,      'RPM', 0);
    sv($('gpu-power'),   d.gpu?.power,       'W',   1);
    if (d.gpu?.load != null) setMeter('bar-gpu', d.gpu.load, 'var(--gpu)');

    // ── RAM ──
    setGauge('g-ram', 'ram-pct', d.mem?.load, 'var(--ram)');
    sv($('ram-used'),  d.mem?.usedGb,  'GB', 2);
    sv($('ram-total'), d.mem?.totalGb, 'GB', 1);
    if (d.mem?.clock != null) {
      $('ram-clock').textContent = d.mem.clock + ' MHz';
      $('ram-clock').style.display = 'inline-block';
    } else {
      $('ram-clock').style.display = 'none';
    }

    // ── VRAM ──
    const vt = d.gpu?.hotSpotTemp ?? d.gpu?.temp;
    if (vt != null) {
      $('vram-temp').textContent = Number(vt).toFixed(1);
      $('vram-temp-wrap').style.display = 'inline-block';
    } else {
      $('vram-temp-wrap').style.display = 'none';
    }
    setGauge('g-vram', 'vram-pct', d.gpu?.vramPct, 'var(--vram)');
    sv($('vram-used'),  d.gpu?.vramUsedGb,  'GB', 2);
    sv($('vram-total'), d.gpu?.vramTotalGb, 'GB', 1);
    sv($('vram-clock'), d.gpu?.memClock,    'MHz', 0);

    // ── FPS ──
    const fc = d.fps?.current, ft = d.fps?.frametimeMs;
    const f1 = d.fps?.low1pct, f0 = d.fps?.low01pct;
    $('fps-cur').textContent = fc != null ? Math.round(fc) : '—';
    $('fps-ft').textContent  = ft != null ? Number(ft).toFixed(2) : '—';
    
    const e1 = $('fps-1p'), e01 = $('fps-01p');
    if (f1 != null) { e1.textContent = Math.round(f1); e1.classList.remove('nil'); }
    else            { e1.textContent = '—'; e1.classList.add('nil'); }
    if (f0 != null) { e01.textContent = Math.round(f0); e01.classList.remove('nil'); }
    else            { e01.textContent = '—'; e01.classList.add('nil'); }

    // ── Power & PLN Cost ──
    const pw = d.power?.totalW;
    $('pwr-val').textContent = pw != null ? Number(pw).toFixed(1) : '—';
    renderPlnCost(pw);
  };
}

// ── PLN Settings & Reactive Logic ──
const DEFAULT_PLN = {
  tier: '900_nonsubsidi',
  rate: 1352.0,
  hours: 8,
  days: 30,
  label: '900 VA'
};

let plnCfg = { ...DEFAULT_PLN };
try {
  const saved = localStorage.getItem('legaxyy_pln_cfg');
  if (saved) {
    plnCfg = { ...DEFAULT_PLN, ...JSON.parse(saved) };
  }
} catch (e) { }

function updatePlnSublabel() {
  const subEl = $('pwr-cost-sub');
  if (subEl) {
    subEl.textContent = `Est. Biaya Listrik PLN (${plnCfg.label} • ${plnCfg.hours} jam/hr)`;
  }
}

function calculateMonthlyCost(watts) {
  if (watts == null || isNaN(watts)) return null;
  const kwhPerMonth = (watts / 1000) * plnCfg.hours * plnCfg.days;
  return kwhPerMonth * plnCfg.rate;
}

function renderPlnCost(watts) {
  if (watts != null && !isNaN(watts)) {
    const cost = calculateMonthlyCost(watts);
    $('pwr-cost').textContent = 'Rp ' + Math.round(cost).toLocaleString('id-ID');
  } else {
    $('pwr-cost').textContent = '—';
  }
}

function updateFormulaPreview() {
  const fEl = $('pln-preview-formula');
  if (fEl) {
    const rateDisp = Number(plnCfg.rate).toLocaleString('id-ID');
    fEl.textContent = `(Watt / 1000) × ${plnCfg.hours} jam × ${plnCfg.days} hari × Rp ${rateDisp}`;
  }
}

const plnModal = $('pln-modal');
const tierSelect = $('pln-select-tier');
const customGroup = $('pln-custom-rate-group');
const customInput = $('pln-input-custom-rate');
const hoursInput = $('pln-input-hours');
const daysInput = $('pln-input-days');

function openPlnModal() {
  tierSelect.value = plnCfg.tier || '900_nonsubsidi';
  if (tierSelect.value === 'custom') {
    customGroup.style.display = 'flex';
    customInput.value = plnCfg.rate;
  } else {
    customGroup.style.display = 'none';
  }
  hoursInput.value = plnCfg.hours;
  daysInput.value = plnCfg.days;
  updateFormulaPreview();
  plnModal.style.display = 'flex';
}

function closePlnModal() {
  plnModal.style.display = 'none';
}

$('btn-open-pln-settings').addEventListener('click', (e) => { e.stopPropagation(); openPlnModal(); });
$('cost-box-click').addEventListener('click', () => openPlnModal());
$('btn-close-pln-modal').addEventListener('click', () => closePlnModal());
plnModal.addEventListener('click', (e) => {
  if (e.target === plnModal) closePlnModal();
});

tierSelect.addEventListener('change', () => {
  if (tierSelect.value === 'custom') {
    customGroup.style.display = 'flex';
    plnCfg.rate = Math.max(1, parseFloat(customInput.value) || 1352);
    plnCfg.label = 'Custom';
  } else {
    customGroup.style.display = 'none';
    const opt = tierSelect.selectedOptions[0];
    plnCfg.rate = parseFloat(opt.getAttribute('data-rate'));
    plnCfg.label = opt.getAttribute('data-label');
  }
  updateFormulaPreview();
});

customInput.addEventListener('input', () => {
  if (tierSelect.value === 'custom') {
    plnCfg.rate = Math.max(1, parseFloat(customInput.value) || 1352);
    updateFormulaPreview();
  }
});

hoursInput.addEventListener('input', () => {
  plnCfg.hours = Math.max(1, Math.min(24, parseInt(hoursInput.value) || 8));
  updateFormulaPreview();
});

daysInput.addEventListener('input', () => {
  plnCfg.days = Math.max(1, Math.min(31, parseInt(daysInput.value) || 30));
  updateFormulaPreview();
});

$('btn-save-pln').addEventListener('click', () => {
  plnCfg.tier = tierSelect.value;
  if (plnCfg.tier === 'custom') {
    plnCfg.rate = Math.max(1, parseFloat(customInput.value) || 1352);
    plnCfg.label = 'Custom';
  } else {
    const opt = tierSelect.selectedOptions[0];
    plnCfg.rate = parseFloat(opt.getAttribute('data-rate'));
    plnCfg.label = opt.getAttribute('data-label');
  }
  plnCfg.hours = Math.max(1, Math.min(24, parseInt(hoursInput.value) || 8));
  plnCfg.days = Math.max(1, Math.min(31, parseInt(daysInput.value) || 30));

  try {
    localStorage.setItem('legaxyy_pln_cfg', JSON.stringify(plnCfg));
  } catch (e) { }

  updatePlnSublabel();
  closePlnModal();

  const curPw = parseFloat($('pwr-val').textContent);
  if (!isNaN(curPw)) {
    renderPlnCost(curPw);
  }
});

$('btn-reset-pln').addEventListener('click', () => {
  plnCfg = { ...DEFAULT_PLN };
  try { localStorage.removeItem('legaxyy_pln_cfg'); } catch (e) { }
  tierSelect.value = plnCfg.tier;
  customGroup.style.display = 'none';
  hoursInput.value = plnCfg.hours;
  daysInput.value = plnCfg.days;
  updateFormulaPreview();
  updatePlnSublabel();
});

updatePlnSublabel();

function autoScale() {
  const scaleX = window.innerWidth / 1920;
  const scaleY = window.innerHeight / 1080;
  const scale = Math.min(scaleX, scaleY);
  const el = document.body;
  el.style.transform = `scale(${scale})`;
  el.style.transformOrigin = 'top left';
  const offsetX = (window.innerWidth - 1920 * scale) / 2;
  const offsetY = (window.innerHeight - 1080 * scale) / 2;
  el.style.left = `${Math.max(0, offsetX)}px`;
  el.style.top = `${Math.max(0, offsetY)}px`;
}

window.addEventListener('resize', autoScale);
window.addEventListener('DOMContentLoaded', autoScale);
autoScale();
connect();
</script>
</body>
</html>
""";
    }

    public void Dispose() => Stop();
}
