# ⚡ LegaxyyFPS

<p align="center">
  <img src="AppIcon.ico" width="96" height="96" alt="LegaxyyFPS Logo" />
</p>

<p align="center">
  <strong>Native Windows Hardware & FPS Telemetry Overlay + Real-Time Electricity Cost Tracker</strong>
</p>

<p align="center">
  <a href="https://github.com/Azizaac/Legaxyy-FPS/releases/latest"><img src="https://img.shields.io/badge/Release-v1.3.0-blue?style=for-the-badge&logo=windows" alt="Latest Release" /></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/8.0"><img src="https://img.shields.io/badge/.NET-8.0_Windows-512BD4?style=for-the-badge&logo=dotnet" alt=".NET 8" /></a>
  <a href="https://www.guru3d.com/files-details/rtss-rivatuner-statistics-server-download.html"><img src="https://img.shields.io/badge/RTSS-Supported-orange?style=for-the-badge" alt="RTSS" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-green?style=for-the-badge" alt="License" /></a>
</p>

---

## 📖 Tentang LegaxyyFPS

**LegaxyyFPS** adalah aplikasi telemetry native Windows berkinerja tinggi yang dirancang untuk gamer, streamer, dan hardware enthusiast. 

Aplikasi ini membaca data sensor PC (CPU, GPU, RAM, VRAM, Fan RPM, Clocks) tingkat kernel menggunakan **LibreHardwareMonitor**, menangkap framerate & frametime akurat via **RTSS (RivaTuner Statistics Server)**, serta menghitung **akumulasi konsumsi listrik riil (kWh & Rupiah PLN)** tanpa membebani performa gaming kamu.

Data disajikan melalui:
1. **In-Game OSD (RTSS)** — Disuntikkan langsung di atas layar game layar penuh (Full Screen).
2. **Cyberpunk Dashboard Window (WebView2)** — Window overlay modern untuk monitor sekunder atau OBS Browser Source.
3. **Local WebSocket & REST API** — Siap diintegrasikan ke perangkat lain (Stream Deck, mobile display, dll.).

---

## ✨ Fitur Unggulan

### ⚡ 1. Real-Time Accumulative Electricity & Cost Tracker (Baru di v1.3)
- Mengukur konsumsi total daya sistem (Watt) setiap detik.
- Mengakumulasikan pemakaian energi aktual: $\text{kWh} = \frac{\text{Watt} \times 1\text{s}}{3600 \times 1000}$.
- **Hari Ini & Bulan Ini:** Melacak total kWh dan tagihan (Rp) hari ini serta siklus tagihan bulanan berjalan.
- **Konfigurasi Fleksibel:** Mendukung seluruh golongan tarif listrik PLN (900 VA, 1.300 VA, 2.200 VA, 3.500 VA – 6.600 VA+, Subsidi, atau Custom) dan tanggal mulai siklus tagihan bulanan (tgl 1–28).
- **Auto-Save:** Riwayat tersimpan otomatis setiap 60 detik di `%LocalAppData%\LegaxyyFPS\energy_log.json` untuk mencegah kehilangan data jika PC mati mendadak.

### 🎮 2. RTSS In-Game OSD Injection
- Menampilkan metrik real-time langsung di dalam game DirectX 9/11/12, Vulkan, dan OpenGL.
- 4 Preset Gaya Tampilan:
  - **Lengkap / All-In-One**: CPU + GPU + Hotspot + VRAM + Frametime + Biaya PLN.
  - **Baris Horizontal (Cyberpunk)**: Ringkas memanjang di atas layar.
  - **Kotak Bertumpuk**: Blok 2 baris hemat ruang.
  - **Minimalis**: Hanya FPS, Suhu, dan Watt.

### ⚙️ 3. Pengaturan Terpusat di Dashboard (Baru di v1.3)
- Akses semua konfigurasi langsung dari tombol **`⚙ Pengaturan`** di pojok kanan atas Dashboard.
- **Mode Performa:**
  - 🎮 *Mode Gamer:* Hanya OSD in-game yang aktif, jendela dashboard tertutup (hemat 100% resource untuk game kompetitif).
  - 🎥 *Mode Streamer:* OSD in-game + Jendela Dashboard aktif untuk OBS / second monitor.
- **Integrasi Sistem:** Toggle *Run on Startup* otomatis via Windows Task Scheduler.
- **System Tray Bersih:** Menu klik kanan tray disederhanakan hanya untuk aksi penting.

### 🔄 4. In-App Auto Update
- Terintegrasi dengan update server otomatis.
- Deteksi versi terbaru sekali klik dan langsung download, pasang, serta restart tanpa perlu membuka browser.

---

## 🏗️ Arsitektur Sistem

```
┌────────────────────────────────────────────────────────────────────────┐
│                        LegaxyyFPS.exe (System Tray)                    │
│                                                                        │
│   ┌───────────────────────────┐      ┌─────────────────────────────┐   │
│   │  HardwareMonitorService   │      │      RtssReaderService      │   │
│   │   (LibreHardwareMonitor)  │      │    (RTSS Shared Memory)     │   │
│   │  • CPU temp, load, clock  │      │  • FPS Realtime             │   │
│   │  • GPU core, hotspot, fan │      │  • 1% & 0.1% Frametime Lows │   │
│   │  • RAM / VRAM used & total│      │  • Frame time ms            │   │
│   └─────────────┬─────────────┘      └──────────────┬──────────────┘   │
│                 │                                   │                  │
│                 ▼                                   ▼                  │
│   ┌───────────────────────────┐      ┌─────────────────────────────┐   │
│   │   PowerAggregatorService  │      │     RtssOsdWriterService    │   │
│   │   Real PSU / Sensor HW    │      │  Injects HUD to in-game OSD │   │
│   └─────────────┬─────────────┘      └─────────────────────────────┘   │
│                 │                                                      │
│                 ▼                                                      │
│   ┌───────────────────────────┐                                        │
│   │   EnergyTrackerService    │ ───► Auto-saves to energy_log.json     │
│   │   Accumulates kWh & Cost  │                                        │
│   └─────────────┬─────────────┘                                        │
│                 │                                                      │
│                 ▼                                                      │
│   ┌───────────────────────────┐      ┌─────────────────────────────┐   │
│   │    WsBroadcastServer      │      │      HttpServerService      │   │
│   │  ws://127.0.0.1:8765      │      │    http://127.0.0.1:8766    │   │
│   │  Broadcast payload @500ms │      │  • Embedded HTML Dashboard  │   │
│   └─────────────┬─────────────┘      │  • REST API (/api/settings) │   │
│                 │                    └──────────────┬──────────────┘   │
└─────────────────┼───────────────────────────────────┼──────────────────┘
                  │                                   │
                  ▼                                   ▼
        ┌────────────────────────────────────────────────────────┐
        │        OverlayWindow (WebView2 Chromium Host)          │
        │   Cyberpunk Dark Dashboard & Settings Modal UI         │
        └────────────────────────────────────────────────────────┘
```

---

## ⌨️ Pintasan Keyboard (Hotkeys)

Saat jendela Dashboard aktif atau berjalan di latar belakang:

| Hotkey | Fungsi |
|---|---|
| <kbd>F11</kbd> | Sembunyikan / Tampilkan Jendela Dashboard secara instan |
| <kbd>F10</kbd> | Aktifkan / Nonaktifkan mode **Tembus Klik** (*Click-Through*) |

---

## 📡 API & WebSocket Reference

### 1. WebSocket Stream (`ws://127.0.0.1:8765`)
Setiap 500ms, server menyiarkan payload JSON berisi seluruh sensor:

```json
{
  "device": { "cpuName": "12th Gen i5-12400F", "gpuName": "RTX 3060 Ti" },
  "cpu": { "temp": 58.4, "load": 34.2, "clock": 4227, "power": 38.5 },
  "gpu": { "temp": 64.0, "hotSpotTemp": 74.8, "load": 82.5, "coreClock": 1860, "memClock": 7000, "fanRpm": 1650, "power": 178.2, "vramUsedGb": 3.04, "vramTotalGb": 8.0, "vramPct": 38.0 },
  "mem": { "usedGb": 6.72, "totalGb": 16.0, "load": 42.0, "clock": 3200 },
  "fps": { "current": 165.0, "frametimeMs": 6.06, "low1pct": 138.0, "low01pct": 112.0 },
  "power": {
    "totalW": 235.8,
    "isEstimate": false,
    "todayKwh": 0.8624,
    "monthKwh": 10.2045
  }
}
```

### 2. REST API Endpoints (`http://127.0.0.1:8766`)
* `GET /api/settings` — Mengambil status mode performa, status OSD, startup, dan konfigurasi.
* `POST /api/settings` — Memperbarui pengaturan dari Dashboard:
  * `{ "action": "setMode", "value": "Gamer" | "Streamer" }`
  * `{ "action": "toggleRtssOsd", "value": true | false }`
  * `{ "action": "setRtssStyle", "value": "FullAllInOne" | "HorizontalBar" | "StackedBlock" | "Minimal" }`
  * `{ "action": "toggleStartup", "value": true | false }`
  * `{ "action": "setBillingCycleDay", "value": 1..28 }`
  * `{ "action": "restartWs" }`
  * `{ "action": "checkUpdate" }`
* `GET /api/pln` & `POST /api/pln` — Konfigurasi tarif PLN & sync ke backend OSD.

---

## 🛠️ Prasyarat & Panduan Kompilasi

### Prasyarat:
1. **Windows 10 / 11 (64-bit)**
2. **.NET 8 SDK** (versi 8.0 ke atas)
3. **Inno Setup 6** (opsional, jika ingin membuat file installer `.exe`)
4. **RTSS (RivaTuner Statistics Server)** (opsional, diperlukan jika ingin mengaktifkan OSD dalam game)

### Kompilasi dari Source:

```powershell
# 1. Clone repository
git clone https://github.com/Azizaac/Legaxyy-FPS.git
cd Legaxyy-FPS

# 2. Build Release Single-File
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\dist\App

# 3. Atau jalankan script otomatisasi penuh (Build + Inno Setup):
.\build-all.bat
```

Installer setup final akan berada di: `Release\LegaxyyFPS_Setup_v1.3.0.exe`.

---

## 🌐 Server Pembaruan (aaPanel / Nginx)

Untuk memasang landing page download dan server auto-update otomatis:
1. Upload isi folder `web-server/` (`index.html`, `version.json`, dan folder `downloads/`) ke root web server kamu (misal: `/www/wwwroot/fps.domainanda.my.id`).
2. Masukkan URL `version.json` kamu ke `appsettings.json` di aplikasi desktop:
   ```json
   {
     "UpdateUrl": "https://fps.domainanda.my.id/version.json"
   }
   ```
3. Saat rilis baru tersedia, user akan otomatis menerima notifikasi pembaruan di dalam aplikasi.

---

## 📄 Lisensi

Proyek ini dirilis di bawah lisensi [MIT License](LICENSE). Bebas digunakan, dimodifikasi, dan didistribusikan.
