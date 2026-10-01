using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Glide.Core;

internal static class TransportBenchmark
{
    internal static async Task Run()
    {
        // Synthetic packets only: no hooks, input injection, or saved credentials.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = timeout.Token;
        using var identity = new PairingIdentity();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var accept = Task.Run(async () => await Connection.AcceptAsync(await listener.AcceptTcpClientAsync(ct), identity, 1920, 1080, ct));
            await using var client = await Connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                Invitation.Parse(identity.Invitation), 1920, 1080, ct);
            await using var server = await accept;
            using var arrived = new SemaphoreSlim(0);
            var samples = new List<double>();
            int sequence = 0;
            server.Input += packet => server.Send(packet);
            client.Input += packet =>
            {
                if (packet.A != ++sequence) throw new Exception("Transport lost or reordered a reliable packet.");
                samples.Add(Stopwatch.GetElapsedTime(packet.Stamp).TotalMilliseconds);
                arrived.Release();
            };
            var clientRun = client.RunAsync(ct); var serverRun = server.RunAsync(ct);
            int sentSequence = 0;
            async Task SendBursts(int bursts, int size)
            {
                for (int burst = 0; burst < bursts; burst++)
                {
                    for (int i = 0; i < size; i++)
                        if (!client.Send(new Packet(MessageKind.Key, ++sentSequence, 30, (i & 1) * 2, Stopwatch.GetTimestamp())))
                            throw new Exception("Benchmark queue overflowed.");
                    for (int i = 0; i < size; i++) await arrived.WaitAsync(ct);
                }
            }
            async Task Measure(string name, int bursts, int size)
            {
                samples.Clear();
                long allocated = GC.GetTotalAllocatedBytes(true), writes = client.WriteOperations + server.WriteOperations;
                var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                var elapsed = Stopwatch.StartNew();
                await SendBursts(bursts, size);
                elapsed.Stop();
                double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
                long allocationDelta = GC.GetTotalAllocatedBytes(true) - allocated;
                var ordered = samples.Order().ToArray();
                double Percentile(double p) => ordered[(int)Math.Ceiling(ordered.Length * p) - 1];
                Console.WriteLine($"MEASURE {name}: packets={ordered.Length}, elapsed={elapsed.Elapsed.TotalMilliseconds:F1} ms, " +
                    $"RTT median={Percentile(.5):F3} p95={Percentile(.95):F3} p99={Percentile(.99):F3} max={ordered[^1]:F3} ms, " +
                    $"TLS writes={client.WriteOperations + server.WriteOperations - writes}, process CPU={cpuMs:F1} ms, allocations={allocationDelta} bytes");
            }
            try
            {
                await SendBursts(100, 16);
                await Measure("sequential encrypted loopback", 2000, 1);
                await Measure("64-event encrypted loopback bursts", 1000, 64);
            }
            finally
            {
                client.Stop(); server.Stop();
                try { await Task.WhenAll(clientRun, serverRun); } catch (Exception) { }
                await client.DisposeAsync(); await server.DisposeAsync();
            }
            Console.WriteLine("Loopback synthetic transport only; excludes two-PC Wi-Fi, input hooks, injection, and display latency.");
        }
        finally { listener.Stop(); }
    }
}
