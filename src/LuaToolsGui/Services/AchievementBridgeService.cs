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
    private readonly object _gate = new();
    private readonly List<Process> _processes = [];
    private bool _started;

    public AchievementBridgeService(SettingsService settings)
    {
        _settings = settings;
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
        string arguments = _settings.AchievementNotifications ? command : $"{command} --no-notifications";
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            });
            if (process is not null) _processes.Add(process);
        }
        catch
        {
            // Optional integration: a missing dependency or blocked process must never prevent LuaTools startup.
        }
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
