using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace Glide.Core;

public sealed class Connection : IAsyncDisposable
{
    public const int Port = 24819;
    private readonly TcpClient client;
    private readonly SslStream stream;
    private readonly Outbox outbox = new();
    private readonly CancellationTokenSource lifetime = new();
    private long lastReceived = Stopwatch.GetTimestamp();
    private long sent, received, writeOperations;
    private double roundTripMs;
    private int stopped;
    private readonly object gate = new();
    private Task? run;
    private bool disposed;
    public int RemoteWidth { get; }
    public int RemoteHeight { get; }
    public double RoundTripMs => Volatile.Read(ref roundTripMs);
    public long Sent => Interlocked.Read(ref sent);
    public long Received => Interlocked.Read(ref received);
    public long WriteOperations => Interlocked.Read(ref writeOperations);
    public long Coalesced => outbox.Coalesced;
    public bool IsAlive => Volatile.Read(ref stopped) == 0;
    public event Action<Packet>? Input;

    private Connection(TcpClient client, SslStream stream, int width, int height)
    { this.client = client; this.stream = stream; RemoteWidth = width; RemoteHeight = height; }

    public static async Task<Connection> ConnectAsync(string host, int port, Invitation invitation, int width, int height, CancellationToken cancellation)
    {
        var client = new TcpClient { NoDelay = true, SendBufferSize = 4096, ReceiveBufferSize = 16384 };
        SslStream? stream = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            stream = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert is not null &&
                CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), invitation.Fingerprint));
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            { TargetHost = "Glide", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(invitation.Secret, timeout.Token).ConfigureAwait(false);
            var accepted = new byte[1];
            await stream.ReadExactlyAsync(accepted, timeout.Token).ConfigureAwait(false);
            if (accepted[0] != 1) throw new AuthenticationException("Pairing was rejected.");
            return await ExchangeDesktop(client, stream, width, height, timeout.Token).ConfigureAwait(false);
        }
        catch { stream?.Dispose(); client.Dispose(); throw; }
    }

    public static async Task<Connection> AcceptAsync(TcpClient client, PairingIdentity identity, int width, int height, CancellationToken cancellation)
    {
        client.NoDelay = true;
        client.SendBufferSize = 4096; client.ReceiveBufferSize = 16384;
        var stream = new SslStream(client.GetStream(), false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            { ServerCertificate = identity.Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token).ConfigureAwait(false);
            var secret = new byte[32];
            await stream.ReadExactlyAsync(secret, timeout.Token).ConfigureAwait(false);
            bool match = CryptographicOperations.FixedTimeEquals(secret, identity.Secret);
            CryptographicOperations.ZeroMemory(secret);
            if (!match) throw new AuthenticationException("Pairing was rejected.");
            await stream.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            return await ExchangeDesktop(client, stream, width, height, timeout.Token).ConfigureAwait(false);
        }
        catch { stream.Dispose(); client.Dispose(); throw; }
    }

    private static async Task<Connection> ExchangeDesktop(TcpClient client, SslStream stream, int width, int height, CancellationToken ct)
    {
        Coordinates.ValidateDesktop(width, height);
        var bytes = new byte[Packet.Size];
        new Packet(MessageKind.Hello, width, height, 1).Write(bytes);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        var hello = Packet.Read(bytes);
        if (hello.Kind != MessageKind.Hello || hello.C != 1) throw new InvalidDataException("Incompatible Glide version.");
        Coordinates.ValidateDesktop(hello.A, hello.B);
        return new Connection(client, stream, hello.A, hello.B);
    }

    public bool Send(Packet packet)
    {
        lock (gate)
        {
            if (!IsAlive) return false;
            if (outbox.TryAdd(packet)) return true;
        }
        // Never silently drop key-up/button-up. Fail the entire session and release input.
        Stop();
        return false;
    }

    public Task RunAsync(CancellationToken cancellation)
    {
        lock (gate)
        {
            if (!IsAlive) throw new OperationCanceledException("Connection closed.");
            if (run is not null) throw new InvalidOperationException("Connection is already running.");
            return run = RunCore(cancellation);
        }
    }
    private async Task RunCore(CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var tasks = new[] { ReadLoop(linked.Token), WriteLoop(linked.Token), Heartbeat(linked.Token) };
        try { await await Task.WhenAny(tasks).ConfigureAwait(false); }
        finally
        {
            Stop(); linked.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* Preserve first failure. */ }
        }
    }
    private async Task ReadLoop(CancellationToken ct)
    {
        var bytes = new byte[Packet.Size];
        while (!ct.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            var packet = Packet.Read(bytes);
            Interlocked.Exchange(ref lastReceived, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref received);
            if (packet.Kind == MessageKind.Ping) Send(packet with { Kind = MessageKind.Pong });
            else if (packet.Kind == MessageKind.Pong)
                Volatile.Write(ref roundTripMs, Math.Max(0, Stopwatch.GetElapsedTime(packet.Stamp).TotalMilliseconds));
            else Input?.Invoke(packet);
        }
    }
    private async Task WriteLoop(CancellationToken ct)
    {
        // One bounded TLS write for an existing burst, with no batching timer.
        // 32 packets = 1 KiB of plaintext; leave the rest available for coalescing.
        var packets = new Packet[32];
        var bytes = new byte[Packet.Size * packets.Length];
        while (!ct.IsCancellationRequested)
        {
            int count = await outbox.TakeBatchAsync(packets, ct).ConfigureAwait(false);
            for (int i = 0; i < count; i++) packets[i].Write(bytes.AsSpan(i * Packet.Size, Packet.Size));
            await stream.WriteAsync(bytes.AsMemory(0, count * Packet.Size), ct).ConfigureAwait(false);
            Interlocked.Increment(ref writeOperations);
            Interlocked.Add(ref sent, count);
        }
    }
    private async Task Heartbeat(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastReceived)).TotalMilliseconds > 1600)
                throw new IOException("Connection stalled. Control returned to this PC.");
            Send(new Packet(MessageKind.Ping, Stamp: Stopwatch.GetTimestamp()));
        }
    }
    public void Stop()
    {
        lock (gate)
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            lifetime.Cancel(); client.Dispose();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Stop();
        Task? pending;
        lock (gate) pending = run;
        if (pending is not null) { try { await pending.ConfigureAwait(false); } catch (Exception) { /* RunAsync reports the session failure. */ } }
        lock (gate)
        {
            if (disposed) return;
            disposed = true; stream.Dispose(); outbox.Dispose(); lifetime.Dispose();
        }
    }
}
