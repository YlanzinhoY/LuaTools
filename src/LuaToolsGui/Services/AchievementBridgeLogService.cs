using System.Diagnostics;
using System.IO;
using System.Text;

namespace LuaToolsGui.Services;

public enum AchievementBridgeLogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record AchievementBridgeLogEntry(
    DateTimeOffset Timestamp,
    AchievementBridgeLogLevel Level,
    string Category,
    string Message)
{
    public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string LevelText => Level.ToString().ToUpperInvariant();
}

/// <summary>
/// Keeps a bounded, UTF-8 activity log for the native Achievement Bridge. It intentionally records
/// operational state only; environment variables, API keys and full process environments are never logged.
/// </summary>
public sealed class AchievementBridgeLogService
{
    private const int MaximumEntries = 1000;
    private readonly object _gate = new();
    private readonly List<AchievementBridgeLogEntry> _entries = [];
    private readonly string _logPath;

    public AchievementBridgeLogService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LuaToolsGui", "Logs", "achievement-bridge.log"))
    {
    }

    internal AchievementBridgeLogService(string logPath) => _logPath = logPath;

    public event Action<AchievementBridgeLogEntry>? EntryAdded;
    public event Action? StateChanged;

    public bool IsRunning { get; private set; }
    public int? ProcessId { get; private set; }
    public int ActiveGameCount { get; private set; }
    public string LogPath => _logPath;

    public IReadOnlyList<AchievementBridgeLogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    public void BridgeStarted(int processId)
    {
        IsRunning = true;
        ProcessId = processId;
        Add(AchievementBridgeLogLevel.Success, "Bridge", $"watch-all started (pid={processId})");
        StateChanged?.Invoke();
    }

    public void BridgeStopped(int? processId, string reason)
    {
        if (processId is null || ProcessId == processId)
        {
            IsRunning = false;
            ProcessId = null;
            ActiveGameCount = 0;
        }
        Add(AchievementBridgeLogLevel.Warning, "Bridge", $"watch-all stopped ({reason})");
        StateChanged?.Invoke();
    }

    public void CaptureBridgeLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        bool isSessionProvider = line.StartsWith("  provider=", StringComparison.Ordinal);
        string value = line.Trim();

        if (value.StartsWith("[AchievementBridge] active_game_sessions=", StringComparison.Ordinal))
        {
            string countText = value[(value.LastIndexOf('=') + 1)..];
            if (int.TryParse(countText, out int count) && count != ActiveGameCount)
            {
                ActiveGameCount = count;
                StateChanged?.Invoke();
            }
            return;
        }

        if (value == "[AchievementBridge]" || (!isSessionProvider && IsEventEnvelopeField(value))) return;

        AchievementBridgeLogLevel level = value.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("reason=", StringComparison.OrdinalIgnoreCase)
            ? AchievementBridgeLogLevel.Error
            : value.Contains("state=finished", StringComparison.OrdinalIgnoreCase)
                ? AchievementBridgeLogLevel.Warning
                : value.Contains("state=watching", StringComparison.OrdinalIgnoreCase) ||
                  value.Contains("status=watching", StringComparison.OrdinalIgnoreCase)
                    ? AchievementBridgeLogLevel.Success
                    : AchievementBridgeLogLevel.Info;
        string category = value.StartsWith("[GameSession]", StringComparison.Ordinal)
            ? "Game"
            : isSessionProvider
                ? "Provider"
                : "Monitor";
        Add(level, category, value);
    }

    internal void AchievementDetected(AchievementBridgeEvent achievement) => Add(
        AchievementBridgeLogLevel.Success,
        "Achievement",
        $"detected provider={achievement.Provider} appid={achievement.AppId?.ToString() ?? "-"} " +
        $"product_id={achievement.ProductId?.ToString() ?? "-"} id={achievement.Achievement} recovered={achievement.Recovered}");

    public void SteamSyncCompleted(long appId, string achievementId, LocalSteamSyncResult result) => Add(
        result.CacheConfirmed || result.SteamConfirmed ? AchievementBridgeLogLevel.Success : AchievementBridgeLogLevel.Warning,
        "Steam",
        $"sync appid={appId} id={achievementId} changed={result.Changed} " +
        $"cache_confirmed={result.CacheConfirmed} steam_confirmed={result.SteamConfirmed} native={result.NativeNotification}");

    public void Warning(string category, string message) =>
        Add(AchievementBridgeLogLevel.Warning, category, message);

    public void Error(string category, string message) =>
        Add(AchievementBridgeLogLevel.Error, category, message);

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            try
            {
                if (File.Exists(_logPath)) File.Delete(_logPath);
            }
            catch { /* The in-memory log remains usable if another process has the file open. */ }
        }
        StateChanged?.Invoke();
    }

    public void OpenLogFolder()
    {
        string directory = Path.GetDirectoryName(_logPath)!;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void Add(AchievementBridgeLogLevel level, string category, string message)
    {
        var entry = new AchievementBridgeLogEntry(DateTimeOffset.Now, level, category, message);
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MaximumEntries) _entries.RemoveRange(0, _entries.Count - MaximumEntries);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(
                    _logPath,
                    $"{entry.Timestamp:O}\t{entry.LevelText}\t{entry.Category}\t{entry.Message}{Environment.NewLine}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch { /* Diagnostics must never interfere with achievement monitoring. */ }
        }
        EntryAdded?.Invoke(entry);
    }

    private static bool IsEventEnvelopeField(string value) =>
        value.StartsWith("provider=", StringComparison.Ordinal) ||
        value.StartsWith("appid=", StringComparison.Ordinal) ||
        value.StartsWith("product_id=", StringComparison.Ordinal) ||
        value.StartsWith("achievement=", StringComparison.Ordinal) ||
        value.StartsWith("state=", StringComparison.Ordinal) ||
        value.StartsWith("timestamp=", StringComparison.Ordinal) ||
        value.StartsWith("recovered=", StringComparison.Ordinal);
}
