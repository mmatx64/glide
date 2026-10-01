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
            var batch = new (Connection Peer, Packet Packet)[1];
            check(inbox.TakeBatch(batch) == 1 && ReferenceEquals(batch[0].Peer, firstClient) && inbox.TakeBatch(batch) == 0 && !inbox.CompleteBatch(), "inbound queue preserves the session identity and releases capacity");
            check(!inbox.TryAdd(firstClient, new(MessageKind.Key, 65, 30, 2)), "closed sender cannot refill the input queue");

            var burstInbox = new Inbox();
            int wakes = 0;
            var burst = Enumerable.Range(0, 192).Select(i => new Packet(MessageKind.Button, i % 3 == 0 ? 4096 : 2048, i % 2 == 0 ? 1 : -120)).ToArray();
            foreach (var packet in burst)
            {
                if (!burstInbox.TryAdd(otherClient, packet, out bool wake)) throw new Exception("Burst overflowed.");
                if (wake) wakes++;
            }
            var receivedBurst = new List<Packet>();
            var burstBatch = new (Connection Peer, Packet Packet)[32];
            int drains = 0;
            do
            {
                int length = burstInbox.TakeBatch(burstBatch); drains++;
                receivedBurst.AddRange(burstBatch.Take(length).Select(item => item.Packet));
            } while (burstInbox.CompleteBatch());
            check(wakes == 1 && drains == 6 && receivedBurst.SequenceEqual(burst),
                "192 wheel events use one initial wake and six bounded drains without losing deltas, axes or reversals");

            var atomicInbox = new Inbox(32);
            check(atomicInbox.TryAddBatch(otherClient, burst.AsSpan(0, 32), out bool batchWake) && batchWake,
                "a whole 32-packet receive batch is queued before its first wake");
            check(atomicInbox.TakeBatch(burstBatch) == 32 && burstBatch.Select(item => item.Packet).SequenceEqual(burst.Take(32)),
                "atomic receive batching preserves original wheel events");
            check(atomicInbox.TryAddBatch(otherClient, burst.AsSpan(32, 32), out batchWake) && !batchWake && atomicInbox.CompleteBatch(),
                "batch arrivals during injection use the existing continuation");
            check(atomicInbox.TakeBatch(burstBatch) == 32 && !atomicInbox.CompleteBatch()
                && atomicInbox.TryAddBatch(otherClient, burst.AsSpan(0, 1), out batchWake) && batchWake,
                "batch completion rearms an immediate isolated-event wake");
            var overflowPair = await Pair();
            await using var overflowClient = overflowPair.Client; await using var overflowServer = overflowPair.Server;
            var smallInbox = new Inbox(2);
            smallInbox.TryAdd(overflowClient, new(MessageKind.Key, 65, 30));
            check(!smallInbox.TryAddBatch(overflowClient, burst.AsSpan(0, 2), out _) && !overflowClient.IsAlive
                && smallInbox.TakeBatch(burstBatch) == 1,
                "receive-batch overflow closes the sender without enqueueing a partial batch");

            // Exercise both sides of the enqueue/complete race with a real producer
            // and consumer. Capacity reservations keep this test below overflow.
            var racingInbox = new Inbox();
            using var slots = new SemaphoreSlim(256, 256);
            using var notifications = new SemaphoreSlim(0);
            var consumer = Task.Run(async () =>
            {
                var packets = new (Connection Peer, Packet Packet)[32];
                int expected = 0;
                while (expected < 10000)
                {
                    await notifications.WaitAsync(ct);
                    int length = racingInbox.TakeBatch(packets);
                    for (int i = 0; i < length; i++)
                    {
                        if (packets[i].Packet.B != expected++ || !ReferenceEquals(packets[i].Peer, otherClient))
                            throw new Exception("Receiver reordered input.");
                        slots.Release();
                    }
                    await Task.Yield(); // arrivals during injection must not lose their wake
                    if (racingInbox.CompleteBatch()) notifications.Release();
                }
            });
            for (int i = 0; i < 10000; i++)
            {
                await slots.WaitAsync(ct);
                if (!racingInbox.TryAdd(otherClient, new(MessageKind.Button, 2048, i), out bool wake))
                    throw new Exception("Reserved input queue overflowed.");
                if (wake) notifications.Release();
                if (i % 7 == 0) await Task.Yield();
            }
            await consumer.WaitAsync(ct);
            check(!racingInbox.CompleteBatch() && otherClient.IsAlive,
                "10000 concurrent receiver arrivals retain order and never lose a wake during batch completion");

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
