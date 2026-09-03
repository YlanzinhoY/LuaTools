using System.Diagnostics;
using System.IO;
using System.Text;
using LuaToolsGui.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly AchievementSteamSyncService _steamSync;
    private readonly AchievementBridgeClient _bridge;
    private readonly AchievementCatalogService _catalogs;
    private readonly SteamAchievementUiProjectionStore _uiProjection;
    private readonly AchievementBridgeLogService _activityLog;
    private readonly ILogger<AchievementBridgeService> _logger;
    private readonly AchievementBurstGate _burstGate = new(TimeSpan.FromSeconds(2), threshold: 3);
    private readonly object _gate = new();
    private readonly List<Process> _processes = [];
    private bool _started;
    private int _processGeneration;
    private int _restartAttempt;
    private bool _restartPending;

    public AchievementBridgeService(
        SettingsService settings,
        AchievementPopupService popups,
        AchievementBridgeSetupService setup,
        AchievementSteamSyncService steamSync,
        AchievementBridgeClient bridge,
        AchievementCatalogService catalogs,
        SteamAchievementUiProjectionStore uiProjection,
        AchievementBridgeLogService activityLog,
        ILogger<AchievementBridgeService> logger)
    {
        _settings = settings;
        _popups = popups;
        _setup = setup;
        _steamSync = steamSync;
        _bridge = bridge;
        _catalogs = catalogs;
        _uiProjection = uiProjection;
        _activityLog = activityLog;
        _logger = logger;
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
            _processGeneration++;
            _restartPending = false;
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
        int generation = ++_processGeneration;
        _restartAttempt = 0;
        _restartPending = false;
        StopProcessesLocked();
        if (!_settings.AchievementsEnabled) return;

        StartBridgeLocked(generation);
    }

    private void StartBridgeLocked(int generation)
    {
        try
        {
            _setup.EnsureInstalled();
        }
        catch (Exception exception)
        {
            _activityLog.Error("Setup", $"setup failed: {exception.Message}");
            _logger.LogWarning(exception, "Achievement Bridge setup failed; retrying in the background");
            ScheduleRestartLocked(generation);
            return;
        }

        string? executable = FindExecutable();
        if (executable is null)
        {
            _activityLog.Error("Setup", "achievement-bridge.exe was not found");
            _logger.LogWarning("Achievement Bridge executable was not found; retrying in the background");
            ScheduleRestartLocked(generation);
            return;
        }

        // The Zig host owns isolated concurrent provider workers while LuaTools
        // keeps a single child process and one ordered event stream.
        if (!StartReader(executable, "watch-all", generation))
            ScheduleRestartLocked(generation);
    }

    private bool StartReader(string executable, string command, int generation)
    {
        // LuaTools owns the image-rich R2 notification. Other providers keep the bridge's legacy
        // notification until their events can be enriched with artwork as well.
        bool usesImagePopup = command is "uplay-r2-watch" or "watch-all";
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
                return false;
            }
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            process.Exited += (_, _) => HandleUnexpectedExit(process, executable, command, generation, startedAt);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            _processes.Add(process);
            _activityLog.BridgeStarted(process.Id);
            _logger.LogInformation("Achievement Bridge started with process id {ProcessId}", process.Id);
            return true;
        }
        catch (Exception exception)
        {
            _activityLog.Error("Bridge", $"could not start: {exception.Message}");
            // Optional integration: a missing dependency or blocked process must never prevent LuaTools startup.
            _logger.LogWarning(exception, "Achievement Bridge could not be started");
            return false;
        }
    }

    private void HandleUnexpectedExit(
        Process process,
        string executable,
        string command,
        int generation,
        DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            _processes.Remove(process);
            int? exitCode = null;
            try { exitCode = process.ExitCode; }
            catch { /* process may already have been disposed during shutdown */ }
            finally { process.Dispose(); }

            if (!ShouldRestartBridge(_started, _settings.AchievementsEnabled, generation, _processGeneration))
                return;

            if (DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromSeconds(30))
                _restartAttempt = 0;
            _logger.LogWarning(
                "Achievement Bridge process exited unexpectedly with code {ExitCode}; scheduling restart",
                exitCode);
            _activityLog.BridgeStopped(process.Id, $"unexpected exit code {exitCode?.ToString() ?? "unknown"}");
            ScheduleRestartLocked(generation);
        }
    }

    private void ScheduleRestartLocked(int generation)
    {
        if (_restartPending ||
            !ShouldRestartBridge(_started, _settings.AchievementsEnabled, generation, _processGeneration))
            return;

        _restartPending = true;
        TimeSpan delay = RestartDelay(++_restartAttempt);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            lock (_gate)
            {
                _restartPending = false;
                if (ShouldRestartBridge(_started, _settings.AchievementsEnabled, generation, _processGeneration))
                    StartBridgeLocked(generation);
            }
        });
    }

    internal static bool ShouldRestartBridge(
        bool serviceStarted,
        bool achievementsEnabled,
        int processGeneration,
        int currentGeneration) =>
        serviceStarted && achievementsEnabled && processGeneration == currentGeneration;

    internal static TimeSpan RestartDelay(int attempt)
    {
        int exponent = Math.Clamp(attempt - 1, 0, 5);
        return TimeSpan.FromSeconds(Math.Min(30, 1 << exponent));
    }

    private void HandleBridgeLine(AchievementBridgeEventParser parser, string? line)
    {
        _activityLog.CaptureBridgeLine(line);
        AchievementBridgeEvent? achievement = parser.PushLine(line);
        if (achievement is not null) _ = HandleBridgeEventAsync(achievement);
    }

    private async Task HandleBridgeEventAsync(AchievementBridgeEvent achievement)
    {
        try
        {
            _activityLog.AchievementDetected(achievement);
            string burstScope = $"{achievement.Provider}:{achievement.ProductId ?? achievement.AppId ?? 0}";
            if (await _burstGate.IsBackfillAsync(burstScope))
            {
                _activityLog.Warning("Achievement", $"backfill burst suppressed ({burstScope})");
                return;
            }
            ResolvedBridgeAchievement? resolved = await ResolveBridgeEventAsync(achievement);
            if (resolved is null)
            {
                _activityLog.Warning("Catalog", $"no mapping for provider={achievement.Provider} id={achievement.Achievement}");
                return;
            }

            bool notificationsEnabled = _settings.AchievementNotifications;
            bool trySteamNotification = notificationsEnabled &&
                _settings.ExperimentalSteamAchievementNotifications &&
                !achievement.Recovered;
            string nativeNotification = "not_requested";
            bool cacheConfirmed = false;
            bool steamConfirmed = false;
            bool changed = false;
            long timestamp = achievement.Timestamp is > 0
                ? achievement.Timestamp.Value
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (achievement.Provider.Equals("rune", StringComparison.OrdinalIgnoreCase))
            {
                try { await _bridge.SyncVerifiedRuneAchievementAsync(resolved.AppId, resolved.Achievement.ApiName); }
                catch (Exception exception)
                {
                    // Protected schemas and transient Steam failures still use
                    // the durable local sync below. The command itself verifies
                    // Achieved=1 in RUNE before any server write is attempted.
                    _activityLog.Warning("RUNE", $"verified Steam write was unavailable: {exception.Message}");
                }
            }
            try
            {
                LocalSteamSyncResult result = await _steamSync.SyncOneAsync(
                    resolved.AppId,
                    resolved.Achievement.ApiName,
                    timestamp,
                    trySteamNotification);
                _activityLog.SteamSyncCompleted(resolved.AppId, resolved.Achievement.ApiName, result);
                nativeNotification = result.NativeNotification;
                cacheConfirmed = result.CacheConfirmed;
                steamConfirmed = result.SteamConfirmed;
                changed = result.Changed;
                if (cacheConfirmed || steamConfirmed)
                    _uiProjection.AddConfirmed(
                        resolved.AppId,
                        resolved.Catalog,
                        resolved.Achievement.ApiName,
                        timestamp);
            }
            catch (Exception exception)
            {
                // The provider journal remains the source of truth and the popup
                // still completes. A later recovered event can retry local sync.
                _activityLog.Error("Steam", $"sync failed appid={resolved.AppId} id={resolved.Achievement.ApiName}: {exception.Message}");
            }
            // A popup is a promise to the player that the achievement now exists
            // in Steam's local state. Never show one for a cache write that Steam
            // did not read back as unlocked, or for an idempotent/replayed event.
            if (ShouldShowLuaToolsPopup(notificationsEnabled, achievement.Recovered, changed, cacheConfirmed, steamConfirmed, nativeNotification))
                try { await _popups.ShowAchievementAsync(resolved.Achievement); }
                catch { /* best-effort visual feedback after confirmed sync */ }
        }
        catch (Exception exception)
        {
            // Provider readers must survive missing schemas, Steam updates, and
            // incomplete mappings for games that are not supported yet.
            _activityLog.Error("Bridge", $"event handling failed: {exception.Message}");
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
        return item is null ? null : new ResolvedBridgeAchievement(appId.Value, item, catalog.Achievements);
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

    internal static bool ShouldSuppressLuaToolsPopup(string nativeNotification) =>
        nativeNotification.Equals("store_queued", StringComparison.OrdinalIgnoreCase) ||
        nativeNotification.Equals("progress_queued", StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldShowLuaToolsPopup(
        bool notificationsEnabled,
        bool recovered,
        bool changed,
        bool cacheConfirmed,
        bool steamConfirmed,
        string nativeNotification) =>
        notificationsEnabled && !recovered && changed && (cacheConfirmed || steamConfirmed) &&
        !ShouldSuppressLuaToolsPopup(nativeNotification);

    private void StopProcessesLocked()
    {
        foreach (var process in _processes)
        {
            int processId = process.Id;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
            catch { /* process already gone / access denied */ }
            finally
            {
                process.Dispose();
                _activityLog.BridgeStopped(processId, "service stopped");
            }
        }
        _processes.Clear();
    }

    public void Dispose()
    {
        _settings.AchievementSettingsChanged -= OnSettingsChanged;
        lock (_gate)
        {
            _started = false;
            _processGeneration++;
            _restartPending = false;
            StopProcessesLocked();
        }
    }
}

/// <summary>
/// Holds provider events briefly so a save-import burst can be distinguished
/// from one or two achievements legitimately awarded together.
/// </summary>
internal sealed class AchievementBurstGate(TimeSpan window, int threshold)
{
    private sealed class BurstWindow(DateTimeOffset endsAt)
    {
        public DateTimeOffset EndsAt { get; } = endsAt;
        public int Count { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, BurstWindow> _windows = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsBackfillAsync(string scope, CancellationToken cancellationToken = default)
    {
        BurstWindow burst;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_windows.TryGetValue(scope, out burst!) || now >= burst.EndsAt)
            {
                burst = new BurstWindow(now + window);
                _windows[scope] = burst;
            }
            burst.Count++;
        }

        TimeSpan delay = burst.EndsAt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);

        lock (_gate)
        {
            bool isBackfill = burst.Count >= threshold;
            if (_windows.TryGetValue(scope, out BurstWindow? current) && ReferenceEquals(current, burst))
                _windows.Remove(scope);
            return isBackfill;
        }
    }
}

internal sealed record AchievementBridgeEvent(
    string Provider,
    int? AppId,
    int? ProductId,
    string Achievement,
    long? Timestamp,
    bool Recovered);

internal sealed record ResolvedBridgeAchievement(
    long AppId,
    Achievement Achievement,
    IReadOnlyList<Achievement> Catalog);

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
