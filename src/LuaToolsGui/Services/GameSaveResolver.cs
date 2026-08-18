using System.IO;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Resolves portable save definitions into paths on the current Windows installation.</summary>
public sealed class GameSaveResolver
{
    private readonly Func<long, string?> _steamInstallResolver;

    public GameSaveResolver() : this(_ => null) { }

    public GameSaveResolver(SteamLibraryService steamLibrary) : this(steamLibrary.GetInstallDir) { }

    internal GameSaveResolver(Func<long, string?> steamInstallResolver) =>
        _steamInstallResolver = steamInstallResolver;

    public IReadOnlyList<string> ResolveTargets(GameSaveDefinition game, GameSaveLocation location) =>
        Resolve(game.AppId, location);

    public IReadOnlyList<string> ResolveExisting(GameSaveDefinition game) =>
        ResolveExistingLocations(game).Select(r => r.RootPath).ToList();

    public IReadOnlyList<ResolvedGameSaveLocation> ResolveExistingLocations(GameSaveDefinition game) =>
        game.SaveLocations
            .Where(s => s.Platform.Equals("windows", StringComparison.OrdinalIgnoreCase))
            .SelectMany(location => Resolve(game.AppId, location)
                .Where(Directory.Exists)
                .Select(root => new ResolvedGameSaveLocation(location, root, FindFiles(location, root))))
            .Where(r => r.Files.Count > 0)
            .GroupBy(r => r.RootPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private static IReadOnlyList<string> FindFiles(GameSaveLocation location, string root)
    {
        var patterns = location.IncludePatterns.Count > 0 ? location.IncludePatterns : ["*"];
        var option = location.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return patterns
            .Where(p => !string.IsNullOrWhiteSpace(p) && !p.Contains('/') && !p.Contains('\\'))
            .SelectMany(pattern => Directory.EnumerateFiles(root, pattern, option))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal IReadOnlyList<string> Resolve(long appId, GameSaveLocation location)
    {
        string? root = ResolveBase(appId, location.Base);
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(location.RelativePath))
            return [];

        string relative = Environment.ExpandEnvironmentVariables(location.RelativePath)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        var candidates = new List<string> { Path.GetFullPath(Path.Combine(root, relative)) };
        if (!relative.Split(Path.DirectorySeparatorChar).Contains("*"))
            return candidates;

        candidates = [Path.GetFullPath(root)];
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "*")
            {
                candidates = candidates
                    .Where(Directory.Exists)
                    .SelectMany(p => Directory.EnumerateDirectories(p))
                    .ToList();
            }
            else
            {
                candidates = candidates.Select(p => Path.Combine(p, segment)).ToList();
            }
        }

        return candidates;
    }

    private string? ResolveBase(long appId, string value) => value.Trim().ToLowerInvariant() switch
    {
        "userprofile" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "localappdata" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "roamingappdata" or "appdata" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "programfiles" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "programfilesx86" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "commonappdata" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "steaminstall" or "gameinstall" => _steamInstallResolver(appId),
        _ => null,
    };
}
