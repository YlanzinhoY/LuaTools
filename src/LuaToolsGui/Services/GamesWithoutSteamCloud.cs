using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Data-driven registry for games that need Cloud Redirect Fix. Each franchise/launcher owns one JSON
/// module under Data/GamesWithoutSteamCloud; adding AppIDs does not require changing C# or the UI.
/// </summary>
public sealed class GamesWithoutSteamCloud
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<long, GamesWithoutSteamCloudMatch> _byAppId;

    public IReadOnlyList<GamesWithoutSteamCloudModule> Modules { get; }

    public GamesWithoutSteamCloud()
        : this(Path.Combine(AppContext.BaseDirectory, "Data", "GamesWithoutSteamCloud"))
    {
    }

    /// <summary>Public for deterministic tests and community-catalog validation tools.</summary>
    public GamesWithoutSteamCloud(string dataDirectory)
    {
        (Modules, _byAppId) = Load(dataDirectory);
    }

    public bool Contains(long appId) => _byAppId.ContainsKey(appId);

    public GamesWithoutSteamCloudMatch? Find(long appId) =>
        _byAppId.GetValueOrDefault(appId);

    private static (IReadOnlyList<GamesWithoutSteamCloudModule>, Dictionary<long, GamesWithoutSteamCloudMatch>)
        Load(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
            return ([], []);

        var modules = new List<GamesWithoutSteamCloudModule>();
        var byAppId = new Dictionary<long, GamesWithoutSteamCloudMatch>();
        var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in Directory.EnumerateFiles(dataDirectory, "*.json").Order())
        {
            GamesWithoutSteamCloudModule module;
            try
            {
                module = JsonSerializer.Deserialize<GamesWithoutSteamCloudModule>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("The module is empty.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new InvalidDataException($"Invalid GamesWithoutSteamCloud module '{Path.GetFileName(path)}'.", ex);
            }

            ValidateModule(module, path, moduleIds);
            modules.Add(module);

            var definitions = module.Games.ToDictionary(g => g.AppId);
            foreach (long appId in module.AppIds)
            {
                if (byAppId.ContainsKey(appId))
                    throw new InvalidDataException($"AppID {appId} is declared by more than one module.");

                definitions.TryGetValue(appId, out var game);
                byAppId.Add(appId, new GamesWithoutSteamCloudMatch(module, game));
            }
        }

        return (modules, byAppId);
    }

    private static void ValidateModule(
        GamesWithoutSteamCloudModule module,
        string path,
        HashSet<string> moduleIds)
    {
        string file = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(module.Id) || string.IsNullOrWhiteSpace(module.Name))
            throw new InvalidDataException($"Module '{file}' needs non-empty id and name fields.");
        if (!moduleIds.Add(module.Id))
            throw new InvalidDataException($"Module id '{module.Id}' is duplicated.");
        if (module.AppIds.Count != module.AppIds.Distinct().Count())
            throw new InvalidDataException($"Module '{module.Id}' contains duplicate AppIDs.");
        if (module.AppIds.Any(id => id <= 0))
            throw new InvalidDataException($"Module '{module.Id}' contains an invalid AppID.");
        if (module.Games.Select(g => g.AppId).Distinct().Count() != module.Games.Count)
            throw new InvalidDataException($"Module '{module.Id}' contains duplicate game definitions.");

        var declared = module.AppIds.ToHashSet();
        foreach (var game in module.Games)
        {
            if (!declared.Contains(game.AppId))
                throw new InvalidDataException(
                    $"Game definition {game.AppId} in module '{module.Id}' is missing from appIds.");
            if (string.IsNullOrWhiteSpace(game.Name))
                throw new InvalidDataException($"Game definition {game.AppId} needs a name.");
            if (game.SaveVariants.Select(variant => variant.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != game.SaveVariants.Count)
                throw new InvalidDataException($"Game definition {game.AppId} contains duplicate save variant IDs.");
            foreach (var variant in game.SaveVariants)
            {
                if (string.IsNullOrWhiteSpace(variant.Id) || string.IsNullOrWhiteSpace(variant.Name))
                    throw new InvalidDataException($"Game definition {game.AppId} contains an unnamed save variant.");
                if (variant.SaveLocations.Count == 0)
                    throw new InvalidDataException(
                        $"Save variant '{variant.Id}' for game {game.AppId} needs at least one save location.");
            }
        }
    }
}
