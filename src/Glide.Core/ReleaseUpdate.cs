using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glide.Core;

public sealed record ReleaseUpdate(Version Version, string Tag, Uri Download, string Sha256, long Size)
{
    public const string Repository = "mmatx64/glide";
    public const long MaxPackageBytes = 64 * 1024 * 1024;
    public static readonly string[] PackageFiles = ["Glide.exe", "Glide.ini", "README.md", "Allow-PrivateNetwork.ps1", "Install-Service.ps1"];

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Glide-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        return client;
    }

    public static async Task<ReleaseUpdate> LatestAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new IOException("No public stable Glide release is available on GitHub. The repository may contain only prereleases or require authentication.");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new IOException("GitHub declined the update check or its rate limit was reached. Try again later.");
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    public static ReleaseUpdate Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()
            || !Regex.IsMatch(tag, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") || !Version.TryParse(tag[1..], out var version))
            throw new InvalidDataException("GitHub did not return a supported stable release.");
        string name = $"Glide-{tag[1..]}-win-x64.zip";
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("The release must contain one Windows x64 Glide package.");
        var asset = assets[0];
        string url = asset.GetProperty("browser_download_url").GetString() ?? "";
        string expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
        string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        long size = asset.GetProperty("size").GetInt64();
        if (url != expected || !Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z") || size is <= 0 or > MaxPackageBytes)
            throw new InvalidDataException("Release URL, SHA-256 digest, or package size is invalid. No files were changed.");
        return new(version, tag, new Uri(url), digest[7..], size);
    }

    public async Task DownloadAsync(HttpClient client, string zipPath, CancellationToken ct)
    {
        using var response = await client.GetAsync(Download, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri?.Scheme != "https" || finalUri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException("Unexpected release download redirect.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > Size) throw new InvalidDataException("Release download exceeded its published size.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        output.Position = 0;
        if (total != Size || !Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release download failed SHA-256 verification. No files were changed.");
    }

    public static void ExtractPackage(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        // Release ZIPs are flat. Reject traversal, duplicates, extra executables and oversized expanded files.
        if (zip.Entries.Count != PackageFiles.Length || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != PackageFiles.Length
            || zip.Entries.Any(e => !PackageFiles.Contains(e.FullName, StringComparer.Ordinal) || e.Length is <= 0 or > MaxPackageBytes))
            throw new InvalidDataException("Unexpected release package contents.");
        Directory.CreateDirectory(destination);
        foreach (var entry in zip.Entries) entry.ExtractToFile(Path.Combine(destination, entry.FullName), false);
    }
}
