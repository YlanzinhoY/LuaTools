using System.Text.Json;
using System.Text.Json.Serialization;
using LibtorrentDotNet;

namespace LuaTools.TorrentHost;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly object OutputGate = new();

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
            {
                using ITorrentSession session = TorrentSession.Create();
                Write(new HostMessage("ready", Engine: "libtorrent"));
                return 0;
            }

            string? line = await Console.In.ReadLineAsync();
            var request = line is null
                ? null
                : JsonSerializer.Deserialize<DownloadRequest>(line, JsonOptions);
            Validate(request);

            Directory.CreateDirectory(request!.DestinationFolder);
            using ITorrentSession torrentSession = TorrentSession.Create();
            torrentSession.ChangeEventTimerInterval(TimeSpan.FromMilliseconds(500));

            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            torrentSession.TorrentStateUpdated += (_, e) =>
            {
                foreach (TorrentStatus status in e.UpdatedTorrents)
                {
                    double percent = Math.Clamp(status.Progress * 100d, 0d, 100d);
                    Write(new HostMessage(
                        "progress",
                        Percent: percent,
                        DownloadRate: status.DownloadRate,
                        Peers: status.NumPeers,
                        State: status.State.ToString()));

                    if (status.State is TorrentState.Finished or TorrentState.Seeding || percent >= 100d)
                        finished.TrySetResult();
                }
            };
            torrentSession.TorrentError += (_, e) =>
                finished.TrySetException(new IOException(string.IsNullOrWhiteSpace(e.Error)
                    ? "libtorrent reported an error."
                    : e.Error));
            torrentSession.TorrentOperationChanged += (_, e) =>
            {
                if (e.OperationEvent == TorrentOperationEvent.Finished)
                    finished.TrySetResult();
            };

            Write(new HostMessage("progress", Percent: 0, DownloadRate: 0, Peers: 0, State: "Starting"));
            bool added = torrentSession.AddTorrent(
                new AddTorrentFromMagnetLinkRequest(request.MagnetUri, request.DestinationFolder));
            if (!added)
                throw new IOException("libtorrent rejected the magnet link.");

            await finished.Task;
            Write(new HostMessage("complete", Percent: 100, DownloadRate: 0, State: "Finished"));
            return 0;
        }
        catch (Exception ex)
        {
            string message = SafeErrorMessage(ex);
            Write(new HostMessage("error", Message: message));
            await Console.Error.WriteLineAsync(message);
            return 1;
        }
    }

    private static void Validate(DownloadRequest? request)
    {
        if (request is null)
            throw new ArgumentException("A download request is required.");
        if (!Uri.TryCreate(request.MagnetUri, UriKind.Absolute, out Uri? magnet)
            || !magnet.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The magnet URI is invalid.");
        if (string.IsNullOrWhiteSpace(request.DestinationFolder))
            throw new ArgumentException("A destination folder is required.");
    }

    private static string SafeErrorMessage(Exception exception)
    {
        string message = exception.GetBaseException().Message;
        return string.IsNullOrWhiteSpace(message) ? "The torrent host failed." : message;
    }

    private static void Write(HostMessage message)
    {
        string json = JsonSerializer.Serialize(message, JsonOptions);
        lock (OutputGate)
        {
            Console.Out.WriteLine(json);
            Console.Out.Flush();
        }
    }
}

internal sealed record DownloadRequest(string MagnetUri, string DestinationFolder);

internal sealed record HostMessage(
    string Type,
    double? Percent = null,
    long? DownloadRate = null,
    int? Peers = null,
    string? State = null,
    string? Engine = null,
    string? Message = null);
