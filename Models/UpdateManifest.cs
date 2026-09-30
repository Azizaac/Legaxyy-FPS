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
}
