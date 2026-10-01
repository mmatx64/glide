using System.Net;
using System.Net.Sockets;
using Glide.Core;
using static Glide.Native;

namespace Glide;

internal static class WheelTests
{
    internal static async Task Run(List<string> lines)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        using var identity = new PairingIdentity();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        async Task<(Connection Client, Connection Server)> Connect()
        {
            var connecting = Connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                Invitation.Parse(identity.Invitation), 1920, 1080, ct);
            var accept = Connection.AcceptAsync(await listener.AcceptTcpClientAsync(ct), identity, 1920, 1080, ct);
            return (await connecting, await accept);
        }
        try
        {
            var pair = await Connect();
            await using var client = pair.Client; await using var server = pair.Server;
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var actual = new List<string>();
            int wheelCalls = 0, largestBatch = 0;
            bool first = true;
            using (var worker = new InputWorker(inputs =>
            {
                if (first) { first = false; entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Wheel test gate timed out."); }
                if (inputs[0].Type == 0 && inputs[0].Data.Mouse.Flags is 2048 or 4096)
                { wheelCalls++; largestBatch = Math.Max(largestBatch, inputs.Length); }
                foreach (var input in inputs) actual.Add(Describe(input));
                if (inputs[^1].Type == 1 && inputs[^1].Data.Keyboard.Scan == 30 && (inputs[^1].Data.Keyboard.Flags & 2) != 0) finished.Set();
                return (uint)inputs.Length;
            }))
            {
                worker.Attach(server, false, true);
                worker.Receive(server, new(MessageKind.Activate, 1000, 2000));
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Receiver did not activate.");
                var packets = new List<Packet>();
                for (int i = 0; i < 192; i++)
                {
                    packets.Add(new(MessageKind.Button, i % 3 == 0 ? 4096 : 2048, (i % 4) switch { 0 => 1, 1 => -1, 2 => 120, _ => -120 }));
                    if (i == 47) packets.Add(new(MessageKind.Move, 2000, 3000));
                    if (i == 95) packets.Add(new(MessageKind.Button, 2));
                    if (i == 143) packets.Add(new(MessageKind.Button, 4));
                }
                // Release must suppress subsequent wheels until a new activation.
                packets.Add(new(MessageKind.Release));
                packets.Add(new(MessageKind.Button, 2048, 999));
                packets.Add(new(MessageKind.Activate, 3000, 4000));
                packets.Add(new(MessageKind.Button, 2048, -7));
                packets.Add(new(MessageKind.Key, 65, 30));
                packets.Add(new(MessageKind.Key, 65, 30, 2));
                for (int i = 0; i < packets.Count; i++)
                {
                    worker.Receive(server, packets[i]);
                    if (i == 70) worker.Receive(client, new(MessageKind.Button, 2048, 666)); // stale session identity
                }
                resume.Set();
                if (!finished.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Wheel burst did not drain.");
                var expected = new List<string> { "M:49153:0:1000:2000" };
                bool receiving = true;
                foreach (var packet in packets)
                {
                    if (packet.Kind == MessageKind.Release) { receiving = false; continue; }
                    if (packet.Kind == MessageKind.Activate) receiving = true;
                    if (!receiving) continue;
                    expected.Add(packet.Kind switch
                    {
                        MessageKind.Move or MessageKind.Activate => $"M:49153:0:{packet.A}:{packet.B}",
                        MessageKind.Button => $"M:{packet.A}:{packet.B}:0:0",
                        MessageKind.Key => $"K:0:{packet.B}:{packet.C | 8}",
                        _ => throw new Exception("Unexpected test packet.")
                    });
                }
                if (!actual.SequenceEqual(expected) || largestBatch != 32 || wheelCalls > 12 || !server.IsAlive)
                    throw new Exception($"Wheel batching lost input/order or did not reduce injection calls: {wheelCalls}, maximum {largestBatch}.");
                lines.Add($"PASS 193 wheel INPUT records in {wheelCalls} mock injection calls (maximum {largestBatch}); exact small/signed deltas, axes, reversals, position/button/key barriers, stale-peer rejection and Release/reactivation order");

                // An isolated event must be delivered immediately without waiting
                // for the batch to fill, using the same production receive loop.
                finished.Reset();
                worker.Receive(server, new(MessageKind.Button, 4096, 1));
                worker.Receive(server, new(MessageKind.Key, 65, 30, 2));
                if (!finished.Wait(TimeSpan.FromSeconds(2)) || actual[^2] != "M:4096:1:0:0")
                    throw new Exception("Isolated wheel stalled.");
            }

            var failedPair = await Connect();
            await using var failedClient = failedPair.Client; await using var failedServer = failedPair.Server;
            using var failed = new ManualResetEventSlim();
            using var failureEntered = new ManualResetEventSlim();
            using var failureResume = new ManualResetEventSlim();
            var releases = new List<string>();
            bool firstFailureInput = true;
            using (var worker = new InputWorker(inputs =>
            {
                if (firstFailureInput)
                {
                    firstFailureInput = false; failureEntered.Set();
                    if (!failureResume.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Failure test gate timed out.");
                }
                if (inputs[0].Type == 0 && inputs[0].Data.Mouse.Flags is 2048 or 4096)
                {
                    if (inputs.Length != 2) throw new Exception("Partial-injection test needs two wheel records.");
                    return 1; // Windows inserted only part of the requested batch
                }
                foreach (var input in inputs) releases.Add(Describe(input));
                return (uint)inputs.Length;
            }))
            {
                worker.Notice += _ => failed.Set();
                worker.Attach(failedServer, false, true);
                worker.Receive(failedServer, new(MessageKind.Activate, 1000, 2000));
                if (!failureEntered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Failure receiver did not activate.");
                worker.Receive(failedServer, new(MessageKind.Key, 65, 30));
                worker.Receive(failedServer, new(MessageKind.Button, 2));
                worker.Receive(failedServer, new(MessageKind.Button, 2048, 120));
                worker.Receive(failedServer, new(MessageKind.Button, 2048, -120));
                failureResume.Set();
                if (!failed.Wait(TimeSpan.FromSeconds(5)) || failedServer.IsAlive
                    || !releases.Contains("K:0:30:10") || !releases.Contains("M:4:0:0:0"))
                    throw new Exception("Failed wheel injection did not stop and release held input.");
            }
            lines.Add("PASS isolated wheel delivery and partial wheel injection releases held keys/buttons and stops the sender (mock injection; no desktop input)");

            using var emergencyEntered = new ManualResetEventSlim();
            using var emergencyResume = new ManualResetEventSlim();
            using var emergencyStopped = new ManualResetEventSlim();
            int emergencyWheels = 0;
            bool firstEmergencyInput = true;
            using (var worker = new InputWorker(inputs =>
            {
                if (firstEmergencyInput)
                {
                    firstEmergencyInput = false; emergencyEntered.Set();
                    if (!emergencyResume.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Emergency test gate timed out.");
                }
                foreach (var input in inputs)
                    if (input.Type == 0 && input.Data.Mouse.Flags is 2048 or 4096) emergencyWheels++;
                return (uint)inputs.Length;
            }))
            {
                worker.EmergencyStopped += () => emergencyStopped.Set();
                worker.Attach(server, false, true);
                worker.Receive(server, new(MessageKind.Activate, 1000, 2000));
                if (!emergencyEntered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Emergency receiver did not activate.");
                for (int i = 0; i < 192; i++) worker.Receive(server, new(MessageKind.Button, 2048, 120));
                worker.Emergency(); emergencyResume.Set();
                if (!emergencyStopped.Wait(TimeSpan.FromSeconds(5)) || server.IsAlive || emergencyWheels != 0)
                    throw new Exception("Wheel backlog delayed emergency stop or injected after stop.");
            }
            lines.Add("PASS emergency command preempts a 192-wheel backlog without injecting queued input (mock injection; no desktop input)");
        }
        finally { listener.Stop(); }
    }

    private static string Describe(Input input)
    {
        if (input.Type == 0)
        {
            var mouse = input.Data.Mouse;
            if (mouse.Extra == 0) throw new Exception("Injected wheel lost its loop-prevention tag.");
            return $"M:{mouse.Flags}:{unchecked((int)mouse.Data)}:{mouse.X}:{mouse.Y}";
        }
        var key = input.Data.Keyboard;
        return $"K:{key.Vk}:{key.Scan}:{key.Flags}";
    }
}
