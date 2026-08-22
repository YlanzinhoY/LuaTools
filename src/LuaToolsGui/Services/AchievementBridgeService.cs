using System.Diagnostics;
using System.IO;
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
    private readonly object _gate = new();
    private readonly List<Process> _processes = [];
    private bool _started;

    public AchievementBridgeService(SettingsService settings, AchievementPopupService popups)
    {
        _settings = settings;
        _popups = popups;
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
        if (achievement is not null && _settings.AchievementNotifications)
            _ = _popups.ShowBridgeEventAsync(achievement);
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
    string Achievement);

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
                achievement);
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
}
