using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Nova.Engine.Torrent;

/// <summary>
/// PEX message payload helpers (BEP-11 ut_pex): a bencoded dict carrying three
/// binary strings of compact IPv4 peer endpoints — "added" (6 bytes each),
/// "added.f" (1 byte each, 0x01 = supports uTP/encrypted inbound, 0x00 = TCP-only)
/// and "dropped" (6 bytes each). Encoding and decoding are symmetric and
/// binary-safe; unknown dict keys are ignored by peers per the BEP.
/// </summary>
public static class PexMessage
{
    /// <summary>A peer endpoint plus its BEP-11 flag byte.</summary>
    public readonly record struct PexPeer(TorrentPeer Peer, byte Flags);

    /// <summary>Builds a ut_pex payload. Empty lists are still emitted (keys always
    /// present) so receivers can rely on the canonical shape.</summary>
    public static byte[] Encode(IReadOnlyList<PexPeer> added, IReadOnlyList<TorrentPeer> dropped)
    {
        var addedBytes = new byte[6 * added.Count];
        var flagBytes = new byte[added.Count];
        for (var i = 0; i < added.Count; i++)
        {
            WriteCompact(addedBytes.AsSpan(i * 6), added[i].Peer);
            flagBytes[i] = added[i].Flags;
        }
        var droppedBytes = new byte[6 * dropped.Count];
        for (var i = 0; i < dropped.Count; i++)
            WriteCompact(droppedBytes.AsSpan(i * 6), dropped[i]);

        return BencValue.OfDict()
            .Set("added", BencValue.Of(addedBytes))
            .Set("added.f", BencValue.Of(flagBytes))
            .Set("dropped", BencValue.Of(droppedBytes))
            .Encode();
    }

    /// <summary>Parses a ut_pex payload; tolerant of missing keys and malformed
    /// compact entries (they are skipped, never thrown).</summary>
    public static (List<PexPeer> Added, List<TorrentPeer> Dropped) Decode(byte[] payload)
    {
        var added = new List<PexPeer>();
        var dropped = new List<TorrentPeer>();
        if (payload is not { Length: > 0 }) return (added, dropped);

        BencValue dict;
        try { dict = BencValue.Decode(payload); }
        catch (InvalidDataException) { return (added, dropped); }

        var flags = Array.Empty<byte>();
        if (dict.Get("added.f")?.AsString is { } f) flags = f;

        if (dict.Get("added")?.AsString is { Length: > 0 } a)
            for (var i = 0; i + 6 <= a.Length; i += 6)
                if (TorrentPeer.TryParseCompact(a.AsSpan(i), out var peer))
                    added.Add(new PexPeer(peer, i / 6 < flags.Length ? flags[i / 6] : (byte)0));

        if (dict.Get("dropped")?.AsString is { Length: > 0 } d)
            for (var i = 0; i + 6 <= d.Length; i += 6)
                if (TorrentPeer.TryParseCompact(d.AsSpan(i), out var dp))
                    dropped.Add(dp);

        return (added, dropped);
    }

    /// <summary>Writes the 6-byte compact form [IPv4 (4)][port BE (2)].</summary>
    public static void WriteCompact(Span<byte> dst, TorrentPeer peer)
    {
        var addr = peer.Address;
        if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            addr = addr.MapToIPv4();
        var octets = addr.GetAddressBytes();   // 4 bytes for IPv4
        if (octets.Length != 4) throw new ArgumentException("PEX supports IPv4 endpoints only");
        octets.CopyTo(dst);
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..], (ushort)peer.Port);
    }

    /// <summary>Binary-safe compact endpoint text key ("1.2.3.4:6881") for dedup sets.</summary>
    public static string KeyOf(TorrentPeer p) => $"{p.Address}:{p.Port}";
}

/// <summary>
/// Per-peer PEX bookkeeping for TorrentSession: which endpoints this peer has
/// already been told about (so re-broadcasts are diffs) and which of ours it was
/// told about last round (for "dropped" lists).
/// </summary>
public sealed class PexPeerState
{
    /// <summary>Endpoints announced to this peer (compact keys).</summary>
    public HashSet<string> Sent { get; } = new();
    /// <summary>Endpoints known-but-not-yet-sent to this peer (compact key → peer).</summary>
    public Dictionary<string, TorrentPeer> Pending { get; } = new();
}
