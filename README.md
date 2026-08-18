# Overlay Data Bridge

A lightweight Windows background service that reads real-time PC hardware sensor data (CPU/GPU/Memory/Power) and RTSS frametime data, then broadcasts it as a JSON stream over a local WebSocket server — ready to be consumed by an HTML overlay in OBS Browser Source.

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   OverlayDataBridge.exe                 │
│  (System Tray, WinExe, runs as Administrator)           │
│                                                         │
│  ┌──────────────────────┐  ┌─────────────────────────┐  │
│  │ HardwareMonitorService│  │   RtssReaderService     │  │
│  │ (LibreHardwareMonitor)│  │ (RTSS Shared Memory)    │  │
│  │ • CPU temp/load/clock │  │ • FPS current           │  │
│  │ • GPU all sensors     │  │ • 1% / 0.1% lows        │  │
│  │ • RAM used/total      │  │ • Frame time ms          │  │
│  └──────────┬───────────┘  └──────────┬──────────────┘  │
│             │                         │                  │
│             ▼                         ▼                  │
│  ┌──────────────────────┐  ┌─────────────────────────┐  │
│  │ PowerAggregatorService│  │   WsBroadcastServer     │  │
│  │ PSU sensor or estimate│  │ ws://localhost:8765     │  │
│  └──────────┬───────────┘  └──────────┬──────────────┘  │
│             └──────────────────────────┘                 │
└─────────────────────────────────────────────────────────┘
                              │  WebSocket JSON
                    ┌─────────▼──────────┐
                    │  OBS Browser Source │
                    │  (your overlay.html)│
                    └────────────────────┘
```

---

## Prerequisites

### 1. Install RTSS (RivaTuner Statistics Server)

Download from: https://www.guru3d.com/files-details/rtss-rivatuner-statistics-server-download.html

- Install and run RTSS before starting OverlayDataBridge
- RTSS must be actively capturing a game for FPS data to appear
- If RTSS is not running, the app still works — FPS fields will be `null`

### 2. .NET 8 Runtime / SDK

Download from: https://dotnet.microsoft.com/download/dotnet/8.0

Check if already installed:
```powershell
dotnet --version
# Should show 8.x.x
```

---

## Build Instructions

```powershell
# Navigate to project directory
cd C:\Users\choir\Desktop\OverlayDataBridge

# Restore NuGet packages and build (Release)
dotnet build -c Release

# Or publish as self-contained single file (no .NET runtime required on target):
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

Build output will be in:
- `bin\Release\net8.0-windows\` (framework-dependent)
- `publish\` (self-contained single file)

---

## Running the Application

> ⚠️ **LibreHardwareMonitor requires Administrator privileges** to access CPU/GPU/PSU sensor data.

### Option A: Run directly (right-click → Run as administrator)

Double-click `OverlayDataBridge.exe` (right-click → Run as administrator).

The app will appear in the **system tray** (bottom-right taskbar area). No window will open.

### Option B: Run from PowerShell (admin)

```powershell
# Open PowerShell as Administrator, then:
.\bin\Release\net8.0-windows\OverlayDataBridge.exe
```

### Tray Icon Usage

| Action | Result |
|--------|--------|
| Right-click | Opens context menu |
| Double-click | Shows status balloon tooltip |
| Context → Status | Shows running state and client count |
| Context → Restart WebSocket Server | Stops and restarts WebSocket on port 8765 |
| Context → Exit | Gracefully shuts down all services |

---

## WebSocket JSON Payload

The server broadcasts to `ws://localhost:8765` at ~2 times/second.

### Example Payload

```json
{
  "cpu": { "temp": 61.2, "load": 42.5, "clock": 4350, "power": 78.1 },
  "mem": { "load": 47.3, "usedGb": 14.8 },
  "gpu": { "temp": 66.0, "load": 71.2, "coreClock": 2510, "memClock": 10502, "fanRpm": 1600, "vramUsedGb": 8.2, "power": 210.4 },
  "fps": { "current": 143.7, "low1pct": 98.2, "low01pct": 71.5, "frametimeMs": 6.96 },
  "power": { "totalW": 412.6, "isEstimate": true }
}
```

### Field Reference

| Field | Unit | Notes |
|-------|------|-------|
| `cpu.temp` | °C | Package temp (preferred) or max core |
| `cpu.load` | % | Total CPU utilization |
| `cpu.clock` | MHz | Average of active cores |
| `cpu.power` | W | CPU Package power |
| `mem.load` | % | RAM utilization |
| `mem.usedGb` | GB | RAM in use |
| `gpu.temp` | °C | GPU core temperature |
| `gpu.load` | % | GPU core utilization |
| `gpu.coreClock` | MHz | GPU core clock |
| `gpu.memClock` | MHz | VRAM clock |
| `gpu.fanRpm` | RPM | GPU fan speed |
| `gpu.vramUsedGb` | GB | VRAM in use |
| `gpu.power` | W | GPU Package power draw |
| `fps.current` | FPS | Instantaneous FPS from RTSS |
| `fps.low1pct` | FPS | Average of slowest 1% frames (1000-sample buffer) |
| `fps.low01pct` | FPS | Average of slowest 0.1% frames |
| `fps.frametimeMs` | ms | Last frame time in milliseconds |
| `power.totalW` | W | System power (PSU sensor or estimate) |
| `power.isEstimate` | bool | `true` if estimated (CPU+GPU+40W overhead) |

> Fields unavailable due to missing hardware or RTSS not running are sent as `null`.

---

## OBS Browser Source Setup

1. Add a **Browser Source** in OBS
2. Check **"Local File"** and point to your `overlay.html`
   - Or use URL: `file:///C:/path/to/overlay.html`
3. Set width/height to match your stream resolution
4. In your overlay HTML, connect with:

```javascript
const ws = new WebSocket('ws://localhost:8765');
ws.onmessage = (event) => {
  const data = JSON.parse(event.data);
  // data.cpu.temp, data.fps.current, etc.
};
```

---

## Auto-Start on Login (Optional)

To run OverlayDataBridge automatically when Windows starts:

### Method 1: Task Scheduler (Recommended — runs as admin automatically)

```powershell
# Run as Administrator:
$action  = New-ScheduledTaskAction -Execute "C:\path\to\OverlayDataBridge.exe"
$trigger = New-ScheduledTaskTrigger -AtLogon
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -RunLevel Highest
Register-ScheduledTask -TaskName "OverlayDataBridge" -Action $action -Trigger $trigger -Principal $principal
```

### Method 2: Startup Folder Shortcut

1. Press `Win+R` → type `shell:startup` → Enter
2. Create a shortcut to `OverlayDataBridge.exe` in that folder
3. Right-click shortcut → Properties → Advanced → ✅ Run as administrator

---

## Configuration (`appsettings.json`)

```json
{
  "WebSocketPort": 8765,
  "HardwareUpdateIntervalMs": 1000,
  "FpsUpdateIntervalMs": 300,
  "BroadcastIntervalMs": 500
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `WebSocketPort` | 8765 | WebSocket server port |
| `HardwareUpdateIntervalMs` | 1000 | How often to poll CPU/GPU/MEM sensors (ms) |
| `FpsUpdateIntervalMs` | 300 | How often to poll RTSS shared memory (ms) |
| `BroadcastIntervalMs` | 500 | How often to broadcast JSON to WS clients (ms) |

---

## Logging

Logs are written to `logs/app.log` (relative to the executable directory).
Log files rotate at 5 MB, keeping 5 archived copies.

```
OverlayDataBridge/
└── logs/
    ├── app.log              ← current log
    ├── app.20240801_120000.log
    └── ...
```

---

## Troubleshooting

| Problem | Solution |
|---------|---------|
| No sensor data (all null) | Run as Administrator |
| FPS always null | Make sure RTSS is running and capturing a game |
| Port 8765 in use | Change `WebSocketPort` in `appsettings.json` |
| Overlay not connecting | Check Windows Firewall isn't blocking `localhost:8765` |
| App crashes on start | Check `logs/app.log` for details |
| No GPU data | iGPU-only systems: data will show iGPU readings (Intel HD/Arc) |

---

## Project Structure

```
OverlayDataBridge/
├── Program.cs                          ← Entry point, WinForms message loop
├── TrayApp.cs                          ← System tray host, owns all services
├── Services/
│   ├── AppLogger.cs                    ← File logger with rotation
│   ├── HardwareMonitorService.cs       ← LibreHardwareMonitor sensor reader
│   ├── RtssReaderService.cs            ← RTSS shared memory reader
│   ├── PowerAggregatorService.cs       ← PSU sensor / power estimation
│   └── WsBroadcastServer.cs            ← Fleck WebSocket server + broadcaster
├── Models/
│   ├── CpuData.cs
│   ├── GpuData.cs
│   ├── MemData.cs
│   ├── FpsData.cs
│   └── PowerData.cs
├── appsettings.json                    ← Configuration
├── app.manifest                        ← Requires admin elevation
└── OverlayDataBridge.csproj
```

---

## License

MIT — free to use and modify.
