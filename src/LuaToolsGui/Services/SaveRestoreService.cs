using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Validates and restores raw save files downloaded by CloudRedirect, with rollback on failure.</summary>
public sealed class SaveRestoreService(GameSaveResolver resolver)
{
    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SaveRestoreResult> RestoreAsync(
        GameSaveDefinition game,
        string downloadedDirectory,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(downloadedDirectory))
            return SaveRestoreResult.Fail(Resources.Strings.CloudFix_InvalidSave);

        string rollback = Path.Combine(Path.GetTempPath(), "LuaToolsGui", "save-rollback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rollback);
        var touchedTargets = new List<(GameSaveLocation Definition, string Target)>();

        try
        {
            string manifestPath = SafeDestination(
                downloadedDirectory, SaveBackupService.GetManifestFileName(game));
            // Backups made before variant cloud folders used one manifest at the game root.
            // Keep them restorable; their save-location metadata is still validated below.
            if (!File.Exists(manifestPath) && game.SelectedSaveVariant is not null)
                manifestPath = SafeDestination(downloadedDirectory, SaveBackupService.ManifestFileName);
            if (!File.Exists(manifestPath)) throw InvalidSave();
            SaveFilesManifest metadata;
            await using (var stream = File.OpenRead(manifestPath))
            {
                metadata = await JsonSerializer.DeserializeAsync<SaveFilesManifest>(stream, JsonOptions, ct)
                    ?? throw InvalidSave();
            }
            if (metadata.FormatVersion is not (2 or 3) || metadata.AppId != game.AppId ||
                !metadata.Game.Equals(game.Name, StringComparison.Ordinal))
                throw InvalidSave();
            if (metadata.FormatVersion == 3 &&
                (game.SelectedSaveVariant is not { } selectedVariant ||
                 !selectedVariant.Id.Equals(metadata.SaveVariantId, StringComparison.OrdinalIgnoreCase) ||
                 !selectedVariant.CloudFolder.Equals(metadata.CloudFolder, StringComparison.OrdinalIgnoreCase)))
                throw InvalidSave();

            int restoredFiles = 0;
            for (int index = 0; index < metadata.SaveLocations.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var saved = metadata.SaveLocations[index];
                var definition = game.AllSaveLocations.FirstOrDefault(location =>
                    location.Base.Equals(saved.Base, StringComparison.OrdinalIgnoreCase) &&
                    NormalizeRelative(location.RelativePath).Equals(
                        NormalizeRelative(saved.RelativePath), StringComparison.OrdinalIgnoreCase));
                if (definition is null)
                    throw InvalidSave();

                string sourceRoot = SafeDestination(downloadedDirectory, saved.Directory);
                if (!Directory.Exists(sourceRoot))
                    throw InvalidSave();
                var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToList();
                if (sourceFiles.Count == 0)
                    throw InvalidSave();

                var targets = resolver.ResolveTargets(game, definition);
                if (targets.Count != 1)
                    throw InvalidSave();
                string target = targets[0];
                touchedTargets.Add((definition, target));

                BackupCurrentFiles(definition, target, Path.Combine(rollback, index.ToString()));
                DeleteCurrentFiles(definition, target);
                Directory.CreateDirectory(target);

                foreach (string source in sourceFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    string destination = SafeDestination(target, Path.GetRelativePath(sourceRoot, source));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: false);
                    restoredFiles++;
                }
            }

            if (restoredFiles == 0) throw InvalidSave();
            return SaveRestoreResult.Ok(restoredFiles);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RollBack(touchedTargets, rollback);
            return SaveRestoreResult.Fail(ex.Message);
        }
        finally
        {
            try { Directory.Delete(downloadedDirectory, recursive: true); } catch { }
            try { Directory.Delete(rollback, recursive: true); } catch { }
        }
    }

    private static void BackupCurrentFiles(GameSaveLocation definition, string target, string backupRoot)
    {
        if (!Directory.Exists(target)) return;
        foreach (string file in MatchingFiles(definition, target))
        {
            string destination = SafeDestination(backupRoot, Path.GetRelativePath(target, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void DeleteCurrentFiles(GameSaveLocation definition, string target)
    {
        if (!Directory.Exists(target)) return;
        foreach (string file in MatchingFiles(definition, target)) File.Delete(file);
    }

    private static IEnumerable<string> MatchingFiles(GameSaveLocation definition, string target)
    {
        var patterns = definition.IncludePatterns.Count > 0 ? definition.IncludePatterns : ["*"];
        var option = definition.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return patterns.SelectMany(pattern => Directory.EnumerateFiles(target, pattern, option))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void RollBack(
        IReadOnlyList<(GameSaveLocation Definition, string Target)> targets,
        string rollbackRoot)
    {
        for (int index = 0; index < targets.Count; index++)
        {
            try
            {
                var (definition, target) = targets[index];
                DeleteCurrentFiles(definition, target);
                string sourceRoot = Path.Combine(rollbackRoot, index.ToString());
                if (!Directory.Exists(sourceRoot)) continue;
                foreach (string source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
                {
                    string destination = SafeDestination(target, Path.GetRelativePath(sourceRoot, source));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination, overwrite: true);
                }
            }
            catch { }
        }
    }

    private static string SafeDestination(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, PathComparison))
            throw InvalidSave();
        return destination;
    }

    private static InvalidDataException InvalidSave() =>
        new(Resources.Strings.CloudFix_InvalidSave);

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').Trim('/');

    private sealed class SaveFilesManifest
    {
        public int FormatVersion { get; set; }
        public long AppId { get; set; }
        public string Game { get; set; } = "";
        public string? SaveVariantId { get; set; }
        public string? CloudFolder { get; set; }
        public List<SaveLocationMetadata> SaveLocations { get; set; } = [];
    }

    private sealed class SaveLocationMetadata
    {
        public string Base { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string Directory { get; set; } = "";
    }
}

public sealed record SaveRestoreResult(bool Success, int RestoredFiles, string? Error)
{
    public static SaveRestoreResult Ok(int restoredFiles) => new(true, restoredFiles, null);
    public static SaveRestoreResult Fail(string error) => new(false, 0, error);
}
