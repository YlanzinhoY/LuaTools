using System.Text.Json;
using System.Text.Json.Serialization;

namespace LuaToolsGui.Models;

/// <summary>A game and its download choices from the bundled Kazumi catalog.</summary>
public sealed class KazumiGame
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("HeaderImage")] public JsonElement HeaderImage { get; set; }
    [JsonPropertyName("ShortDescription")] public string? ShortDescription { get; set; }
    [JsonPropertyName("downloads")] public List<KazumiDownload> Downloads { get; set; } = [];

    [JsonIgnore]
    public string? HeaderImageUrl
    {
        get
        {
            if (HeaderImage.ValueKind == JsonValueKind.String) return HeaderImage.GetString();
            if (HeaderImage.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in HeaderImage.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) return item.GetString();
            return null;
        }
    }
}

public sealed class KazumiDownload
{
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("isTorrent")] public bool IsTorrent { get; set; }
}

public readonly record struct TorrentDownloadProgress(
    double Percent,
    long DownloadRate,
    int Peers,
    string State,
    int DhtNodes);
