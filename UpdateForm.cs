using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using OverlayDataBridge.Models;

namespace OverlayDataBridge;

public sealed class UpdateForm : Form
{
    private readonly UpdateManifest _manifest;
    private readonly string _currentVersion;

    private readonly Label _lblTitle;
    private readonly Label _lblSub;
    private readonly Label _lblChangelog;
    private readonly TextBox _txtChangelog;
    private readonly ProgressBar _progressBar;
    private readonly Label _lblStatus;
    private readonly Button _btnUpdate;
    private readonly Button _btnCancel;

    private bool _isDownloading = false;

    public UpdateForm(UpdateManifest manifest, string currentVersion)
    {
        _manifest = manifest;
        _currentVersion = currentVersion;

        // Window configuration
        Text = "Pembaruan Tersedia - LegaxyyFPS";
        Width = 520;
        Height = 440;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(24, 24, 27); // Zinc-900 dark
        ForeColor = Color.FromArgb(244, 244, 245);
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        // Header Title
        _lblTitle = new Label
        {
            Text = "Pembaruan Baru Tersedia!",
            Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(96, 165, 250), // Blue-400
            Location = new Point(24, 20),
            AutoSize = true
        };

        // Header Subtitle
        _lblSub = new Label
        {
            Text = $"Versi Baru: v{manifest.Version}  •  Versi Anda: v{currentVersion}",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(161, 161, 170), // Zinc-400
            Location = new Point(26, 52),
            AutoSize = true
        };

        // Changelog Label
        _lblChangelog = new Label
        {
            Text = "Catatan Perubahan (Changelog):",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(212, 212, 216),
            Location = new Point(26, 88),
            AutoSize = true
        };

        // Changelog Box
        _txtChangelog = new TextBox
        {
            Location = new Point(26, 112),
            Width = 450,
            Height = 150,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(39, 39, 42), // Zinc-800
            ForeColor = Color.FromArgb(244, 244, 245),
            BorderStyle = BorderStyle.FixedSingle,
            Text = string.IsNullOrWhiteSpace(manifest.Changelog) ? "Tidak ada catatan rilis." : manifest.Changelog
        };

        // Progress Bar
        _progressBar = new ProgressBar
        {
            Location = new Point(26, 276),
            Width = 450,
            Height = 16,
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Visible = false
        };

        // Status Label
        _lblStatus = new Label
        {
            Text = "Klik tombol di bawah untuk mulai memperbarui.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(161, 161, 170),
            Location = new Point(26, 300),
            Width = 450,
            Height = 35
        };

        // Button: Update Now
        _btnUpdate = new Button
        {
            Text = "Update Sekarang",
            Location = new Point(216, 345),
            Width = 140,
            Height = 36,
            BackColor = Color.FromArgb(37, 99, 235), // Blue-600
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnUpdate.FlatAppearance.BorderSize = 0;
        _btnUpdate.Click += async (_, _) => await StartDownloadAndUpdateAsync();

        // Button: Cancel / Later
        _btnCancel = new Button
        {
            Text = manifest.Mandatory ? "Tutup" : "Nanti Saja",
            Location = new Point(366, 345),
            Width = 110,
            Height = 36,
            BackColor = Color.FromArgb(63, 63, 70), // Zinc-700
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnCancel.FlatAppearance.BorderSize = 0;
        _btnCancel.Click += (_, _) => Close();

        if (manifest.Mandatory)
        {
            _btnCancel.Enabled = false;
        }

        Controls.Add(_lblTitle);
        Controls.Add(_lblSub);
        Controls.Add(_lblChangelog);
        Controls.Add(_txtChangelog);
        Controls.Add(_progressBar);
        Controls.Add(_lblStatus);
        Controls.Add(_btnUpdate);
        Controls.Add(_btnCancel);
    }

    private async Task StartDownloadAndUpdateAsync()
    {
        if (_isDownloading) return;
        _isDownloading = true;

        _btnUpdate.Enabled = false;
        _btnCancel.Enabled = false;
        _progressBar.Visible = true;
        _progressBar.Value = 0;
        _lblStatus.Text = "Menghubungkan ke server...";

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            using var response = await client.GetAsync(_manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var tempDir = Path.Combine(Path.GetTempPath(), "LegaxyyFPS_Update");
            Directory.CreateDirectory(tempDir);
            var setupFilePath = Path.Combine(tempDir, $"LegaxyyFPS_Setup_v{_manifest.Version}.exe");

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var fs = new FileStream(setupFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 16384, true))
            {
                var buffer = new byte[16384];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        int percent = (int)((totalRead * 100) / totalBytes);
                        _progressBar.Value = Math.Clamp(percent, 0, 100);
                        _lblStatus.Text = $"Mengunduh: {totalRead / (1024.0 * 1024):F1} MB / {totalBytes / (1024.0 * 1024):F1} MB ({percent}%)";
                    }
                    else
                    {
                        _lblStatus.Text = $"Mengunduh: {totalRead / (1024.0 * 1024):F1} MB";
                    }
                }
            }

            // ── Verify integrity before running the installer (as Administrator) ──
            _lblStatus.ForeColor = Color.FromArgb(212, 212, 216); // Zinc-300
            _lblStatus.Text = "Memverifikasi keamanan file (SHA-256)...";

            if (!string.IsNullOrWhiteSpace(_manifest.Sha256))
            {
                string actual = await ComputeSha256Async(setupFilePath);
                if (!string.Equals(actual, _manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(setupFilePath); } catch { }
                    throw new Exception(
                        "Verifikasi keamanan GAGAL: checksum SHA-256 tidak cocok. " +
                        "File installer kemungkinan korup atau dimodifikasi. Update dibatalkan.");
                }
            }
            else
            {
                var choice = MessageBox.Show(
                    "Server pembaruan tidak menyertakan checksum SHA-256.\n\n" +
                    "File installer TIDAK dapat diverifikasi keasliannya, namun akan dijalankan " +
                    "dengan hak Administrator.\n\nLanjutkan tetap menginstal?",
                    "Peringatan Keamanan — LegaxyyFPS",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (choice != DialogResult.Yes)
                    throw new Exception("Update dibatalkan oleh pengguna (checksum tidak tersedia).");
            }

            _lblStatus.ForeColor = Color.FromArgb(74, 222, 128); // Green-400
            _lblStatus.Text = "Download selesai! Memulai instalasi...";
            await Task.Delay(1000);

            // Launch Inno Setup installer with /SILENT flag to close app, install update, and auto-restart!
            var psi = new ProcessStartInfo
            {
                FileName = setupFilePath,
                Arguments = "/SILENT /CLOSEAPPLICATIONS",
                UseShellExecute = true
            };
            Process.Start(psi);

            // Exit current application smoothly
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            _progressBar.Visible = false;
            _lblStatus.ForeColor = Color.FromArgb(248, 113, 113); // Red-400
            _lblStatus.Text = $"Gagal download: {ex.Message}";
            _btnUpdate.Enabled = true;
            _btnCancel.Enabled = !_manifest.Mandatory;
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        byte[] hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
