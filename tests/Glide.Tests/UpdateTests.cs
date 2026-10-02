using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Glide.Core;

internal static class UpdateTests
{
    internal static async Task Run(Action<bool, string> check, CancellationToken ct)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Glide-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string zipPath = Path.Combine(directory, "fixture.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (string name in ReleaseUpdate.PackageFiles)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write("new " + name);
                }
            byte[] bytes = File.ReadAllBytes(zipPath);
            string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            const string url = "https://github.com/mmatx64/glide/releases/download/v1.2.3/Glide-1.2.3-win-x64.zip";
            string Json(string tag = "v1.2.3", string download = url, string? hash = null, long? size = null, bool prerelease = false) =>
                JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease, assets = new[] { new { name = "Glide-1.2.3-win-x64.zip", browser_download_url = download, digest = "sha256:" + (hash ?? digest), size = size ?? bytes.Length } } });
            var release = ReleaseUpdate.Parse(Json());
            check(release.Version == new Version(1, 2, 3) && release.Version > new Version(1, 2, 2), "stable update version comparison");
            void Reject(Action action, string label)
            {
                bool failed = false;
                try { action(); } catch (InvalidDataException) { failed = true; }
                check(failed, label);
            }
            Reject(() => ReleaseUpdate.Parse(Json(prerelease: true)), "updater rejects prereleases");
            Reject(() => ReleaseUpdate.Parse(Json(tag: "v1.2.3/../bad")), "updater rejects malformed version tags");
            Reject(() => ReleaseUpdate.Parse(Json(download: "https://evil.example/Glide.zip")), "updater pins release asset to the Glide repository");
            Reject(() => ReleaseUpdate.Parse(Json(hash: "")), "updater requires a published SHA-256 digest");
            Reject(() => ReleaseUpdate.Parse(Json(size: ReleaseUpdate.MaxPackageBytes + 1)), "updater rejects oversized packages");
            using var client = new HttpClient(new FixtureHandler(bytes));
            string downloaded = Path.Combine(directory, "download.zip");
            await release.DownloadAsync(client, downloaded, ct);
            check(File.ReadAllBytes(downloaded).SequenceEqual(bytes), "updater verifies and downloads a release fixture");
            bool badDigest = false;
            try { await (release with { Sha256 = new string('0', 64) }).DownloadAsync(client, Path.Combine(directory, "bad.zip"), ct); }
            catch (InvalidDataException) { badDigest = true; }
            check(badDigest, "updater refuses corrupted downloads before installation");
            bool overlong = false;
            try { await (release with { Size = 1 }).DownloadAsync(client, Path.Combine(directory, "long.zip"), ct); }
            catch (InvalidDataException) { overlong = true; }
            check(overlong, "updater bounds the streamed download");
            using var missingClient = new HttpClient(new FixtureHandler(bytes, HttpStatusCode.NotFound));
            bool noRelease = false;
            try { await ReleaseUpdate.LatestAsync(missingClient, ct); } catch (IOException ex) { noRelease = ex.Message.Contains("No public"); }
            check(noRelease, "updater explains unavailable public releases");

            string package = Path.Combine(directory, "package");
            ReleaseUpdate.ExtractPackage(downloaded, package);
            check(Directory.GetFiles(package).Length == 5, "updater extracts the expected flat release package");
            string traversal = Path.Combine(directory, "traversal.zip");
            using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Create))
            {
                foreach (string name in ReleaseUpdate.PackageFiles)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name == "Glide.exe" ? "../Glide.exe" : name).Open());
                    writer.Write("bad");
                }
            }
            Reject(() => ReleaseUpdate.ExtractPackage(traversal, Path.Combine(directory, "unsafe")), "updater rejects ZIP path traversal before extraction");
            string portable = Path.Combine(directory, "portable"), service = Path.Combine(directory, "service");
            foreach (string folder in new[] { portable, service })
            {
                Directory.CreateDirectory(folder);
                foreach (string name in ReleaseUpdate.PackageFiles) File.WriteAllText(Path.Combine(folder, name), "old " + name);
            }
            using (var transaction = new UpdateTransaction(package, [portable, service]))
            {
                transaction.Apply();
                check(File.ReadAllText(Path.Combine(portable, "Glide.exe")) == "new Glide.exe" && File.ReadAllText(Path.Combine(service, "Glide.exe")) == "new Glide.exe"
                    && File.ReadAllText(Path.Combine(portable, "Glide.ini")) == "old Glide.ini" && File.ReadAllText(Path.Combine(service, "Glide.ini")) == "old Glide.ini", "one transaction updates both binaries and preserves both settings files");
                transaction.Rollback();
            }
            check(File.ReadAllText(Path.Combine(portable, "Glide.exe")) == "old Glide.exe" && File.ReadAllText(Path.Combine(service, "Glide.exe")) == "old Glide.exe", "rollback restores both previous copies");
            using (var transaction = new UpdateTransaction(package, [portable]))
            {
                using (var locked = new FileStream(Path.Combine(portable, "README.md"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    bool failed = false;
                    try { transaction.Apply(); } catch (IOException) { failed = true; }
                    check(failed, "a locked destination aborts partial installation");
                }
                transaction.Rollback();
            }
            check(File.ReadAllText(Path.Combine(portable, "Glide.exe")) == "old Glide.exe", "partial-install failure restores the already replaced executable");
            var setupService = new FixtureService(true);
            int setups = 0;
            using (var transaction = new UpdateTransaction(package, [portable, service]))
            {
                bool failed = false;
                try
                {
                    UpdateLifecycle.Apply(transaction, setupService, () =>
                    {
                        setups++;
                        check(!setupService.Running && File.ReadAllText(Path.Combine(service, "Install-Service.ps1")) == "new Install-Service.ps1",
                            "service setup runs after verified file replacement and before service restart");
                        throw new IOException("Fixture service setup failed.");
                    });
                }
                catch (IOException) { failed = true; }
                check(failed && setups == 1 && setupService.Running && setupService.Stops == 1 && setupService.Starts == 1
                    && new[] { portable, service }.All(folder => ReleaseUpdate.PackageFiles.All(name => File.ReadAllText(Path.Combine(folder, name)) == "old " + name)),
                    "installer failure rolls back both copies and scripts, preserves settings, and restarts the previous service");
            }
            var setupStoppedService = new FixtureService(false);
            using (var transaction = new UpdateTransaction(package, [portable]))
            {
                UpdateLifecycle.Apply(transaction, setupStoppedService, () =>
                {
                    setups++;
                    check(!setupStoppedService.Running, "service setup also runs for an intentionally stopped service");
                });
            }
            check(setups == 2 && !setupStoppedService.Running && setupStoppedService.Starts == 0 && setupStoppedService.Stops == 0,
                "successful setup keeps an intentionally stopped service stopped");
            // Restore the fixture for the existing startup-failure regression.
            foreach (string name in ReleaseUpdate.PackageFiles) File.WriteAllText(Path.Combine(portable, name), "old " + name);
            var failedStoppedSetup = new FixtureService(false);
            using (var transaction = new UpdateTransaction(package, [portable]))
            {
                bool failed = false;
                try { UpdateLifecycle.Apply(transaction, failedStoppedSetup, () => throw new IOException("Fixture stopped-service setup failed.")); }
                catch (IOException) { failed = true; }
                check(failed && !failedStoppedSetup.Running && failedStoppedSetup.Starts == 0 && failedStoppedSetup.Stops == 0
                    && File.ReadAllText(Path.Combine(portable, "Glide.exe")) == "old Glide.exe",
                    "installer failure restores files without starting an intentionally stopped service");
            }
            var failedStart = new FixtureService(true, true);
            using (var transaction = new UpdateTransaction(package, [portable]))
            {
                bool failed = false;
                bool setupCompleted = false;
                try { UpdateLifecycle.Apply(transaction, failedStart, () => setupCompleted = true); } catch (IOException) { failed = true; }
                check(failed && setupCompleted && failedStart.Running && failedStart.Starts == 2 && failedStart.Stops == 2
                    && File.ReadAllText(Path.Combine(portable, "Glide.exe")) == "old Glide.exe", "failed service start stops the new instance, rolls files back, and restarts the old service");
            }
            var stoppedService = new FixtureService(false);
            using (var transaction = new UpdateTransaction(package, [portable])) { UpdateLifecycle.Apply(transaction, stoppedService); }
            check(!stoppedService.Running && stoppedService.Starts == 0 && stoppedService.Stops == 0, "update preserves an intentionally stopped service");
            var runningService = new FixtureService(true);
            using (var transaction = new UpdateTransaction(package, [portable]))
            {
                UpdateLifecycle.Apply(transaction, runningService, () =>
                {
                    check(!runningService.Running && runningService.Starts == 0,
                        "successful service setup completes while stopped, before the only restart");
                });
            }
            check(runningService.Running && runningService.Starts == 1 && runningService.Stops == 1, "successful update stops and restarts a running service once");
            using (var transaction = new UpdateTransaction(package, [portable])) { transaction.Apply(); transaction.Commit(); }
            check(File.ReadAllText(Path.Combine(portable, "Glide.exe")) == "new Glide.exe" && Directory.GetFiles(portable, "*.bak").Length == 0
                && Directory.GetFiles(portable, "*.tmp").Length == 0, "committed update retains replacements and cleans temporary/backup files");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FixtureService(bool running, bool failFirstStart = false) : IUpdateService
    {
        public bool Running { get; private set; } = running;
        internal int Starts { get; private set; }
        internal int Stops { get; private set; }
        public void Stop() { Stops++; Running = false; }
        public void Start()
        {
            Starts++; Running = true;
            if (failFirstStart && Starts == 1) throw new IOException("Fixture service failed after starting.");
        }
    }

    private sealed class FixtureHandler(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
    }
}
