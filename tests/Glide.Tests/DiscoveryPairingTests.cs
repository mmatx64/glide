using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Glide.Core;

internal static class DiscoveryPairingTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        using var local = new PairingIdentity(); using var other = new PairingIdentity();
        var announcement = new Announcement("LAPTOP-02", other.Fingerprint, true, false);
        var data = announcement.Encode(); var decoded = Announcement.Decode(data);
        check(decoded.Name == announcement.Name && decoded.Id == announcement.Id && decoded.Receiver, "bounded discovery packet round-trip");
        check(data.AsSpan().IndexOf(other.Secret) == -1, "discovery never includes pairing secret");
        bool malformed = false;
        data[41] = 255;
        try { Announcement.Decode(data); } catch (InvalidDataException) { malformed = true; }
        check(malformed, "malformed discovery payload rejected");
        var directory = new PeerDirectory(local.Fingerprint);
        directory.Observe(new Announcement("Self", local.Fingerprint, false, false), IPAddress.Loopback, 100);
        directory.Observe(announcement, IPAddress.Parse("192.168.1.2"), 100);
        directory.Observe(announcement, IPAddress.Parse("192.168.1.3"), 300);
        check(directory.Snapshot(300).Length == 1 && directory.Snapshot(300)[0].Address.ToString() == "192.168.1.3", "self-filtering and peer IP changes");
        check(directory.Snapshot(7401).Length == 0, "offline peers expire");
        for (int i = 0; i < 100; i++) directory.Observe(new Announcement("PC" + i, SHA256.HashData(BitConverter.GetBytes(i)), false, false), IPAddress.Loopback, 8000);
        check(directory.Snapshot(8000).Length == 32, "discovery table bounded under unsolicited traffic");

        using var initialTarget = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var targets = new[] { (IPEndPoint)initialTarget.Client.LocalEndPoint! };
        using (var first = new DiscoveryService(local.Fingerprint, () => new("DESKTOP", local.Fingerprint, false, false), new(IPAddress.Loopback, 0), targets))
        using (var second = new DiscoveryService(other.Fingerprint, () => announcement, new(IPAddress.Loopback, 0), [first.LocalEndpoint]))
        {
            targets[0] = second.LocalEndpoint;
            while (first.Peers.Snapshot(Environment.TickCount64).Length == 0 || second.Peers.Snapshot(Environment.TickCount64).Length == 0)
                await Task.Delay(30, ct);
            check(first.Peers.Snapshot(Environment.TickCount64)[0].Info.Name == "LAPTOP-02", "two real UDP endpoints discover each other");
        }
        byte[] nonce = RandomNumberGenerator.GetBytes(32), serverNonce = RandomNumberGenerator.GetBytes(32);
        check(!EasyPairing.Commitment(nonce, local.Fingerprint).AsSpan().SequenceEqual(EasyPairing.Commitment(nonce, other.Fingerprint)), "pairing commitment binds the certificate");
        check(EasyPairing.ComparisonCode(other.Fingerprint, nonce, serverNonce) != EasyPairing.ComparisonCode(other.Fingerprint, nonce, RandomNumberGenerator.GetBytes(32)), "comparison codes bind a fresh pairing session");

        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var serverPrompt = new TaskCompletionSource<PairingPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var clientPrompt = new TaskCompletionSource<PairingPrompt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverTask = Accept();
            async Task<PairedPeer> Accept() => await EasyPairing.AcceptAsync(await listener.AcceptTcpClientAsync(ct), other,
                async (prompt, token) => { serverPrompt.TrySetResult(prompt); return await serverDecision.Task.WaitAsync(token); }, ct);
            var clientTask = EasyPairing.RequestAsync("127.0.0.1", port, other.Fingerprint, "DESKTOP", Invitation.Parse(local.Invitation),
                (prompt, _) => { clientPrompt.TrySetResult(prompt); return Task.FromResult(true); }, ct);
            var shownServer = await serverPrompt.Task.WaitAsync(ct); var shownClient = await clientPrompt.Task.WaitAsync(ct);
            await Task.Delay(80, ct);
            check(!clientTask.IsCompleted && !serverTask.IsCompleted, "credentials withheld until both PCs approve");
            check(shownServer.Code == shownClient.Code && shownServer.PeerName == "DESKTOP", "both PCs show the same session confirmation code");
            serverDecision.SetResult(true);
            var invitation = await clientTask; var paired = await serverTask;
            check(invitation.Encode() == other.Invitation && paired.Invitation.Encode() == local.Invitation, "confirmed pairing exchanges both identities over TLS for role reversal");

            var rejectServer = Task.Run(async () => await EasyPairing.AcceptAsync(await listener.AcceptTcpClientAsync(ct), other, (_, _) => Task.FromResult(false), ct));
            bool canceledPrompt = false;
            var rejectClient = EasyPairing.RequestAsync("127.0.0.1", port, other.Fingerprint, "DESKTOP", Invitation.Parse(local.Invitation),
                async (_, token) => { try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { canceledPrompt = true; } return false; }, ct);
            check(await Failed(rejectClient) && await Failed(rejectServer) && canceledPrompt, "rejection aborts pairing and cancels the other PC's pending prompt");

            bool approvalCalled = false;
            var badPinServer = Task.Run(async () => await EasyPairing.AcceptAsync(await listener.AcceptTcpClientAsync(ct), other,
                (_, _) => { approvalCalled = true; return Task.FromResult(true); }, ct));
            var badPinClient = EasyPairing.RequestAsync("127.0.0.1", port, local.Fingerprint, "DESKTOP", Invitation.Parse(local.Invitation), (_, _) => Task.FromResult(true), ct);
            check(await Failed(badPinClient) && await Failed(badPinServer) && !approvalCalled, "spoofed discovery fingerprint cannot reach pairing approval");

            // A sender that changes its nonce after learning the receiver nonce is rejected.
            var tamperedServer = Task.Run(async () => await EasyPairing.AcceptAsync(await listener.AcceptTcpClientAsync(ct), other,
                (_, _) => { approvalCalled = true; return Task.FromResult(true); }, ct));
            using var rawClient = new TcpClient(); await rawClient.ConnectAsync(IPAddress.Loopback, port, ct);
            using var tls = new SslStream(rawClient.GetStream(), false, (_, certificate, _, _) => certificate is not null && certificate.GetCertHash(HashAlgorithmName.SHA256).AsSpan().SequenceEqual(other.Fingerprint));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "Glide", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, ct);
            var hello = new byte[104]; "GLPAIR02"u8.CopyTo(hello); hello[8] = 1; hello[9] = (byte)'X';
            EasyPairing.Commitment(nonce, other.Fingerprint).CopyTo(hello, 72);
            await tls.WriteAsync(hello, ct); await tls.ReadExactlyAsync(new byte[32], ct);
            await tls.WriteAsync(RandomNumberGenerator.GetBytes(32), ct);
            check(await Failed(tamperedServer) && !approvalCalled, "altered committed nonce rejected before approval");
        }
        finally { listener.Stop(); }
    }
    private static async Task<bool> Failed(Task task)
    {
        try { await task; return false; }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException) { return true; }
    }
}
