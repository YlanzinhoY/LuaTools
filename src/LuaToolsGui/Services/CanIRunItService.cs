using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Runs the isolated Go backend and converts its single JSON response into UI models.</summary>
public sealed class CanIRunItService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SettingsService _settings;

    public CanIRunItService(SettingsService settings) => _settings = settings;

    public async Task<CanIRunItAnalysis> AnalyzeAsync(
        long appId,
        string gameName,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        string? executable = FindExecutable();
        if (executable is null)
            throw new CanIRunItException("backend_missing", "The Can I Run It backend is not installed.");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("--appid");
        start.ArgumentList.Add(appId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--game-name");
        start.ArgumentList.Add(gameName);
        start.ArgumentList.Add("--language");
        start.ArgumentList.Add(string.IsNullOrWhiteSpace(_settings.Language)
            ? CultureInfo.CurrentUICulture.Name
            : _settings.Language);
        start.Environment["OPENROUTER_API_KEY"] = apiKey.Trim();
        start.Environment["OPENROUTER_MODEL"] = _settings.OpenRouterModel;

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new CanIRunItException("backend_start_failed", "The Can I Run It backend could not be started.");
        }
        catch (CanIRunItException) { throw; }
        catch (Exception ex)
        {
            throw new CanIRunItException("backend_start_failed", ex.Message);
        }

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* process already exited */ }
        });

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string stdout = await stdoutTask;
        _ = await stderrTask; // intentionally never expose backend diagnostics or accidental secrets

        CanIRunItCommandOutput? output;
        try { output = JsonSerializer.Deserialize<CanIRunItCommandOutput>(stdout, JsonOptions); }
        catch (JsonException)
        {
            throw new CanIRunItException("backend_invalid_response", "The Can I Run It backend returned invalid data.");
        }

        if (output?.Success == true && output.Result is not null) return output.Result;
        if (output?.Error is not null)
            throw new CanIRunItException(output.Error.Code, output.Error.Message);
        throw new CanIRunItException("backend_failed", "The Can I Run It backend did not return an analysis.");
    }

    internal static string? FindExecutable()
    {
        string? developmentOverride = Environment.GetEnvironmentVariable("CAN_I_RUN_IT_BACKEND_PATH");
        string[] candidates =
        [
            developmentOverride ?? "",
            Path.Combine(AppContext.BaseDirectory, "CanIRunIt", "can-i-run-it.exe"),
            Path.Combine(AppContext.BaseDirectory, "can-i-run-it.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LuaToolsGui", "CanIRunIt", "can-i-run-it.exe"),
        ];
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }
}

public sealed class CanIRunItException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
