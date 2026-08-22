using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>
/// Installs the bridge's standalone OpenSteamTool host once. Unlocks themselves
/// never restart Steam or ask for confirmation; a newly installed host is picked
/// up naturally on the next Steam launch.
/// </summary>
public sealed class AchievementBridgeSetupService(SteamService steam)
{
    internal const string ProxyFileName = "achievement-bridge-cloud.dll";
    internal const string ProxyRelativePath = "AchievementBridge/achievement-bridge-cloud.dll";

    public AchievementBridgeSetupResult EnsureInstalled()
    {
        string? steamRoot = steam.EffectivePath;
        string? source = FindProxy();
        if (steamRoot is null || source is null || !File.Exists(Path.Combine(steamRoot, "opensteamtool.toml")))
            return new(false, false);

        bool proxyChanged = false;
        string targetDirectory = Path.Combine(steamRoot, "AchievementBridge");
        string target = Path.Combine(targetDirectory, ProxyFileName);
        try
        {
            Directory.CreateDirectory(targetDirectory);
            if (!File.Exists(target) || !FilesEqual(source, target))
            {
                File.Copy(source, target, overwrite: true);
                proxyChanged = true;
            }
        }
        catch
        {
            // A loaded DLL is locked by Steam. Keep the working installed copy;
            // LuaTools will retry the update on its next launch.
            if (!File.Exists(target)) return new(false, false);
        }

        string configPath = Path.Combine(steamRoot, "opensteamtool.toml");
        bool configChanged = false;
        try
        {
            string[] current = File.ReadAllLines(configPath);
            string[] configured = ConfigureOpenSteamTool(current);
            configChanged = !current.SequenceEqual(configured, StringComparer.Ordinal);
            if (configChanged)
            {
                string backup = configPath + ".achievement-bridge.bak";
                if (!File.Exists(backup)) File.Copy(configPath, backup);
                File.WriteAllLines(configPath, configured);
            }
        }
        catch
        {
            return new(File.Exists(target), proxyChanged);
        }

        return new(true, proxyChanged || configChanged);
    }

    internal static string[] ConfigureOpenSteamTool(IEnumerable<string> source)
    {
        var lines = source.ToList();
        int header = lines.FindIndex(line => IsTableHeader(line, "cloud"));
        if (header < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[cloud]");
            lines.Add("enabled = true");
            lines.Add($"library = \"{ProxyRelativePath}\"");
            return [.. lines];
        }

        int end = lines.FindIndex(header + 1, IsAnyTableHeader);
        if (end < 0) end = lines.Count;
        Upsert(lines, header, ref end, "enabled", "true");
        Upsert(lines, header, ref end, "library", $"\"{ProxyRelativePath}\"");
        return [.. lines];
    }

    private static void Upsert(List<string> lines, int header, ref int sectionEnd, string key, string value)
    {
        for (int index = header + 1; index < sectionEnd; index++)
        {
            string trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith('#')) continue;
            if (!Regex.IsMatch(trimmed, $@"^{Regex.Escape(key)}\s*=")) continue;
            string indent = lines[index][..(lines[index].Length - trimmed.Length)];
            lines[index] = $"{indent}{key} = {value}";
            return;
        }
        lines.Insert(sectionEnd, $"{key} = {value}");
        sectionEnd++;
    }

    private static bool IsTableHeader(string line, string name)
    {
        string trimmed = line.TrimStart();
        return !trimmed.StartsWith('#') && Regex.IsMatch(trimmed, $@"^\[\s*{Regex.Escape(name)}\s*\]");
    }

    private static bool IsAnyTableHeader(string line)
    {
        string trimmed = line.TrimStart();
        return !trimmed.StartsWith('#') && Regex.IsMatch(trimmed, @"^\[[^\[].*\]");
    }

    private static string? FindProxy()
    {
        string? executable = AchievementBridgeService.FindExecutable();
        string? besideExecutable = executable is null ? null : Path.Combine(Path.GetDirectoryName(executable)!, ProxyFileName);
        string[] candidates =
        [
            besideExecutable ?? "",
            Path.Combine(AppContext.BaseDirectory, "AchievementBridge", ProxyFileName),
            Path.Combine(AppContext.BaseDirectory, ProxyFileName),
        ];
        return candidates.FirstOrDefault(path => path.Length > 0 && File.Exists(path));
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return SHA256.HashData(leftStream).SequenceEqual(SHA256.HashData(rightStream));
    }
}

public sealed record AchievementBridgeSetupResult(bool Installed, bool RestartRequired);
