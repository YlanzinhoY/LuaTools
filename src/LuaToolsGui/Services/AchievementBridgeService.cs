using System.Diagnostics;
using System.IO;
using System.Text;
using LuaToolsGui.Models;
using Microsoft.Extensions.Hosting;

namespace LuaToolsGui.Services;

/// <summary>
/// Owns the Windows Achievement Bridge child processes for the lifetime of LuaTools. Missing bridge
/// binaries are deliberately a no-op so development builds and upgrades remain safe.
/// </summary>
public sealed class AchievementBridgeService : IHostedService, IDisposable
{
    private readonly SettingsService _settings;
    private readonly AchievementPopupService _popups;
    private readonly AchievementBridgeSetupService _setup;
    private readonly AchievementBridgeClient _client;
    private readonly AchievementCatalogService _catalogs;
    private readonly SemaphoreSlim _syncQueue = new(1, 1);
    private readonly object _gate = new();
    private readonly List<Process> _processes = [];
    private bool _started;

    public AchievementBridgeService(
        SettingsService settings,
        AchievementPopupService popups,
        AchievementBridgeSetupService setup,
        AchievementBridgeClient client,
        AchievementCatalogService catalogs)
    {
        _settings = settings;
        _popups = popups;
        _setup = setup;
        _client = client;
        _catalogs = catalogs;
        _settings.AchievementSettingsChanged += OnSettingsChanged;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _started = true;
            ApplySettingsLocked();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _started = false;
            StopProcessesLocked();
        }
        return Task.CompletedTask;
    }

    private void OnSettingsChanged()
    {
        lock (_gate)
        {
            if (_started) ApplySettingsLocked();
        }
    }

    private void ApplySettingsLocked()
    {
        StopProcessesLocked();
        if (!_settings.AchievementsEnabled) return;
        _setup.EnsureInstalled();
        string? executable = FindExecutable();
        if (executable is null) return;

        // Providers remain separate OS processes until the Zig host owns concurrent provider workers.
        // A failure in one reader therefore cannot take down the other sources or the LuaTools UI.
        StartReader(executable, "watch");
        StartReader(executable, "ubisoft-watch");
        StartReader(executable, "uplay-r2-watch");
    }

    private void StartReader(string executable, string command)
    {
        // LuaTools owns the image-rich R2 notification. Other providers keep the bridge's legacy
        // notification until their events can be enriched with artwork as well.
        bool usesImagePopup = command == "uplay-r2-watch";
        string arguments = usesImagePopup || !_settings.AchievementNotifications
            ? $"{command} --no-notifications"
            : command;
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
                EnableRaisingEvents = true,
            };
            var stderr = new AchievementBridgeEventParser();
            var stdout = new AchievementBridgeEventParser();
            process.ErrorDataReceived += (_, eventArgs) => HandleBridgeLine(stderr, eventArgs.Data);
            process.OutputDataReceived += (_, eventArgs) => HandleBridgeLine(stdout, eventArgs.Data);
            if (!process.Start())
            {
                process.Dispose();
                return;
            }
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            _processes.Add(process);
        }
        catch
        {
            // Optional integration: a missing dependency or blocked process must never prevent LuaTools startup.
        }
    }

    private void HandleBridgeLine(AchievementBridgeEventParser parser, string? line)
    {
        AchievementBridgeEvent? achievement = parser.PushLine(line);
        if (achievement is not null) _ = HandleBridgeEventAsync(achievement);
    }

    private async Task HandleBridgeEventAsync(AchievementBridgeEvent achievement)
    {
        try
        {
            ResolvedBridgeAchievement? resolved = await ResolveBridgeEventAsync(achievement);
            if (resolved is null) return;

            // The visual feedback begins immediately. Persistence is serialized so
            // two providers cannot rewrite the same native cache concurrently.
            Task popup = _settings.AchievementNotifications
                ? _popups.ShowAchievementAsync(resolved.Achievement)
                : Task.CompletedTask;
            await _syncQueue.WaitAsync();
            try
            {
                long timestamp = achievement.Timestamp is > 0
                    ? achievement.Timestamp.Value
                    : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _client.SyncLocalAchievementAsync(
                    resolved.AppId,
                    resolved.Achievement.ApiName,
                    timestamp);
            }
            catch
            {
                // The provider journal remains the source of truth and the popup
                // still completes. A later recovered event can retry local sync.
            }
            finally
            {
                _syncQueue.Release();
            }
            try { await popup; } catch { /* best-effort visual feedback */ }
        }
        catch
        {
            // Provider readers must survive missing schemas, Steam updates, and
            // incomplete mappings for games that are not supported yet.
        }
    }

    internal async Task<ResolvedBridgeAchievement?> ResolveBridgeEventAsync(
        AchievementBridgeEvent achievement,
        CancellationToken cancellationToken = default)
    {
        long? appId = achievement.AppId;
        if (appId is null && achievement.ProductId == R2AchievementService.BlackFlagProductId)
            appId = R2AchievementService.BlackFlagSteamAppId;
        if (appId is null) return null;

        AchievementCatalog catalog = await _catalogs.LoadAsync(appId.Value, cancellationToken);
        Achievement? item = catalog.Achievements.FirstOrDefault(candidate =>
            candidate.ApiName.Equals(achievement.Achievement, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = catalog.Achievements.FirstOrDefault(candidate =>
                NumericSuffix(candidate.ApiName) == achievement.Achievement);
        }
        return item is null ? null : new ResolvedBridgeAchievement(appId.Value, item);
    }

    private static string? NumericSuffix(string apiName)
    {
        int start = apiName.Length;
        while (start > 0 && char.IsDigit(apiName[start - 1])) start--;
        return start == apiName.Length ? null : apiName[start..];
    }

    internal static string? FindExecutable()
    {
        string? developmentOverride = Environment.GetEnvironmentVariable("ACHIEVEMENT_BRIDGE_PATH");
        string[] candidates =
        [
            developmentOverride ?? "",
            Path.Combine(AppContext.BaseDirectory, "AchievementBridge", "achievement-bridge.exe"),
            Path.Combine(AppContext.BaseDirectory, "achievement-bridge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LuaToolsGui", "AchievementBridge", "achievement-bridge.exe"),
        ];
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private void StopProcessesLocked()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch { /* process already gone / access denied */ }
            finally { process.Dispose(); }
        }
        _processes.Clear();
    }

    public void Dispose()
    {
        _settings.AchievementSettingsChanged -= OnSettingsChanged;
        lock (_gate) StopProcessesLocked();
    }
}

internal sealed record AchievementBridgeEvent(
    string Provider,
    int? AppId,
    int? ProductId,
    string Achievement,
    long? Timestamp,
    bool Recovered);

internal sealed record ResolvedBridgeAchievement(long AppId, Achievement Achievement);

/// <summary>Incrementally parses the bridge's stable key/value event envelope from stdout/stderr.</summary>
internal sealed class AchievementBridgeEventParser
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);
    private bool _inside;

    public AchievementBridgeEvent? PushLine(string? line)
    {
        if (line == "[AchievementBridge]")
        {
            _fields.Clear();
            _inside = true;
            return null;
        }
        if (!_inside) return null;

        if (string.IsNullOrWhiteSpace(line))
        {
            _inside = false;
            if (!_fields.TryGetValue("provider", out string? provider) ||
                !_fields.TryGetValue("achievement", out string? achievement) ||
                (_fields.TryGetValue("state", out string? state) && state != "unlocked")) return null;

            return new AchievementBridgeEvent(
                provider,
                ReadInt("appid"),
                ReadInt("product_id"),
                achievement,
                ReadLong("timestamp"),
                ReadBool("recovered"));
        }

        int separator = line.IndexOf('=');
        if (separator > 0)
            _fields[line[..separator]] = line[(separator + 1)..];
        return null;
    }

    private int? ReadInt(string key) =>
        _fields.TryGetValue(key, out string? value) && int.TryParse(value, out int number)
            ? number
            : null;

    private long? ReadLong(string key) =>
        _fields.TryGetValue(key, out string? value) && long.TryParse(value, out long number)
            ? number
            : null;

    private bool ReadBool(string key) =>
        _fields.TryGetValue(key, out string? value) && bool.TryParse(value, out bool result) && result;
}
