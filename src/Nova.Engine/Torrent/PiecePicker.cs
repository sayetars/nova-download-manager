namespace Nova.Engine.Torrent;

/// <summary>
/// Piece selection (BEP-3 strategy): rarest-first with a randomized tie-break,
/// optional sequential mode (for preview), block-granular allocation with a
/// dedup set so two peers never fetch the same block simultaneously (endgame
/// handling cancels duplicates when a late peer delivers first).
/// </summary>
public sealed class PiecePicker
{
    private readonly int _pieceCount;
    private readonly long _pieceLength;
    private readonly long _totalLength;
    private readonly int[] _availability;          // per piece, how many peers have it
    private readonly HashSet<long>[] _pendingBlocks; // piece → in-flight blocks
    private readonly bool[] _have;

    public bool Sequential { get; set; }

    public PiecePicker(int pieceCount, long pieceLength, long totalLength)
    {
        _pieceCount = pieceCount;
        _pieceLength = pieceLength;
        _totalLength = totalLength;
        _availability = new int[pieceCount];
        _pendingBlocks = new HashSet<long>[pieceCount];
        for (var i = 0; i < pieceCount; i++) _pendingBlocks[i] = new HashSet<long>();
        _have = new bool[pieceCount];
    }

    public int HaveCount { get; private set; }
    public int PieceCount => _pieceCount;

    public bool IsComplete => HaveCount == _pieceCount;

    public bool this[int piece] => _have[piece];

    // ------------------------------------------------------------------ availability
    /// <summary>Adds a peer's bitfield (index 0..n-1 = has it).</summary>
    public void AddAvailability(bool[] peerBits)
    {
        lock (this)
        {
            var n = Math.Min(peerBits.Length, _pieceCount);
            for (var i = 0; i < n; i++)
                if (peerBits[i]) _availability[i]++;
        }
    }

    public void RemoveAvailability(bool[] peerBits)
    {
        lock (this)
        {
            var n = Math.Min(peerBits.Length, _pieceCount);
            for (var i = 0; i < n; i++)
                if (peerBits[i] && _availability[i] > 0) _availability[i]--;
        }
    }

    public void AddHave(int piece)
    {
        lock (this)
            if (piece >= 0 && piece < _pieceCount) _availability[piece]++;
    }

    // ------------------------------------------------------------------ have/restore
    public void MarkHave(int piece)
    {
        lock (this)
        {
            if (!_have[piece])
            {
                _have[piece] = true;
                HaveCount++;
            }
        }
    }

    /// <summary>Restores a persisted bitfield (resume).</summary>
    public void RestoreFrom(bool[] bits)
    {
        lock (this)
        {
            var n = Math.Min(bits.Length, _pieceCount);
            for (var i = 0; i < n; i++)
                if (bits[i]) MarkHave(i);
        }
    }

    public bool[] Snapshot()
    {
        lock (this)
        {
            var copy = new bool[_pieceCount];
            Array.Copy(_have, copy, _pieceCount);
            return copy;
        }
    }

    // ------------------------------------------------------------------ allocation
    /// <summary>Picks the next block to request from a peer, given its bitfield.
    /// Returns (pieceIndex, beginOffset, length) or null when nothing useful.</summary>
    public (int Piece, int Begin, int Length)? PickBlock(bool[] peerBits)
    {
        lock (this)
        {
            var bestPiece = -1;
            if (Sequential)
            {
                for (var i = 0; i < _pieceCount; i++)
                    if (!_have[i] && i < peerBits.Length && peerBits[i]) { bestPiece = i; break; }
            }
            else
            {
                var bestAvail = int.MaxValue;
                var candidates = new List<int>();
                for (var i = 0; i < _pieceCount; i++)
                {
                    if (_have[i] || i >= peerBits.Length || !peerBits[i]) continue;
                    var a = _availability[i];
                    if (a < bestAvail) { bestAvail = a; candidates.Clear(); candidates.Add(i); }
                    else if (a == bestAvail && candidates.Count < 4) candidates.Add(i);
                }
                if (candidates.Count == 0) return null;
                bestPiece = candidates[Random.Shared.Next(candidates.Count)];
            }
            if (bestPiece < 0) return null;

            // find first non-pending block range within the piece
            var pieceLen = (int)PieceLengthOf(bestPiece);
            const int block = PeerWireClient.BlockSize;
            for (var off = 0; off < pieceLen; off += block)
            {
                if (!_pendingBlocks[bestPiece].Contains(off))
                {
                    var len = (int)Math.Min(block, pieceLen - off);
                    _pendingBlocks[bestPiece].Add(off);
                    return (bestPiece, off, len);
                }
            }
            return null; // all blocks of bestPiece are in flight
        }
    }

    /// <summary>Picks a specific piece's specific block (used when a peer sends requests
    /// to us as a seeder — not used by the downloader).</summary>
    public void ReleaseBlock(int piece, int begin)
    {
        lock (this) _pendingBlocks[piece].Remove(begin);
    }

    /// <summary>Drops all in-flight blocks of a piece (peer died mid-piece; retry them).</summary>
    public void ReleasePiece(int piece)
    {
        lock (this) _pendingBlocks[piece].Clear();
    }

    /// <summary>Everything downloaded? (have + in-flight would be checked by session)</summary>
    public bool IsPieceDownloadable(int piece, bool[] peerBits)
        => !_have[piece] && piece < peerBits.Length && peerBits[piece];

    public long PieceLengthOf(int piece)
        => piece == _pieceCount - 1 ? _totalLength - (long)piece * _pieceLength : _pieceLength;

    /// <summary>Remaining bytes to download.</summary>
    public long RemainingBytes(Func<int, long> pieceSizeOf)
    {
        lock (this)
        {
            long left = 0;
            for (var i = 0; i < _pieceCount; i++)
                if (!_have[i]) left += pieceSizeOf(i);
            return left;
        }
    }
}
