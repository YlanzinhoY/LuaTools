using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Runs short-lived, read-only queries against the independently built Zig bridge.</summary>
public sealed class AchievementBridgeClient(SteamService steam)
{
    public async Task<AchievementCatalog> LoadSteamCatalogAsync(long appId, CancellationToken cancellationToken = default)
    {
        string executable = AchievementBridgeService.FindExecutable()
            ?? throw new FileNotFoundException("Achievement Bridge is not installed.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            },
        };
        process.StartInfo.ArgumentList.Add("catalog");
        process.StartInfo.ArgumentList.Add("--appid");
        process.StartInfo.ArgumentList.Add(appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (steam.EffectivePath is { } steamRoot)
        {
            process.StartInfo.ArgumentList.Add("--steam-root");
            process.StartInfo.ArgumentList.Add(steamRoot);
        }

        if (!process.Start()) throw new InvalidOperationException("Achievement Bridge did not start.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? $"Achievement Bridge exited with code {process.ExitCode}."
                : stderr.Trim());
        return ParseCatalog(stdout, GetLocalStorePath());
    }

    internal static AchievementCatalog ParseCatalog(string json, string? localStorePath = null)
    {
        BridgeCatalogDto dto = JsonSerializer.Deserialize<BridgeCatalogDto>(json)
            ?? throw new InvalidDataException("Achievement Bridge returned an empty catalog.");
        if (dto.SchemaVersion != 1 || dto.Kind != "achievement_catalog")
            throw new InvalidDataException("Achievement Bridge returned an unsupported catalog contract.");

        List<Achievement> items = dto.Achievements.Select(item => new Achievement(
            item.ApiName,
            string.IsNullOrWhiteSpace(item.Name) ? item.ApiName : item.Name,
            item.Description ?? "",
            ResolveIconUrl(dto.AppId, item.Icon),
            ResolveIconUrl(dto.AppId, item.IconGray),
            item.Unlocked,
            item.UnlockedAt,
            item.StateSource ?? "steam_client",
            item.Hidden,
            item.GlobalPercent)).ToList();
        return new AchievementCatalog(
            dto.AppId,
            dto.MetadataSource ?? "steam_client",
            localStorePath,
            localStorePath is not null && File.Exists(localStorePath),
            items);
    }

    internal static string? ResolveIconUrl(long appId, string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        if (Uri.TryCreate(icon, UriKind.Absolute, out Uri? absolute)) return absolute.ToString();
        return $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{Uri.EscapeDataString(icon)}";
    }

    private static string GetLocalStorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AchievementBridge",
        "local-achievements.json");

    private sealed class BridgeCatalogDto
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("app_id")] public long AppId { get; init; }
        [JsonPropertyName("metadata_source")] public string? MetadataSource { get; init; }
        [JsonPropertyName("achievements")] public List<BridgeAchievementDto> Achievements { get; init; } = [];
    }

    private sealed class BridgeAchievementDto
    {
        [JsonPropertyName("api_name")] public string ApiName { get; init; } = "";
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("icon")] public string? Icon { get; init; }
        [JsonPropertyName("icon_gray")] public string? IconGray { get; init; }
        [JsonPropertyName("unlocked")] public bool Unlocked { get; init; }
        [JsonPropertyName("unlocked_at")] public long? UnlockedAt { get; init; }
        [JsonPropertyName("state_source")] public string? StateSource { get; init; }
        [JsonPropertyName("hidden")] public bool Hidden { get; init; }
        [JsonPropertyName("global_percent")] public double? GlobalPercent { get; init; }
    }
}
