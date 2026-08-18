using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Stages the original save files as a directory tree. CloudRedirect uploads every file separately;
/// no archive or timestamped history is created in the cloud.
/// </summary>
public sealed class SaveBackupService(GameSaveResolver resolver)
{
    internal const string ManifestFileName = "_cloudredirect-save.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string GetManifestFileName(GameSaveDefinition game) =>
        game.SelectedSaveVariant is { } variant
            ? $"_cloudredirect-save-{variant.CloudFolder}.json"
            : ManifestFileName;

    public async Task<PreparedSaveFiles?> CreateSaveFilesAsync(
        GameSaveDefinition game,
        CancellationToken ct = default)
    {
        var locations = resolver.ResolveExistingLocations(game);
        if (locations.Count == 0) return null;

        string staging = Path.Combine(Path.GetTempPath(), "LuaToolsGui", "save-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            var metadataLocations = new List<SaveLocationMetadata>();
            int fileCount = 0;
            string cloudRoot = game.SelectedSaveVariant?.CloudFolder ?? "saves";

            for (int index = 0; index < locations.Count; index++)
            {
                var location = locations[index];
                // A selected variant owns an independent data-defined folder, allowing multiple save layouts
                // to coexist for one AppID. Games without variants retain the original saves/ path.
                string directoryName = locations.Count == 1 ? cloudRoot : $"{cloudRoot}/{index}";
                metadataLocations.Add(new SaveLocationMetadata(
                    location.Definition.Base,
                    location.Definition.RelativePath,
                    directoryName));

                foreach (string file in location.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    string relative = Path.GetRelativePath(location.RootPath, file);
                    string destination = SafeDestination(staging, Path.Combine(directoryName, relative));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920, useAsync: true);
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, bufferSize: 81920, useAsync: true);
                    await input.CopyToAsync(output, ct);
                    fileCount++;
                }
            }

            var manifest = new SaveFilesManifest(
                FormatVersion: game.SelectedSaveVariant is null ? 2 : 3,
                AppId: game.AppId,
                Game: game.Name,
                SaveVariantId: game.SelectedSaveVariant?.Id,
                CloudFolder: game.SelectedSaveVariant?.CloudFolder,
                SaveLocations: metadataLocations);
            string manifestPath = Path.Combine(staging, GetManifestFileName(game));
            await using (var output = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(output, manifest, JsonOptions, ct);
            }

            return new PreparedSaveFiles(staging, fileCount);
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    private static string SafeDestination(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Save path traversal was rejected.");
        return destination;
    }

    internal sealed record SaveFilesManifest(
        int FormatVersion,
        long AppId,
        string Game,
        string? SaveVariantId,
        string? CloudFolder,
        IReadOnlyList<SaveLocationMetadata> SaveLocations);

    internal sealed record SaveLocationMetadata(string Base, string RelativePath, string Directory);
}

public sealed class PreparedSaveFiles(string directoryPath, int fileCount) : IDisposable
{
    public string DirectoryPath { get; } = directoryPath;
    public int FileCount { get; } = fileCount;

    public void Dispose()
    {
        try { Directory.Delete(DirectoryPath, recursive: true); } catch { }
    }
}
