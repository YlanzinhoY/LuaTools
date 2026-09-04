using System.IO;
using LuaToolsGui.Models;
using MonoTorrent;
using MonoTorrent.Client;

namespace LuaToolsGui.Services;

/// <summary>Small in-process BitTorrent client used by Kazumi magnet entries.</summary>
public sealed class TorrentDownloadService : IDisposable
{
    private readonly object _engineGate = new();
    private ClientEngine? _engine;

    private ClientEngine GetEngine()
    {
        lock (_engineGate)
        {
            if (_engine is not null) return _engine;

            string cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LuaToolsGui", "torrent-cache");
            Directory.CreateDirectory(cache);

            var settings = new EngineSettingsBuilder
            {
                AllowPortForwarding = true,
                AutoSaveLoadDhtCache = true,
                AutoSaveLoadFastResume = true,
                AutoSaveLoadMagnetLinkMetadata = true,
                CacheDirectory = cache,
            };
            return _engine = new ClientEngine(settings.ToSettings());
        }
    }

    public async Task DownloadMagnetAsync(
        string magnetUri,
        string destinationFolder,
        IProgress<TorrentDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!MagnetLink.TryParse(magnetUri, out var magnet))
            throw new ArgumentException("Invalid magnet URI.", nameof(magnetUri));

        ClientEngine engine = GetEngine();
        Directory.CreateDirectory(destinationFolder);
        TorrentManager? manager = null;
        try
        {
            manager = await engine.AddAsync(magnet, destinationFolder);
            await manager.StartAsync();

            while (manager.Progress < 100 && manager.State != TorrentState.Error)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new TorrentDownloadProgress(
                    manager.Progress,
                    manager.Monitor.DownloadRate,
                    manager.Peers.Available + manager.Peers.Leechs + manager.Peers.Seeds,
                    manager.State.ToString(),
                    engine.Dht.NodeCount));
                await Task.Delay(750, ct);
            }

            if (manager.State == TorrentState.Error)
                throw new IOException("The torrent client reported an error.");

            progress?.Report(new TorrentDownloadProgress(100, 0, manager.Peers.Available, "Complete", engine.Dht.NodeCount));
        }
        finally
        {
            if (manager is not null)
            {
                try { await manager.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
                try { await engine.RemoveAsync(manager); } catch { /* best effort */ }
            }
        }
    }

    public void Dispose()
    {
        lock (_engineGate)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
