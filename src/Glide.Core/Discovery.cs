using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Glide.Core;

public sealed record Announcement(string Name, byte[] Fingerprint, bool Receiver, bool Connected)
{
    public string Id => Convert.ToHexString(Fingerprint);
    public byte[] Encode()
    {
        if (Fingerprint.Length != 32) throw new ArgumentException("Invalid discovery identity.");
        var name = Encoding.UTF8.GetBytes(CleanName(Name));
        var data = new byte[44 + name.Length];
        "GLIDE002"u8.CopyTo(data); Fingerprint.CopyTo(data, 8);
        data[40] = (byte)((Receiver ? 1 : 0) | (Connected ? 2 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(41), (ushort)name.Length);
        name.CopyTo(data, 44);
        return data;
    }
    public static Announcement Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 44 or > 172 || !data[..8].SequenceEqual("GLIDE002"u8) || data[40] > 3 || data[43] != 0)
            throw new InvalidDataException("Invalid discovery announcement.");
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data[41..]);
        if (length != data.Length - 44 || length == 0) throw new InvalidDataException("Invalid discovery name.");
        string name = new UTF8Encoding(false, true).GetString(data[44..]);
        if (CleanName(name) != name) throw new InvalidDataException("Invalid discovery name.");
        return new(name, data.Slice(8, 32).ToArray(), (data[40] & 1) != 0, (data[40] & 2) != 0);
    }
    public static string CleanName(string name)
    {
        var clean = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').Take(63).ToArray()).Trim();
        return clean.Length == 0 ? "Windows PC" : clean;
    }
}

public sealed record NearbyPeer(Announcement Info, IPAddress Address, long LastSeen);

public sealed class PeerDirectory
{
    private readonly Dictionary<string, NearbyPeer> peers = new();
    private readonly string selfId;
    public PeerDirectory(byte[] fingerprint) => selfId = Convert.ToHexString(fingerprint);
    public void Observe(Announcement info, IPAddress address, long now)
    {
        if (info.Id == selfId || address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any) || address.GetAddressBytes()[0] >= 224) return;
        lock (peers)
        {
            Prune(now);
            if (peers.Count >= 32 && !peers.ContainsKey(info.Id)) return;
            peers[info.Id] = new(info, address, now);
        }
    }
    public NearbyPeer[] Snapshot(long now)
    {
        lock (peers) { Prune(now); return peers.Values.OrderBy(p => p.Info.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Info.Id).ToArray(); }
    }
    private void Prune(long now)
    {
        foreach (var id in peers.Where(p => now - p.Value.LastSeen > 7000).Select(p => p.Key).ToArray()) peers.Remove(id);
    }
}

public sealed class DiscoveryService : IDisposable
{
    public const int Port = 24820;
    private readonly UdpClient socket;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<Announcement> announce;
    private readonly Task task;
    public PeerDirectory Peers { get; }
    public string? Error { get; private set; }
    public IPEndPoint LocalEndpoint => (IPEndPoint)socket.Client.LocalEndPoint!;
    public DiscoveryService(byte[] fingerprint, Func<Announcement> announce, IPEndPoint? bind = null, IPEndPoint[]? testTargets = null)
    {
        this.announce = announce; Peers = new(fingerprint);
        socket = new UdpClient(bind ?? new IPEndPoint(IPAddress.Any, Port)) { EnableBroadcast = true };
        socket.Ttl = 1;
        task = Task.Run(() => Run(testTargets, cancellation.Token));
    }
    private async Task Run(IPEndPoint[]? testTargets, CancellationToken ct)
    {
        var receive = Receive(ct); var send = Send(testTargets, ct);
        try { await await Task.WhenAny(receive, send).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Error = ex.Message; }
        catch (Exception) when (ct.IsCancellationRequested) { }
        finally
        {
            cancellation.Cancel(); socket.Dispose();
            try { await Task.WhenAll(receive, send).ConfigureAwait(false); } catch (Exception) { }
        }
    }
    private async Task Receive(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult message;
            try { message = await socket.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (SocketException) when (!ct.IsCancellationRequested) { await Task.Delay(150, ct).ConfigureAwait(false); continue; }
            try { Peers.Observe(Announcement.Decode(message.Buffer), message.RemoteEndPoint.Address, Environment.TickCount64); }
            catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException) { }
        }
    }
    private async Task Send(IPEndPoint[]? testTargets, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            var bytes = announce().Encode();
            IPEndPoint[] targets;
            try { targets = testTargets ?? BroadcastTargets(); }
            catch (NetworkInformationException) { targets = [new IPEndPoint(IPAddress.Broadcast, Port)]; }
            foreach (var target in targets)
            {
                try { await socket.SendAsync(bytes, target, ct).ConfigureAwait(false); }
                catch (SocketException) { /* An unplugged adapter must not stop other interfaces. */ }
            }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }
    private static IPEndPoint[] BroadcastTargets()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = unicast.Address.GetAddressBytes(); var mask = unicast.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < 4; i++) ip[i] |= (byte)~mask[i];
                addresses.Add(new IPAddress(ip));
            }
        }
        return addresses.Select(address => new IPEndPoint(address, Port)).ToArray();
    }
    public void Dispose() { cancellation.Cancel(); socket.Dispose(); try { task.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { } }
}
