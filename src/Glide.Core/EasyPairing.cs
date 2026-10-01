using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace Glide.Core;

public sealed record PairingPrompt(string PeerName, string Address, string Code, bool Incoming);
public sealed record PairedPeer(string Name, string Address, Invitation Invitation);

public static class EasyPairing
{
    public const int Port = 24821;
    public static byte[] Commitment(byte[] nonce, byte[] fingerprint) => SHA256.HashData("Glide commitment v2"u8.ToArray().Concat(nonce).Concat(fingerprint).ToArray());
    public static string ComparisonCode(byte[] fingerprint, byte[] clientNonce, byte[] serverNonce)
    {
        string code = Convert.ToHexString(SHA256.HashData("Glide compare v2"u8.ToArray().Concat(fingerprint).Concat(clientNonce).Concat(serverNonce).ToArray()))[..12];
        return $"{code[..4]} {code[4..8]} {code[8..]}";
    }
    public static async Task<Invitation> RequestAsync(string host, int port, byte[] fingerprint, string name, PairingIdentity localIdentity,
        Func<PairingPrompt, CancellationToken, Task<bool>> approve, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        var ct = timeout.Token;
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
        using var stream = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null &&
            CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), fingerprint));
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "Glide pairing",
            ClientCertificates = new() { localIdentity.Certificate },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => localIdentity.Certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, connectTimeout.Token).ConfigureAwait(false);
        byte[] clientNonce = RandomNumberGenerator.GetBytes(32);
        byte[] hello = new byte[104]; "GLPAIR03"u8.CopyTo(hello);
        byte[] encodedName = Encoding.ASCII.GetBytes(Announcement.CleanName(name));
        hello[8] = (byte)encodedName.Length; encodedName.CopyTo(hello, 9);
        Commitment(clientNonce, fingerprint).CopyTo(hello, 72);
        await stream.WriteAsync(hello, connectTimeout.Token).ConfigureAwait(false);
        byte[] serverNonce = new byte[32]; await stream.ReadExactlyAsync(serverNonce, connectTimeout.Token).ConfigureAwait(false);
        await stream.WriteAsync(clientNonce, connectTimeout.Token).ConfigureAwait(false);
        string code = ComparisonCode(fingerprint, clientNonce, serverNonce);
        await ConfirmBoth(stream, token => approve(new("Receiving PC", host, code, false), token), ct).ConfigureAwait(false);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var credentials = new byte[64]; await stream.ReadExactlyAsync(credentials, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(credentials.AsSpan(0, 32), fingerprint)) throw new AuthenticationException("Pairing identity changed.");
        var result = new Invitation(credentials[..32], credentials[32..]);
        CryptographicOperations.ZeroMemory(credentials);
        byte[] ownCredentials = localIdentity.Fingerprint.Concat(localIdentity.Secret).ToArray();
        try { await stream.WriteAsync(ownCredentials, ct).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(ownCredentials); }
        var acknowledged = new byte[1]; await stream.ReadExactlyAsync(acknowledged, ct).ConfigureAwait(false);
        if (acknowledged[0] != 1) throw new AuthenticationException("Pairing acknowledgement missing.");
        return result;
    }

    public static async Task<PairedPeer> AcceptAsync(TcpClient client, PairingIdentity identity,
        Func<PairingPrompt, CancellationToken, Task<bool>> approve, CancellationToken cancellation,
        Func<byte[], bool>? authorizeRequester = null)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(75)); var ct = timeout.Token;
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(6));
            client.NoDelay = true;
            byte[]? requesterFingerprint = null;
            using var stream = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                requesterFingerprint = certificate.GetCertHash(HashAlgorithmName.SHA256);
                return authorizeRequester?.Invoke(requesterFingerprint) ?? true;
            });
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = identity.Certificate,
                ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, handshakeTimeout.Token).ConfigureAwait(false);
            if (requesterFingerprint is null) throw new AuthenticationException("The requesting PC did not prove its identity.");
            var hello = new byte[104]; await stream.ReadExactlyAsync(hello, handshakeTimeout.Token).ConfigureAwait(false);
            if (!hello.AsSpan(0, 8).SequenceEqual("GLPAIR03"u8) || hello[8] is < 1 or > 63) throw new InvalidDataException("Invalid pairing request. Update Glide on both PCs.");
            string name = Encoding.ASCII.GetString(hello, 9, hello[8]);
            if (Announcement.CleanName(name) != name) throw new InvalidDataException("Invalid PC name.");
            byte[] fingerprint = identity.Fingerprint;
            byte[] serverNonce = RandomNumberGenerator.GetBytes(32);
            await stream.WriteAsync(serverNonce, handshakeTimeout.Token).ConfigureAwait(false);
            byte[] clientNonce = new byte[32]; await stream.ReadExactlyAsync(clientNonce, handshakeTimeout.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(Commitment(clientNonce, fingerprint), hello.AsSpan(72, 32)))
                throw new AuthenticationException("Pairing commitment mismatch.");
            string code = ComparisonCode(fingerprint, clientNonce, serverNonce);
            string address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
            await ConfirmBoth(stream, token => approve(new(name, address, code, true), token), ct).ConfigureAwait(false);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            // Pause/hide may revoke permission while the initiator is checking the code.
            if (authorizeRequester is not null && !authorizeRequester(requesterFingerprint))
                throw new AuthenticationException("Pairing is no longer available on this PC.");
            byte[] credentials = fingerprint.Concat(identity.Secret).ToArray();
            try { await stream.WriteAsync(credentials, ct).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(credentials); }
            var peerCredentials = new byte[64]; await stream.ReadExactlyAsync(peerCredentials, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(peerCredentials.AsSpan(0, 32), requesterFingerprint))
                throw new AuthenticationException("The requesting PC changed its pairing identity.");
            var invitation = new Invitation(peerCredentials[..32], peerCredentials[32..]);
            CryptographicOperations.ZeroMemory(peerCredentials);
            await stream.WriteAsync(new byte[] { 1 }, ct).ConfigureAwait(false);
            return new(name, address, invitation);
        }
    }
    private static async Task ConfirmBoth(SslStream stream, Func<CancellationToken, Task<bool>> approve, CancellationToken ct)
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Local()
        {
            bool accepted = await approve(pending.Token).WaitAsync(pending.Token).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, pending.Token).ConfigureAwait(false);
            if (!accepted) throw new AuthenticationException("Pairing declined.");
        }
        async Task Remote()
        {
            var answer = new byte[1]; await stream.ReadExactlyAsync(answer, pending.Token).ConfigureAwait(false);
            if (answer[0] != 1) throw new AuthenticationException("Pairing declined on the other PC.");
        }
        var tasks = new[] { Local(), Remote() };
        try { await await Task.WhenAny(tasks).ConfigureAwait(false); await Task.WhenAll(tasks).ConfigureAwait(false); }
        finally
        {
            pending.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (Exception) { }
        }
    }
}

public sealed class PairingServer : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource epoch = new();
    private readonly TcpServer server;
    private int pairing;
    private bool disposed;
    public event Func<PairedPeer, CancellationToken, Task>? Paired;
    public event Action<string>? Failed;
    public bool IsPairing => Volatile.Read(ref pairing) != 0;
    public IPEndPoint LocalEndpoint => server.LocalEndpoint;
    public PairingServer(PairingIdentity identity, Func<PairingPrompt, CancellationToken, Task<bool>> approve, Func<bool> available,
        Func<byte[], bool> authorizeRequester, int port = EasyPairing.Port)
    {
        server = new(new(IPAddress.Any, port), async (client, ct) =>
        {
            CancellationToken requestEpoch;
            lock (gate) requestEpoch = epoch.Token;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, requestEpoch);
            bool ownsPrompt = false;
            try
            {
                if (!available()) return;
                var peer = await EasyPairing.AcceptAsync(client, identity, async (prompt, token) =>
                {
                    if (!available() || Interlocked.CompareExchange(ref pairing, 1, 0) != 0) return false;
                    ownsPrompt = true;
                    return await approve(prompt, token).ConfigureAwait(false);
                }, request.Token, authorizeRequester).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                if (Paired is { } paired) await paired(peer, requestEpoch).WaitAsync(request.Token).ConfigureAwait(false);
            }
            catch (Exception) when (request.IsCancellationRequested) { }
            catch (Exception ex) { if (ownsPrompt) Failed?.Invoke(ex.Message); }
            finally { if (ownsPrompt) Volatile.Write(ref pairing, 0); }
        });
    }
    // Completed results carry this epoch too, so a queued UI notification can
    // still be rejected if Pause occurs after the network exchange completes.
    public void CancelPairing()
    {
        lock (gate)
        {
            if (disposed) return;
            epoch.Cancel(); epoch.Dispose(); epoch = new();
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; epoch.Cancel();
        }
        server.Dispose(); epoch.Dispose();
    }
}
