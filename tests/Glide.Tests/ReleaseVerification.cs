using System.Security.Cryptography;
using Glide.Core;

internal static class ReleaseVerification
{
    internal static async Task Run(string expectedTag, string destination)
    {
        Directory.CreateDirectory(destination);
        using var client = ReleaseUpdate.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var release = await ReleaseUpdate.LatestAsync(client, deadline.Token);
        if (release.Tag != expectedTag) throw new IOException($"Expected {expectedTag}; GitHub latest is {release.Tag}.");
        string zip = Path.Combine(destination, "Glide-" + release.Version.ToString(3) + "-win-x64.zip");
        await release.DownloadAsync(client, zip, deadline.Token);
        string package = Path.Combine(destination, "package");
        ReleaseUpdate.ExtractPackage(zip, package);
        string[] settings = File.ReadAllLines(Path.Combine(package, "Glide.ini"));
        foreach (string key in new[] { "PeerAddress", "PeerCredential", "ReceiverIdentity", "PeerName" })
            if (!settings.Contains(key + "=")) throw new IOException("Release INI contains configured settings or is missing " + key + ".");
        Console.WriteLine($"PASS public latest release {release.Tag}, verified SHA-256 download, exact package contents, and pristine INI");
        Console.WriteLine($"ZIP SHA256 {release.Sha256}");
        Console.WriteLine($"EXE SHA256 {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(package, "Glide.exe")))).ToLowerInvariant()}");
    }
}
