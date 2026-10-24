using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;

namespace Nova.Engine.Torrent;

/// <summary>Announce event codes (BEP-3).</summary>
public enum TrackerEvent
{
    Started = 0,
    Stopped = 1,
    Completed = 2,
    Empty = 3,          // periodic re-announce
}

public readonly record struct TrackerPeers(IReadOnlyList<TorrentPeer> Peers, int IntervalSeconds,
    int Complete, int Incomplete, string? FailureReason)
{
    public bool IsFailure => FailureReason is { Length: > 0 };
}

/// <summary>
/// HTTP(S) tracker client (BEP-3): GET announce with info_hash, peer_id, port, uploaded,
/// downloaded, left, compact=1&event=…; parses compact (6 bytes/peer) and dictionary forms.
/// </summary>
public static class HttpTracker
{
    private static readonly HttpClient Http = SharedHttp();

    private static HttpClient SharedHttp()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public static async Task<TrackerPeers> AnnounceAsync(string announceUrl, byte[] infoHash,
        byte[] peerId, int listenPort, long uploaded, long downloaded, long left,
        TrackerEvent ev, CancellationToken ct, string? key = null)
    {
        var q = new StringBuilder(announceUrl);
        q.Append(announceUrl.Contains('?') ? '&' : '?')
          .Append("info_hash=").Append(PercentEncode(infoHash))
          .Append("&peer_id=").Append(PercentEncode(peerId))
          .Append("&port=").Append(listenPort)
          .Append("&uploaded=").Append(uploaded)
          .Append("&downloaded=").Append(downloaded)
          .Append("&left=").Append(left)
          .Append("&compact=1&no_peer_id=1&numwant=50");
        if (ev != TrackerEvent.Empty) q.Append("&event=").Append(EventName(ev));
        if (key is not null) q.Append("&key=").Append(Uri.EscapeDataString(key));

        using var resp = await Http.GetAsync(q.ToString(), ct);
        var body = await resp.Content.ReadAsByteArrayAsync(ct);   // binary compact peers!
        if (!resp.IsSuccessStatusCode)
            return new TrackerPeers(Array.Empty<TorrentPeer>(), 0, 0, 0,
                $"HTTP {(int)resp.StatusCode} from tracker");
        return ParseResponse(body);
    }

    private static string EventName(TrackerEvent ev) => ev switch
    {
        TrackerEvent.Started => "started",
        TrackerEvent.Stopped => "stopped",
        TrackerEvent.Completed => "completed",
        _ => "",
    };

    public static TrackerPeers ParseResponse(byte[] body)
    {
        BencValue root;
        try { root = BencValue.Decode(body); }
        catch (InvalidDataException e) { return Fail(e.Message); }

        if (root.Get("failure reason") is { } fr)
            return Fail(fr.AsUtf8);

        var peers = new List<TorrentPeer>();
        if (root.Get("peers") is { } peersVal)
        {
            if (peersVal.Type == BencValue.Kind.String)
            {
                // compact: 6 bytes per peer (IPv4)
                var compact = peersVal.AsString;
                for (var i = 0; i + 6 <= compact.Length; i += 6)
                    peers.Add(TorrentPeer.ParseCompact(compact.AsSpan(i)));
            }
            else if (peersVal.Type == BencValue.Kind.List)
            {
                foreach (var p in peersVal.AsList)
                {
                    var ip = p.Get("ip")?.AsUtf8;
                    var port = (int)(p.Get("port")?.AsInteger ?? 0);
                    if (!string.IsNullOrEmpty(ip) && port > 0 && IPAddress.TryParse(ip, out var addr))
                        peers.Add(new TorrentPeer(addr, port));
                }
            }
        }
        var interval = (int)(root.Get("interval")?.AsInteger ?? 1800);
        var complete = (int)(root.Get("complete")?.AsInteger ?? 0);
        var incomplete = (int)(root.Get("incomplete")?.AsInteger ?? 0);
        return new TrackerPeers(peers, interval, complete, incomplete, null);

        static TrackerPeers Fail(string reason) =>
            new(Array.Empty<TorrentPeer>(), 0, 0, 0, reason);
    }

    /// <summary>BEP-3 percent-encoding: info_hash/peer_id are raw bytes, not UTF-8 text.</summary>
    public static string PercentEncode(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 3);
        foreach (var b in data)
        {
            if (b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'A' and <= (byte)'Z' or (byte)'-' or (byte)'.' or (byte)'_'
                or (byte)'~')
                sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}

/// <summary>UDP scrape reply for one info-hash (BEP-15 action=2).</summary>
public readonly record struct TrackerScrape(int Seeders, int Completed, int Leechers);

/// <summary>
/// UDP tracker client (BEP-15): connection handshake + announce, both with 32-bit
/// transaction IDs. Retries on timeout; connection ids are cached per endpoint.
/// v1.4.1 adds scrape (action=2) for swarm health without joining it.
/// </summary>
public sealed class UdpTrackerClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Dictionary<EndPoint, (long ConnectionId, DateTime ExpiresAt)> _connections = new();
    private readonly object _lock = new();
    private int _txnCounter = Random.Shared.Next(1, int.MaxValue - 100000);

    public UdpTrackerClient() : this(0) { }

    public UdpTrackerClient(int bindPort)
    {
        _udp = bindPort > 0 ? new UdpClient(bindPort) : new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.ReceiveTimeout = 5000;
        _udp.Client.SendTimeout = 5000;
    }

    public async Task<TrackerPeers> AnnounceAsync(string announceUrl, byte[] infoHash,
        byte[] peerId, int listenPort, long uploaded, long downloaded, long left,
        TrackerEvent ev, CancellationToken ct, string? key = null)
    {
        // udp://host:port/announce → endpoint
        if (!announceUrl.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("not a udp announce url", nameof(announceUrl));
        var hostPort = announceUrl[6..].Split('/', 2)[0];
        var colon = hostPort.LastIndexOf(':');
        if (colon < 0) throw new InvalidDataException($"udp tracker missing port: {announceUrl}");
        var host = hostPort[..colon];
        var port = int.Parse(hostPort[(colon + 1)..]);
        var endpoint = new IPEndPoint(IPAddress.Parse(host), port);
        return await AnnounceAsync(endpoint, infoHash, peerId, listenPort, uploaded,
            downloaded, left, ev, ct);
    }

    public async Task<TrackerPeers> AnnounceAsync(IPEndPoint tracker, byte[] infoHash,
        byte[] peerId, int listenPort, long uploaded, long downloaded, long left,
        TrackerEvent ev, CancellationToken ct)
    {
        var connId = await GetConnectionIdAsync(tracker, ct);
        try
        {
            return await AnnounceWithAsync(connId, tracker, infoHash, peerId, listenPort,
                uploaded, downloaded, left, ev, ct);
        }
        catch (TimeoutException)
        {
            // stale connection id is the most common cause — reconnect once
            lock (_lock) _connections.Remove(tracker);
            connId = await GetConnectionIdAsync(tracker, ct);
            return await AnnounceWithAsync(connId, tracker, infoHash, peerId, listenPort,
                uploaded, downloaded, left, ev, ct);
        }
    }

    private async Task<long> GetConnectionIdAsync(IPEndPoint tracker, CancellationToken ct)
    {
        lock (_lock)
            if (_connections.TryGetValue(tracker, out var c) && c.ExpiresAt > DateTime.UtcNow)
                return c.ConnectionId;

        var txn = NextTxn();
        var packet = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(packet, 0x41727101980L);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), 0); // action 0 = connect
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), txn);
        var reply = await SendReceiveAsync(tracker, packet, 16, txn, ct);
        if (reply.Length < 16)
            throw new InvalidDataException("udp tracker: short connect reply");
        var action = BinaryPrimitives.ReadInt32BigEndian(reply);
        var replyTxn = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(4));
        if (action != 0 || replyTxn != txn)
            throw new InvalidDataException($"udp tracker: bad connect reply action={action}");
        var connId = BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(8));
        lock (_lock) _connections[tracker] = (connId, DateTime.UtcNow.AddMinutes(55));
        return connId;
    }

    private async Task<TrackerPeers> AnnounceWithAsync(long connId, IPEndPoint tracker,
        byte[] infoHash, byte[] peerId, int listenPort, long uploaded, long downloaded,
        long left, TrackerEvent ev, CancellationToken ct)
    {
        var txn = NextTxn();
        var packet = new byte[98];
        BinaryPrimitives.WriteInt64BigEndian(packet, connId);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), 1); // action 1 = announce
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), txn);
        infoHash.CopyTo(packet, 16);
        peerId.CopyTo(packet, 36);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(56), downloaded);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(64), left);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(72), uploaded);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(80), ev switch
        {
            TrackerEvent.Started => 2,
            TrackerEvent.Stopped => 3,
            TrackerEvent.Completed => 1,
            _ => 0,
        });
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(84), listenPort);
        // key(88), num_want(92) = -1
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(92), -1);
        packet[96] = 0; packet[97] = 0; // extensions

        var reply = await SendReceiveAsync(tracker, packet, 98, txn, ct);
        if (reply.Length < 20) throw new InvalidDataException("udp tracker: short announce reply");
        var action = BinaryPrimitives.ReadInt32BigEndian(reply);
        var replyTxn = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(4));
        if (replyTxn != txn) throw new InvalidDataException("udp tracker: txn mismatch");
        if (action == 3)
        {
            var msg = Encoding.UTF8.GetString(reply, 8, reply.Length - 8).TrimEnd('\0');
            return new TrackerPeers(Array.Empty<TorrentPeer>(), 0, 0, 0, msg);
        }
        if (action != 1) throw new InvalidDataException($"udp tracker: action {action} reply");

        var interval = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(8));
        var leechers = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(12));
        var seeders = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(16));
        var peers = new List<TorrentPeer>();
        for (var i = 20; i + 6 <= reply.Length; i += 6)
            peers.Add(TorrentPeer.ParseCompact(reply.AsSpan(i)));
        return new TrackerPeers(peers, interval, seeders, leechers, null);
    }

    // ------------------------------------------------------------------ scrape (v1.4.1)
    /// <summary>Scrapes swarm stats for one or more info-hashes (BEP-15 action=2).
    /// udp://host:port/announce → the scrape endpoint is the same host:port.</summary>
    public async Task<IReadOnlyList<TrackerScrape>> ScrapeAsync(string announceUrl,
        IReadOnlyList<byte[]> infoHashes, CancellationToken ct)
    {
        if (!announceUrl.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("not a udp tracker url", nameof(announceUrl));
        var hostPort = announceUrl[6..].Split('/', 2)[0];
        var colon = hostPort.LastIndexOf(':');
        if (colon < 0) throw new InvalidDataException($"udp tracker missing port: {announceUrl}");
        var endpoint = new IPEndPoint(IPAddress.Parse(hostPort[..colon]),
            int.Parse(hostPort[(colon + 1)..]));
        return await ScrapeAsync(endpoint, infoHashes, ct);
    }

    public async Task<IReadOnlyList<TrackerScrape>> ScrapeAsync(IPEndPoint tracker,
        IReadOnlyList<byte[]> infoHashes, CancellationToken ct)
    {
        if (infoHashes.Count == 0 || infoHashes.Count > 74)   // BEP-15: ≤74 fits one datagram
            throw new ArgumentException("scrape takes 1..74 info-hashes", nameof(infoHashes));

        var connId = await GetConnectionIdAsync(tracker, ct);
        try { return await ScrapeWithAsync(connId, tracker, infoHashes, ct); }
        catch (TimeoutException)
        {
            lock (_lock) _connections.Remove(tracker);
            connId = await GetConnectionIdAsync(tracker, ct);
            return await ScrapeWithAsync(connId, tracker, infoHashes, ct);
        }
    }

    private async Task<IReadOnlyList<TrackerScrape>> ScrapeWithAsync(long connId,
        IPEndPoint tracker, IReadOnlyList<byte[]> infoHashes, CancellationToken ct)
    {
        var txn = NextTxn();
        var packet = new byte[16 + 20 * infoHashes.Count];
        BinaryPrimitives.WriteInt64BigEndian(packet, connId);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), 2);   // action 2 = scrape
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), txn);
        for (var i = 0; i < infoHashes.Count; i++)
            infoHashes[i].CopyTo(packet, 16 + 20 * i);

        var reply = await SendReceiveAsync(tracker, packet, 8 + 12 * infoHashes.Count, txn, ct);
        var action = BinaryPrimitives.ReadInt32BigEndian(reply);
        var replyTxn = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(4));
        if (replyTxn != txn) throw new InvalidDataException("udp tracker: txn mismatch");
        if (action == 3)
        {
            var msg = Encoding.UTF8.GetString(reply, 8, Math.Max(0, reply.Length - 8)).TrimEnd('\0');
            throw new InvalidDataException($"udp tracker scrape failed: {msg}");
        }
        if (action != 2) throw new InvalidDataException($"udp tracker: action {action} reply");

        var result = new List<TrackerScrape>(infoHashes.Count);
        for (var i = 0; i < infoHashes.Count; i++)
        {
            var off = 8 + 12 * i;
            if (off + 12 > reply.Length) break;
            result.Add(new TrackerScrape(
                Seeders: (int)BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(off)),
                Completed: (int)BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(off + 4)),
                Leechers: (int)BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(off + 8))));
        }
        return result;
    }

    private async Task<byte[]> SendReceiveAsync(IPEndPoint tracker, byte[] packet,
        int expectedMin, int txn, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _udp.Client.SendTo(packet, SocketFlags.None, tracker);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (_udp.Available > 0)
                {
                    var result = await _udp.ReceiveAsync(ct);
                    return result.Buffer;
                }
                await Task.Delay(30, ct);
            }
        }
        throw new TimeoutException("udp tracker: no reply within 10s");
    }

    private int NextTxn() => Interlocked.Increment(ref _txnCounter);

    public void Dispose()
    {
        _udp.Dispose();
    }
}
