// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Glide contributors

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Glide.Core;
using static Glide.Native;

namespace Glide;

internal static class WheelPipelineTests
{
    internal static async Task Run(List<string> lines)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        using var identity = new PairingIdentity();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var connecting = Connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                Invitation.Parse(identity.Invitation), 1920, 1080, ct);
            var accepting = Connection.AcceptAsync(await listener.AcceptTcpClientAsync(ct), identity, 1920, 1080, ct);
            await using var client = await connecting; await using var server = await accepting;
            using var activated = new ManualResetEventSlim();
            using var arrived = new SemaphoreSlim(0);
            var stamps = new long[13000];
            var samples = new double[13000];
            int received = 0, calls = 0, maximum = 0;
            using var worker = new InputWorker(inputs =>
            {
                if (inputs[0].Data.Mouse.Flags == 0xc001) { activated.Set(); return (uint)inputs.Length; }
                calls++; maximum = Math.Max(maximum, inputs.Length);
                foreach (var input in inputs)
                {
                    int expectedFlags = received % 3 == 0 ? 4096 : 2048;
                    int expectedDelta = received % 2 == 0 ? 1 : -120;
                    if (input.Type != 0 || input.Data.Mouse.Flags != expectedFlags || unchecked((int)input.Data.Mouse.Data) != expectedDelta)
                        throw new Exception("TLS-to-injection wheel order changed.");
                    samples[received] = Stopwatch.GetElapsedTime(stamps[received]).TotalMilliseconds;
                    received++; arrived.Release();
                }
                return (uint)inputs.Length;
            });
            worker.Attach(server, false, true);
            server.InputBatch += packets => worker.ReceiveBatch(server, packets);
            var clientRun = client.RunAsync(ct); var serverRun = server.RunAsync(ct);
            try
            {
                if (!client.Send(new(MessageKind.Activate, 1000, 2000)) || !activated.Wait(TimeSpan.FromSeconds(3)))
                    throw new Exception("TLS-to-injection receiver did not activate.");
                int sent = 0;
                async Task Measure(string name, int bursts, int size)
                {
                    int start = sent, startCalls = calls;
                    var elapsed = Stopwatch.StartNew();
                    for (int burst = 0; burst < bursts; burst++)
                    {
                        for (int i = 0; i < size; i++)
                        {
                            stamps[sent] = Stopwatch.GetTimestamp();
                            if (!client.Send(new(MessageKind.Button, sent % 3 == 0 ? 4096 : 2048, sent % 2 == 0 ? 1 : -120)))
                                throw new Exception("TLS-to-injection queue overflowed.");
                            sent++;
                        }
                        for (int i = 0; i < size; i++) await arrived.WaitAsync(ct);
                    }
                    var sorted = samples[start..sent].Order().ToArray();
                    lines.Add($"MEASURE TLS-to-mock-injection {name}: events={sorted.Length}, calls={calls - startCalls}, " +
                        $"elapsed={elapsed.Elapsed.TotalMilliseconds:F1} ms, latency median={sorted[sorted.Length / 2]:F3} " +
                        $"p95={sorted[(int)(sorted.Length * .95)]:F3} p99={sorted[(int)(sorted.Length * .99)]:F3} max={sorted[^1]:F3} ms");
                }
                await Measure("64-wheel bursts", 200, 64);
                await Measure("isolated wheel", 200, 1);
                if (received != 13000 || maximum > 32) throw new Exception("TLS-to-injection lost input or exceeded its batch bound.");
                lines.Add("PASS 13000 encrypted wheels reach the production receiver loop with exact deltas/axes/order; measured locally with mock injection, no desktop input");
            }
            finally
            {
                client.Stop(); server.Stop();
                try { await Task.WhenAll(clientRun, serverRun); } catch (Exception) { }
            }
        }
        finally { listener.Stop(); }
    }
}
