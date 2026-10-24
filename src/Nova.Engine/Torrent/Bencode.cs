using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Nova.Engine.Torrent;

/// <summary>
/// Bencode value (BEP-3): integer, byte-string, list, dictionary (keys are byte-strings,
/// sorted lexicographically when encoded — required for valid info-hash computation).
/// Reference type on purpose: nullable access chains naturally (struct nullability
/// does not promote through `is not null` checks).
/// </summary>
public sealed class BencValue
{
    public enum Kind { Integer, String, List, Dictionary }

    private long _integer;
    private byte[]? _string;
    private List<BencValue>? _list;
    private List<KeyValuePair<byte[], BencValue>>? _dict;

    private BencValue(Kind kind, long i, byte[]? s, List<BencValue>? l,
        List<KeyValuePair<byte[], BencValue>>? d)
    {
        Type = kind; _integer = i; _string = s; _list = l; _dict = d;
    }

    public Kind Type { get; }

    public static BencValue Of(long v) => new(Kind.Integer, v, null, null, null);
    public static BencValue Of(byte[] v) => new(Kind.String, 0, v, null, null);
    public static BencValue Of(string v) => new(Kind.String, 0, Encoding.UTF8.GetBytes(v), null, null);
    public static BencValue Of(List<BencValue> v) => new(Kind.List, 0, null, v, null);

    public static BencValue OfDict()
        => new(Kind.Dictionary, 0, null, null, new List<KeyValuePair<byte[], BencValue>>());

    /// <summary>Dictionary setter (fluent) — replaces an existing key if present.</summary>
    public BencValue Set(byte[] key, BencValue value)
    {
        if (Type != Kind.Dictionary) throw new InvalidOperationException("not a dictionary");
        _dict!.RemoveAll(kv => kv.Key.AsSpan().SequenceEqual(key));
        _dict.Add(new KeyValuePair<byte[], BencValue>(key, value));
        return this;
    }

    public BencValue Set(string key, BencValue value) => Set(Encoding.UTF8.GetBytes(key), value);

    /// <summary>Sorts dictionary keys lexicographically — MUST be called before encoding
    /// a dictionary that participates in the info-hash (BEP-3 requires sorted keys).</summary>
    public BencValue SortKeys()
    {
        if (Type == Kind.Dictionary)
            _dict!.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        else if (Type == Kind.List)
            foreach (var v in _list!) v.SortKeys();
        return this;
    }

    public long AsInteger => Type == Kind.Integer ? _integer
        : throw new InvalidDataException($"bencode: expected integer, got {Type}");
    public byte[] AsString => Type == Kind.String ? _string!
        : throw new InvalidDataException($"bencode: expected string, got {Type}");
    public string AsUtf8 => Encoding.UTF8.GetString(AsString);
    public List<BencValue> AsList => Type == Kind.List ? _list!
        : throw new InvalidDataException($"bencode: expected list, got {Type}");
    public IReadOnlyList<KeyValuePair<byte[], BencValue>> AsDictionary => Type == Kind.Dictionary ? _dict!
        : throw new InvalidDataException($"bencode: expected dictionary, got {Type}");

    /// <summary>Dictionary lookup (exact byte comparison); null when absent.</summary>
    public BencValue? Get(string key) => Get(Encoding.UTF8.GetBytes(key));

    public BencValue? Get(ReadOnlySpan<byte> key)
    {
        if (Type != Kind.Dictionary) return null;
        foreach (var kv in _dict!)
            if (kv.Key.AsSpan().SequenceEqual(key)) return kv.Value;
        return null;
    }
    // ------------------------------------------------------------------ encode
    public byte[] Encode()
    {
        var buf = new MemoryStream(256);
        EncodeTo(buf);
        return buf.ToArray();
    }

    private void EncodeTo(MemoryStream buf)
    {
        switch (Type)
        {
            case Kind.Integer:
                buf.WriteByte((byte)'i');
                WriteAscii(buf, _integer.ToString());
                buf.WriteByte((byte)'e');
                break;
            case Kind.String:
                WriteAscii(buf, _string!.Length.ToString());
                buf.WriteByte((byte)':');
                buf.Write(_string, 0, _string.Length);
                break;
            case Kind.List:
                buf.WriteByte((byte)'l');
                foreach (var v in _list!) v.EncodeTo(buf);
                buf.WriteByte((byte)'e');
                break;
            case Kind.Dictionary:
            {
                buf.WriteByte((byte)'d');
                // canonical form: keys MUST be lexicographically sorted (BEP-3)
                foreach (var kv in _dict!.OrderBy(kv => kv.Key, LexicographicByteComparer.Instance))
                {
                    WriteAscii(buf, kv.Key.Length.ToString());
                    buf.WriteByte((byte)':');
                    buf.Write(kv.Key, 0, kv.Key.Length);
                    kv.Value.EncodeTo(buf);
                }
                buf.WriteByte((byte)'e');
                break;
            }
        }
    }

    private sealed class LexicographicByteComparer : IComparer<byte[]>
    {
        public static readonly LexicographicByteComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) =>
            (x ?? Array.Empty<byte>()).AsSpan().SequenceCompareTo(y ?? Array.Empty<byte>());
    }

    private static void WriteAscii(MemoryStream buf, string s)
    {
        foreach (var ch in s) buf.WriteByte((byte)ch);
    }

    // ------------------------------------------------------------------ decode
    public static BencValue Decode(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var v = DecodeValue(data, ref pos);
        if (pos != data.Length)
            throw new InvalidDataException($"bencode: trailing bytes at {pos} (len {data.Length})");
        return v;
    }

    public static BencValue Decode(byte[] data) => Decode(data.AsSpan());

    /// <summary>Measures the byte length of the FIRST complete bencode value in data
    /// (grammar-aware: binary string contents cannot confuse it). -1 when truncated.
    /// Used to split [bencoded header][raw bytes] frames (BEP-9 ut_metadata).</summary>
    public static int MeasureFirstValue(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        return TryMeasure(data, ref pos) ? pos : -1;
    }

    private static bool TryMeasure(ReadOnlySpan<byte> d, ref int pos)
    {
        if (pos >= d.Length) return false;
        var c = (char)d[pos];
        switch (c)
        {
            case 'i':
            {
                var end = d.Slice(pos + 1).IndexOf((byte)'e');
                if (end < 0) return false;
                pos += 1 + end + 1;
                return true;
            }
            case 'l':
            case 'd':
            {
                pos++;
                if (c == 'd')
                {
                    while (pos < d.Length && (char)d[pos] != 'e')
                    {
                        if (!TryMeasureString(d, ref pos)) return false;  // key
                        if (!TryMeasure(d, ref pos)) return false;          // value
                    }
                }
                else
                {
                    while (pos < d.Length && (char)d[pos] != 'e')
                    {
                        if (!TryMeasure(d, ref pos)) return false;
                    }
                }
                if (pos >= d.Length) return false;
                pos++; // 'e'
                return true;
            }
            default:
                return TryMeasureString(d, ref pos);
        }
    }

    private static bool TryMeasureString(ReadOnlySpan<byte> d, ref int pos)
    {
        var colon = d.Slice(pos).IndexOf((byte)':');
        if (colon < 0) return false;
        var lenText = Encoding.ASCII.GetString(d.Slice(pos, colon));
        if (!int.TryParse(lenText, out var len) || len < 0) return false;
        if (pos + colon + 1 + len > d.Length) return false;
        pos += colon + 1 + len;
        return true;
    }

    private static BencValue DecodeValue(ReadOnlySpan<byte> d, ref int pos)
    {
        if (pos >= d.Length) throw new InvalidDataException("bencode: unexpected end");
        var c = (char)d[pos];
        switch (c)
        {
            case 'i':
            {
                pos++;
                var end = d.Slice(pos).IndexOf((byte)'e');
                if (end < 0) throw new InvalidDataException("bencode: unterminated integer");
                var text = Encoding.ASCII.GetString(d.Slice(pos, end));
                if (!long.TryParse(text, out var v))
                    throw new InvalidDataException($"bencode: bad integer '{text}'");
                pos += end + 1;
                return Of(v);
            }
            case 'l':
            {
                pos++;
                var list = new List<BencValue>();
                while ((char)d[pos] != 'e') list.Add(DecodeValue(d, ref pos));
                pos++;
                return Of(list);
            }
            case 'd':
            {
                pos++;
                var dict = OfDict();
                while ((char)d[pos] != 'e')
                {
                    var key = DecodeString(d, ref pos);
                    var val = DecodeValue(d, ref pos);
                    dict.Set(key, val);
                }
                pos++;
                return dict.SortKeys();
            }
            default:
                return Of(DecodeString(d, ref pos));
        }
    }

    private static byte[] DecodeString(ReadOnlySpan<byte> d, ref int pos)
    {
        var colon = d.Slice(pos).IndexOf((byte)':');
        if (colon < 0) throw new InvalidDataException("bencode: missing ':' in string");
        var lenText = Encoding.ASCII.GetString(d.Slice(pos, colon));
        if (!int.TryParse(lenText, out var len) || len < 0)
            throw new InvalidDataException($"bencode: bad string length '{lenText}'");
        pos += colon + 1;
        if (pos + len > d.Length) throw new InvalidDataException("bencode: string overruns buffer");
        var result = d.Slice(pos, len).ToArray();
        pos += len;
        return result;
    }
}

/// <summary>Parsed peer address (compact or dictionary form).</summary>
public readonly record struct TorrentPeer(IPAddress Address, int Port)
{
    public override string ToString() => $"{Address}:{Port}";

    public static TorrentPeer ParseCompact(ReadOnlySpan<byte> six)
    {
        if (six.Length < 6) throw new InvalidDataException("compact peer < 6 bytes");
        var ip = new IPAddress(six.Slice(0, 4).ToArray());
        var port = BinaryPrimitives.ReadUInt16BigEndian(six.Slice(4, 2));
        return new TorrentPeer(ip, port);
    }

    /// <summary>Non-throwing compact parse (PEX / DHT tolerate junk entries).</summary>
    public static bool TryParseCompact(ReadOnlySpan<byte> six, out TorrentPeer peer)
    {
        peer = default;
        if (six.Length < 6) return false;
        var port = BinaryPrimitives.ReadUInt16BigEndian(six.Slice(4, 2));
        if (port == 0) return false;   // port 0 is never a valid peer endpoint
        peer = ParseCompact(six);
        return true;
    }
}
