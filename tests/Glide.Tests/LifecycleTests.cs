using System.Net;
using System.Net.Sockets;
using Glide.Core;

internal static class LifecycleTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        using var identity = new PairingIdentity();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        async Task<(Connection Client, Connection Server)> Pair()
        {
            var accept = Task.Run(async () => await Connection.AcceptAsync(await listener.AcceptTcpClientAsync(ct), identity, 1920, 1080, ct));
            var client = await Connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Invitation.Parse(identity.Invitation), 1920, 1080, ct);
            return (client, await accept);
        }
        try
        {
            var first = await Pair(); var other = await Pair();
            await using var firstClient = first.Client; await using var firstServer = first.Server;
            await using var otherClient = other.Client; await using var otherServer = other.Server;
            var inbox = new Inbox(1);
            check(inbox.TryAdd(firstClient, new(MessageKind.Key, 65, 30)), "inbound queue accepts an event before input attachment");
            check(!inbox.TryAdd(firstClient, new(MessageKind.Key, 65, 30, 2)) && !firstClient.IsAlive && otherClient.IsAlive,
                "inbound overflow closes its sender without closing another session");
            check(inbox.TryTake(out var item) && ReferenceEquals(item.Peer, firstClient) && !inbox.TryTake(out _), "inbound queue preserves the session identity and releases capacity");
            check(!inbox.TryAdd(firstClient, new(MessageKind.Key, 65, 30, 2)), "closed sender cannot refill the input queue");

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                for (int i = 0; i < 10000; i++) if (!otherClient.Send(new(MessageKind.Move, i))) break;
            })).ToArray();
            start.SetResult();
            await Task.WhenAll(otherClient.DisposeAsync().AsTask(), otherClient.DisposeAsync().AsTask());
            await Task.WhenAll(writers);
            check(!otherClient.IsAlive && !otherClient.Send(new(MessageKind.Key, 65)), "concurrent send, stop and repeated asynchronous disposal are safe");
        }
        finally { listener.Stop(); }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var server = new TcpServer(new(IPAddress.Loopback, 0), async (socket, token) =>
        {
            entered.TrySetResult();
            await using var peer = await Connection.AcceptAsync(socket, identity, 1920, 1080, token);
            accepted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }))
        {
            using var stalled = new TcpClient(); await stalled.ConnectAsync(server.LocalEndpoint, ct);
            await entered.Task.WaitAsync(ct);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct); handshake.CancelAfter(TimeSpan.FromSeconds(2));
            await using var valid = await Connection.ConnectAsync("127.0.0.1", server.LocalEndpoint.Port, Invitation.Parse(identity.Invitation), 1920, 1080, handshake.Token);
            await accepted.Task.WaitAsync(ct);
            check(valid.IsAlive, "valid input handshake succeeds beside a stalled TLS client");
            server.Stop(); await server.Completion.WaitAsync(ct);
            check(server.Completion.IsCompletedSuccessfully, "listener stop drains active and stalled handlers");
        }

        int active = 0, maximum = 0;
        using (var server = new TcpServer(new(IPAddress.Loopback, 0), async (_, token) =>
        {
            int count = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref maximum); }
            while (count > observed && Interlocked.CompareExchange(ref maximum, count, observed) != observed);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Interlocked.Decrement(ref active); }
        }, capacity: 2))
        {
            using var one = new TcpClient(); using var two = new TcpClient(); using var excess = new TcpClient();
            await one.ConnectAsync(server.LocalEndpoint, ct); await two.ConnectAsync(server.LocalEndpoint, ct);
            while (Volatile.Read(ref active) != 2) await Task.Delay(10, ct);
            await excess.ConnectAsync(server.LocalEndpoint, ct);
            bool closed;
            try { closed = await excess.GetStream().ReadAsync(new byte[1], ct) == 0; }
            catch (IOException) { closed = true; }
            check(closed && maximum == 2, "listener rejects excess clients without creating unbounded workers");
            server.Stop(); await server.Completion.WaitAsync(ct);
            check(active == 0, "all bounded listener workers finish on shutdown");
        }

        using var requester = new PairingIdentity();
        var resultArrived = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var apply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int prompts = 0, failures = 0;
        bool holdResult = true;
        using (var server = new PairingServer(identity, (_, _) => { Interlocked.Increment(ref prompts); return Task.FromResult(true); }, () => true, _ => true, 0))
        {
            server.Failed += _ => Interlocked.Increment(ref failures);
            server.Paired += (_, epoch) => { resultArrived.TrySetResult(epoch); return Volatile.Read(ref holdResult) ? apply.Task : Task.CompletedTask; };
            using var stalled = new TcpClient(); await stalled.ConnectAsync(IPAddress.Loopback, server.LocalEndpoint.Port, ct);
            await EasyPairing.RequestAsync("127.0.0.1", server.LocalEndpoint.Port, identity.Fingerprint, "Requester", requester, (_, _) => Task.FromResult(true), ct);
            var epoch = await resultArrived.Task.WaitAsync(ct);
            check(server.IsPairing && prompts == 1, "pairing reservation survives the network exchange until the UI applies its result");
            bool rejected = false;
            try { await EasyPairing.RequestAsync("127.0.0.1", server.LocalEndpoint.Port, identity.Fingerprint, "Second", requester, (_, _) => Task.FromResult(true), ct); }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException) { rejected = true; }
            check(rejected && prompts == 1 && failures == 0, "competing pairing cannot replace or clear an active confirmation");
            server.CancelPairing();
            check(epoch.IsCancellationRequested, "pause invalidates completed pairing results queued for the UI");
            while (server.IsPairing) await Task.Delay(10, ct);
            Volatile.Write(ref holdResult, false);
            await EasyPairing.RequestAsync("127.0.0.1", server.LocalEndpoint.Port, identity.Fingerprint, "Retry", requester, (_, _) => Task.FromResult(true), ct);
            check(prompts == 2, "pairing can resume with a fresh epoch after cancellation");
            apply.TrySetResult();
        }
    }
}
