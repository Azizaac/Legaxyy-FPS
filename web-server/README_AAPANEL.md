# 🚀 Panduan Setup Server Pembaruan di aaPanel

Panduan ini menjelaskan cara memasang landing page & server pembaruan otomatis (**LegaxyyFPS Update Server**) di **aaPanel**.

---

## 📁 Struktur File di Server

Di aaPanel, website kamu cukup berisi file-file yang ada di dalam folder `web-server`:

```text
/www/wwwroot/domainanda.com/
├── index.html        (Halaman web download modern)
├── version.json      (File manifest info versi untuk aplikasi desktop)
└── downloads/        (Folder tempat menyimpan file installer .exe)
    └── LegaxyyFPS_Setup_v1.1.exe
```

---

## 🛠️ Langkah 1: Buat Website di aaPanel

1. Buka dashboard **aaPanel** kamu di browser.
2. Masuk ke menu **Website** > klik tombol **Add site**.
3. Isi kolom:
   - **Domain**: Masukkan domain atau subdomain kamu (misal: `fps.domainanda.com` atau `update.domainanda.com`).
   - **PHP Version**: Pilih `pure static` (HTML biasa) atau versi PHP berapa saja (karena ini hanya file statis HTML/JSON).
4. Klik **Submit**.

---

## 📤 Langkah 2: Upload File ke aaPanel

1. Di menu **Website**, klik nama direktori website kamu (atau buka menu **Files** > `/www/wwwroot/domainanda.com`).
2. Hapus file bawaan aaPanel seperti `index.html` dan `404.html` default.
3. Upload isi folder `web-server` dari project ini:
   - `index.html`
   - `version.json`
   - Folder `downloads`
4. Buat file installer kamu (misal `LegaxyyFPS_Setup_v1.1.exe`) lalu upload ke dalam folder `downloads/`.

---

## ⚙️ Langkah 3: Konfigurasi `version.json`

Buka file `version.json` di file manager aaPanel, lalu sesuaikan URL-nya dengan domain kamu:

```json
{
  "version": "1.1.0",
  "downloadUrl": "https://fps.domainanda.com/downloads/LegaxyyFPS_Setup_v1.1.exe",
  "changelog": "- Fitur Auto Update langsung di dalam aplikasi\n- Peningkatan kestabilan pembacaan sensor\n- Perbaikan performa overlay",
  "mandatory": false
}
```

> **Catatan:** Pastikan URL `downloadUrl` bisa diakses dan didownload lewat browser.

---

## 🔗 Langkah 4: Hubungkan ke Aplikasi Desktop

Buka file [`appsettings.json`](file:///c:/Users/Aziz/Documents/Legaxyy-FPS/appsettings.json) di project aplikasi C# kamu:

```json
{
  "UpdateUrl": "https://fps.domainanda.com/version.json",
  "WebSocketPort": 8765,
  "HttpPort": 8766,
  ...
}
```

Ganti `https://update.domainkamu.com/version.json` dengan domain kamu di aaPanel (misal: `https://fps.domainanda.com/version.json`).

---

## 🔄 Cara Rilis Update Baru di Masa Mendatang (Hanya 2 Menit!)

Setiap kali kamu ada update fitur baru atau perbaikan bug:

1. **Ubah Versi di Aplikasi:**
   - Di `OverlayDataBridge.csproj`, ubah `<Version>1.2.0</Version>`.
   - Di `installer.iss`, ubah `AppVersion=1.2` dan `OutputBaseFilename=LegaxyyFPS_Setup_v1.2`.
2. **Compile Installer:**
   - Build aplikasi & compile installer via Inno Setup menjadi `LegaxyyFPS_Setup_v1.2.exe`.
3. **Upload ke aaPanel:**
   - Upload file `LegaxyyFPS_Setup_v1.2.exe` ke folder `/www/wwwroot/domainanda.com/downloads/`.
4. **Edit `version.json` di aaPanel:**
   - Ubah `"version": "1.2.0"`
   - Ubah `"downloadUrl": "https://fps.domainanda.com/downloads/LegaxyyFPS_Setup_v1.2.exe"`
   - Tulis catatan perubahan di `"changelog"`.

**SELESAI!** Begitu `version.json` disimpan:
- Semua user yang membuka aplikasi LegaxyyFPS akan otomatis mendapatkan pop-up update.
- User tinggal klik **"Update Sekarang"**, aplikasi langsung download, install, dan restart sendiri tanpa perlu ke browser sama sekali!
