using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LuaToolsGui.Services;

/// <summary>Hands a magnet to a working desktop client, ignoring stale Windows associations.</summary>
public sealed class ExternalTorrentClientService
{
    private static readonly Regex ExecutablePattern = new(
        "^\\s*(?:\"(?<quoted>[^\"]+\\.exe)\"|(?<bare>.+?\\.exe))(?:\\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool TryOpenMagnet(string magnetUri, out string clientName)
    {
        clientName = "";
        if (!magnetUri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return false;

        string? executable = FindWorkingExecutable();
        if (executable is null) return false;

        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = true };
            start.ArgumentList.Add(magnetUri);
            Process.Start(start);
            clientName = GetClientName(executable);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var match = ExecutablePattern.Match(Environment.ExpandEnvironmentVariables(command));
        string? value = match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value
            : match.Groups["bare"].Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FindWorkingExecutable()
    {
        foreach (string? command in ReadMagnetAssociationCommands())
        {
            string? executable = ExtractExecutable(command);
            if (executable is not null && File.Exists(executable)) return executable;
        }

        // A removed application can leave the magnet association behind. qBittorrent is common and
        // registers inconsistently across installer/portable upgrades, so use it as a safe fallback.
        string[] qBittorrentCandidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qBittorrent", "qbittorrent.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "qBittorrent", "qbittorrent.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "qBittorrent", "qbittorrent.exe"),
        ];
        return qBittorrentCandidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string?> ReadMagnetAssociationCommands()
    {
        const string subKey = @"magnet\shell\open\command";
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + subKey))
            yield return key?.GetValue(null) as string;
        using (var key = Registry.ClassesRoot.OpenSubKey(subKey))
            yield return key?.GetValue(null) as string;
        using (var key = Registry.LocalMachine.OpenSubKey(@"Software\Classes\" + subKey))
            yield return key?.GetValue(null) as string;
    }

    private static string GetClientName(string executable)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executable);
            return version.ProductName ?? version.FileDescription ?? Path.GetFileNameWithoutExtension(executable);
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(executable);
        }
    }
}
