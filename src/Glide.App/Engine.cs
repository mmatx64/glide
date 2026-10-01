using System.Net;
using System.Net.Sockets;
using Glide.Core;

namespace Glide;

internal sealed class Engine : IDisposable
{
    private CancellationTokenSource? cancellation;
    private Task? task;
    private InputWorker? input;
    private TcpListener? listener;
    private volatile Connection? connection;
    private volatile string status = "Ready when you are";
    private volatile string peerHost = "";
    internal event Action? EmergencyStopped;
    internal string Status => status;
    internal bool Running => task is { IsCompleted: false } && cancellation is { IsCancellationRequested: false };
    internal bool Connected => connection is { IsAlive: true };
    internal bool ControllingRemote => input?.IsRemote ?? false;
    internal double Latency => connection?.RoundTripMs ?? 0;
    internal long Sent => connection?.Sent ?? 0;
    internal long Coalesced => connection?.Coalesced ?? 0;
    internal void Start(bool controller, string host, Invitation? invitation, PairingIdentity? identity, bool right, int port = Connection.Port)
    {
        if (task is { IsCompleted: false }) throw new InvalidOperationException("The previous connection is still closing. Try again in a moment.");
        input ??= CreateInput();
        peerHost = host;
        cancellation?.Dispose(); cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        status = controller ? "Connecting securely…" : "Listening on TCP 24819";
        task = Task.Run(async () =>
        {
            try
            {
                if (!controller)
                {
                    listener = new TcpListener(IPAddress.Any, port);
                    listener.Start(4);
                }
                while (!token.IsCancellationRequested)
                {
                    Connection? peer = null;
                    try
                    {
                        var desktop = Native.Desktop;
                        if (controller)
                            peer = await Connection.ConnectAsync(peerHost, port, invitation!, desktop.Width, desktop.Height, token);
                        else
                        {
                            var socket = await listener!.AcceptTcpClientAsync(token);
                            peer = await Connection.AcceptAsync(socket, identity!, desktop.Width, desktop.Height, token);
                        }
                        connection = peer;
                        input.Attach(peer, controller, right);
                        peer.Input += packet => input.Receive(peer, packet);
                        status = controller ? "Connected · move across the screen edge" : "Connected · ready to receive input";
                        await peer.RunAsync(token);
                    }
                    catch (Exception) when (token.IsCancellationRequested) { break; }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        status = controller ? "Reconnecting · " + Friendly(ex) : "Listening · " + Friendly(ex);
                    }
                    finally
                    {
                        peer?.Dispose(); input.Detach(); connection = null; peer?.Finish();
                    }
                    await Task.Delay(controller ? 1800 : 300, token);
                }
            }
            catch (Exception) when (token.IsCancellationRequested) { }
            catch (Exception ex) { status = Friendly(ex); }
            finally { listener?.Stop(); listener = null; input.Detach(); }
        });
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
        status = reason;
        cancellation?.Cancel(); listener?.Stop(); connection?.Dispose(); input?.Detach();
    }
    internal void UpdateAddress(string address) => peerHost = address;
    internal async Task StopAsync(string reason = "Sharing paused · local control")
    {
        Stop(reason);
        if (task is not null) await task.ConfigureAwait(false);
    }
    private static string Friendly(Exception ex) => ex switch
    {
        SocketException se when se.SocketErrorCode == SocketError.AddressAlreadyInUse => "Port 24819 is already in use.",
        SocketException => "Peer unavailable. Check its address, listener, and private-network firewall rule.",
        System.Security.Authentication.AuthenticationException => "Pairing changed. Pause, then pair with the other PC again.",
        OperationCanceledException => "Connection timed out. Check the other PC and firewall.",
        EndOfStreamException => "Peer disconnected.",
        _ => ex.Message
    };
    public void Dispose()
    {
        Stop();
        try { task?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        input?.Dispose(); cancellation?.Dispose();
    }
}
