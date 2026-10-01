using System.Net;
using System.Net.Sockets;
using Glide.Core;

namespace Glide;

internal sealed class Engine : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task? task;
    private InputWorker? input;
    private TcpServer? listener;
    private volatile Connection? connection;
    private volatile string status = "Ready when you are";
    private volatile string peerHost = "";
    private bool disposed;
    internal event Action? EmergencyStopped;
    internal string Status => status;
    internal bool Running { get { lock (gate) return task is { IsCompleted: false } && cancellation is { IsCancellationRequested: false }; } }
    internal bool Connected => connection is { IsAlive: true };
    internal bool ControllingRemote => input?.IsRemote ?? false;
    internal double Latency => connection?.RoundTripMs ?? 0;
    internal long Sent => connection?.Sent ?? 0;
    internal long Coalesced => connection?.Coalesced ?? 0;
    internal void Start(bool controller, string host, Invitation? invitation, PairingIdentity? identity, bool right, int port = Connection.Port)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (task is { IsCompleted: false }) throw new InvalidOperationException("The previous connection is still closing. Try again in a moment.");
            input ??= CreateInput();
            peerHost = host;
            cancellation?.Dispose(); cancellation = new();
            var token = cancellation.Token;
            status = controller ? "Connecting securely…" : "Listening on TCP " + port;
            task = Task.Run(async () =>
            {
                try
                {
                    if (controller)
                    {
                        while (!token.IsCancellationRequested)
                        {
                            try
                            {
                                var desktop = Native.Desktop;
                                await Handle(await Connection.ConnectAsync(peerHost, port, invitation!, desktop.Width, desktop.Height, token), token);
                            }
                            catch (Exception) when (token.IsCancellationRequested) { break; }
                            catch (Exception ex) { status = "Reconnecting · " + Friendly(ex); }
                            await Task.Delay(1800, token);
                        }
                    }
                    else
                    {
                        using var server = new TcpServer(new(IPAddress.Any, port), async (socket, ct) =>
                        {
                            if (Connected) return;
                            var desktop = Native.Desktop;
                            await Handle(await Connection.AcceptAsync(socket, identity!, desktop.Width, desktop.Height, ct), ct);
                        }, ex => { if (!token.IsCancellationRequested && !Connected) status = "Listening · " + Friendly(ex); });
                        lock (gate) listener = server;
                        using var registration = token.Register(server.Stop);
                        await server.Completion;
                    }
                }
                catch (Exception) when (token.IsCancellationRequested) { }
                catch (Exception ex) { status = Friendly(ex); }
                finally { lock (gate) { listener = null; input.Detach(); } }
            });

            async Task Handle(Connection peer, CancellationToken ct)
            {
                await using (peer)
                {
                    lock (gate)
                    {
                        if (token.IsCancellationRequested || ct.IsCancellationRequested || connection is not null) return;
                        connection = peer;
                        input.Attach(peer, controller, right);
                        peer.Input += packet => input.Receive(peer, packet);
                        status = controller ? "Connected · move across the screen edge" : "Connected · ready to receive input";
                    }
                    try { await peer.RunAsync(ct); }
                    finally
                    {
                        peer.Stop();
                        lock (gate) { input.Detach(); connection = null; }
                    }
                }
            }
        }
    }
    private InputWorker CreateInput()
    {
        var worker = new InputWorker();
        worker.Notice += text => status = text;
        worker.EmergencyStopped += () => { Stop("Emergency stop · sharing is off"); EmergencyStopped?.Invoke(); };
        return worker;
    }
    internal void Stop(string reason = "Sharing paused · local control")
    {
        lock (gate)
        {
            status = reason;
            cancellation?.Cancel(); listener?.Stop(); connection?.Stop(); input?.Detach();
        }
    }
    internal void UpdateAddress(string address) => peerHost = address;
    internal async Task StopAsync(string reason = "Sharing paused · local control")
    {
        Task? pending;
        lock (gate) { Stop(reason); pending = task; }
        if (pending is not null) await pending.ConfigureAwait(false);
    }
    private static string Friendly(Exception ex) => ex switch
    {
        SocketException se when se.SocketErrorCode == SocketError.AddressAlreadyInUse => "Input port is already in use.",
        SocketException => "Peer unavailable. Check its address, listener, and private-network firewall rule.",
        System.Security.Authentication.AuthenticationException => "Pairing changed. Pause, then pair with the other PC again.",
        OperationCanceledException => "Connection timed out. Check the other PC and firewall.",
        EndOfStreamException => "Peer disconnected.",
        _ => ex.Message
    };
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        StopAsync().GetAwaiter().GetResult();
        input?.Dispose(); cancellation?.Dispose(); cancellation = null;
    }
}
