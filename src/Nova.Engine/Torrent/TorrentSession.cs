using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Nova.Domain;

namespace Nova.Engine.Torrent;

/// <summary>Options for one torrent download/seeding session.</summary>
public sealed class TorrentSessionOptions
{
    public string DestinationDir { get; set; } = ".";
    /// <summary>Where the .novadl temp lives (defaults next to the destination).</summary>
    public string? TempRoot { get; set; }
    public int MaxPeers { get; set; } = 12;
    /// <summary>Peer-wire listening port for inbound peers (null/0 = no listener).</summary>
    public int? ListenPort { get; set; }
    public bool Sequential { get; set; }
    /// <summary>Keep serving after completion (until session disposed).</summary>
    public bool SeedAfterComplete { get; set; }
    /// <summary>Upload cap bytes/sec while seeding (0 = unlimited).</summary>
    public long UploadCapBytes { get; set; }
    public bool EnableDht { get; set; } = true;
    public IReadOnlyList<string> Trackers { get; set; } = Array.Empty<string>();
    /// <summary>Directly-known peers (tests, LAN seeds, PEX-learned).</summary>
    public IReadOnlyList<TorrentPeer> StaticPeers { get; set; } = Array.Empty<TorrentPeer>();
    /// <summary>Overall deadline for magnet metadata acquisition.</summary>
    public TimeSpan MetadataTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public string PeerId { get; set; } = GeneratePeerId();

    public static string GeneratePeerId()
    {
        // Azureus-style: -NO1400- + 12 random
        var suffix = Random.Shared.NextInt64().ToString("x12").PadLeft(12, '0');
        return "-NO1400-" + suffix[..Math.Min(12, suffix.Length)];
    }
}

/// <summary>
/// One BitTorrent transfer: peer discovery (trackers HTTP/UDP + DHT + static),
/// peer pool, piece pipeline (16 KiB blocks), SHA-1 piece verification, crash-safe
/// resume via bitfield sidecar, optional seeding, and magnet metadata acquisition
/// via BEP-9 ut_metadata. All I/O is against the contiguous .novadl temp file;
/// multi-file torrents are split into per-file paths on completion.
/// </summary>
public sealed class TorrentSession : IAsyncDisposable
{
    private readonly TorrentSessionOptions _opt;
    private readonly byte[] _infoHash;
    private readonly byte[] _peerId;
    private readonly SpeedMeter _speed = new();
    private readonly object _sync = new();

    private DownloadDataFile? _data;
    private PiecePicker? _picker;
    private FileStream? _readView;                 // read view for seeding
    private Dictionary<int, long>? _pieceRemaining; // bytes still missing per piece
    private readonly Dictionary<(int Piece, int Begin), (int Len, DateTime Sent)> _inFlight = new();
    private readonly Dictionary<PeerWireClient, List<(int Piece, int Begin)>> _peerInFlight = new();
    private readonly List<PeerWireClient> _peers = new();
    private readonly HashSet<string> _triedPeers = new();
    private TcpListener? _listener;
    private DhtClient? _dht;
    private long _uploaded;
    private long _downloaded;
    private int _announcePort;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _announceLoop;
    private string? _bitfieldSidecarPath;

    public TorrentMeta? Meta { get; private set; }
    public string TempPath { get; private set; } = "";
    public string FinalPath { get; private set; } = "";
    public int PeerCount { get; private set; }
    public long Uploaded => _uploaded;
    public long Downloaded => _downloaded;
    public bool Completed { get; private set; }

    /// <summary>Fired when magnet metadata is acquired (before download starts).</summary>
    public event Action<TorrentMeta>? MetadataAcquired;
    public event Action<long /*delta*/, long /*rateBps*/>? Progress;
    public event Action<int>? PeerCountChanged;
    public event Action<string>? Log;

    public TorrentSession(TorrentMeta meta, TorrentSessionOptions opt)
    {
        Meta = meta;
        _infoHash = meta.InfoHash;
        _peerId = EncodingToBytes(opt.PeerId);
        _opt = opt;
    }

    private static byte[] EncodingToBytes(string s) =>
        System.Text.Encoding.UTF8.GetBytes(s.Length >= 20 ? s[..20] : s.PadRight(20, '0'));

    // ================================================================= download
    public async Task<string> DownloadAsync(CancellationToken ct)
    {
        var meta = Meta!;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        PreparePaths(meta, out var folder);
        _bitfieldSidecarPath = TempPath + ".bf";
        Directory.CreateDirectory(_opt.DestinationDir);
        Directory.CreateDirectory(folder);

        // preallocate + restore resume state
        _data = DownloadDataFile.OpenOrCreate(FinalPath, meta.TotalLength, _opt.TempRoot);
        _picker = new PiecePicker(meta.PieceCount, meta.PieceLength, meta.TotalLength)
        {
            Sequential = _opt.Sequential,
        };
        _pieceRemaining = new Dictionary<int, long>(meta.PieceCount);
        for (var i = 0; i < meta.PieceCount; i++)
            _pieceRemaining[i] = meta.PieceSize(i);
        RestoreBitfield();

        if (_picker.IsComplete)
        {
            Log?.Invoke($"resume: all {meta.PieceCount} pieces already verified");
            await FinishAsync(_cts.Token);
            return FinalPath;
        }

        // persist metadata sidecar (magnet resume + future seeding)
        var torrentSidecar = Path.Combine(folder, "metadata.torrent");
        await File.WriteAllBytesAsync(torrentSidecar, meta.RawBytes, ct);

        StartAnnouncer();
        StartListener();
        if (_opt.EnableDht) _ = RunDhtAsync(_cts.Token);
        ConnectStaticPeers();

        // main supervisory loop
        try
        {
            while (!_picker.IsComplete)
            {
                _cts.Token.ThrowIfCancellationRequested();
                PruneStaleRequests();
                MaintainPeerCount();
                await Task.Delay(200, _cts.Token);
            }
            await FinishAsync(_cts.Token);
            return FinalPath;
        }
        finally
        {
            if (!Completed) SaveBitfield();   // never re-create what FinishAsync removed
        }
    }

    private void PreparePaths(TorrentMeta meta, out string folder)
    {
        if (meta.IsSingleFile)
        {
            FinalPath = Path.GetFullPath(Path.Combine(_opt.DestinationDir, meta.Name));
            folder = Path.GetDirectoryName(FinalPath)!;
            TempPath = string.IsNullOrWhiteSpace(_opt.TempRoot)
                ? FinalPath + DownloadDataFile.TempSuffix
                : Path.Combine(Path.GetFullPath(_opt.TempRoot), Path.GetFileName(FinalPath) + DownloadDataFile.TempSuffix);
        }
        else
        {
            folder = Path.GetFullPath(Path.Combine(_opt.DestinationDir, meta.Name));
            FinalPath = folder;                        // completed = the folder with split files
            TempPath = string.IsNullOrWhiteSpace(_opt.TempRoot)
                ? folder + DownloadDataFile.TempSuffix
                : Path.Combine(Path.GetFullPath(_opt.TempRoot), meta.Name + DownloadDataFile.TempSuffix);
        }
    }

    private async Task FinishAsync(CancellationToken ct)
    {
        if (Completed) return;
        Completed = true;

        // release the write handle BEFORE moving the file (Windows-safe)
        if (_data is not null)
        {
            await _data.DisposeAsync();
            _data = null;
        }

        var meta = Meta!;
        if (meta.IsSingleFile)
        {
            var target = File.Exists(FinalPath)
                ? FilenamePolicy.MakeUniquePath(FinalPath)
                : FinalPath;
            File.Move(TempPath, target, overwrite: false);
            FinalPath = target;
        }
        else
        {
            await SplitMultiFileAsync(meta, ct);
            try { if (File.Exists(TempPath)) File.Delete(TempPath); }
            catch (IOException) { /* shared handle — retried on next run */ }
        }

        try { if (_bitfieldSidecarPath is not null && File.Exists(_bitfieldSidecarPath)) File.Delete(_bitfieldSidecarPath); }
        catch { /* best effort */ }

        if (_opt.SeedAfterComplete)
        {
            OpenReadView();
            Log?.Invoke("seeding enabled (task marked complete; session keeps serving)");
        }
    }

    private async Task SplitMultiFileAsync(TorrentMeta meta, CancellationToken ct)
    {
        // contiguous temp → per-file paths
        await using var src = new FileStream(TempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (var f in meta.Files)
        {
            var target = string.IsNullOrEmpty(f.SubPath)
                ? Path.Combine(FinalPath, meta.Name)
                : Path.Combine(FinalPath, f.SubPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            src.Seek(f.Offset, SeekOrigin.Begin);
            var remaining = f.Length;
            var buffer = new byte[1 << 16];
            while (remaining > 0)
            {
                var n = await src.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (n <= 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                remaining -= n;
            }
        }
    }

    // ================================================================= magnet metadata
    /// <summary>Fetches metadata for a magnet link via ut_metadata (BEP-9).</summary>
    public static async Task<TorrentMeta> FetchMetadataAsync(MagnetInfo magnet,
        TorrentSessionOptions opt, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(opt.MetadataTimeout + TimeSpan.FromSeconds(15));
        var deadline = DateTime.UtcNow + opt.MetadataTimeout;

        var infoHash = magnet.InfoHash;
        var peerId = EncodingToBytes(opt.PeerId);
        var peers = new List<TorrentPeer>(magnet.Trackers.Count > 0 ? 32 : 16);
        peers.AddRange(opt.StaticPeers);
        var dht = opt.EnableDht ? new DhtClient() : null;
        try
        {
            // gather peers continuously until deadline
            var gatherTask = Task.Run(async () =>
            {
                while (DateTime.UtcNow < deadline && !cts.Token.IsCancellationRequested && peers.Count < 16)
                {
                    foreach (var t in magnet.Trackers.Concat(opt.Trackers))
                        try
                        {
                            if (t.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
                            {
                                using var udp = new UdpTrackerClient();
                                var r = await udp.AnnounceAsync(t, infoHash, peerId, 6881, 0, 0, 1,
                                    TrackerEvent.Started, cts.Token);
                                lock (peers) peers.AddRange(r.Peers);
                            }
                            else
                            {
                                var r = await HttpTracker.AnnounceAsync(t, infoHash, peerId, 6881, 0, 0, 1,
                                    TrackerEvent.Started, cts.Token);
                                lock (peers) peers.AddRange(r.Peers);
                            }
                        }
                        catch (Exception) when (cts.Token.IsCancellationRequested is false) { /* tracker down */ }
                    if (dht is not null)
                        try
                        {
                            await dht.BootstrapAsync(Array.Empty<IPEndPoint>(), cts.Token);
                            var found = await dht.GetPeersAsync(infoHash, 8, cts.Token);
                            lock (peers) peers.AddRange(found);
                        }
                        catch (Exception) when (cts.Token.IsCancellationRequested is false) { /* DHT dry */ }
                    if (peers.Count == 0) await Task.Delay(2000, cts.Token);
                }
            }, cts.Token);

            // try each peer for ut_metadata
            while (DateTime.UtcNow < deadline)
            {
                cts.Token.ThrowIfCancellationRequested();
                TorrentPeer? candidate = null;
                lock (peers)
                {
                    if (peers.Count > 0)
                    {
                        candidate = peers[0];
                        peers.RemoveAt(0);
                    }
                }
                if (candidate is null) { await Task.Delay(300, cts.Token); continue; }
                try
                {
                    var meta = await FetchMetadataFromPeerAsync(candidate.Value, infoHash, peerId, magnet, cts.Token);
                    if (meta is not null) return meta;
                }
                catch (Exception) when (cts.Token.IsCancellationRequested is false) { /* peer failed */ }
            }
            throw new TimeoutException(
                $"magnet metadata not acquired within {opt.MetadataTimeout.TotalSeconds:F0}s " +
                $"(no peer offered ut_metadata)");
        }
        finally
        {
            dht?.Dispose();
        }
    }

    private static async Task<TorrentMeta?> FetchMetadataFromPeerAsync(TorrentPeer peer,
        byte[] infoHash, byte[] peerId, MagnetInfo magnet, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        try { tcp.Connect(peer.Address, peer.Port); }
        catch (Exception) { return null; }
        tcp.NoDelay = true;
        var wire = new PeerWireClient(tcp, infoHash, peerId);
        try
        {
            if (!await wire.HandshakeAsync(timeoutCts.Token)) return null;
            // wait for their BEP-10 extension handshake (comes right after ours)
            var waited = 0;
            while (wire.UtMetadataId == 0 || wire.PeerMetadataSize < 0)
            {
                if (waited > 8000) return null;
                var boot = await wire.ReadMessageAsync(timeoutCts.Token);
                if (boot is null) return null;
                waited += 50;
            }
            if (wire.PeerMetadataSize is <= 0 or > 4 * 1024 * 1024) return null;

            var size = wire.PeerMetadataSize;
            var info = new byte[size];
            var got = 0;
            for (var piece = 0; piece * UtMetadata.MetadataPiece < size; piece++)
            {
                await wire.SendMessageInternalAsync(PeerWireClient.MsgExtended, wire.UtMetadataId,
                    UtMetadata.BuildRequest(piece), timeoutCts.Token);
                // read until data for this piece arrives
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    var msg = await wire.ReadMessageAsync(timeoutCts.Token);
                    if (msg is null) return null;
                    if (msg.Value.Id == PeerWireClient.MsgExtended && msg.Value.ExtendedId == wire.UtMetadataId)
                    {
                        var data = UtMetadata.ParseData(msg.Value.Payload, piece);
                        if (data is null) return null; // reject
                        var off = piece * UtMetadata.MetadataPiece;
                        Array.Copy(data, 0, info, off, Math.Min(data.Length, size - off));
                        got += data.Length;
                        break;
                    }
                }
            }
            if (got < size) return null;
            if (!SHA1.HashData(info).AsSpan().SequenceEqual(infoHash)) return null;

            // build full metainfo: info + trackers from the magnet
            var root = BencValue.OfDict().Set("info", BencValue.Decode(info));
            var trackers = magnet.Trackers.Concat(
                magnet.Name is null ? Array.Empty<string>() : Array.Empty<string>()).ToList();
            if (trackers.Count > 0)
            {
                root.Set("announce", BencValue.Of(trackers[0]));
                if (trackers.Count > 1)
                    root.Set("announce-list",
                        BencValue.Of(trackers.Select(BencValue.Of).ToList()));
            }
            return TorrentMeta.Parse(root.SortKeys().Encode());
        }
        finally
        {
            await wire.DisposeAsync();
        }
    }

    // ================================================================= seeding
    /// <summary>Serves the torrent (already complete) until cancelled.</summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        if (!Completed && _picker is null)
        {
            // seeding an existing complete torrent (no .novadl temp involved)
            PreparePaths(Meta!, out _);
            _bitfieldSidecarPath = TempPath + ".bf";
            _picker = new PiecePicker(Meta!.PieceCount, Meta!.PieceLength, Meta!.TotalLength);
            for (var i = 0; i < Meta!.PieceCount; i++) _picker.MarkHave(i);
            Completed = true;
        }
        OpenReadView();
        if (_readView is null)
        {
            Log?.Invoke("seeding unavailable: no readable single-file data");
            return;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        StartAnnouncer();
        StartListener();
        if (_opt.EnableDht) _ = RunDhtAsync(_cts.Token);
        ConnectStaticPeers();
        try
        {
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException) { /* stopped */ }
    }

    private void OpenReadView()
    {
        if (_readView is not null) return;
        var path = File.Exists(TempPath) ? TempPath : FinalPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || Directory.Exists(path)) return;
        try
        {
            _readView = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
        }
        catch (IOException)
        {
            _readView = null;
        }
    }

    // ================================================================= networking
    private void StartListener()
    {
        if (_opt.ListenPort is not { } port) return;
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start(8);
            _announcePort = port;
            _acceptLoop = Task.Run(async () =>
            {
                while (_cts is { IsCancellationRequested: false })
                    try
                    {
                        var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                        _ = Task.Run(() => InboundAsync(tcp), _cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception) { /* transient */ }
            });
        }
        catch (Exception e)
        {
            Log?.Invoke($"listener failed on {port}: {e.Message}");
            _listener = null;
            _announcePort = 0;
        }
    }

    private async Task InboundAsync(TcpClient tcp)
    {
        PeerWireClient? wire = null;
        try
        {
            tcp.NoDelay = true;
            wire = await PeerWireClient.AcceptAsync(tcp, _infoHash, _peerId,
                Meta?.InfoDictBytes.Length ?? -1, _cts!.Token);
            wire.InitBitfield(Meta!.PieceCount);
            lock (_peers) if (_peers.Count >= _opt.MaxPeers + 4) return;
            await PeerPumpAsync(wire, inbound: true);
        }
        catch (Exception) { /* peer died */ }
        finally
        {
            RemovePeer(wire);
        }
    }

    private void ConnectStaticPeers()
    {
        foreach (var p in _opt.StaticPeers)
            _ = Task.Run(() => ConnectAsync(p), _cts!.Token);
    }

    private async Task ConnectAsync(TorrentPeer p)
    {
        var key = p.ToString()!;
        lock (_triedPeers)
        {
            if (_triedPeers.Contains(key)) return;
            _triedPeers.Add(key);
        }
        PeerWireClient? wire = null;
        try
        {
            wire = PeerWireClient.CreateConnected(p, _infoHash, _peerId);
            wire.MetadataSize = Meta?.InfoDictBytes.Length ?? -1;
            wire.InitBitfield(Meta!.PieceCount);
            await PeerPumpAsync(wire, inbound: false);
        }
        catch (Exception) { /* connect failed */ }
        finally
        {
            RemovePeer(wire);
        }
    }

    private void RemovePeer(PeerWireClient? wire)
    {
        if (wire is null) return;
        bool removed;
        lock (_peers)
        {
            removed = _peers.Remove(wire);
            if (removed && wire.PeerBitfield is not null) _picker?.RemoveAvailability(wire.PeerBitfield);
        }
        // release anything this peer had in flight so others can retry
        if (removed)
        {
            lock (_peerInFlight)
            {
                if (_peerInFlight.Remove(wire, out var blocks))
                    foreach (var (piece, begin) in blocks)
                    {
                        lock (_inFlight) _inFlight.Remove((piece, begin));
                        _picker?.ReleaseBlock(piece, begin);
                    }
            }
            PeerCount = _peers.Count;
            PeerCountChanged?.Invoke(PeerCount);
        }
        _ = wire.DisposeAsync();
    }

    /// <summary>Reads the peer socket in the background and buffers messages into a
    /// channel — cancelling a channel read NEVER aborts the underlying TCP stream.</summary>
    private static async Task ReadIntoChannelAsync(PeerWireClient wire,
        System.Threading.Channels.Channel<PeerWireClient.PeerMessage?> channel, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await wire.ReadMessageAsync(ct);
                await channel.Writer.WriteAsync(msg, CancellationToken.None);
                if (msg is null) break;
            }
        }
        catch (OperationCanceledException) { /* session ending */ }
        catch (Exception) { channel.Writer.TryWrite(null); }
        channel.Writer.TryComplete();
    }

    private async Task PeerPumpAsync(PeerWireClient wire, bool inbound)
    {
        var ct = _cts!.Token;
        if (!inbound && !await wire.HandshakeAsync(ct)) return;
        lock (_peers)
        {
            if (_peers.Count >= _opt.MaxPeers + 4) return;
            _peers.Add(wire);
        }
        PeerCount = _peers.Count;
        PeerCountChanged?.Invoke(PeerCount);

        // announce what we have, declare interest when incomplete
        var ourBits = _picker!.Snapshot();
        await wire.SendBitfieldAsync(ourBits, ct);
        if (Completed || _picker.IsComplete)
            await wire.SendNotInterestedAsync(ct);
        else
            await wire.SendInterestedAsync(ct);

        if (Completed || _picker.IsComplete)
        {
            await wire.SendUnchokeAsync(ct);   // seed mode: unchoke everyone
            await SeedLoopAsync(wire, ct);
            return;
        }

        var channel = System.Threading.Channels.Channel.CreateUnbounded<PeerWireClient.PeerMessage?>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        var reader = Task.Run(() => ReadIntoChannelAsync(wire, channel, ct), ct);
        var lastKeepalive = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested && !_picker.IsComplete)
            {
                // top up the request pipeline for this peer
                var topups = 0;
                while (!wire.AmChoked && topups < 6)
                {
                    var pick = _picker.PickBlock(wire.PeerBitfield ?? EmptyBits);
                    if (pick is not { } blk) break;
                    lock (_inFlight) _inFlight[(blk.Piece, blk.Begin)] =
                        (blk.Length, DateTime.UtcNow.AddSeconds(60));
                    lock (_peerInFlight)
                    {
                        if (!_peerInFlight.TryGetValue(wire, out var list))
                            _peerInFlight[wire] = list = new List<(int, int)>();
                        list.Add((blk.Piece, blk.Begin));
                    }
                    await wire.SendRequestAsync(blk.Piece, blk.Begin, blk.Length, ct);
                    topups++;
                }

                // wait for the next message with a soft deadline (cancel-safe)
                PeerWireClient.PeerMessage? msg;
                using (var soft = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    soft.CancelAfter(1500);
                    try
                    {
                        var has = await channel.Reader.WaitToReadAsync(soft.Token);
                        msg = has && channel.Reader.TryRead(out var m) ? m : null;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        msg = null;                       // deadline → keepalive slot
                    }
                }
                if (channel.Reader.Completion.IsCompleted && !channel.Reader.TryPeek(out _))
                {
                    break;                                // peer gone
                }
                if (msg is null)
                {
                    if (DateTime.UtcNow - lastKeepalive > TimeSpan.FromSeconds(45))
                    {
                        await wire.SendKeepaliveAsync(ct);
                        lastKeepalive = DateTime.UtcNow;
                    }
                    continue;
                }

                switch (msg.Value.Id)
                {
                    case PeerWireClient.MsgChoke:
                        // our outstanding requests stay; timeouts + peer-removal release them
                        break;
                    case PeerWireClient.MsgHave:
                        if (msg.Value.Payload.Length >= 4)
                            _picker.AddHave(BinaryPrimitives.ReadInt32BigEndian(msg.Value.Payload));
                        break;
                    case PeerWireClient.MsgBitfield:
                        if (wire.PeerBitfield is { } bits) _picker.AddAvailability(bits);
                        break;
                    case PeerWireClient.MsgPiece:
                        await OnBlockAsync(wire, msg.Value, ct);
                        break;
                    case PeerWireClient.MsgRequest:
                        await ServeRequestAsync(wire, msg.Value, ct);
                        break;
                    case PeerWireClient.MsgExtended:
                        await TryServeUtMetadataAsync(wire, msg.Value, ct);
                        break;
                    case 255: // keepalive
                        break;
                }
            }
        }
        finally
        {
            try { await reader; } catch { /* socket teardown raced */ }
        }
    }

    private static readonly bool[] EmptyBits = Array.Empty<bool>();

    private async Task SeedLoopAsync(PeerWireClient wire, CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<PeerWireClient.PeerMessage?>();
        var reader = Task.Run(() => ReadIntoChannelAsync(wire, channel, ct), ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PeerWireClient.PeerMessage? msg;
                try
                {
                    if (!await channel.Reader.WaitToReadAsync(ct)) break;
                    msg = channel.Reader.TryRead(out var m) ? m : null;
                }
                catch (OperationCanceledException) { break; }
                if (msg is null) break;
                if (msg.Value.Id == PeerWireClient.MsgRequest)
                    await ServeRequestAsync(wire, msg.Value, ct);
                else if (msg.Value.Id == PeerWireClient.MsgExtended)
                    await TryServeUtMetadataAsync(wire, msg.Value, ct);
            }
        }
        finally
        {
            try { await reader; } catch { /* socket teardown raced */ }
        }
    }

    /// <summary>Answers BEP-9 ut_metadata requests from magnet-only peers when we own the info dict.</summary>
    private async Task TryServeUtMetadataAsync(PeerWireClient wire, PeerWireClient.PeerMessage msg,
        CancellationToken ct)
    {
        if (msg.ExtendedId == 0) return;
        if (!wire.PeerExtensions.TryGetValue("ut_metadata", out var utm) || msg.ExtendedId != utm) return;
        var info = Meta?.InfoDictBytes;
        if (info is null || info.Length == 0) return;
        try
        {
            var dict = BencValue.Decode(msg.Payload);
            if (dict.Get("msg_type")?.AsInteger != 0) return;   // only requests
            var piece = (int)(dict.Get("piece")?.AsInteger ?? -1);
            if (piece < 0 || piece * UtMetadata.MetadataPiece >= info.Length) return;
            await wire.SendMessageInternalAsync(PeerWireClient.MsgExtended, utm,
                UtMetadata.BuildData(piece, info, info.Length), ct);
        }
        catch (InvalidDataException) { /* junk request */ }
    }

    private async Task ServeRequestAsync(PeerWireClient wire, PeerWireClient.PeerMessage msg,
        CancellationToken ct)
    {
        var index = msg.PieceIndex;
        var begin = msg.BlockBegin;
        var length = msg.RequestLength;    // REQUEST payload = [index][begin][length]
        if (length <= 0 || length > 256 * 1024) return;
        var meta = Meta!;
        if (index < 0 || index >= meta.PieceCount) return;
        var pieceLen = meta.PieceSize(index);
        if (begin < 0 || begin + length > pieceLen) return;
        if (_picker is { } && !_picker[index]) return;   // don't serve unverified data

        var offset = meta.PieceOffset(index) + begin;
        var buffer = new byte[length];
        var view = _readView;
        if (view is null) return;
        lock (view)
        {
            view.Seek(offset, SeekOrigin.Begin);
            var got = 0;
            while (got < length)
            {
                var n = view.Read(buffer, got, length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < length) return;
        }
        await ApplyUploadCapAsync(length, ct);
        await wire.SendPieceAsync(index, begin, buffer, ct);
        _uploaded += length;
    }

    private long _uploadBudget;
    private DateTime _budgetWindow = DateTime.UtcNow;

    private async Task ApplyUploadCapAsync(int bytes, CancellationToken ct)
    {
        if (_opt.UploadCapBytes <= 0) return;
        while (true)
        {
            lock (this)
            {
                var now = DateTime.UtcNow;
                if ((now - _budgetWindow).TotalSeconds >= 1)
                {
                    _budgetWindow = now;
                    _uploadBudget = _opt.UploadCapBytes;
                }
                if (_uploadBudget >= bytes) { _uploadBudget -= bytes; return; }
            }
            await Task.Delay(100, ct);
        }
    }

    /// <summary>One block (≤16 KiB) arrived: persist it, then verify the piece when full.</summary>
    private async Task OnBlockAsync(PeerWireClient wire, PeerWireClient.PeerMessage msg,
        CancellationToken ct)
    {
        var meta = Meta!;
        var index = msg.PieceIndex;
        var begin = msg.BlockBegin;
        var block = msg.Block.ToArray();

        if (index < 0 || index >= meta.PieceCount) return;
        lock (_inFlight) _inFlight.Remove((index, begin));
        lock (_peerInFlight)
            if (_peerInFlight.TryGetValue(wire, out var list))
                list.Remove((index, begin));
        // NOTE: the block stays "pending" in the picker until its PIECE is verified —
        // releasing on arrival would let the picker re-request it (duplicate traffic).

        var pieceLen = (int)meta.PieceSize(index);
        if (begin < 0 || begin + block.Length > pieceLen) return;

        lock (_sync)
        {
            if (_pieceRemaining is not null &&
                _pieceRemaining.TryGetValue(index, out var left))
                _pieceRemaining[index] = left - block.Length;
        }

        if (_data is null) return;
        await _data.WriteAsync(meta.PieceOffset(index) + begin, block, ct);
        _downloaded += block.Length;
        _speed.Add(block.Length);
        Progress?.Invoke(block.Length, _speed.SpeedBps);

        bool pieceDone;
        lock (_sync)
            pieceDone = _pieceRemaining is not null &&
                        _pieceRemaining.TryGetValue(index, out var left2) && left2 <= 0;
        if (!pieceDone) return;

        // read back + verify SHA-1 (only verified pieces count toward resume state)
        var piece = new byte[pieceLen];
        lock (_sync)
        {
            var view = EnsureReadViewLocked();
            if (view is null)
            {
                Log?.Invoke($"piece {index}: no read view — will retry");
                return;
            }
            view.Seek(meta.PieceOffset(index), SeekOrigin.Begin);
            var got = 0;
            while (got < pieceLen)
            {
                var n = view.Read(piece, got, pieceLen - got);
                if (n <= 0) break;
                got += n;
            }
            if (got < pieceLen)
            {
                Log?.Invoke($"piece {index}: short read-back, will retry");
                lock (_pieceRemaining!) _pieceRemaining[index] = pieceLen;
                return;
            }
        }
        if (!SHA1.HashData(piece).AsSpan().SequenceEqual(meta.PieceHashes[index]))
        {
            Log?.Invoke($"piece {index}: hash mismatch — requeueing");
            lock (_pieceRemaining!) _pieceRemaining[index] = pieceLen;
            _picker?.ReleasePiece(index);
            return;
        }

        _picker!.MarkHave(index);
        _picker.ReleasePiece(index);
        SaveBitfield();
        var haveMsg = index;
        List<PeerWireClient> peers;
        lock (_peers) peers = _peers.ToList();
        foreach (var p in peers)
            try { await p.SendHaveAsync(haveMsg, ct); } catch { /* peer gone */ }
    }

    private FileStream? EnsureReadViewLocked()
    {
        if (_readView is not null) return _readView;
        try
        {
            _readView = new FileStream(TempPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            return _readView;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void MaintainPeerCount()
    {
        // connect to known-but-untried peers from discovery
        lock (_peers)
        {
            if (_peers.Count >= _opt.MaxPeers) return;
        }
        var candidates = DrainDiscoveredPeers(_opt.MaxPeers);
        foreach (var c in candidates)
            _ = Task.Run(() => ConnectAsync(c), _cts!.Token);
    }

    private readonly Queue<TorrentPeer> _discovered = new();

    private List<TorrentPeer> DrainDiscoveredPeers(int max)
    {
        var list = new List<TorrentPeer>();
        lock (_discovered)
            while (list.Count < max && _discovered.Count > 0)
                list.Add(_discovered.Dequeue());
        return list;
    }

    private void PruneStaleRequests()
    {
        var now = DateTime.UtcNow;
        lock (_inFlight)
        {
            var stale = _inFlight.Where(kv => now > kv.Value.Sent).ToList();
            foreach (var kv in stale)
            {
                _inFlight.Remove(kv.Key);
                _picker?.ReleaseBlock(kv.Key.Piece, kv.Key.Begin);
            }
        }
    }

    // ================================================================= discovery
    private void StartAnnouncer()
    {
        _announceLoop = Task.Run(async () =>
        {
            var token = _cts!.Token;
            var intervals = new Dictionary<string, int>();
            while (!token.IsCancellationRequested)
            {
                var meta = Meta!;
                foreach (var t in meta.Trackers.Concat(_opt.Trackers).Distinct())
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        TrackerPeers result;
                        if (t.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
                        {
                            using var udp = new UdpTrackerClient();
                            result = await udp.AnnounceAsync(t, meta.InfoHash, _peerId,
                                _announcePort > 0 ? _announcePort : 6881, _uploaded, _downloaded,
                                _picker?.RemainingBytes(meta.PieceSize) ?? 0,
                                Completed ? TrackerEvent.Completed : TrackerEvent.Started, token);
                        }
                        else
                        {
                            result = await HttpTracker.AnnounceAsync(t, meta.InfoHash, _peerId,
                                _announcePort > 0 ? _announcePort : 6881, _uploaded, _downloaded,
                                _picker?.RemainingBytes(meta.PieceSize) ?? 0,
                                Completed ? TrackerEvent.Completed : TrackerEvent.Started, token);
                        }
                        if (result.IsFailure)
                        {
                            Log?.Invoke($"tracker {Host(t)}: {result.FailureReason}");
                            continue;
                        }
                        intervals[t] = Math.Max(result.IntervalSeconds, 30);
                        lock (_discovered)
                            foreach (var p in result.Peers)
                                _discovered.Enqueue(p);
                        MaintainPeerCount();
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        Log?.Invoke($"tracker {Host(t)}: {e.Message}");
                    }
                }
                var wait = intervals.Count == 0 ? 60 : intervals.Values.Min();
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 30, 1800)), token); }
                catch (OperationCanceledException) { break; }
            }
        }, _cts!.Token);
    }

    private static string Host(string url)
    {
        try { return new Uri(url).Host; }
        catch (UriFormatException) { return url; }
    }

    private async Task RunDhtAsync(CancellationToken ct)
    {
        try
        {
            _dht = new DhtClient();
            await _dht.BootstrapAsync(Array.Empty<IPEndPoint>(), ct);
            var peers = await _dht.GetPeersAsync(_infoHash, 16, ct);
            lock (_discovered)
                foreach (var p in peers) _discovered.Enqueue(p);
            MaintainPeerCount();
        }
        catch (Exception) when (ct.IsCancellationRequested is false) { /* DHT unavailable */ }
    }

    // ================================================================= bitfield sidecar
    private void RestoreBitfield()
    {
        var meta = Meta!;
        try
        {
            if (!File.Exists(_bitfieldSidecarPath)) return;
            var raw = File.ReadAllBytes(_bitfieldSidecarPath);
            if (raw.Length < 4) return;
            var count = BinaryPrimitives.ReadInt32BigEndian(raw);
            if (count != meta.PieceCount) return;         // different torrent
            var bits = new bool[count];
            for (var i = 0; i < count && (4 + i / 8) < raw.Length; i++)
                bits[i] = (raw[4 + i / 8] & (0x80 >> (i % 8))) != 0;
            _picker!.RestoreFrom(bits);
            lock (_pieceRemaining!)
                for (var i = 0; i < count; i++)
                    if (bits[i]) _pieceRemaining[i] = 0;
            Log?.Invoke($"resume: {bits.Count(b => b)}/{count} pieces verified");
        }
        catch (Exception) { /* corrupt sidecar → fresh */ }
    }

    private DateTime _lastBfSave = DateTime.MinValue;

    private void SaveBitfield()
    {
        if (_picker is null) return;
        var now = DateTime.UtcNow;
        if ((now - _lastBfSave).TotalMilliseconds < 500 && !_picker.IsComplete) return;
        _lastBfSave = now;
        try
        {
            var bits = _picker.Snapshot();
            var raw = new byte[4 + (bits.Length + 7) / 8];
            BinaryPrimitives.WriteInt32BigEndian(raw, bits.Length);
            for (var i = 0; i < bits.Length; i++)
                if (bits[i]) raw[4 + i / 8] |= (byte)(0x80 >> (i % 8));
            File.WriteAllBytes(_bitfieldSidecarPath!, raw);
        }
        catch (Exception) { /* disk hiccup — retried on next piece */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        try { _listener?.Stop(); } catch { /* already stopped */ }
        _dht?.Dispose();
        try { _readView?.Dispose(); } catch { /* closed */ }
        await (_data?.DisposeAsync() ?? ValueTask.CompletedTask);
        _data = null;
        SaveBitfield();
    }
}
