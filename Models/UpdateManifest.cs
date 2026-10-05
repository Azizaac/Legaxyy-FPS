using Newtonsoft.Json;

namespace OverlayDataBridge.Models;

public sealed class UpdateManifest
{
    [JsonProperty("version")]
    public string Version { get; set; } = string.Empty;

    [JsonProperty("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonProperty("changelog")]
    public string Changelog { get; set; } = string.Empty;

    [JsonProperty("mandatory")]
    public bool Mandatory { get; set; } = false;

    /// <summary>
    /// SHA-256 hash (hex) of the installer file. Used to verify integrity
    /// before launching it. Strongly recommended; required for a verified update.
    /// </summary>
    [JsonProperty("sha256")]
    public string? Sha256 { get; set; }
}
