using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Glide.Core;
using static TestSupport;

if (args.Contains("--benchmark")) { await TransportBenchmark.Run(); return; }

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
async Task Reject(Func<Task> action, string name)
{
    bool rejected = false;
    try { await action(); } catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException) { rejected = true; }
    Check(rejected, name);
}
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
var ct = deadline.Token;
await OutboxBatchTests.Run(Check, ct);
var original = new Packet(MessageKind.Key, 0x41, 30, 3, 123456789012);
var bytes = new byte[Packet.Size]; original.Write(bytes);
Check(Packet.Read(bytes) == original, "binary protocol round-trip");
bytes[0] = 255;
try { Packet.Read(bytes); throw new Exception("Accepted unknown message."); }
catch (InvalidDataException) { Check(true, "unknown message rejected"); }
Check(Coordinates.Normalize(0, 1920) == 0 && Coordinates.Normalize(1919, 1920) == 65535, "pixel endpoints map exactly");
Check(Math.Abs(Coordinates.Denormalize(Coordinates.Normalize(1300, 3840), 3840) - 1300) < .04, "mixed-resolution coordinate mapping");
Check(Coordinates.Normalize(-100, 1920) == 0 && Coordinates.Normalize(4000, 1920) == 65535, "coordinate clamping");
using (var queue = new Outbox(4))
{
    for (int i = 0; i < 8000; i++) CheckSilent(queue.TryAdd(new Packet(MessageKind.Move, i)));
    queue.TryAdd(new Packet(MessageKind.Button, 2)); queue.TryAdd(new Packet(MessageKind.Move, 9000));
    queue.TryAdd(new Packet(MessageKind.Button, 4));
    Check(!queue.TryAdd(new Packet(MessageKind.Key, 65)), "reliable-event overflow fails closed");
    Check((await TestSupport.TakeOne(queue, ct)).A == 7999, "8000 queued motion events collapse to latest position");
    Check((await TestSupport.TakeOne(queue, ct)).Kind == MessageKind.Button && (await TestSupport.TakeOne(queue, ct)).A == 9000 && (await TestSupport.TakeOne(queue, ct)).A == 4,
        "motion coalescing preserves click ordering");
    Check(queue.Coalesced == 7999, "coalescing telemetry");
}
using var identity = new PairingIdentity();
var invitation = Invitation.Parse(identity.Invitation);
Check(invitation.Encode() == identity.Invitation, "pairing code round-trip");
foreach (string bad in new[] { "", "GLIDE1-abc", "GLIDE2-" + new string('A', 88) })
{
    try { Invitation.Parse(bad); throw new Exception("Bad invitation accepted."); }
    catch (FormatException) { }
}
Check(true, "malformed invitations rejected");
using (var restored = new PairingIdentity(identity.Export(), identity.Secret.ToArray()))
    Check(restored.Invitation == identity.Invitation, "certificate and secret persist together");

var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
async Task<Connection> Accept()
{
    try { return await Connection.AcceptAsync(await listener.AcceptTcpClientAsync(ct), identity, 2560, 1440, ct); }
    catch (Exception ex) { Console.WriteLine("SERVER: " + ex.Message + " " + ex.InnerException?.Message); throw; }
}

var accept = Accept();
await using var client = await Connection.ConnectAsync("127.0.0.1", port, invitation, 1920, 1080, ct);
await using var server = await accept;
Check(client.RemoteWidth == 2560 && server.RemoteWidth == 1920, "TLS-authenticated desktop negotiation");
var samples = new ConcurrentQueue<double>();
var echoes = new ConcurrentQueue<Packet>();
var arrived = new SemaphoreSlim(0);
server.Input += packet => server.Send(packet);
client.Input += packet => { echoes.Enqueue(packet); samples.Enqueue(Stopwatch.GetElapsedTime(packet.Stamp).TotalMilliseconds); arrived.Release(); };
var serverRun = server.RunAsync(ct); var clientRun = client.RunAsync(ct);
for (int i = 0; i < 250; i++)
{
    client.Send(new Packet(MessageKind.Move, i, 123, Stamp: Stopwatch.GetTimestamp()));
    await arrived.WaitAsync(ct);
}
Check(echoes.Count == 250 && echoes.Last().A == 249, "250 encrypted input round-trips without loss");
var ordered = samples.Order().ToArray();
Console.WriteLine($"MEASURE loopback encrypted RTT: median={ordered[125]:F3} ms, p95={ordered[237]:F3} ms, p99={ordered[247]:F3} ms (not two-PC latency)");
await Task.Delay(500, ct);
Check(client.RoundTripMs > 0, "idle heartbeat keeps channel warm and measures RTT");
echoes.Clear();
for (int burst = 0; burst < 5; burst++)
{
    var expected = Enumerable.Range(0, 96).Select(i => (i % 4) switch
    {
        0 => new Packet(MessageKind.Move, i, burst, Stamp: Stopwatch.GetTimestamp()),
        1 => new Packet(MessageKind.Key, 65, 30, Stamp: Stopwatch.GetTimestamp()),
        2 => new Packet(MessageKind.Key, 65, 30, 2, Stopwatch.GetTimestamp()),
        _ => new Packet(MessageKind.Button, 4, Stamp: Stopwatch.GetTimestamp())
    }).ToArray();
    foreach (var packet in expected) CheckSilent(client.Send(packet));
    foreach (var _ in expected) await arrived.WaitAsync(ct);
    CheckSilent(echoes.ToArray().SequenceEqual(expected)); echoes.Clear();
}
Check(true, "batched TLS preserves 480 mixed movement/key/button events in exact order");
server.Stop(); await Ignore(serverRun); await Ignore(clientRun);
Check(!client.IsAlive, "disconnect stops session");
await server.DisposeAsync(); await client.DisposeAsync();

var wrongSecret = new Invitation(invitation.Fingerprint, new byte[32]);
accept = Accept();
await Reject(async () => { await using var peer = await Connection.ConnectAsync("127.0.0.1", port, wrongSecret, 1920, 1080, ct); }, "wrong pairing secret rejected");
await Ignore(accept);
var wrongPin = new Invitation(new byte[32], invitation.Secret);
accept = Accept();
await Reject(async () => { await using var peer = await Connection.ConnectAsync("127.0.0.1", port, wrongPin, 1920, 1080, ct); }, "wrong server certificate rejected before pairing secret is sent");
await Ignore(accept);

// Authenticated peer goes silent without closing its socket: watchdog must recover.
accept = Accept();
await using var stallClient = await Connection.ConnectAsync("127.0.0.1", port, invitation, 1920, 1080, ct);
await using var stallServer = await accept;
var watch = Stopwatch.StartNew();
await Reject(() => stallClient.RunAsync(ct), "silent connection watchdog fires");
Check(watch.Elapsed.TotalSeconds < 3 && !stallClient.IsAlive, "silent-peer recovery occurs within three seconds");
await stallClient.DisposeAsync(); await stallServer.DisposeAsync(); listener.Stop();
await DiscoveryPairingTests.Run(Check);
await LifecycleTests.Run(Check);
Console.WriteLine($"All {passed} checks passed including discovery and confirmed pairing.");
static void CheckSilent(bool value) { if (!value) throw new Exception("Queue rejected coalescible motion."); }
