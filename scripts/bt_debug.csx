using System.Net;
using System.Net.Sockets;
using System.Text;
using Nova.Engine.Torrent;

// quick diagnostic: message flow between two PeerWireClients
var infoHash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
var serverPeerId = Encoding.UTF8.GetBytes("-SV1400-serverpeer1");
var clientPeerId = Encoding.UTF8.GetBytes("-CL1400-clientpeer1");

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var accepted = Task.Run(async () =>
{
    using var tcp = await listener.AcceptTcpClientAsync();
    var wire = await PeerWireClient.AcceptAsync(tcp, infoHash, serverPeerId, CancellationToken.None);
    wire.InitBitfield(4);
    Console.WriteLine("[server] accepted, sending bitfield + unchoke");
    await wire.SendBitfieldAsync(new[] { true, true, true, true }, CancellationToken.None);
    await wire.SendUnchokeAsync(CancellationToken.None);
    var msg = await wire.ReadMessageAsync(CancellationToken.None);
    Console.WriteLine($"[server] got msg: {(msg is null ? "NULL" : msg.Value.Id.ToString())}");
    if (msg is { } m && m.Id == PeerWireClient.MsgRequest)
    {
        await wire.SendPieceAsync(0, 0, new byte[16384], CancellationToken.None);
        Console.WriteLine("[server] sent piece");
    }
    await wire.DisposeAsync();
});

var ep = (IPEndPoint)listener.LocalEndpoint;
await using var client = PeerWireClient.CreateConnected(new TorrentPeer(ep.Address, ep.Port),
    infoHash, clientPeerId);
client.InitBitfield(4);
Console.WriteLine($"[client] handshake: {await client.HandshakeAsync(CancellationToken.None)}");
for (var i = 0; i < 6; i++)
{
    var m = await client.ReadMessageAsync(CancellationToken.None);
    Console.WriteLine($"[client] msg #{i}: {(m is null ? "NULL" : $"id={m.Value.Id} len={m.Value.Payload.Length}")}");
    if (m is null) break;
}
await accepted;
listener.Stop();
Console.WriteLine("done");
