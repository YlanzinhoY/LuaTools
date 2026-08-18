using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;
using Microsoft.Win32;

namespace LuaToolsGui.Services;

/// <summary>
/// Downloads + launches CloudRedirect.exe. The user-facing CloudRedirect GUI manager (Selectively11). The
/// exe is fetched once from the latest GitHub release (via <see cref="GithubProxy"/>, so it works in blocked
/// regions) and cached under %AppData%\LuaToolsGui\cloudredirect. It self-updates, so we always grab the
/// latest asset and don't track versions. The Mode page's "Manage" button (shown when CloudRedirect is the
/// active mode) calls <see cref="LaunchAsync"/>, which opens its window visibly and returns immediately.
/// (Separate from the silent CloudRedirectCLI.exe the mode-install flow runs.)
/// </summary>
public class CloudRedirectService(GithubProxy gh)
{
    private static readonly string ToolDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "cloudredirect");
    private static string ExePath => Path.Combine(ToolDir, "CloudRedirect.exe");
    private static string SaveCliPath => Path.Combine(ToolDir, "cloud_redirect_cli.exe");
    private static string SaveDllPath => Path.Combine(ToolDir, "cloud_redirect.dll");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Ensure CloudRedirect.exe is on disk (downloads the latest release asset once). Returns its
    /// path, or null if it couldn't be obtained.</summary>
    public async Task<string?> EnsureToolAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (File.Exists(ExePath)) return ExePath;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(ExePath)) return ExePath; // won the race

            string url = $"https://api.github.com/repos/{AppConfig.CloudRedirectRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a => a.Name.Equals("CloudRedirect.exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null) return null;

            Directory.CreateDirectory(ToolDir);
            await gh.DownloadAsync(asset.DownloadUrl, ExePath, progress, ct); // single exe, no zip to extract

            return File.Exists(ExePath) ? ExePath : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _gate.Release(); }
    }

    /// <summary>Download (if needed), and launch the CloudRedirect GUI. Fire-and-forget. Its window opens and
    /// we don't wait for it. Returns false if the tool couldn't be downloaded or started.</summary>
    public async Task<bool> LaunchAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        string? exe = await EnsureToolAsync(progress, ct);
        if (exe is null) return false;

        try
        {
            // GUI app: show its window (UseShellExecute) and don't block our app on it.
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = ToolDir,
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Uploads a prepared directory of original save files. The CLI stores them under the game's
    /// name and replaces files in place instead of creating timestamped archives.
    /// </summary>
    public Task<CloudRedirectCommandResult> UploadSaveAsync(
        GameSaveDefinition game,
        string preparedDirectory,
        IProgress<double?>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(preparedDirectory))
            return Task.FromResult(CloudRedirectCommandResult.Fail("The prepared save directory was not found."));
        var context = ResolveSaveContext();
        if (context.Error is not null)
            return Task.FromResult(CloudRedirectCommandResult.Fail(context.Error));
        return RunSaveCommandAsync(
            ["save", "upload", context.Provider!, context.AccountId!, game.AppId.ToString(), game.Name, preparedDirectory],
            progress, ct);
    }

    /// <summary>Downloads the game's individual save files into a private temporary directory.</summary>
    public Task<CloudRedirectCommandResult> DownloadSaveAsync(
        GameSaveDefinition game,
        IProgress<double?>? progress = null,
        CancellationToken ct = default)
    {
        var context = ResolveSaveContext();
        if (context.Error is not null)
            return Task.FromResult(CloudRedirectCommandResult.Fail(context.Error));
        string outputDir = Path.Combine(Path.GetTempPath(), "LuaToolsGui", "save-downloads");
        Directory.CreateDirectory(outputDir);
        string output = Path.Combine(outputDir, $"{game.AppId}_{Guid.NewGuid():N}");
        return RunSaveCommandAsync(
            ["save", "download", context.Provider!, context.AccountId!, game.AppId.ToString(), game.Name, output],
            progress, ct, resultFilePath: output);
    }

    private (string? Provider, string? AccountId, string? Error) ResolveSaveContext()
    {
        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloudRedirect", "config.json");
        string? provider = null;
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            if (config.RootElement.TryGetProperty("provider", out var value)) provider = value.GetString();
        }
        catch
        {
            return (null, null, "Configure a CloudRedirect provider before backing up saves.");
        }
        if (string.IsNullOrWhiteSpace(provider) || provider.Equals("local", StringComparison.OrdinalIgnoreCase))
            return (null, null, "Configure a CloudRedirect cloud or folder provider before backing up saves.");

        uint accountId = 0;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            accountId = Convert.ToUInt32(key?.GetValue("ActiveUser") ?? 0);
        }
        catch { }

        if (accountId == 0)
            return (null, null, "The active Steam account could not be identified. Open Steam and try again.");
        return (provider, accountId.ToString(), null);
    }

    private async Task<CloudRedirectCommandResult> RunSaveCommandAsync(
        IReadOnlyCollection<string> arguments,
        IProgress<double?>? progress,
        CancellationToken ct,
        string? resultFilePath = null)
    {
        string? cli = await EnsureSaveCliAsync(progress, ct);
        if (cli is null)
        {
            return CloudRedirectCommandResult.Fail(
                "The installed CloudRedirect release does not provide cloud_redirect_cli.exe with save upload/download commands yet.");
        }

        try
        {
            var psi = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = ToolDir,
            };
            foreach (string argument in arguments) psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process is null) return CloudRedirectCommandResult.Fail("CloudRedirect CLI could not be started.");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            string output = (await stdout).Trim();
            string error = (await stderr).Trim();
            if (process.ExitCode == 0 && resultFilePath is not null &&
                !File.Exists(resultFilePath) && !Directory.Exists(resultFilePath))
                return CloudRedirectCommandResult.Fail("CloudRedirect reported success but did not create the save files.", output);
            return process.ExitCode == 0
                ? CloudRedirectCommandResult.Ok(output, resultFilePath)
                : CloudRedirectCommandResult.Fail(
                    string.IsNullOrWhiteSpace(error) ? $"CloudRedirect CLI exited with code {process.ExitCode}." : error,
                    output);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return CloudRedirectCommandResult.Fail(ex.Message); }
    }

    /// <summary>Use an already-installed save CLI or download it when a future official release
    /// publishes the asset. Current releases are handled honestly: absence is returned to the UI.</summary>
    private async Task<string?> EnsureSaveCliAsync(IProgress<double?>? progress, CancellationToken ct)
    {
        if (File.Exists(SaveCliPath) && File.Exists(SaveDllPath)) return SaveCliPath;

        string? extracted = FindExtractedSaveCli();
        if (extracted is not null) return extracted;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(SaveCliPath) && File.Exists(SaveDllPath)) return SaveCliPath;

            extracted = FindExtractedSaveCli();
            if (extracted is not null) return extracted;

            string url = $"https://api.github.com/repos/{AppConfig.CloudRedirectRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var cliAsset = release?.Assets.FirstOrDefault(a =>
                a.Name.Equals("cloud_redirect_cli.exe", StringComparison.OrdinalIgnoreCase));
            var dllAsset = release?.Assets.FirstOrDefault(a =>
                a.Name.Equals("cloud_redirect.dll", StringComparison.OrdinalIgnoreCase));
            if (cliAsset is null || dllAsset is null) return null;

            Directory.CreateDirectory(ToolDir);
            await gh.DownloadAsync(cliAsset.DownloadUrl, SaveCliPath, progress, ct);
            await gh.DownloadAsync(dllAsset.DownloadUrl, SaveDllPath, progress, ct);
            return File.Exists(SaveCliPath) && File.Exists(SaveDllPath) ? SaveCliPath : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _gate.Release(); }
    }

    private static string? FindExtractedSaveCli()
    {
        string root = Path.Combine(Path.GetTempPath(), "CloudRedirect");
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateDirectories(root)
                .Select(dir => new
                {
                    Cli = Path.Combine(dir, "cloud_redirect_cli.exe"),
                    Dll = Path.Combine(dir, "cloud_redirect.dll"),
                })
                .Where(x => File.Exists(x.Cli) && File.Exists(x.Dll))
                .OrderByDescending(x => File.GetLastWriteTimeUtc(x.Cli))
                .Select(x => x.Cli)
                .FirstOrDefault();
        }
        catch { return null; }
    }
}

public sealed record CloudRedirectCommandResult(bool Success, string? Output, string? Error, string? FilePath = null)
{
    public static CloudRedirectCommandResult Ok(string? output = null, string? filePath = null) =>
        new(true, output, null, filePath);
    public static CloudRedirectCommandResult Fail(string error, string? output = null) => new(false, output, error);
}
