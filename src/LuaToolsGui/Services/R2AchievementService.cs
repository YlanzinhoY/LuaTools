using System.Globalization;
using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Reads achievements produced by Ubisoft Connect's legacy R2 interface. This is intentionally a
/// local viewer: it reflects the emulator save and never writes achievement state to Steam.
/// </summary>
public sealed class R2AchievementService(SteamLibraryService steamLibrary)
{
    public const long BlackFlagSteamAppId = 3751950;
    public const int BlackFlagProductId = 66088;

    public static bool Supports(long steamAppId) => steamAppId == BlackFlagSteamAppId;

    public R2AchievementCatalog Load(long steamAppId)
    {
        if (!Supports(steamAppId))
            throw new NotSupportedException($"Steam App ID {steamAppId} has no R2 achievement binding.");

        string schemaPath = ResolveSchemaPath(steamAppId)
            ?? throw new FileNotFoundException("The bundled R2 achievement catalog could not be found.");
        string statePath = GetStatePath(BlackFlagProductId);

        string schemaJson = ReadShared(schemaPath);
        bool stateExists = File.Exists(statePath);
        string? stateJson = stateExists ? ReadShared(statePath) : null;

        return Parse(
            steamAppId,
            BlackFlagProductId,
            statePath,
            stateExists,
            schemaJson,
            stateJson);
    }

    private string? ResolveSchemaPath(long steamAppId)
    {
        string? installDir = steamLibrary.GetInstallDir(steamAppId);
        if (installDir is not null)
        {
            string installed = Path.Combine(installDir, "achievements_schema.json");
            if (File.Exists(installed)) return installed;
        }

        string bundled = Path.Combine(AppContext.BaseDirectory, "Achievements", $"{steamAppId}.json");
        return File.Exists(bundled) ? bundled : null;
    }

    internal static string GetStatePath(int productId)
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roaming, "Goldberg UplayEmu Saves", productId.ToString(CultureInfo.InvariantCulture),
            "achievements.json");
    }

    /// <summary>Parse independently of the filesystem so malformed/partial state cases are testable.</summary>
    internal static R2AchievementCatalog Parse(
        long steamAppId,
        int productId,
        string statePath,
        bool stateFileExists,
        string schemaJson,
        string? stateJson)
    {
        using JsonDocument schema = JsonDocument.Parse(schemaJson);
        if (schema.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The R2 achievement catalog must be a JSON object.");

        JsonDocument? state = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(stateJson)) state = JsonDocument.Parse(stateJson);
            JsonElement stateRoot = state?.RootElement ?? default;

            var parsed = new List<R2Achievement>();
            foreach (JsonProperty property in schema.RootElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                    continue;

                JsonElement definition = property.Value;
                string displayName = ReadString(definition, "displayName") ?? $"Achievement {id}";
                string description = ReadString(definition, "description") ?? "";

                JsonElement saved = default;
                bool hasSaved = stateRoot.ValueKind == JsonValueKind.Object &&
                    stateRoot.TryGetProperty(property.Name, out saved) && saved.ValueKind == JsonValueKind.Object;
                bool earned = hasSaved
                    ? ReadTruthy(saved, "earned")
                    : ReadTruthy(definition, "earned");
                long? earnedTime = hasSaved ? ReadInt64(saved, "earned_time") : null;

                parsed.Add(new R2Achievement(
                    id,
                    $"ACObsidian_Ach_{id}",
                    displayName,
                    description,
                    earned,
                    earnedTime));
            }

            parsed.Sort((a, b) => a.Id.CompareTo(b.Id));
            return new R2AchievementCatalog(
                steamAppId,
                productId,
                statePath,
                stateFileExists,
                parsed);
        }
        finally
        {
            state?.Dispose();
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private static bool ReadTruthy(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt64(out long numeric) && numeric != 0,
            JsonValueKind.String => value.GetString() is { } text &&
                (text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                 (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long numericText) && numericText != 0)),
            _ => false,
        };
    }
}
