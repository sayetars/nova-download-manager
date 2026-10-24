using System.Security.Cryptography;
using System.Text;

namespace Nova.Engine.Torrent;

/// <summary>
/// Parsed torrent metadata (BEP-3 metainfo). Supports single- and multi-file torrents,
/// the standard fields plus the v1 extensions we actually use (url-list web seeds is
/// deliberately out of scope for v1.4.0). Immutable once parsed.
/// </summary>
public sealed class TorrentMeta
{
    public required byte[] InfoHash { get; init; }       // 20 bytes SHA-1 of the canonical info dict
    public required string InfoHashHex { get; init; }    // lowercase hex (magnet xt / UI)
    public required string MagnetUri { get; init; }       // rebuilt magnet with trackers + name
    public required long TotalLength { get; init; }      // sum of all files
    public required long PieceLength { get; init; }
    public required byte[][] PieceHashes { get; init; }   // SHA-1 digests (20 bytes each)
    public required string Name { get; init; }            // single-file name or folder name
    public required TorrentFileInfo[] Files { get; init; }
    public required string[] Trackers { get; init; }
    public string Comment { get; init; } = "";
    public string CreatedBy { get; init; } = "";
    public long CreationDate { get; init; }
    public bool IsPrivate { get; init; }
    /// <summary>Raw bencoded "info" dictionary — needed for BEP-10 extension handshakes
    /// (BEP-3 requires serving it verbatim to peers via ut_metadata).</summary>
    public required byte[] InfoDictBytes { get; init; }
    /// <summary>Raw bencoded whole-file metainfo (what we persist as the .torrent sidecar).</summary>
    public required byte[] RawBytes { get; init; }

    public int PieceCount => PieceHashes.Length;
    /// <summary>True when the torrent contains exactly one file with no sub-path.</summary>
    public bool IsSingleFile => Files.Length == 1 && string.IsNullOrEmpty(Files[0].SubPath);

    /// <summary>Offset of a piece within the (possibly multi-file) contiguous byte stream.</summary>
    public long PieceOffset(int index) => (long)index * PieceLength;

    public long PieceSize(int index) => index == PieceCount - 1
        ? TotalLength - PieceOffset(index)
        : PieceLength;

    public static TorrentMeta Parse(byte[] raw)
        => Parse(raw.AsSpan());

    public static TorrentMeta Parse(ReadOnlySpan<byte> raw)
    {
        var root = BencValue.Decode(raw);
        var info = root.Get("info") ?? throw new InvalidDataException("torrent: missing info dict");
        return FromRoot(root, info, raw.ToArray());
    }

    private static TorrentMeta FromRoot(BencValue root, BencValue info, byte[] raw)
    {
        // --- piece layer -----------------------------------------------------
        var pieces = info.Get("pieces") ?? throw new InvalidDataException("torrent: missing pieces");
        var piecesBytes = pieces.AsString;
        if (piecesBytes.Length == 0 || piecesBytes.Length % 20 != 0)
            throw new InvalidDataException("torrent: pieces length not a multiple of 20");
        var count = piecesBytes.Length / 20;
        var hashes = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var h = new byte[20];
            Array.Copy(piecesBytes, i * 20, h, 0, 20);
            hashes[i] = h;
        }

        var pieceLength = info.Get("piece length")?.AsInteger
            ?? throw new InvalidDataException("torrent: missing piece length");

        // --- files -----------------------------------------------------------
        var name = info.Get("name")?.AsUtf8 ?? throw new InvalidDataException("torrent: missing name");
        var files = new List<TorrentFileInfo>();
        if (info.Get("files") is { } filesList)
        {
            long offset = 0;
            foreach (var f in filesList.AsList)
            {
                var len = f.Get("length")?.AsInteger
                    ?? throw new InvalidDataException("torrent: file entry missing length");
                var pathSegments = f.Get("path")?.AsList
                    ?? throw new InvalidDataException("torrent: file entry missing path");
                var parts = pathSegments.Select(p => p.AsUtf8).ToArray();
                // sanitize: strip directory traversal
                var safe = parts.Where(p => p is not ("" or "." or "..")).ToArray();
                var sub = string.Join(Path.DirectorySeparatorChar.ToString(), safe);
                files.Add(new TorrentFileInfo(sub, len, offset));
                offset += len;
            }
            if (files.Count == 0) throw new InvalidDataException("torrent: empty files list");
        }
        else
        {
            var len = info.Get("length")?.AsInteger
                ?? throw new InvalidDataException("torrent: single-file missing length");
            files.Add(new TorrentFileInfo("", len, 0));
        }

        // --- announce / announce-list ---------------------------------------
        var trackers = new List<string>();
        if (root.Get("announce") is { } ann)
        {
            var a = ann.AsUtf8;
            if (!string.IsNullOrWhiteSpace(a)) trackers.Add(a);
        }
        if (root.Get("announce-list") is { } annList)
            foreach (var tier in annList.AsList)
                foreach (var t in tier.AsList)
                {
                    var u = t.AsUtf8;
                    if (!string.IsNullOrWhiteSpace(u) && !trackers.Contains(u)) trackers.Add(u);
                }

        var total = files.Sum(f => f.Length);
        var infoHash = SHA1.HashData(info.Encode());
        var magnet = BuildMagnet(infoHash, name, trackers);

        return new TorrentMeta
        {
            InfoHash = infoHash,
            InfoHashHex = Convert.ToHexString(infoHash).ToLowerInvariant(),
            MagnetUri = magnet,
            TotalLength = total,
            PieceLength = pieceLength,
            PieceHashes = hashes,
            Name = name,
            Files = files.ToArray(),
            Trackers = trackers.ToArray(),
            Comment = root.Get("comment")?.AsUtf8 ?? "",
            CreatedBy = root.Get("created by")?.AsUtf8 ?? "",
            CreationDate = root.Get("creation date")?.AsInteger ?? 0,
            IsPrivate = info.Get("private")?.AsInteger == 1,
            InfoDictBytes = info.Encode(),
            RawBytes = raw,
        };
    }

    // ------------------------------------------------------------------ magnet
    /// <summary>Parses a magnet URI (BEP-9 / BEP-53): xt (btih hex or base32), dn, tr[], xl.</summary>
    public static MagnetInfo ParseMagnet(string uri)
    {
        if (!uri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("magnet: must start with 'magnet:?'");
        var infoHash = (byte[]?)null;
        string? name = null;
        long length = -1;
        var trackers = new List<string>();
        var webSeeds = new List<string>();

        foreach (var kv in uri.Substring(8).Split('&'))
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            var key = kv[..eq].ToLowerInvariant();
            var value = Uri.UnescapeDataString(kv[(eq + 1)..]);
            switch (key)
            {
                case "xt":
                    if (value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                    {
                        var hashText = value[9..];
                        if (hashText.Length == 40)
                            infoHash = Convert.FromHexString(hashText);
                        else if (hashText.Length == 32)
                            infoHash = Base32Decode(hashText);
                        else throw new InvalidDataException($"magnet: bad btih length {hashText.Length}");
                    }
                    break;
                case "dn": name = value; break;
                case "xl": _ = long.TryParse(value, out length); break;
                case "tr": trackers.Add(value); break;
                case "ws": webSeeds.Add(value); break;
            }
        }
        if (infoHash is null || infoHash.Length != 20)
            throw new InvalidDataException("magnet: missing urn:btih info hash");
        return new MagnetInfo(infoHash, name, trackers, webSeeds, length);
    }

    public static string BuildMagnet(byte[] infoHash, string? name, IEnumerable<string> trackers)
    {
        var sb = new StringBuilder("magnet:?xt=urn:btih:");
        sb.Append(Convert.ToHexString(infoHash).ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(name))
            sb.Append("&dn=").Append(Uri.EscapeDataString(name));
        foreach (var t in trackers)
            if (!string.IsNullOrWhiteSpace(t))
                sb.Append("&tr=").Append(Uri.EscapeDataString(t));
        return sb.ToString();
    }

    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var bits = 0; var value = 0; var outBytes = new List<byte>();
        foreach (var c in s.ToLowerInvariant())
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) continue;
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                outBytes.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return outBytes.ToArray();
    }
}

/// <summary>One file inside a (possibly multi-file) torrent. SubPath is "" for single-file.</summary>
public readonly record struct TorrentFileInfo(string SubPath, long Length, long Offset);

/// <summary>Parsed magnet URI pieces (metadata itself must come from the swarm / DHT).</summary>
public sealed record MagnetInfo(byte[] InfoHash, string? Name, IReadOnlyList<string> Trackers,
    IReadOnlyList<string> WebSeeds, long Length)
{
    public string InfoHashHex => Convert.ToHexString(InfoHash).ToLowerInvariant();
}

/// <summary>Builds .torrent metainfo from local files (used by tests; also exposed to users
/// as "create torrent" in a future release).</summary>
public static class TorrentCreator
{
    /// <summary>Creates metainfo for a single file. pieceLength typically 262144..4194304.</summary>
    public static byte[] CreateSingleFile(string filePath, string[] trackers, string comment = "",
        long pieceLength = 262144, string createdBy = "Nova Download Manager 1.4.0")
    {
        var name = Path.GetFileName(filePath);
        var length = new FileInfo(filePath).Length;
        var pieces = new List<byte>();
        using var fs = File.OpenRead(filePath);
        var buffer = new byte[pieceLength];
        while (true)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) break;
            pieces.AddRange(SHA1.HashData(buffer.AsSpan(0, read)));
            if (read < buffer.Length) break;
        }

        var info = BencValue.OfDict()
            .Set("length", BencValue.Of(length))
            .Set("name", BencValue.Of(name))
            .Set("piece length", BencValue.Of(pieceLength))
            .Set("pieces", BencValue.Of(pieces.ToArray()));

        var root = BencValue.OfDict()
            .Set("announce", BencValue.Of(trackers is { Length: > 0 } ? trackers[0] : ""))
            .Set("comment", BencValue.Of(comment))
            .Set("created by", BencValue.Of(createdBy))
            .Set("creation date", BencValue.Of(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            .Set("info", info);
        if (trackers is { Length: > 1 })
            root.Set("announce-list", BencValue.Of(trackers
                .Select(t => BencValue.Of(new List<BencValue> { BencValue.Of(t) }))
                .ToList()));
        return root.SortKeys().Encode();
    }
}
