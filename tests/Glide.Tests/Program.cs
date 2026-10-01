using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Glide.Core;

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
    Check((await queue.TakeAsync(ct)).A == 7999, "8000 queued motion events collapse to latest position");
    Check((await queue.TakeAsync(ct)).Kind == MessageKind.Button && (await queue.TakeAsync(ct)).A == 9000 && (await queue.TakeAsync(ct)).A == 4,
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
async Task Ignore(Task task) { try { await task; } catch (Exception) { } }

var accept = Accept();
using var client = await Connection.ConnectAsync("127.0.0.1", port, invitation, 1920, 1080, ct);
using var server = await accept;
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
server.Dispose(); await Ignore(serverRun); await Ignore(clientRun);
Check(!client.IsAlive, "disconnect stops session");
server.Finish(); client.Finish();

var wrongSecret = new Invitation(invitation.Fingerprint, new byte[32]);
accept = Accept();
await Reject(async () => { using var peer = await Connection.ConnectAsync("127.0.0.1", port, wrongSecret, 1920, 1080, ct); }, "wrong pairing secret rejected");
await Ignore(accept);
var wrongPin = new Invitation(new byte[32], invitation.Secret);
accept = Accept();
await Reject(async () => { using var peer = await Connection.ConnectAsync("127.0.0.1", port, wrongPin, 1920, 1080, ct); }, "wrong server certificate rejected before pairing secret is sent");
await Ignore(accept);

// Authenticated peer goes silent without closing its socket: watchdog must recover.
accept = Accept();
using var stallClient = await Connection.ConnectAsync("127.0.0.1", port, invitation, 1920, 1080, ct);
using var stallServer = await accept;
var watch = Stopwatch.StartNew();
await Reject(() => stallClient.RunAsync(ct), "silent connection watchdog fires");
Check(watch.Elapsed.TotalSeconds < 3 && !stallClient.IsAlive, "silent-peer recovery occurs within three seconds");
stallClient.Finish(); stallServer.Finish(); listener.Stop();
Console.WriteLine($"All {passed} checks passed.");
await DiscoveryPairingTests.Run(Check);
Console.WriteLine($"All {passed} checks passed including discovery and confirmed pairing.");
static void CheckSilent(bool value) { if (!value) throw new Exception("Queue rejected coalescible motion."); }
