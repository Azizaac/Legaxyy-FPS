using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using OverlayDataBridge.Models;

namespace OverlayDataBridge.Services;

public sealed class UpdateService
{
    private readonly IConfiguration _config;
    private readonly AppLogger _logger;
    private readonly string _currentVersion;

    public UpdateService(IConfiguration config, AppLogger logger)
    {
        _config = config;
        _logger = logger;
        
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        _currentVersion = ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.3.2";
    }

    public string CurrentVersion => _currentVersion;

    /// <summary>
    /// Checks for updates against the update server.
    /// </summary>
    /// <param name="isManualCheck">True if initiated by user click; false if periodic/startup.</param>
    public async Task CheckForUpdatesAsync(bool isManualCheck = false)
    {
        string? updateUrl = _config["UpdateUrl"];
        if (string.IsNullOrWhiteSpace(updateUrl))
        {
            _logger.Warn("UpdateService: UpdateUrl is not configured in appsettings.json.");
            if (isManualCheck)
            {
                MessageBox.Show(
                    "URL pembaruan belum dikonfigurasi di appsettings.json.",
                    "Pembaruan Aplikasi",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return;
        }

        try
        {
            _logger.Info($"UpdateService: Checking for updates at {updateUrl} (Current: v{_currentVersion})...");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            
            // Add cache-busting timestamp
            string separator = updateUrl.Contains('?') ? "&" : "?";
            string requestUrl = $"{updateUrl}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            string json = await client.GetStringAsync(requestUrl);
            var manifest = JsonConvert.DeserializeObject<UpdateManifest>(json);

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                _logger.Warn("UpdateService: Invalid update manifest received.");
                if (isManualCheck)
                {
                    MessageBox.Show(
                        "Respon dari server pembaruan tidak valid.",
                        "Pembaruan Aplikasi",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return;
            }

            _logger.Info($"UpdateService: Server version is v{manifest.Version}.");

            if (IsNewerVersion(manifest.Version, _currentVersion))
            {
                _logger.Info($"UpdateService: New version available (v{manifest.Version}). Showing update form.");
                
                // Show update form on UI thread
                if (Application.OpenForms.Count > 0 && Application.OpenForms[0]!.InvokeRequired)
                {
                    Application.OpenForms[0]!.Invoke(new Action(() =>
                    {
                        using var form = new UpdateForm(manifest, _currentVersion);
                        form.ShowDialog();
                    }));
                }
                else
                {
                    using var form = new UpdateForm(manifest, _currentVersion);
                    form.ShowDialog();
                }
            }
            else
            {
                _logger.Info("UpdateService: Application is up to date.");
                if (isManualCheck)
                {
                    MessageBox.Show(
                        $"LegaxyyFPS Anda sudah menggunakan versi terbaru (v{_currentVersion}).",
                        "Pembaruan Aplikasi",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"UpdateService: Failed to check for updates: {ex.Message}");
            if (isManualCheck)
            {
                MessageBox.Show(
                    $"Gagal terhubung ke server pembaruan:\n{ex.Message}\n\nPastikan koneksi internet atau server aktif.",
                    "Pembaruan Aplikasi",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

    /// <summary>
    /// Compares two semver strings (e.g. "1.2.0" vs "1.1.0" or "v1.2").
    /// </summary>
    public static bool IsNewerVersion(string serverVerStr, string currentVerStr)
    {
        string s = serverVerStr.Trim().TrimStart('v', 'V');
        string c = currentVerStr.Trim().TrimStart('v', 'V');

        // Normalize to at least 3 parts (e.g. "1.1" -> "1.1.0")
        if (s.Split('.').Length == 1) s += ".0.0";
        else if (s.Split('.').Length == 2) s += ".0";

        if (c.Split('.').Length == 1) c += ".0.0";
        else if (c.Split('.').Length == 2) c += ".0";

        if (Version.TryParse(s, out var sVer) && Version.TryParse(c, out var cVer))
        {
            return sVer > cVer;
        }

        return false;
    }
}
