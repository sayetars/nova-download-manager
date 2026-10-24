using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Nova.Engine.Torrent;

/// <summary>
/// DHT node (BEP-5): KRPC over UDP (bencoded queries/responses), a Kademlia-style
/// routing table (160-bit id space, 8-node k-buckets), get_peers lookups that
/// converge on the closest nodes to the info-hash, and announce_peer so others find us.
/// This is a compact but real implementation: node-id = SHA-1 of the UDP socket's
/// local endpoint + random nonce (re-read per boot, standard practice).
/// </summary>
public sealed class DhtClient : IDisposable
{
    public const int BucketSize = 8;          // k
    public const int IdLength = 20;           // bytes
    private const int QueryTimeoutMs = 4000;

    private readonly UdpClient _udp;
    private readonly object _lock = new();
    private readonly List<Bucket> _buckets = new();   // 160 buckets, lazily created
    private readonly Dictionary<byte[], string> _tokens = new(new ByteArrayComparer());
    private readonly Dictionary<string, PendingQuery> _pending = new();
    /// <summary>v1.4.1: announced peers per info-hash (BEP-5 serving) — hex → (compact, expiry).</summary>
    private readonly Dictionary<string, List<(byte[] Compact, DateTime ExpiresAt)>> _peerStore = new();
    private const int PeerStoreMaxPerHash = 64;
    private static readonly TimeSpan PeerStoreTtl = TimeSpan.FromMinutes(30);
    private int _txnCounter = Random.Shared.Next(0, int.MaxValue / 2);
    private Task? _receiveLoop;
    private CancellationTokenSource? _cts;

    public byte[] NodeId { get; }
    public int KnownNodes
    {
        get { lock (_lock) return _buckets.Sum(b => b.Nodes.Count); }
    }

    private sealed class Bucket
    {
        public readonly List<DhtNodeInfo> Nodes = new();
    }

    private sealed record PendingQuery(byte[] Txn, IPEndPoint From, TaskCompletionSource<BencValue> Tcs,
        DateTime ExpiresAt);

    public sealed record DhtNodeInfo(byte[] Id, IPEndPoint Endpoint, DateTime LastSeen)
    {
        public bool IsGood => DateTime.UtcNow - LastSeen < TimeSpan.FromMinutes(15);
    }

    public DhtClient(int bindPort = 0)
    {
        // ALWAYS bind — LocalPort must be real (unbound IPv4 sockets report 0 and
        // later SendTo calls to a port-0 endpoint fail with EINVAL)
        _udp = new UdpClient(bindPort);
        _udp.Client.ReceiveTimeout = 1000;
        // stable-ish node id
        var seed = $"{Dns.GetHostName()}:{_udp.Client.LocalEndPoint}:{Random.Shared.NextInt64()}";
        NodeId = SHA1.HashData(Encoding.UTF8.GetBytes(seed));
        for (var i = 0; i < 160; i++) _buckets.Add(new Bucket());
    }

    public int LocalPort => ((IPEndPoint?)_udp.Client.LocalEndPoint)?.Port ?? 0;

    /// <summary>Starts the KRPC receive loop without bootstrapping (tests, LAN setups).</summary>
    public void StartListening() => StartReceiveLoop();

    // ------------------------------------------------------------------ public API
    public async Task BootstrapAsync(IReadOnlyList<IPEndPoint> routers, CancellationToken ct)
    {
        StartReceiveLoop();
        var list = routers.Count > 0 ? routers : await ResolveRoutersAsync(ct);
        foreach (var r in list)
        {
            try { await PingAsync(r, ct); }
            catch (Exception) when (ct.IsCancellationRequested is false) { /* router down — skip */ }
        }
    }

    private static async Task<List<IPEndPoint>> ResolveRoutersAsync(CancellationToken ct)
    {
        var hosts = new[]
        {
            ("router.bittorrent.com", 6881),
            ("dht.transmissionbt.com", 6881),
            ("router.bitcomet.com", 6881),
        };
        var result = new List<IPEndPoint>();
        foreach (var (host, port) in hosts)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct);
                if (addrs is { Length: > 0 }) result.Add(new IPEndPoint(addrs[0], port));
            }
            catch (Exception) when (ct.IsCancellationRequested is false) { /* DNS fail */ }
        }
        return result;
    }

    /// <summary>Finds peers for an info-hash by iterating get_peers toward the hash.</summary>
    public async Task<List<TorrentPeer>> GetPeersAsync(byte[] infoHash, int maxPeers,
        CancellationToken ct)
    {
        StartReceiveLoop();
        var seen = new HashSet<string>();
        var queried = new HashSet<string>();
        var result = new List<TorrentPeer>();

        // start from all known live nodes (or routers if table is empty)
        var start = LiveNodes();
        if (start.Count == 0)
            start = (await ResolveRoutersAsync(ct)).Select(r => new DhtNodeInfo(new byte[20], r, DateTime.UtcNow)).ToList();

        var frontier = new Queue<DhtNodeInfo>(start);
        var rounds = 0;
        while (result.Count < maxPeers && frontier.Count > 0 && rounds < 8)
        {
            var batch = new List<DhtNodeInfo>();
            while (frontier.Count > 0 && batch.Count < 16) batch.Add(frontier.Dequeue());
            rounds++;

            foreach (var node in batch)
            {
                if (ct.IsCancellationRequested) break;
                var ep = node.Endpoint.ToString();
                if (string.IsNullOrEmpty(ep) || !queried.Add(ep)) continue;
                try
                {
                    var (peers, nodes, token) = await GetPeersQueryAsync(node.Endpoint, infoHash, ct);
                    if (!string.IsNullOrEmpty(token))
                        lock (_lock) _tokens[node.Id] = token!;
                    foreach (var p in peers)
                    {
                        var key = p.ToString()!;
                        if (seen.Add(key)) result.Add(p);
                    }
                    if (result.Count >= maxPeers) break;
                    foreach (var n in nodes)
                    {
                        var nkey = n.Endpoint.ToString()!;
                        if (!queried.Contains(nkey) && DistanceRank(n.Id, infoHash) > DistanceRank(node.Id, infoHash))
                            frontier.Enqueue(n);
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested is false) { /* dead node */ }
            }
        }
        return result;
    }

    /// <summary>announce_peer to the nodes closest to the info hash (after GetPeers).</summary>
    public async Task AnnounceAsync(byte[] infoHash, int listenPort, CancellationToken ct)
    {
        var targets = LiveNodes()
            .OrderBy(n => DistanceRank(n.Id, infoHash))
            .Take(8)
            .ToList();
        foreach (var t in targets)
        {
            try
            {
                string? token;
                lock (_lock) _tokens.TryGetValue(t.Id, out token);
                await AnnounceQueryAsync(t.Endpoint, infoHash, listenPort, token, ct);
            }
            catch (Exception) when (ct.IsCancellationRequested is false) { /* skip */ }
        }
    }

    // ------------------------------------------------------------------ KRPC queries
    public async Task<BencValue> PingAsync(IPEndPoint to, CancellationToken ct)
    {
        var txn = Txn();
        var query = BencValue.OfDict()
            .Set("t", BencValue.Of(txn))
            .Set("y", BencValue.Of("q"))
            .Set("q", BencValue.Of("ping"))
            .Set("a", BencValue.OfDict().Set("id", BencValue.Of(NodeId)));
        var reply = await RoundTripAsync(txn, to, query.Encode(), ct);
        if (reply is not null && reply.Get("r") is { } r1 &&
            r1.Get("id")?.AsString is { Length: IdLength } id)
        {
            InsertNode(new DhtNodeInfo(id, to, DateTime.UtcNow));
        }
        return reply;
    }

    private async Task<(List<TorrentPeer> Peers, List<DhtNodeInfo> Nodes, string? Token)>
        GetPeersQueryAsync(IPEndPoint to, byte[] infoHash, CancellationToken ct)
    {
        var txn = Txn();
        var query = BencValue.OfDict()
            .Set("t", BencValue.Of(txn))
            .Set("y", BencValue.Of("q"))
            .Set("q", BencValue.Of("get_peers"))
            .Set("a", BencValue.OfDict()
                .Set("id", BencValue.Of(NodeId))
                .Set("info_hash", BencValue.Of(infoHash)));
        var reply = await RoundTripAsync(txn, to, query.Encode(), ct);
        if (reply is null) return ([], [], null);

        if (reply.Get("r") is not { } r) return ([], [], null);

        var nodes = new List<DhtNodeInfo>();
        if (r.Get("nodes") is { } raw)
        {
            var b = raw.AsString;
            for (var i = 0; i + 26 <= b.Length; i += 26)
            {
                var id = b.AsSpan(i, 20).ToArray();
                var ip = new IPAddress(b.AsSpan(i + 20, 4).ToArray());
                var port = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 24, 2));
                if (port > 0) nodes.Add(new DhtNodeInfo(id, new IPEndPoint(ip, port), DateTime.MinValue));
            }
        }

        var peers = new List<TorrentPeer>();
        if (r.Get("values") is { } values)
            foreach (var v in values.AsList)
            {
                var cb = v.AsString;
                if (cb.Length == 6) peers.Add(TorrentPeer.ParseCompact(cb));
            }

        var token = r.Get("token")?.AsString is { Length: > 0 } tk ? Encoding.UTF8.GetString(tk) : null;
        return (peers, nodes, token);
    }

    private async Task<BencValue> AnnounceQueryAsync(IPEndPoint to, byte[] infoHash, int port,
        string? token, CancellationToken ct)
    {
        var txn = Txn();
        var a = BencValue.OfDict()
            .Set("id", BencValue.Of(NodeId))
            .Set("info_hash", BencValue.Of(infoHash))
            .Set("port", BencValue.Of(port))
            .Set("token", BencValue.Of(Encoding.UTF8.GetBytes(token ?? "")));
        var query = BencValue.OfDict()
            .Set("t", BencValue.Of(txn))
            .Set("y", BencValue.Of("q"))
            .Set("q", BencValue.Of("announce_peer"))
            .Set("a", a);
        var reply = await RoundTripAsync(txn, to, query.Encode(), ct);
        if (reply is not null && reply.Get("r") is { } rr &&
            rr.Get("id")?.AsString is { Length: IdLength } rid)
        {
            InsertNode(new DhtNodeInfo(rid, to, DateTime.UtcNow));
        }
        return reply;
    }

    // ------------------------------------------------------------------ inbound
    /// <summary>Handles an inbound KRPC message (query or response).</summary>
    public async Task HandleDatagramAsync(byte[] data, IPEndPoint from, CancellationToken ct)
    {
        BencValue msg;
        try { msg = BencValue.Decode(data); }
        catch (InvalidDataException) { return; }

        if (msg.Get("y")?.AsUtf8 == "q")
        {
            var senderId = msg.Get("a")?.Get("id")?.AsString;
            if (senderId is { Length: IdLength })
                InsertNode(new DhtNodeInfo(senderId, from, DateTime.UtcNow));

            var q = msg.Get("q")?.AsUtf8;
            var txn = msg.Get("t")?.AsString ?? Array.Empty<byte>();
            var response = q switch
            {
                "ping" => BuildR(txn, null),
                "find_node" => FindNodeReply(txn, msg),
                "get_peers" => GetPeersReply(txn, senderId, msg.Get("a")?.Get("info_hash")?.AsString),
                "announce_peer" => ValidateAndStoreAnnounce(txn, msg, from),
                _ => BuildError(txn, 204, "Method Unknown"),
            };
            if (response is not null)
                await _udp.SendAsync(response, from, ct);
        }
    }

    private byte[] BuildR(byte[] txn, BencValue? extra)
    {
        var r = BencValue.OfDict().Set("id", BencValue.Of(NodeId));
        if (extra is not null)
            foreach (var kv in extra.AsDictionary)
                r.Set(kv.Key, kv.Value);
        return BencValue.OfDict()
            .Set("t", BencValue.Of(txn))
            .Set("y", BencValue.Of("r"))
            .Set("r", r)
            .Encode();
    }

    /// <summary>find_node reply: id + k closest nodes we know (routing-table serving).</summary>
    private byte[] FindNodeReply(byte[] txn, BencValue query)
    {
        var target = query.Get("a")?.Get("target")?.AsString ?? Array.Empty<byte>();
        var r = BencValue.OfDict()
            .Set("id", BencValue.Of(NodeId))
            .Set("nodes", BencValue.Of(ClosestNodesBin(target, 8)));
        return BencValue.OfDict()
            .Set("t", BencValue.Of(txn))
            .Set("y", BencValue.Of("r"))
            .Set("r", r)
            .Encode();
    }

    private byte[] GetPeersReply(byte[] txn, byte[]? senderId, byte[]? infoHash)
    {
        // token bound to the sender address + rotating minute window (BEP-5 security)
        var token = MakeToken(senderId);
        var r = BencValue.OfDict()
            .Set("id", BencValue.Of(NodeId))
            .Set("token", BencValue.Of(Encoding.UTF8.GetBytes(token)));

        if (infoHash is { Length: IdLength })
        {
            // v1.4.1: serve stored peers for this info-hash (BEP-5 "values")
            var stored = TakeStoredPeers(infoHash);
            if (stored.Count > 0)
                r.Set("values", BencValue.Of(stored.Select(BencValue.Of).ToList()));
            else
                r.Set("nodes", BencValue.Of(ClosestNodesBin(infoHash, 8)));
        }
        return BencValue.OfDict()
            .Set("t", BencValue.Of(txn))
            .Set("y", BencValue.Of("r"))
            .Set("r", r)
            .Encode();
    }

    /// <summary>Valid token → store the announced peer (implied_port uses the sender's port)
    /// and answer OK; otherwise protocol error (BEP-5).</summary>
    private byte[]? ValidateAndStoreAnnounce(byte[] txn, BencValue query, IPEndPoint from)
    {
        var a = query.Get("a");
        var token = a?.Get("token")?.AsString is { Length: > 0 } tk ? Encoding.UTF8.GetString(tk) : null;
        var senderId = a?.Get("id")?.AsString;
        if (token is null || token != MakeToken(senderId))
            return BuildError(txn, 203, "Protocol Error (bad token)");

        var infoHash = a?.Get("info_hash")?.AsString;
        if (infoHash is { Length: IdLength })
        {
            var implied = a?.Get("implied_port")?.AsInteger == 1;
            var port = implied
                ? from.Port
                : (int)(a?.Get("port")?.AsInteger ?? 0);
            if (port > 0)
            {
                var compact = new byte[6];
                from.Address.MapToIPv4().GetAddressBytes().CopyTo(compact, 0);
                BinaryPrimitives.WriteUInt16BigEndian(compact.AsSpan(4), (ushort)port);
                lock (_lock)
                {
                    if (!_peerStore.TryGetValue(Convert.ToHexString(infoHash), out var list))
                        _peerStore[Convert.ToHexString(infoHash)] = list = new List<(byte[], DateTime)>();
                    // replace same endpoint, cap the list, keep newest
                    list.RemoveAll(p => p.Compact.AsSpan().SequenceEqual(compact));
                    list.Add((compact, DateTime.UtcNow.Add(PeerStoreTtl)));
                    if (list.Count > PeerStoreMaxPerHash)
                        list.RemoveRange(0, list.Count - PeerStoreMaxPerHash);
                }
            }
        }
        return BuildR(txn, null);
    }

    private List<byte[]> TakeStoredPeers(byte[] infoHash)
    {
        lock (_lock)
        {
            var key = Convert.ToHexString(infoHash);
            if (!_peerStore.TryGetValue(key, out var list)) return [];
            list.RemoveAll(p => p.ExpiresAt < DateTime.UtcNow);
            if (list.Count == 0) { _peerStore.Remove(key); return []; }
            // return up to 50 newest (BEP-5: single packet worth)
            return list.Skip(Math.Max(0, list.Count - 50)).Select(p => p.Compact).ToList();
        }
    }

    /// <summary>Test/telemetry helper: how many live peers are stored for an info-hash.</summary>
    public int StoredPeerCount(byte[] infoHash)
    {
        lock (_lock)
        {
            if (!_peerStore.TryGetValue(Convert.ToHexString(infoHash), out var list)) return 0;
            list.RemoveAll(p => p.ExpiresAt < DateTime.UtcNow);
            return list.Count;
        }
    }

    /// <summary>Test/telemetry helper: do we hold an announce token from this node id?</summary>
    public bool HasTokenFor(byte[] nodeId)
    {
        lock (_lock) return _tokens.ContainsKey(nodeId);
    }

    /// <summary>Builds the binary "nodes" string: 26 bytes per node, closest first.</summary>
    private byte[] ClosestNodesBin(byte[] target, int max)
    {
        List<DhtNodeInfo> nodes;
        lock (_lock)
            nodes = _buckets.SelectMany(b => b.Nodes)
                .Where(n => n.Id.Length == IdLength)
                .OrderBy(n => DistanceRank(n.Id, target))
                .Take(max)
                .ToList();
        var buf = new byte[26 * nodes.Count];
        var off = 0;
        foreach (var n in nodes)
        {
            n.Id.CopyTo(buf, off);
            n.Endpoint.Address.MapToIPv4().GetAddressBytes().CopyTo(buf, off + 20);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(off + 24), (ushort)n.Endpoint.Port);
            off += 26;
        }
        return buf;
    }

    private string MakeToken(byte[]? senderId)
    {
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var src = (senderId ?? Array.Empty<byte>())
            .Concat(Encoding.UTF8.GetBytes(minute.ToString()))
            .Concat(NodeId).ToArray();
        return Convert.ToHexString(SHA1.HashData(src))[..10];
    }

    private static byte[] BuildError(byte[] txn, int code, string message) =>
        BencValue.OfDict()
            .Set("t", BencValue.Of(txn))
            .Set("y", BencValue.Of("e"))
            .Set("e", BencValue.Of(new List<BencValue> { BencValue.Of(code), BencValue.Of(message) }))
            .Encode();

    // ------------------------------------------------------------------ plumbing
    private async Task<BencValue?> RoundTripAsync(byte[] txn, IPEndPoint to, byte[] encoded,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<BencValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new PendingQuery(txn, to, tcs, DateTime.UtcNow.AddMilliseconds(QueryTimeoutMs));
        lock (_pending) _pending[txnKey(txn)] = entry;

        try
        {
            await _udp.SendAsync(encoded, to, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryTimeoutMs);
            try
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(QueryTimeoutMs, timeout.Token));
                if (done == tcs.Task) return await tcs.Task;
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }
        finally
        {
            lock (_pending) _pending.Remove(txnKey(txn));
        }
    }

    private void StartReceiveLoop()
    {
        if (_receiveLoop is not null) return;
        lock (_lock)
        {
            if (_receiveLoop is not null) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _receiveLoop = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _udp.ReceiveAsync(ct);
                        var msg = TryDecode(result.Buffer);
                        if (msg is null) continue;
                        var txn = msg.Get("t")?.AsString;
                        if (msg.Get("y")?.AsUtf8 == "r" && txn is not null)
                        {
                            PendingQuery? pending;
                            lock (_pending) _pending.TryGetValue(txnKey(txn), out pending);
                            pending?.Tcs.TrySetResult(msg);
                        }
                        else if (msg.Get("y")?.AsUtf8 == "q")
                        {
                            await HandleDatagramAsync(result.Buffer, result.RemoteEndPoint, ct);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception) { /* malformed datagram */ }
                }
            }, ct);
        }
    }

    private static BencValue? TryDecode(byte[] data)
    {
        try { return BencValue.Decode(data); }
        catch (InvalidDataException) { return null; }
    }

    private static string txnKey(byte[] txn) => Convert.ToHexString(txn);

    private byte[] Txn()
    {
        var n = Interlocked.Increment(ref _txnCounter);
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)(n & 0xFFFF));
        return b;
    }

    // ------------------------------------------------------------------ routing table
    private void InsertNode(DhtNodeInfo node)
    {
        var bucket = BucketFor(node.Id);
        lock (_lock)
        {
            var existing = bucket.Nodes.FindIndex(n => n.Id.AsSpan().SequenceEqual(node.Id));
            if (existing >= 0)
            {
                bucket.Nodes[existing] = node;
                return;
            }
            if (bucket.Nodes.Count < BucketSize)
                bucket.Nodes.Add(node);
            else
            {
                // evict stalest bad node; when all good, drop (conservative)
                var stalest = bucket.Nodes
                    .Select((n, i) => (n, i))
                    .Where(t => !t.n.IsGood)
                    .OrderBy(t => t.n.LastSeen)
                    .FirstOrDefault();
                if (stalest.n is not null) bucket.Nodes[stalest.i] = node;
            }
        }
    }

    private Bucket BucketFor(byte[] id)
    {
        var bit = SharedPrefixLength(NodeId, id);   // 0..159
        var index = Math.Min(bit, 159);
        return _buckets[index];
    }

    private List<DhtNodeInfo> LiveNodes()
    {
        lock (_lock) return _buckets.SelectMany(b => b.Nodes).Where(n => n.IsGood).ToList();
    }

    /// <summary>XOR distance rank: higher = closer (we order by rank descending).</summary>
    public static int DistanceRank(byte[] a, byte[] b)
    {
        var rank = 0;
        for (var i = 0; i < 20 && i < a.Length && i < b.Length; i++)
        {
            var x = (byte)(a[i] ^ b[i]);
            if (x == 0) { rank += 8; continue; }
            rank += BitOperations.LeadingZeroCount(x) - 24;
            break;
        }
        return rank;
    }

    public static int SharedPrefixLength(byte[] a, byte[] b)
    {
        var bits = 0;
        for (var i = 0; i < 20 && i < a.Length && i < b.Length; i++)
        {
            var x = (byte)(a[i] ^ b[i]);
            if (x == 0) { bits += 8; continue; }
            bits += BitOperations.LeadingZeroCount(x) - 24;
            break;
        }
        return bits;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _udp.Dispose(); } catch { /* closed */ }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));
        public int GetHashCode(byte[] obj)
        {
            var h = new HashCode();
            h.AddBytes(obj);
            return h.ToHashCode();
        }
    }
}
