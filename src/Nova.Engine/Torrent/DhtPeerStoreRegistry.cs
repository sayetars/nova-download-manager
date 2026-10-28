using Nova.Domain;

namespace Nova.Engine.Torrent;

/// <summary>
/// v1.6.0: process-wide hookup point for DHT peer-store persistence. TorrentTask is a
/// static adapter (no constructor) so the engine's session options receive these
/// delegates indirectly: the host app configures this registry once at startup with a
/// Nova.Persistence.DhtPeerStoreDb-backed pair; tests configure in-memory lists.
/// </summary>
public static class DhtPeerStoreRegistry
{
    private static Func<IReadOnlyList<PeerStoreEntry>>? _load;
    private static Action<IReadOnlyList<PeerStoreEntry>>? _save;

    /// <summary>Installs the persistence pair (idempotent; last call wins).</summary>
    public static void Configure(
        Func<IReadOnlyList<PeerStoreEntry>>? load,
        Action<IReadOnlyList<PeerStoreEntry>>? save)
    {
        _load = load;
        _save = save;
    }

    internal static Func<IReadOnlyList<PeerStoreEntry>>? Load => _load;
    internal static Action<IReadOnlyList<PeerStoreEntry>>? Save => _save;

    /// <summary>Test helper: detach persistence (fresh-process semantics).</summary>
    public static void Reset() => Configure(null, null);
}
