// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Glide contributors

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Glide.Core;

public sealed class PairingIdentity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public byte[] Secret { get; }
    public PairingIdentity(byte[]? pfx = null, byte[]? secret = null)
    {
        if (secret is not null && secret.Length != 32) throw new ArgumentException("Invalid pairing secret.", nameof(secret));
        if (pfx is null)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Glide", key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            Certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, KeyStorage);
        }
        else Certificate = X509CertificateLoader.LoadPkcs12(pfx, null, KeyStorage);
        Secret = secret ?? RandomNumberGenerator.GetBytes(32);
    }
    public byte[] Fingerprint => Certificate.GetCertHash(HashAlgorithmName.SHA256);
    public string Invitation => new Invitation(Fingerprint, Secret).Encode();
    // Schannel cannot use ephemeral TLS private keys. DefaultKeySet creates an OS-managed
    // temporary key deleted on Dispose; durable identity storage remains in Glide.ini.
    private static X509KeyStorageFlags KeyStorage => X509KeyStorageFlags.Exportable |
        (OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
    public byte[] Export() => Certificate.Export(X509ContentType.Pfx);
    public void Dispose() { Certificate.Dispose(); CryptographicOperations.ZeroMemory(Secret); }
}

public sealed record Invitation(byte[] Fingerprint, byte[] Secret)
{
    public string Encode() => "GLIDE1-" + Convert.ToBase64String(Fingerprint.Concat(Secret).ToArray());
    public static Invitation Parse(string text)
    {
        text = text.Trim();
        if (!text.StartsWith("GLIDE1-", StringComparison.Ordinal)) throw new FormatException("Paste the pairing code from the receiving PC.");
        byte[] data;
        try { data = Convert.FromBase64String(text[7..]); }
        catch (FormatException) { throw new FormatException("The pairing code is incomplete."); }
        if (data.Length != 64) throw new FormatException("The pairing code is incomplete.");
        return new(data[..32], data[32..]);
    }
}
