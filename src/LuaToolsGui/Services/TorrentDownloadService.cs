using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Runs each Kazumi magnet in an isolated libtorrent host. Native failures and slow torrent setup stay
/// outside the WPF process, so they cannot freeze or terminate LuaTools.
/// </summary>
public sealed class TorrentDownloadService : IDisposable
{
    private static readonly Regex NumberedTrackerKey = new(
        @"(?<prefix>[?&])tr\.\d+=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BtihParameter = new(
        @"(?:[?&])xt=urn:btih:(?:[a-f0-9]{40}|[a-z2-7]{32})(?:&|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<int, Process> _activeHosts = new();
    private bool _disposed;

    public async Task DownloadMagnetAsync(
        string magnetUri,
        string destinationFolder,
        IProgress<TorrentDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalizedMagnet = NormalizeMagnetUri(magnetUri);
        if (!IsValidMagnetUri(normalizedMagnet))
            throw new ArgumentException("Invalid magnet URI.", nameof(magnetUri));
        if (string.IsNullOrWhiteSpace(destinationFolder))
            throw new ArgumentException("A destination folder is required.", nameof(destinationFolder));
        ct.ThrowIfCancellationRequested();

        string? executable = FindTorrentHostExecutable(AppContext.BaseDirectory);
        if (executable is null)
            throw new FileNotFoundException("The LuaTools torrent host was not found.");

        Directory.CreateDirectory(destinationFolder);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };

        if (!process.Start())
            throw new IOException("The LuaTools torrent host could not be started.");

        _activeHosts.TryAdd(process.Id, process);
        using var cancellation = ct.Register(static state =>
        {
            try
            {
                var host = (Process)state!;
                if (!host.HasExited) host.Kill(entireProcessTree: true);
            }
            catch
            {
                // The host may have exited between HasExited and Kill.
            }
        }, process);

        try
        {
            string request = JsonSerializer.Serialize(
                new TorrentHostRequest(normalizedMagnet, Path.GetFullPath(destinationFolder)),
                JsonOptions);
            await process.StandardInput.WriteLineAsync(request);
            process.StandardInput.Close();

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            string? hostError = null;
            bool completed = false;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                TorrentHostMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<TorrentHostMessage>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                switch (message?.Type)
                {
                    case "progress":
                        progress?.Report(new TorrentDownloadProgress(
                            Math.Clamp(message.Percent ?? 0d, 0d, 100d),
                            Math.Max(0, message.DownloadRate ?? 0),
                            Math.Max(0, message.Peers ?? 0),
                            message.State ?? "Starting"));
                        break;
                    case "complete":
                        completed = true;
                        progress?.Report(new TorrentDownloadProgress(100, 0, 0, "Finished"));
                        break;
                    case "error":
                        hostError = message.Message;
                        break;
                }
            }

            await process.WaitForExitAsync();
            string stderr = await stderrTask;
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(hostError))
                throw new IOException(hostError);
            if (process.ExitCode != 0)
                throw new IOException(string.IsNullOrWhiteSpace(stderr)
                    ? "The LuaTools torrent host failed."
                    : stderr.Trim());
            if (!completed)
                throw new IOException("The LuaTools torrent host exited before the download completed.");
        }
        catch when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            _activeHosts.TryRemove(process.Id, out _);
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup during shutdown or an IPC failure.
            }
        }
    }

    /// <summary>
    /// Kazumi contains a few non-standard numbered tracker keys. Translate those into repeated standard
    /// <c>tr</c> keys without decoding and rebuilding the rest of the magnet URI.
    /// </summary>
    internal static string NormalizeMagnetUri(string magnetUri) =>
        NumberedTrackerKey.Replace(magnetUri, "${prefix}tr=");

    internal static bool IsValidMagnetUri(string? magnetUri) =>
        !string.IsNullOrWhiteSpace(magnetUri)
        && Uri.TryCreate(magnetUri, UriKind.Absolute, out Uri? uri)
        && uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)
        && BtihParameter.IsMatch(magnetUri);

    internal static string? FindTorrentHostExecutable(string baseDirectory)
    {
        string? configured = Environment.GetEnvironmentVariable("LUATOOLS_TORRENT_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        string[] candidates =
        [
            Path.Combine(baseDirectory, "TorrentHost", "LuaTools.TorrentHost.exe"),
            Path.Combine(baseDirectory, "LuaTools.TorrentHost.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (Process process in _activeHosts.Values)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup while the app is closing.
            }
        }
        _activeHosts.Clear();
    }

    private sealed record TorrentHostRequest(string MagnetUri, string DestinationFolder);

    private sealed record TorrentHostMessage(
        string Type,
        double? Percent,
        long? DownloadRate,
        int? Peers,
        string? State,
        string? Message);
}
