using System.Text.Json.Serialization;

namespace LuaToolsGui.Models;

/// <summary>
/// One independently-maintained family of games without usable Steam Cloud. A module can start with
/// only <see cref="AppIds"/> and gain per-game save metadata later without changing the registry.
/// </summary>
public sealed class GamesWithoutSteamCloudModule
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("launcher")] public string? Launcher { get; set; }
    [JsonPropertyName("appIds")] public List<long> AppIds { get; set; } = [];
    [JsonPropertyName("games")] public List<GameSaveDefinition> Games { get; set; } = [];
}

/// <summary>Save metadata for one supported Steam AppID.</summary>
public sealed class GameSaveDefinition
{
    [JsonPropertyName("appid")] public long AppId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("executables")] public List<string> Executables { get; set; } = [];
    [JsonPropertyName("saveLocations")] public List<GameSaveLocation> SaveLocations { get; set; } = [];
    [JsonPropertyName("saveVariants")] public List<GameSaveVariant> SaveVariants { get; set; } = [];

    [JsonIgnore]
    public bool HasConfiguredSaveLocations =>
        SaveLocations.Count > 0 || SaveVariants.Any(variant => variant.SaveLocations.Count > 0);

    [JsonIgnore]
    public IEnumerable<GameSaveLocation> AllSaveLocations =>
        SaveLocations.Concat(SaveVariants.SelectMany(variant => variant.SaveLocations));

    public GameSaveDefinition ForVariant(GameSaveVariant variant) => new()
    {
        AppId = AppId,
        Name = Name,
        Executables = Executables,
        SaveLocations = variant.SaveLocations,
    };
}

/// <summary>A selectable release/mod whose saves live in different locations.</summary>
public sealed class GameSaveVariant
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("saveLocations")] public List<GameSaveLocation> SaveLocations { get; set; } = [];
}

/// <summary>
/// Portable Windows save location. <see cref="Base"/> is resolved on the current PC and
/// <see cref="RelativePath"/> may contain a '*' directory segment for launcher account IDs.
/// </summary>
public sealed class GameSaveLocation
{
    [JsonPropertyName("platform")] public string Platform { get; set; } = "windows";
    [JsonPropertyName("base")] public string Base { get; set; } = "UserProfile";
    [JsonPropertyName("relativePath")] public string RelativePath { get; set; } = "";
    [JsonPropertyName("includePatterns")] public List<string> IncludePatterns { get; set; } = [];
    [JsonPropertyName("recursive")] public bool Recursive { get; set; } = true;
}

public sealed record ResolvedGameSaveLocation(
    GameSaveLocation Definition,
    string RootPath,
    IReadOnlyList<string> Files);

/// <summary>A registry hit, including optional save metadata for the AppID.</summary>
public sealed record GamesWithoutSteamCloudMatch(
    GamesWithoutSteamCloudModule Module,
    GameSaveDefinition? Game);
