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

    private double _plnRate = 1352.0;
    private int _plnHours = 8;
    private int _plnDays = 30;
    private string _plnTier = "900_nonsubsidi";
    private string _plnLabel = "900 VA";

    public event Action<double, int, int, string, string>? PlnConfigChanged;

// ─── Settings event delegates ────────────────────────────────────────────
public event Action<string>? ModeChanged;              // "Gamer" or "Streamer"
public event Action<bool>? RtssOsdToggled;             // true/false
public event Action<string>? RtssStyleChanged;         // style name
public event Action<bool>? StartupToggled;             // true/false  
public event Action? RestartWsRequested;
public event Action? CheckUpdateRequested;
public event Action<int>? BillingCycleStartDayChanged; // 1-28

// ─── State exposed to dashboard settings UI ──────────────────────────────
private string _currentMode = "Streamer";
private bool _rtssOsdEnabled = true;
private string _rtssStyle = "FullAllInOne";
private bool _startupEnabled = false;
private int _billingCycleStartDay = 1;
private int _wsClientCount = 0;

public void SetSettingsState(string mode, bool rtssEnabled, string rtssStyle, bool startupEnabled, int billingCycleStartDay, int wsClientCount)
{
    _currentMode = mode;
    _rtssOsdEnabled = rtssEnabled;
    _rtssStyle = rtssStyle;
    _startupEnabled = startupEnabled;
    _billingCycleStartDay = billingCycleStartDay;
    _wsClientCount = wsClientCount;
}

public void UpdateWsClientCount(int count) => _wsClientCount = count;
public void UpdateStartupState(bool enabled) => _startupEnabled = enabled;
public void UpdateRtssState(bool enabled, string style) { _rtssOsdEnabled = enabled; _rtssStyle = style; }
public void UpdateMode(string mode) => _currentMode = mode;


    public void SetInitialPlnConfig(double rate, int hours, int days, string tier, string label)
    {
        if (rate > 0) _plnRate = rate;
        if (hours > 0) _plnHours = hours;
        if (days > 0) _plnDays = days;
        if (!string.IsNullOrEmpty(tier)) _plnTier = tier;
        if (!string.IsNullOrEmpty(label)) _plnLabel = label;
    }

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
            string path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/api/pln")
            {
                if (context.Request.HttpMethod == "GET")
                {
                    string cfg = Newtonsoft.Json.JsonConvert.SerializeObject(new { rate = _plnRate, hours = _plnHours, days = _plnDays, tier = _plnTier, label = _plnLabel });
                    byte[] gbuf = Encoding.UTF8.GetBytes(cfg);
                    context.Response.ContentType = "application/json";
                    context.Response.StatusCode = 200;
                    context.Response.ContentLength64 = gbuf.Length;
                    using var gos = context.Response.OutputStream;
                    gos.Write(gbuf, 0, gbuf.Length);
                    return;
                }
                if (context.Request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    string json = reader.ReadToEnd();
                    try
                    {
                        var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                        double rate = obj.Value<double?>("rate") ?? 1352.0;
                        int hours = obj.Value<int?>("hours") ?? 8;
                        int days = obj.Value<int?>("days") ?? 30;
                        string tier = obj.Value<string>("tier") ?? "custom";
                        string label = obj.Value<string>("label") ?? "Custom";

                        _plnRate = rate;
                        _plnHours = hours;
                        _plnDays = days;
                        _plnTier = tier;
                        _plnLabel = label;

                        PlnConfigChanged?.Invoke(rate, hours, days, tier, label);

                        byte[] resp = Encoding.UTF8.GetBytes("{\"ok\":true}");
                        context.Response.ContentType = "application/json";
                        context.Response.StatusCode = 200;
                        context.Response.ContentLength64 = resp.Length;
                        using var os = context.Response.OutputStream;
                        os.Write(resp, 0, resp.Length);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"HttpServerService: /api/pln parse error: {ex.Message}");
                        context.Response.StatusCode = 400;
                        return;
                    }
                }
            }

            if (path == "/api/settings")
            {
                if (context.Request.HttpMethod == "GET")
                {
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(new {
                        mode = _currentMode,
                        rtssOsdEnabled = _rtssOsdEnabled,
                        rtssStyle = _rtssStyle,
                        startupEnabled = _startupEnabled,
                        billingCycleStartDay = _billingCycleStartDay,
                        wsClientCount = _wsClientCount
                    });
                    byte[] buf = Encoding.UTF8.GetBytes(json);
                    context.Response.ContentType = "application/json";
                    context.Response.StatusCode = 200;
                    context.Response.ContentLength64 = buf.Length;
                    using var os = context.Response.OutputStream;
                    os.Write(buf, 0, buf.Length);
                    return;
                }
                if (context.Request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    string json = reader.ReadToEnd();
                    try
                    {
                        var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                        string? action = obj.Value<string>("action");

                        switch (action)
                        {
                            case "setMode":
                                ModeChanged?.Invoke(obj.Value<string>("value") ?? "Streamer");
                                break;
                            case "toggleRtssOsd":
                                RtssOsdToggled?.Invoke(obj.Value<bool?>("value") ?? true);
                                break;
                            case "setRtssStyle":
                                RtssStyleChanged?.Invoke(obj.Value<string>("value") ?? "FullAllInOne");
                                break;
                            case "toggleStartup":
                                StartupToggled?.Invoke(obj.Value<bool?>("value") ?? false);
                                break;
                            case "restartWs":
                                RestartWsRequested?.Invoke();
                                break;
                            case "checkUpdate":
                                CheckUpdateRequested?.Invoke();
                                break;
                            case "setBillingCycleDay":
                                BillingCycleStartDayChanged?.Invoke(obj.Value<int?>("value") ?? 1);
                                break;
                        }

                        byte[] resp = Encoding.UTF8.GetBytes("{\"ok\":true}");
                        context.Response.ContentType = "application/json";
                        context.Response.StatusCode = 200;
                        context.Response.ContentLength64 = resp.Length;
                        using var os = context.Response.OutputStream;
                        os.Write(resp, 0, resp.Length);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"HttpServerService: /api/settings parse error: {ex.Message}");
                        context.Response.StatusCode = 400;
                        return;
                    }
                }
            }

            // Always serve the embedded dashboard so OSD + dashboard share one
            // PLN source of truth. (Never serve a stale index.html from disk —
            // that desynced OSD (900 VA default) from the dashboard before.)
            string html = GetHtmlContent();
            html = html.Replace("{{{_wsPort}}}", _wsPort.ToString())
                       .Replace("{{_wsPort}}", _wsPort.ToString())
                       .Replace("{{{_plnRate}}}", _plnRate.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Replace("{{{_plnHours}}}", _plnHours.ToString())
                       .Replace("{{{_plnDays}}}", _plnDays.ToString())
                       .Replace("{{{_plnTier}}}", _plnTier)
                       .Replace("{{{_plnLabel}}}", _plnLabel);
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
        // Serialize PLN defaults as JSON (Newtonsoft always uses '.' decimals)
        // so the dashboard script can never break on Indonesian comma-decimal
        // formatting, regardless of Windows locale.
        string plnJson = Newtonsoft.Json.JsonConvert.SerializeObject(
            new { rate = _plnRate, hours = _plnHours, days = _plnDays, tier = _plnTier, label = _plnLabel, billingDay = _billingCycleStartDay })
            .Replace("'", "\\'");
        return $$$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>LegaxyyFPS Overlay</title>
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700;800;900&family=JetBrains+Mono:wght@400;500;600;700;800;900&display=swap" rel="stylesheet">
  <style>
    :root {
      --bg:        #060810;
      --surface:   rgba(12,15,26,0.94);
      --border:    rgba(255,255,255,0.09);
      --border-hi: rgba(255,255,255,0.2);

      --cpu:       #38bdf8;
      --cpu-dim:   rgba(56,189,248,0.15);
      --cpu-glow:  rgba(56,189,248,0.5);

      --gpu:       #c084fc;
      --gpu-dim:   rgba(192,132,252,0.15);
      --gpu-glow:  rgba(192,132,252,0.5);

      --ram:       #34d399;
      --ram-dim:   rgba(52,211,153,0.15);
      --ram-glow:  rgba(52,211,153,0.5);

      --vram:      #818cf8;
      --vram-dim:  rgba(129,140,248,0.15);
      --vram-glow: rgba(129,140,248,0.5);

      --fps:       #fb923c;
      --fps-dim:   rgba(251,146,60,0.15);
      --fps-glow:  rgba(251,146,60,0.55);

      --pwr:       #f472b6;
      --pwr-dim:   rgba(244,114,182,0.15);
      --pwr-glow:  rgba(244,114,182,0.5);

      --green:     #4ade80;
      --green-dim: rgba(74,222,128,0.15);
      --green-glow:rgba(74,222,128,0.5);

      --hot:       #f43f5e;
      --warn:      #fbbf24;
      --t1:        #ffffff;
      --t2:        #cbd5e1;
      --t3:        #64748b;
      --mono:      'JetBrains Mono', monospace;
    }

    *,*::before,*::after{box-sizing:border-box;margin:0;padding:0}

    html{width:100%;height:100%;background:transparent;overflow:hidden}

    body{
      width:1920px;height:1080px;
      background:transparent;
      font-family:'Inter',system-ui,sans-serif;
      -webkit-font-smoothing:antialiased;
      user-select:none;overflow:hidden;
      position:absolute;top:0;left:0;
    }

    body::before{
      display:none;
    }

    /* ── Layout ── */
    .stage{
      position:absolute;inset:18px;
      display:flex;flex-direction:column;gap:14px;
      z-index:1;
    }
    .row-top{display:grid;grid-template-columns:1fr 1fr;gap:14px;flex:1.15;min-height:0}
    .row-bot{display:grid;grid-template-columns:1fr 1fr 1fr 1fr;gap:14px;flex:1;min-height:0}

    /* ── Card ── */
    .card{
      position:relative;
      background:var(--surface);
      border:1.5px solid var(--border);
      border-radius:20px;
      padding:22px 28px 20px;
      display:flex;flex-direction:column;
      overflow:hidden;
      backdrop-filter:blur(32px) saturate(140%);
      -webkit-backdrop-filter:blur(32px) saturate(140%);
      box-shadow:
        0 0 0 1px rgba(255,255,255,0.04) inset,
        0 28px 56px -14px rgba(0,0,0,0.85),
        0 2px 4px rgba(0,0,0,0.5);
    }
    .card::before{
      content:'';position:absolute;
      top:0;left:20px;right:20px;height:1px;
      background:linear-gradient(90deg,transparent,rgba(255,255,255,0.25) 50%,transparent);
    }
    .card-cpu::after {content:'';position:absolute;inset:0;border-radius:20px;background:radial-gradient(ellipse 90% 55% at 8% 0%,  var(--cpu-dim)  0%,transparent 60%);pointer-events:none}
    .card-gpu::after {content:'';position:absolute;inset:0;border-radius:20px;background:radial-gradient(ellipse 90% 55% at 92% 0%, var(--gpu-dim)  0%,transparent 60%);pointer-events:none}
    .card-ram::after {content:'';position:absolute;inset:0;border-radius:20px;background:radial-gradient(ellipse 100% 65% at 50% 0%,var(--ram-dim)  0%,transparent 65%);pointer-events:none}
    .card-vram::after{content:'';position:absolute;inset:0;border-radius:20px;background:radial-gradient(ellipse 100% 65% at 50% 0%,var(--vram-dim) 0%,transparent 65%);pointer-events:none}
    .card-fps::after {content:'';position:absolute;inset:0;border-radius:20px;background:radial-gradient(ellipse 100% 65% at 50% 0%,var(--fps-dim)  0%,transparent 65%);pointer-events:none}
    .card-pwr::after {content:'';position:absolute;inset:0;border-radius:20px;background:radial-gradient(ellipse 100% 65% at 50% 0%,var(--pwr-dim)  0%,transparent 65%);pointer-events:none}

    /* ── Card Header ── */
    .card-head{
      display:flex;align-items:center;justify-content:space-between;
      margin-bottom:14px;position:relative;z-index:1;
    }

    .chip{
      display:inline-flex;align-items:center;gap:9px;
      padding:6px 18px 6px 14px;
      border-radius:999px;
      border:1.5px solid var(--border-hi);
      background:rgba(255,255,255,0.06);
    }

    .chip-dot{
      width:10px;height:10px;border-radius:50%;flex-shrink:0;
      animation:pdot 2.4s ease-in-out infinite;
    }
    @keyframes pdot{
      0%,100%{opacity:1;transform:scale(1)}
      50%    {opacity:0.55;transform:scale(0.8)}
    }

    .chip-label{
      font-size:22px;font-weight:900;
      letter-spacing:0.12em;text-transform:uppercase;
      color:var(--t1);
    }

    .device-name{
      font-size:28px;font-weight:800;
      color:#ffffff;
      white-space:nowrap;overflow:hidden;text-overflow:ellipsis;
      max-width:500px;letter-spacing:-0.01em;
      text-shadow:0 0 16px rgba(255,255,255,0.35);
    }

    /* ── Temperature Hero ── */
    .temp-hero{
      display:inline-flex;align-items:baseline;gap:8px;
      padding:10px 24px;
      border-radius:18px;margin-bottom:14px;width:fit-content;
      border:1.5px solid rgba(255,255,255,0.12);
      background:rgba(255,255,255,0.04);
      position:relative;z-index:1;
      transition:color 0.3s;
    }

    .temp-val{
      font-family:var(--mono);
      font-size:88px;font-weight:900;
      letter-spacing:-0.04em;line-height:1;
      font-variant-numeric:tabular-nums;
    }
    .temp-deg{
      font-family:var(--mono);
      font-size:38px;font-weight:800;opacity:0.8;
      align-self:flex-start;margin-top:6px;
    }
    .temp-tag{
      font-size:18px;font-weight:800;
      letter-spacing:0.08em;text-transform:uppercase;
      color:#ffffff;background:rgba(255,255,255,0.1);
      padding:4px 14px;border-radius:8px;
      align-self:center;margin-left:6px;
    }

    /* ── Load Bar ── */
    .load-row{
      display:flex;align-items:center;gap:14px;
      margin-bottom:14px;position:relative;z-index:1;
    }
    .load-lbl{
      font-size:22px;font-weight:900;
      letter-spacing:0.12em;text-transform:uppercase;
      color:var(--t2);min-width:65px;
    }
    .bar-track{
      flex:1;height:16px;
      background:rgba(255,255,255,0.08);
      border-radius:8px;overflow:visible;position:relative;
    }
    .bar-fill{
      height:100%;width:0%;border-radius:8px;
      transition:width 0.35s cubic-bezier(.4,0,.2,1),background 0.3s;
      position:relative;
    }
    .bar-fill::after{
      content:'';position:absolute;
      right:-1px;top:50%;transform:translateY(-50%);
      width:14px;height:14px;border-radius:50%;
      background:currentColor;
      box-shadow:0 0 10px 3px currentColor;
      opacity:0.8;
    }
    .bar-val{
      font-family:var(--mono);
      font-size:36px;font-weight:900;
      color:var(--t1);min-width:110px;text-align:right;
      font-variant-numeric:tabular-nums;
    }

    /* ── Stats List (Large, Clear, High-Contrast) ── */
    .stats{
      display:flex;flex-direction:column;gap:0;
      margin-top:auto;
      border-top:1.5px solid rgba(255,255,255,0.1);
      padding-top:12px;position:relative;z-index:1;
    }
    .stat{
      display:flex;align-items:center;justify-content:space-between;
      padding:8px 0;
      border-bottom:1px solid rgba(255,255,255,0.05);
    }
    .stat:last-child{border-bottom:none}
    .stat-k{font-size:26px;font-weight:800;color:var(--t2);letter-spacing:-0.01em}
    .stat-v{
      font-family:var(--mono);font-size:36px;font-weight:900;
      color:#ffffff;font-variant-numeric:tabular-nums;
    }
    .stat-v .u{font-size:22px;font-weight:800;color:#94a3b8;margin-left:4px}

    /* ── Radial Gauges ── */
    .gauge-wrap{
      display:flex;flex-direction:column;
      align-items:center;justify-content:center;
      flex:1;position:relative;z-index:1;margin:6px 0;
    }
    .gauge-box{position:relative;width:170px;height:170px}
    .gauge-box svg{
      width:170px;height:170px;
      transform:rotate(-90deg);
      filter:drop-shadow(0 0 6px currentColor);
    }
    .gauge-bg{fill:none;stroke:rgba(255,255,255,0.07);stroke-width:15}
    .gauge-arc{
      fill:none;stroke-width:15;stroke-linecap:round;
      transition:stroke-dashoffset 0.45s cubic-bezier(.4,0,.2,1),stroke 0.3s;
    }
    .gauge-center{
      position:absolute;inset:0;
      display:flex;flex-direction:column;align-items:center;justify-content:center;
    }
    .gauge-pct{
      font-family:var(--mono);font-size:54px;font-weight:900;
      line-height:1;font-variant-numeric:tabular-nums;
    }
    .gauge-sub{
      font-size:16px;font-weight:800;letter-spacing:0.09em;
      color:var(--t2);text-transform:uppercase;margin-top:3px;
    }
    .temp-pill{
      font-family:var(--mono);font-size:20px;font-weight:800;
      padding:4px 14px;border-radius:999px;
      border:1.5px solid rgba(255,255,255,0.14);
      background:rgba(255,255,255,0.06);
      color:#ffffff;letter-spacing:0.02em;
    }

    /* ── FPS Card ── */
    .fps-num{
      font-family:var(--mono);
      font-size:104px;font-weight:900;line-height:0.95;
      letter-spacing:-0.04em;color:var(--fps);
      text-shadow:0 0 40px var(--fps-glow);
      font-variant-numeric:tabular-nums;
      position:relative;z-index:1;
    }
    .fps-unit{font-size:28px;font-weight:800;color:var(--t2);margin-left:4px;letter-spacing:.04em}
    .fps-ft{
      font-family:var(--mono);font-size:24px;font-weight:800;
      color:var(--t2);margin:8px 0 12px;position:relative;z-index:1;
    }
    .fps-ft span{color:#ffffff;font-weight:900}
    .fps-lows{
      display:grid;grid-template-columns:1fr 1fr;gap:12px;
      margin-top:auto;position:relative;z-index:1;
    }
    .low-cell{
      background:rgba(255,255,255,0.04);
      border:1.5px solid var(--border);border-radius:14px;
      padding:12px 16px;display:flex;flex-direction:column;gap:3px;
    }
    .low-lbl{font-size:18px;font-weight:800;letter-spacing:.08em;text-transform:uppercase;color:var(--t2)}
    .low-val{
      font-family:var(--mono);font-size:46px;font-weight:900;
      color:#ffffff;font-variant-numeric:tabular-nums;line-height:1;
    }
    .live-badge{
      font-size:16px;font-weight:800;letter-spacing:.08em;text-transform:uppercase;
      padding:5px 14px;border-radius:999px;
      background:var(--fps-dim);border:1.5px solid rgba(251,146,60,.35);color:var(--fps);
      display:flex;align-items:center;gap:6px;
    }
    .live-badge::before{
      content:'';width:6px;height:6px;border-radius:50%;
      background:var(--fps);animation:pdot 1.2s ease-in-out infinite;
    }

    /* ── Power Card & Listrik PLN (Huge, Prominent) ── */
    .pwr-num{
      font-family:var(--mono);font-size:80px;font-weight:900;
      line-height:0.95;letter-spacing:-0.04em;color:var(--pwr);
      text-shadow:0 0 38px var(--pwr-glow);
      font-variant-numeric:tabular-nums;position:relative;z-index:1;
    }
    .pwr-unit{font-size:28px;font-weight:800;color:var(--t2);margin-left:4px}

    /* ── Accumulative Energy Panel ── */
    .energy-panel{
      margin-top:8px;
      background:linear-gradient(135deg,rgba(74,222,128,.15) 0%,rgba(34,197,94,.05) 100%);
      border:2px solid rgba(74,222,128,.35);border-radius:18px;
      padding:14px 18px;display:flex;flex-direction:column;gap:4px;
      position:relative;z-index:1;overflow:hidden;
    }
    .energy-panel::before{
      content:'';position:absolute;top:0;left:0;right:0;height:1px;
      background:linear-gradient(90deg,transparent,rgba(74,222,128,.5) 50%,transparent);
    }
    .energy-month-cost{
      font-family:var(--mono);font-size:36px;font-weight:900;
      color:#4ade80;letter-spacing:-.03em;line-height:1;
      text-shadow:0 0 20px rgba(74,222,128,.45);
      font-variant-numeric:tabular-nums;
    }
    .energy-month-kwh{
      font-size:16px;font-weight:700;color:#86efac;letter-spacing:.01em;
      font-family:var(--mono);
    }
    .energy-today{
      font-size:14px;font-weight:700;color:rgba(134,239,172,.7);
      margin-top:2px;font-family:var(--mono);
    }

    /* ── Gear Button (Settings) ── */
    .gear-btn{
      width:38px;height:38px;border-radius:10px;
      background:rgba(255,255,255,.07);border:1.5px solid var(--border-hi);
      color:var(--t2);font-size:18px;
      display:inline-flex;align-items:center;justify-content:center;
      cursor:pointer;transition:background .2s,border-color .2s,color .2s,transform .3s;
    }
    .gear-btn:hover{
      background:var(--green-dim);border-color:rgba(74,222,128,.45);
      color:var(--green);transform:rotate(45deg);
    }

    /* ── Top Bar Container (No overlap) ── */
    .top-bar{
      position:fixed;top:16px;right:18px;
      display:flex;align-items:center;gap:10px;
      z-index:200;
    }

    /* ── Top Settings Button ── */
    .top-settings-btn{
      display:inline-flex;align-items:center;gap:7px;
      padding:6px 14px;border-radius:999px;
      background:rgba(8,10,20,.88);
      border:1.5px solid rgba(255,255,255,.1);
      color:var(--t2);font-size:13px;font-weight:700;letter-spacing:.04em;
      cursor:pointer;transition:all .2s;
      backdrop-filter:blur(12px);-webkit-backdrop-filter:blur(12px);
    }
    .top-settings-btn .gear-icon{
      font-size:15px;line-height:1;transition:transform .3s ease;
    }
    .top-settings-btn:hover{
      background:var(--green-dim);border-color:rgba(74,222,128,.45);
      color:var(--green);
    }
    .top-settings-btn:hover .gear-icon{
      transform:rotate(90deg);
    }

    /* ── Beacon ── */
    .beacon{
      display:flex;align-items:center;gap:7px;
      padding:6px 14px;border-radius:999px;
      background:rgba(8,10,20,.88);
      border:1.5px solid rgba(255,255,255,.1);
      backdrop-filter:blur(12px);-webkit-backdrop-filter:blur(12px);
      transition:border-color .3s;
    }
    .beacon.live{border-color:rgba(74,222,128,.3)}
    .beacon-dot{
      width:8px;height:8px;border-radius:50%;
      background:#ef4444;transition:background .3s,box-shadow .3s;
    }
    .beacon.live .beacon-dot{
      background:#22c55e;box-shadow:0 0 10px #22c55e;
      animation:pdot 1.8s ease-in-out infinite;
    }
    .beacon-lbl{font-size:13px;font-weight:700;letter-spacing:.06em;color:var(--t3)}
    .beacon.live .beacon-lbl{color:var(--t2)}
    .beacon-sep{opacity:0.35;margin:0 2px;font-size:12px;color:var(--t2)}
    .beacon-hint{font-size:12px;font-weight:700;color:var(--t2);letter-spacing:0.04em}

    /* ── Modal ── */
    .modal-overlay{
      position:fixed;inset:0;
      background:rgba(3,5,13,.9);
      backdrop-filter:blur(22px);
      z-index:1000;display:flex;align-items:center;justify-content:center;
      animation:mfade .18s ease-out;
    }
    @keyframes mfade{from{opacity:0}to{opacity:1}}

    .modal-card{
      background:rgba(9,11,21,.98);
      border:1.5px solid rgba(255,255,255,.12);
      border-radius:24px;width:620px;max-width:92vw;max-height:88vh;
      box-shadow:
        0 0 0 1px rgba(255,255,255,.04) inset,
        0 48px 96px -24px rgba(0,0,0,1),
        0 0 48px rgba(74,222,128,.08);
      overflow:hidden;display:flex;flex-direction:column;
      animation:mslide .22s cubic-bezier(.34,1.56,.64,1);
    }
    @keyframes mslide{
      from{opacity:0;transform:translateY(14px) scale(.97)}
      to  {opacity:1;transform:translateY(0)    scale(1)}
    }

    .modal-head{
      padding:22px 28px 18px;
      display:flex;align-items:center;justify-content:space-between;
      border-bottom:1px solid var(--border);
      background:linear-gradient(180deg,rgba(74,222,128,.05) 0%,transparent 100%);
      flex-shrink:0;
    }
    .modal-title{display:flex;align-items:center;gap:10px}
    .modal-dot{
      width:10px;height:10px;border-radius:50%;
      background:var(--green);box-shadow:0 0 10px var(--green);
    }
    .modal-title h3{font-size:18px;font-weight:800;color:var(--t1);letter-spacing:-.02em}
    .modal-x{
      background:rgba(255,255,255,.07);border:1px solid var(--border-hi);
      color:var(--t2);width:32px;height:32px;border-radius:8px;
      font-size:16px;cursor:pointer;
      display:flex;align-items:center;justify-content:center;
      transition:background .2s,color .2s;
    }
    .modal-x:hover{background:rgba(255,255,255,.14);color:var(--t1)}

    .modal-body{padding:22px 28px;display:flex;flex-direction:column;gap:16px;overflow-y:auto;flex:1}
    .fg{display:flex;flex-direction:column;gap:6px}
    .flbl{
      font-size:13px;font-weight:800;letter-spacing:.08em;
      text-transform:uppercase;color:var(--t2);
    }
    .fctl{
      background:rgba(255,255,255,.05);
      border:1.5px solid rgba(255,255,255,.12);
      border-radius:10px;padding:11px 15px;
      color:var(--t1);font-family:'Inter',sans-serif;
      font-size:15px;font-weight:600;outline:none;
      transition:border-color .2s,box-shadow .2s,background .2s;
    }
    .fctl:focus{
      border-color:rgba(74,222,128,.5);
      box-shadow:0 0 0 3px rgba(74,222,128,.1);
      background:rgba(255,255,255,.08);
    }
    .fctl option{background:#0c0f1c}
    .frow{display:flex;gap:14px}
    .frow .fg{flex:1}
    .iwrap{position:relative;display:flex;align-items:center}
    .iwrap .fctl{width:100%;padding-right:48px}
    .iadd{
      position:absolute;right:15px;
      font-size:13px;font-weight:800;color:var(--t3);pointer-events:none;
    }
    .preview{
      background:rgba(74,222,128,.06);
      border:1.5px dashed rgba(74,222,128,.25);
      border-radius:12px;padding:12px 16px;
    }
    .preview-lbl{
      font-size:11px;font-weight:800;letter-spacing:.1em;
      text-transform:uppercase;color:rgba(134,239,172,.75);margin-bottom:4px;
    }
    .preview-val{font-family:var(--mono);font-size:14px;font-weight:700;color:var(--t1)}

    .modal-foot{
      padding:16px 28px 22px;
      display:flex;align-items:center;justify-content:flex-end;gap:12px;
      border-top:1px solid var(--border);
      flex-shrink:0;
    }
    .btn{
      padding:10px 22px;border-radius:10px;
      font-size:14px;font-weight:800;cursor:pointer;
      transition:all .18s ease;letter-spacing:.01em;
    }
    .btn-ghost{
      background:rgba(255,255,255,.07);
      border:1.5px solid rgba(255,255,255,.12);color:var(--t2);
    }
    .btn-ghost:hover{background:rgba(255,255,255,.12);color:var(--t1)}
    .btn-primary{
      background:#22c55e;border:1.5px solid transparent;
      color:#052e12;box-shadow:0 0 20px rgba(34,197,94,.35);
    }
    .btn-primary:hover{background:#4ade80;box-shadow:0 0 28px rgba(74,222,128,.55);transform:translateY(-1px)}
    .btn-danger{
      background:rgba(239,68,68,.15);border:1.5px solid rgba(239,68,68,.3);
      color:#fca5a5;
    }
    .btn-danger:hover{background:rgba(239,68,68,.25);color:#ffffff}

    /* ── Settings Section Cards ── */
    .settings-section{
      border:1.5px solid var(--border);border-radius:16px;
      padding:18px 22px;background:rgba(255,255,255,.02);
    }
    .settings-section-title{
      font-size:14px;font-weight:800;letter-spacing:.1em;
      text-transform:uppercase;color:var(--t2);margin-bottom:14px;
      display:flex;align-items:center;gap:8px;
    }
    .settings-section-title .s-icon{font-size:16px}

    /* Toggle Switch */
    .toggle-wrap{display:flex;align-items:center;justify-content:space-between;padding:4px 0}
    .toggle-label{font-size:15px;font-weight:700;color:var(--t1)}
    .toggle-sub{font-size:12px;font-weight:600;color:var(--t3);margin-top:2px}
    .toggle{
      position:relative;width:48px;height:26px;
      background:rgba(255,255,255,.1);border-radius:13px;
      cursor:pointer;transition:background .2s;flex-shrink:0;
    }
    .toggle.active{background:rgba(74,222,128,.45)}
    .toggle::after{
      content:'';position:absolute;top:3px;left:3px;
      width:20px;height:20px;border-radius:50%;
      background:#fff;transition:transform .2s;
    }
    .toggle.active::after{transform:translateX(22px)}

    /* Action Buttons Row */
    .action-row{display:flex;gap:10px;flex-wrap:wrap}
    .action-btn{
      padding:8px 16px;border-radius:10px;
      font-size:13px;font-weight:800;cursor:pointer;
      background:rgba(255,255,255,.06);
      border:1.5px solid rgba(255,255,255,.12);
      color:var(--t2);transition:all .18s;letter-spacing:.02em;
    }
    .action-btn:hover{background:rgba(255,255,255,.12);color:var(--t1);border-color:var(--border-hi)}

    .nil{color:var(--t3)!important;font-weight:400!important}
  </style>
</head>
<body>

<div class="top-bar">
  <button class="top-settings-btn" id="btn-top-settings" type="button" title="Buka Pengaturan Aplikasi">
    <span class="gear-icon">⚙</span>
    <span>Pengaturan</span>
  </button>
  <div class="beacon" id="conn">
    <div class="beacon-dot"></div>
    <span class="beacon-lbl" id="conn-lbl">Connecting…</span>
    <span class="beacon-sep">|</span>
    <span class="beacon-hint">F11: Sembunyikan • F10: Tembus Klik</span>
  </div>
</div>

<div class="stage">

  <!-- TOP ROW: CPU + GPU -->
  <div class="row-top">

    <!-- CPU -->
    <div class="card card-cpu">
      <div class="card-head">
        <div class="chip">
          <div class="chip-dot" style="background:var(--cpu);box-shadow:0 0 9px var(--cpu-glow)"></div>
          <span class="chip-label">CPU</span>
        </div>
        <span class="device-name" id="cpu-name">—</span>
      </div>

      <div class="temp-hero" id="cpu-temp-wrap" style="color:var(--cpu)">
        <span class="temp-val" id="cpu-temp">—</span>
        <span class="temp-deg">°C</span>
        <span class="temp-tag">Package</span>
      </div>

      <div class="load-row">
        <span class="load-lbl">LOAD</span>
        <div class="bar-track">
          <div class="bar-fill" id="bar-cpu" style="background:var(--cpu);color:var(--cpu)"></div>
        </div>
        <span class="bar-val" id="cpu-load">—</span>
      </div>

      <div class="stats">
        <div class="stat"><span class="stat-k">Core Clock</span><span class="stat-v" id="cpu-clock">—</span></div>
        <div class="stat"><span class="stat-k">Package Power</span><span class="stat-v" id="cpu-power">—</span></div>
      </div>
    </div>

    <!-- GPU -->
    <div class="card card-gpu">
      <div class="card-head">
        <div class="chip">
          <div class="chip-dot" style="background:var(--gpu);box-shadow:0 0 9px var(--gpu-glow)"></div>
          <span class="chip-label">GPU</span>
        </div>
        <span class="device-name" id="gpu-name">—</span>
      </div>

      <div class="temp-hero" id="gpu-temp-wrap" style="color:var(--gpu)">
        <span class="temp-val" id="gpu-temp">—</span>
        <span class="temp-deg">°C</span>
        <span class="temp-tag">Core</span>
      </div>

      <div class="load-row">
        <span class="load-lbl">LOAD</span>
        <div class="bar-track">
          <div class="bar-fill" id="bar-gpu" style="background:var(--gpu);color:var(--gpu)"></div>
        </div>
        <span class="bar-val" id="gpu-load">—</span>
      </div>

      <div class="stats">
        <div class="stat"><span class="stat-k">Hotspot</span><span class="stat-v" id="gpu-hotspot">—</span></div>
        <div class="stat"><span class="stat-k">Core Clock</span><span class="stat-v" id="gpu-clock">—</span></div>
        <div class="stat"><span class="stat-k">Fan Speed</span><span class="stat-v" id="gpu-fan">—</span></div>
        <div class="stat"><span class="stat-k">Board Power</span><span class="stat-v" id="gpu-power">—</span></div>
      </div>
    </div>

  </div><!-- /row-top -->

  <!-- BOTTOM ROW: RAM / VRAM / FPS / POWER -->
  <div class="row-bot">

    <!-- RAM -->
    <div class="card card-ram">
      <div class="card-head">
        <div class="chip">
          <div class="chip-dot" style="background:var(--ram);box-shadow:0 0 9px var(--ram-glow)"></div>
          <span class="chip-label">RAM</span>
        </div>
        <span class="temp-pill" id="ram-clock" style="display:none"></span>
      </div>

      <div class="gauge-wrap">
        <div class="gauge-box" style="color:var(--ram)">
          <svg viewBox="0 0 170 170">
            <circle class="gauge-bg"  cx="85" cy="85" r="68"/>
            <circle class="gauge-arc" id="g-ram" cx="85" cy="85" r="68"
              stroke="var(--ram)" stroke-dasharray="427.26" stroke-dashoffset="427.26"/>
          </svg>
          <div class="gauge-center">
            <span class="gauge-pct" id="ram-pct" style="color:var(--ram)">—</span>
            <span class="gauge-sub">% used</span>
          </div>
        </div>
      </div>

      <div class="stats">
        <div class="stat"><span class="stat-k">Used</span><span class="stat-v" id="ram-used">—</span></div>
        <div class="stat"><span class="stat-k">Total</span><span class="stat-v" id="ram-total">—</span></div>
      </div>
    </div>

    <!-- VRAM -->
    <div class="card card-vram">
      <div class="card-head">
        <div class="chip">
          <div class="chip-dot" style="background:var(--vram);box-shadow:0 0 9px var(--vram-glow)"></div>
          <span class="chip-label">VRAM</span>
        </div>
        <span class="temp-pill" id="vram-temp-wrap" style="display:none">
          <span id="vram-temp">—</span> °C
        </span>
      </div>

      <div class="gauge-wrap">
        <div class="gauge-box" style="color:var(--vram)">
          <svg viewBox="0 0 170 170">
            <circle class="gauge-bg"  cx="85" cy="85" r="68"/>
            <circle class="gauge-arc" id="g-vram" cx="85" cy="85" r="68"
              stroke="var(--vram)" stroke-dasharray="427.26" stroke-dashoffset="427.26"/>
          </svg>
          <div class="gauge-center">
            <span class="gauge-pct" id="vram-pct" style="color:var(--vram)">—</span>
            <span class="gauge-sub">% used</span>
          </div>
        </div>
      </div>

      <div class="stats">
        <div class="stat"><span class="stat-k">Used</span><span class="stat-v" id="vram-used">—</span></div>
        <div class="stat"><span class="stat-k">Total</span><span class="stat-v" id="vram-total">—</span></div>
        <div class="stat"><span class="stat-k">Clock</span><span class="stat-v" id="vram-clock">—</span></div>
      </div>
    </div>

    <!-- FPS -->
    <div class="card card-fps">
      <div class="card-head">
        <div class="chip">
          <div class="chip-dot" style="background:var(--fps);box-shadow:0 0 9px var(--fps-glow)"></div>
          <span class="chip-label">FPS</span>
        </div>
        <span class="live-badge">RTSS Live</span>
      </div>

      <div style="position:relative;z-index:1">
        <span class="fps-num" id="fps-cur">—</span><span class="fps-unit">fps</span>
      </div>

      <div class="fps-ft">Frametime: <span id="fps-ft">—</span> ms</div>

      <div class="fps-lows">
        <div class="low-cell">
          <span class="low-lbl">1% Low</span>
          <span class="low-val nil" id="fps-1p">—</span>
        </div>
        <div class="low-cell">
          <span class="low-lbl">0.1% Low</span>
          <span class="low-val nil" id="fps-01p">—</span>
        </div>
      </div>
    </div>

    <!-- POWER & ELECTRICITY TRACKER -->
    <div class="card card-pwr">
      <div class="card-head">
        <div class="chip">
          <div class="chip-dot" style="background:var(--pwr);box-shadow:0 0 9px var(--pwr-glow)"></div>
          <span class="chip-label">Power</span>
        </div>
        <button class="gear-btn" id="btn-gear" type="button" title="Pengaturan Tarif PLN">⚙</button>
      </div>

      <div style="position:relative;z-index:1">
        <span class="pwr-num" id="pwr-val">—</span><span class="pwr-unit">W</span>
      </div>

      <!-- Accumulative Energy Panel (replaces old static cost panel) -->
      <div class="energy-panel">
        <span class="energy-month-cost" id="energy-month-cost">—</span>
        <span class="energy-month-kwh" id="energy-month-kwh">— kWh Bulan Ini</span>
        <span class="energy-today" id="energy-today">Hari ini: —</span>
      </div>
    </div>

  </div><!-- /row-bot -->

</div><!-- /stage -->

<!-- PLN Settings Modal (tarif listrik) -->
<div class="modal-overlay" id="pln-modal" style="display:none">
  <div class="modal-card">
    <div class="modal-head">
      <div class="modal-title">
        <div class="modal-dot"></div>
        <h3>Kustomisasi Biaya Listrik PLN</h3>
      </div>
      <button class="modal-x" id="btn-close-modal" type="button">✕</button>
    </div>

    <div class="modal-body">
      <div class="fg">
        <label class="flbl">Golongan Daya Listrik PLN</label>
        <select class="fctl" id="pln-select-tier">
          <option value="900_nonsubsidi" data-rate="1352"    data-label="900 VA">900 VA (R-1/TR Non-Subsidi) — Rp 1.352 / kWh</option>
          <option value="1300_2200"      data-rate="1444.7"  data-label="1300/2200 VA">1.300 VA &amp; 2.200 VA (R-1/TR) — Rp 1.444,70 / kWh</option>
          <option value="3500_5500"      data-rate="1699.53" data-label="3500-5500 VA">3.500 VA – 5.500 VA (R-2/TR) — Rp 1.699,53 / kWh</option>
          <option value="6600_up"        data-rate="1699.53" data-label="6600 VA+">6.600 VA ke atas (R-3/TR) — Rp 1.699,53 / kWh</option>
          <option value="900_subsidi"    data-rate="605"     data-label="900 VA Subsidi">900 VA (R-1/TR Bersubsidi) — Rp 605 / kWh</option>
          <option value="450_subsidi"    data-rate="415"     data-label="450 VA Subsidi">450 VA (R-1/TR Bersubsidi) — Rp 415 / kWh</option>
          <option value="custom"         data-rate="0"       data-label="Custom">Tarif Kustom (Input Manual Rp/kWh)</option>
        </select>
      </div>

      <div class="fg" id="pln-custom-group" style="display:none">
        <label class="flbl">Tarif Manual (Rp per kWh)</label>
        <input type="number" class="fctl" id="pln-custom-rate" min="1" step="0.01" value="1352" placeholder="Contoh: 1444.70">
      </div>

      <div class="fg">
        <label class="flbl">Tanggal Mulai Periode Tagihan Bulanan</label>
        <div class="iwrap">
          <input type="number" class="fctl" id="pln-billing-day" min="1" max="28" value="1">
          <span class="iadd">Tgl</span>
        </div>
      </div>

      <div class="preview">
        <div class="preview-lbl">Info Tarif Aktif</div>
        <div class="preview-val" id="pln-formula">Tarif: Rp 1.352/kWh · Periode mulai tanggal 1</div>
      </div>
    </div>

    <div class="modal-foot">
      <button class="btn btn-ghost"   id="btn-reset-pln" type="button">Reset Default</button>
      <button class="btn btn-primary" id="btn-save-pln"  type="button">Simpan Pengaturan</button>
    </div>
  </div>
</div>

<!-- App Settings Modal -->
<div class="modal-overlay" id="settings-modal" style="display:none">
  <div class="modal-card" style="width:640px">
    <div class="modal-head">
      <div class="modal-title">
        <div class="modal-dot"></div>
        <h3>⚙ Pengaturan Aplikasi</h3>
      </div>
      <button class="modal-x" id="btn-close-settings" type="button">✕</button>
    </div>

    <div class="modal-body">

      <!-- Performance Mode -->
      <div class="settings-section">
        <div class="settings-section-title"><span class="s-icon">🎮</span> Mode Performa</div>
        <div class="fg">
          <select class="fctl" id="set-mode">
            <option value="Gamer">🎮 Mode Gamer — Hanya OSD In-Game (Hemat Resource)</option>
            <option value="Streamer">🎥 Mode Streamer — OSD + Dashboard Window (OBS / Layar Kedua)</option>
          </select>
        </div>
      </div>

      <!-- In-Game OSD (RTSS) -->
      <div class="settings-section">
        <div class="settings-section-title"><span class="s-icon">📊</span> In-Game OSD (RTSS)</div>
        <div class="toggle-wrap">
          <div>
            <div class="toggle-label">In-Game OSD (RivaTuner / RTSS)</div>
            <div class="toggle-sub">Injeksi teks real-time ke overlay RTSS di dalam game</div>
          </div>
          <div class="toggle" id="set-rtss-toggle"></div>
        </div>
        <div class="fg" style="margin-top:12px">
          <label class="flbl">Gaya Tampilan OSD In-Game</label>
          <select class="fctl" id="set-rtss-style">
            <option value="FullAllInOne">Lengkap + Hotspot + VRAM + FT (All-In-One)</option>
            <option value="HorizontalBar">Baris Horizontal (Cyberpunk)</option>
            <option value="StackedBlock">Kotak Bertumpuk (2 Baris)</option>
            <option value="Minimal">Minimalis Ringkas</option>
          </select>
        </div>
      </div>

      <!-- System Integration -->
      <div class="settings-section">
        <div class="settings-section-title"><span class="s-icon">🖥️</span> Integrasi Sistem</div>
        <div class="toggle-wrap">
          <div>
            <div class="toggle-label">Run on Startup</div>
            <div class="toggle-sub">Jalankan otomatis saat Windows login</div>
          </div>
          <div class="toggle" id="set-startup-toggle"></div>
        </div>
        <div class="action-row" style="margin-top:14px">
          <button class="action-btn" id="set-btn-update" type="button">🔄 Periksa Pembaruan...</button>
          <button class="action-btn" id="set-btn-restart-ws" type="button">🔌 Restart WebSocket Server</button>
        </div>
      </div>

    </div>

    <div class="modal-foot">
      <button class="btn btn-ghost" id="btn-close-settings2" type="button">Tutup</button>
    </div>
  </div>
</div>

<script>
const $ = id => document.getElementById(id);
const CIRC = 2 * Math.PI * 68; // r=68 → 427.26

function st(el, txt) {
  if (el && el.textContent !== txt) el.textContent = txt;
}

function sv(el, val, unit='', d=0) {
  if (!el) return;
  const h = val == null ? '—' : Number(val).toFixed(d)+(unit?`<span class="u">${unit}</span>`:'');
  if (el.innerHTML !== h) {
    el.innerHTML = h;
    if (val == null) el.classList.add('nil');
    else el.classList.remove('nil');
  }
}

function setBar(id, pct, base) {
  const el=$(id); if (!el) return;
  const w=Math.min(100,Math.max(0,pct||0));
  const newW = w+'%';
  if (el.style.width !== newW) el.style.width = newW;
  const c=pct>=90?'var(--hot)':pct>=80?'var(--warn)':base;
  if (el.style.color !== c) { el.style.background=c; el.style.color=c; }
}

function setGauge(arcId, pctId, pct, base) {
  const arc=$(arcId); if (!arc) return;
  const offset = (CIRC*(1-Math.min(100,Math.max(0,pct||0))/100))+'px';
  if (arc.style.strokeDashoffset !== offset) arc.style.strokeDashoffset = offset;
  const c=pct>=90?'var(--hot)':pct>=80?'var(--warn)':base;
  if (arc.style.stroke !== c) arc.style.stroke=c;
  const pe=$(pctId);
  if (pe) {
    const txt = pct!=null?Math.round(pct).toString():'—';
    if (pe.textContent !== txt) pe.textContent = txt;
    if (pe.style.color !== c) pe.style.color = c;
  }
}

function tempColor(t) { return t>=85?'var(--hot)':t>=75?'var(--warn)':null; }

function shortName(n) {
  if(!n) return null;
  return n.replace(/AMD Radeon\s*/i,'').replace(/NVIDIA GeForce\s*/i,'')
          .replace(/Intel Core\s*/i,'').replace(/AMD Ryzen\s*/i,'Ryzen ')
          .trim().substring(0,38);
}

// ─── PLN Config (tariff, billing) ──────────────────────────────────────────
const DFLT=Object.assign({tier:'900_nonsubsidi',rate:1352,hours:8,days:30,label:'900 VA',billingDay:1},JSON.parse('{{{plnJson}}}'));
let pln={...DFLT};
let _hasLocal=false;
try{const s=localStorage.getItem('legaxyy_pln_cfg');if(s){pln={...DFLT,...JSON.parse(s)};_hasLocal=true;}}catch{}

function syncPlnToBackend(){
  try{
    fetch('/api/pln', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        rate: pln.rate,
        hours: pln.hours,
        days: pln.days,
        tier: pln.tier,
        label: pln.label
      })
    }).catch(()=>{});
  }catch(e){}
}

// Sync PLN on startup
(async function initPlnSync(){
  try{
    const r=await fetch('/api/pln',{cache:'no-store'});
    if(r.ok){
      const srv=await r.json();
      if(srv && srv.rate>0){
        if(_hasLocal && (srv.rate!==pln.rate||srv.hours!==pln.hours||srv.days!==pln.days||srv.tier!==pln.tier)){
          syncPlnToBackend();
        }else{
          pln={rate:srv.rate,hours:srv.hours,days:srv.days,tier:srv.tier||pln.tier,label:srv.label||pln.label,billingDay:pln.billingDay};
          try{localStorage.setItem('legaxyy_pln_cfg',JSON.stringify(pln));}catch{}
        }
        return;
      }
    }
  }catch(e){}
  syncPlnToBackend();
})();

// ─── Energy cost rendering ────────────────────────────────────────────────
function renderEnergy(pw, todayKwh, monthKwh) {
  const elMonthCost = $('energy-month-cost');
  const elMonthKwh  = $('energy-month-kwh');
  const elToday     = $('energy-today');

  if (monthKwh != null && !isNaN(monthKwh)) {
    const monthCost = monthKwh * pln.rate;
    elMonthCost.textContent = 'Rp ' + Math.round(monthCost).toLocaleString('id-ID');
    elMonthKwh.textContent  = Number(monthKwh).toFixed(2) + ' kWh Bulan Ini';
  } else {
    elMonthCost.textContent = '—';
    elMonthKwh.textContent  = '— kWh Bulan Ini';
  }

  if (todayKwh != null && !isNaN(todayKwh)) {
    const todayCost = todayKwh * pln.rate;
    elToday.textContent = 'Hari ini: Rp ' + Math.round(todayCost).toLocaleString('id-ID') + ' (' + Number(todayKwh).toFixed(3) + ' kWh)';
  } else {
    elToday.textContent = 'Hari ini: —';
  }
}

// ─── WebSocket Connect ────────────────────────────────────────────────────
function connect() {
  const bcon=$('conn'), lbl=$('conn-lbl');
  const isS=location.protocol==='https:';
  const proto=isS?'wss://':'ws://';
  const host=location.hostname||'localhost';
  const url=isS?proto+'ws.'+location.hostname:proto+host+':{{{_wsPort}}}';

  let pendingData = null;
  let rafPending = false;

  function applyData(d) {
    st($('cpu-name'), shortName(d.device?.cpuName)||'—');
    st($('gpu-name'), shortName(d.device?.gpuName)||'—');

    // CPU
    const ct=d.cpu?.temp;
    if(ct!=null){
      st($('cpu-temp'), Number(ct).toFixed(1));
      const col = tempColor(ct)||'var(--cpu)';
      const elWrap = $('cpu-temp-wrap');
      if (elWrap && elWrap.style.color !== col) elWrap.style.color = col;
    }
    sv($('cpu-load'),  d.cpu?.load,  '%',   1);
    sv($('cpu-clock'), d.cpu?.clock, 'MHz', 0);
    sv($('cpu-power'), d.cpu?.power, 'W',   1);
    if(d.cpu?.load!=null) setBar('bar-cpu',d.cpu.load,'var(--cpu)');

    // GPU
    const gt=d.gpu?.temp;
    if(gt!=null){
      st($('gpu-temp'), Number(gt).toFixed(1));
      const col = tempColor(gt)||'var(--gpu)';
      const elWrap = $('gpu-temp-wrap');
      if (elWrap && elWrap.style.color !== col) elWrap.style.color = col;
    }
    sv($('gpu-load'),    d.gpu?.load,        '%',   1);
    sv($('gpu-hotspot'), d.gpu?.hotSpotTemp, '°C',  1);
    sv($('gpu-clock'),   d.gpu?.coreClock,   'MHz', 0);
    sv($('gpu-fan'),     d.gpu?.fanRpm,      'RPM', 0);
    sv($('gpu-power'),   d.gpu?.power,       'W',   1);
    if(d.gpu?.load!=null) setBar('bar-gpu',d.gpu.load,'var(--gpu)');

    // RAM
    setGauge('g-ram','ram-pct',d.mem?.load,'var(--ram)');
    sv($('ram-used'),  d.mem?.usedGb,  'GB',2);
    sv($('ram-total'), d.mem?.totalGb, 'GB',1);
    const rc=$('ram-clock');
    if(d.mem?.clock!=null){
      st(rc, d.mem.clock+' MHz');
      if (rc.style.display!=='') rc.style.display='';
    } else if (rc && rc.style.display!=='none') rc.style.display='none';

    // VRAM
    const vt=d.gpu?.hotSpotTemp??d.gpu?.temp;
    const vtw=$('vram-temp-wrap');
    if(vt!=null){
      st($('vram-temp'), Number(vt).toFixed(1));
      if (vtw && vtw.style.display!=='') vtw.style.display='';
    } else if (vtw && vtw.style.display!=='none') vtw.style.display='none';
    setGauge('g-vram','vram-pct',d.gpu?.vramPct,'var(--vram)');
    sv($('vram-used'),  d.gpu?.vramUsedGb,  'GB',2);
    sv($('vram-total'), d.gpu?.vramTotalGb, 'GB',1);
    sv($('vram-clock'), d.gpu?.memClock,    'MHz',0);

    // FPS
    const fc=d.fps?.current,ft=d.fps?.frametimeMs,f1=d.fps?.low1pct,f0=d.fps?.low01pct;
    st($('fps-cur'), fc!=null?Math.round(fc).toString():'—');
    st($('fps-ft'), ft!=null?Number(ft).toFixed(2):'—');
    const e1=$('fps-1p'),e01=$('fps-01p');
    if(f1!=null){ st(e1, Math.round(f1).toString()); e1.classList.remove('nil'); }
    else if (e1) { st(e1, '—'); e1.classList.add('nil'); }
    if(f0!=null){ st(e01, Math.round(f0).toString()); e01.classList.remove('nil'); }
    else if (e01) { st(e01, '—'); e01.classList.add('nil'); }

    // Power & Energy
    const pw=d.power?.totalW;
    st($('pwr-val'), pw!=null?Number(pw).toFixed(1):'—');
    renderEnergy(pw, d.power?.todayKwh, d.power?.monthKwh);
  }

  const ws=new WebSocket(url);
  ws.onopen  =()=>{ bcon.className='beacon live'; lbl.textContent='Connected'; };
  ws.onclose =()=>{ bcon.className='beacon'; lbl.textContent='Reconnecting…'; setTimeout(connect,2000); };
  ws.onerror =()=>{ bcon.className='beacon'; lbl.textContent='Error'; };

  ws.onmessage=({data})=>{
    try{ pendingData=JSON.parse(data); }catch{ return; }
    if (!rafPending) {
      rafPending = true;
      requestAnimationFrame(() => {
        rafPending = false;
        if (pendingData) applyData(pendingData);
      });
    }
  };
}

// ─── PLN Modal ─────────────────────────────────────────────────────────────
const modal=$('pln-modal');
const tierSel=$('pln-select-tier');
const cgrp=$('pln-custom-group');
const cIn=$('pln-custom-rate');
const bDayIn=$('pln-billing-day');

function updateFormula(){
  $('pln-formula').textContent=`Tarif: Rp ${Number(pln.rate).toLocaleString('id-ID')}/kWh · Periode mulai tanggal ${pln.billingDay||1}`;
}

function openPlnModal(){
  tierSel.value=pln.tier||'900_nonsubsidi';
  cgrp.style.display=tierSel.value==='custom'?'flex':'none';
  if(tierSel.value==='custom')cIn.value=pln.rate;
  bDayIn.value=pln.billingDay||1;
  updateFormula(); modal.style.display='flex';
}
function closePlnModal(){modal.style.display='none';}

$('btn-gear').addEventListener('click',e=>{e.stopPropagation();openPlnModal();});
$('btn-close-modal').addEventListener('click',closePlnModal);
modal.addEventListener('click',e=>{if(e.target===modal)closePlnModal();});

tierSel.addEventListener('change',()=>{
  if(tierSel.value==='custom'){cgrp.style.display='flex';pln.rate=Math.max(1,parseFloat(cIn.value)||1352);pln.label='Custom';}
  else{cgrp.style.display='none';const o=tierSel.selectedOptions[0];pln.rate=parseFloat(o.getAttribute('data-rate'));pln.label=o.getAttribute('data-label');}
  updateFormula();
});
cIn.addEventListener('input',()=>{if(tierSel.value==='custom'){pln.rate=Math.max(1,parseFloat(cIn.value)||1352);updateFormula();}});
bDayIn.addEventListener('input',()=>{pln.billingDay=Math.max(1,Math.min(28,parseInt(bDayIn.value)||1));updateFormula();});

$('btn-save-pln').addEventListener('click',()=>{
  pln.tier=tierSel.value;
  if(pln.tier==='custom'){pln.rate=Math.max(1,parseFloat(cIn.value)||1352);pln.label='Custom';}
  else{const o=tierSel.selectedOptions[0];pln.rate=parseFloat(o.getAttribute('data-rate'));pln.label=o.getAttribute('data-label');}
  pln.billingDay=Math.max(1,Math.min(28,parseInt(bDayIn.value)||1));
  try{localStorage.setItem('legaxyy_pln_cfg',JSON.stringify(pln));}catch{}
  syncPlnToBackend();
  // Also update billing cycle start day in backend
  fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({action:'setBillingCycleDay',value:pln.billingDay})}).catch(()=>{});
  closePlnModal();
});

$('btn-reset-pln').addEventListener('click',()=>{
  pln={...DFLT};
  try{localStorage.removeItem('legaxyy_pln_cfg');}catch{}
  syncPlnToBackend();
  tierSel.value=pln.tier; cgrp.style.display='none';
  bDayIn.value=pln.billingDay||1;
  updateFormula();
  fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({action:'setBillingCycleDay',value:1})}).catch(()=>{});
});

// ─── App Settings Modal ────────────────────────────────────────────────────
const settingsModal=$('settings-modal');
const setMode=$('set-mode');
const setRtssToggle=$('set-rtss-toggle');
const setRtssStyle=$('set-rtss-style');
const setStartupToggle=$('set-startup-toggle');

// Fetch current settings from backend on load
async function loadSettings(){
  try{
    const r=await fetch('/api/settings',{cache:'no-store'});
    if(r.ok){
      const s=await r.json();
      if(s){
        setMode.value=s.mode||'Streamer';
        setRtssToggle.classList.toggle('active',!!s.rtssOsdEnabled);
        setRtssStyle.value=s.rtssStyle||'FullAllInOne';
        setStartupToggle.classList.toggle('active',!!s.startupEnabled);
        if(s.billingCycleStartDay) pln.billingDay=s.billingCycleStartDay;
      }
    }
  }catch{}
}

function openSettingsModal(){
  loadSettings();
  settingsModal.style.display='flex';
}
function closeSettingsModal(){settingsModal.style.display='none';}

$('btn-top-settings').addEventListener('click',openSettingsModal);
$('btn-close-settings').addEventListener('click',closeSettingsModal);
$('btn-close-settings2').addEventListener('click',closeSettingsModal);
settingsModal.addEventListener('click',e=>{if(e.target===settingsModal)closeSettingsModal();});

// Settings actions — send to backend via API
setMode.addEventListener('change',()=>{
  fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({action:'setMode',value:setMode.value})}).catch(()=>{});
});

setRtssToggle.addEventListener('click',()=>{
  const active=!setRtssToggle.classList.contains('active');
  setRtssToggle.classList.toggle('active',active);
  fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({action:'toggleRtssOsd',value:active})}).catch(()=>{});
});

setRtssStyle.addEventListener('change',()=>{
  fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({action:'setRtssStyle',value:setRtssStyle.value})}).catch(()=>{});
});

setStartupToggle.addEventListener('click',()=>{
  const active=!setStartupToggle.classList.contains('active');
  setStartupToggle.classList.toggle('active',active);
  fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({action:'toggleStartup',value:active})}).catch(()=>{});
});

$('set-btn-update').addEventListener('click',()=>{
  fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({action:'checkUpdate'})}).catch(()=>{});
});

$('set-btn-restart-ws').addEventListener('click',()=>{
  fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({action:'restartWs'})}).catch(()=>{});
});

// ─── Auto-scale ────────────────────────────────────────────────────────────
function autoScale(){
  const sx=window.innerWidth/1920,sy=window.innerHeight/1080,s=Math.min(sx,sy);
  const el=document.body;
  el.style.transform=`scale(${s})`;
  el.style.transformOrigin='top left';
  el.style.left=`${Math.max(0,(window.innerWidth-1920*s)/2)}px`;
  el.style.top =`${Math.max(0,(window.innerHeight-1080*s)/2)}px`;
}
window.addEventListener('resize',autoScale);
window.addEventListener('DOMContentLoaded',autoScale);
autoScale();
connect();
</script>
</body>
</html>

""";
    }

    public void Dispose() => Stop();
}