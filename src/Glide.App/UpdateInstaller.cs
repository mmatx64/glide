using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Glide.Core;

namespace Glide;

internal static class UpdateInstaller
{
    internal static Version CurrentVersion => new(typeof(Program).Assembly.GetName().Version!.ToString(3));
    internal static string VersionText => CurrentVersion.ToString(3);
    internal const string EventPrefix = "Local\\Glide.Update.";

    internal static Process Launch(string tag, string readyEvent)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Glide-updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string helper = Path.Combine(directory, "Glide.exe");
        File.Copy(Environment.ProcessPath!, helper, false);
        var start = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WorkingDirectory = directory };
        start.Arguments = $"--apply-update {tag} \"{Environment.ProcessPath}\" {Environment.ProcessId} {readyEvent}";
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            ServiceNative.Check(IsProcessInJob(Process.GetCurrentProcess().Handle, 0, out bool inJob), "Check app process job");
            if (inJob && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return CreateDetached(helper, start.Arguments);
            return Process.Start(start) ?? throw new IOException("Could not start the administrator update helper.");
        }
        catch { File.Delete(helper); Directory.Delete(directory); throw; }
    }

    private static Process CreateDetached(string executable, string arguments)
    {
        var startup = new ServiceNative.StartupInfo { Size = Marshal.SizeOf<ServiceNative.StartupInfo>() };
        ServiceNative.Check(CreateProcess(executable, new StringBuilder($"\"{executable}\" {arguments}"), 0, 0, false,
            0x01000000, 0, Path.GetDirectoryName(executable)!, ref startup, out var process), "Start detached update helper");
        try { return Process.GetProcessById((int)process.ProcessId); }
        finally { ServiceNative.CloseHandle(process.Thread); ServiceNative.CloseHandle(process.Process); }
    }

    internal static int RunJobProbe(string[] args)
    {
        if (args.Length != 2) return 1;
        string marker = Path.GetFullPath(args[1]);
        if (args[0] == "--update-job-probe")
        {
            using var helper = CreateDetached(Environment.ProcessPath!, $"--update-lifetime-probe \"{marker}\"");
            File.WriteAllText(marker + ".parent", Environment.ProcessId.ToString());
            Thread.Sleep(Timeout.Infinite);
        }
        else
        {
            File.WriteAllText(marker + ".ready", Environment.ProcessId.ToString());
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(marker + ".closed"))
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(10)) return 1;
                Thread.Sleep(25);
            }
            File.WriteAllText(marker + ".result", "survived service job closure");
        }
        return 0;
    }

    internal static void TestJobLifetime(string directory)
    {
        string marker = Path.Combine(directory, "update-job-" + Guid.NewGuid().ToString("N"));
        nint job = ServiceNative.CreateJobObject(0, null);
        ServiceNative.Check(job != 0, "Create update test job");
        var child = new ServiceNative.ProcessInfo();
        try
        {
            var limits = new ServiceNative.JobLimits { Basic = new ServiceNative.JobBasic { Flags = 0x2800 } };
            ServiceNative.Check(ServiceNative.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ServiceNative.JobLimits>()), "Configure update test job");
            var startup = new ServiceNative.StartupInfo { Size = Marshal.SizeOf<ServiceNative.StartupInfo>() };
            ServiceNative.Check(CreateProcess(Environment.ProcessPath!, new StringBuilder($"\"{Environment.ProcessPath}\" --update-job-probe \"{marker}\""), 0, 0, false,
                4, 0, AppContext.BaseDirectory, ref startup, out child), "Start updater job probe");
            ServiceNative.Check(ServiceNative.AssignProcessToJobObject(job, child.Process), "Attach updater test parent");
            ServiceNative.Check(ServiceNative.ResumeThread(child.Thread) != uint.MaxValue, "Resume updater test parent");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(marker + ".ready"))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("Detached update probe did not start.");
                Thread.Sleep(25);
            }
            using var helper = Process.GetProcessById(int.Parse(File.ReadAllText(marker + ".ready")));
            ServiceNative.Check(IsProcessInJob(helper.Handle, job, out bool stillAttached), "Check updater's service job membership");
            if (stillAttached) throw new IOException("Updater remained attached to the service job.");
            ServiceNative.CloseHandle(job); job = 0;
            if (ServiceNative.WaitForSingleObject(child.Process, 5000) != 0) throw new IOException("Test job did not terminate its parent.");
            File.WriteAllText(marker + ".closed", "closed");
            while (!File.Exists(marker + ".result"))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("Detached updater did not survive service-job shutdown.");
                Thread.Sleep(25);
            }
            if (File.ReadAllText(marker + ".result") != "survived service job closure") throw new IOException("Updater did not complete after service job closure.");
        }
        finally
        {
            if (job != 0) ServiceNative.CloseHandle(job);
            if (child.Thread != 0) ServiceNative.CloseHandle(child.Thread);
            if (child.Process != 0) ServiceNative.CloseHandle(child.Process);
        }
    }

    internal static int Run(string[] args)
    {
        string? stage = null;
        try
        {
            if (args.Length != 5 || args[0] != "--apply-update" || !int.TryParse(args[3], out int parentId)
                || !args[4].StartsWith(EventPrefix, StringComparison.Ordinal) || !Guid.TryParseExact(args[4][EventPrefix.Length..], "N", out _))
                throw new InvalidOperationException("Invalid update request.");
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Updates require the same Windows user's administrator token.");
            string target = Path.GetFullPath(args[2]);
            if (Path.GetFileName(target) != "Glide.exe" || target.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid update destination.");
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The requesting app no longer matches the update destination.");
            using var ready = EventWaitHandle.OpenExisting(args[4]);
            using var service = new InstalledService();
            bool serviceTarget = target.Equals(ServiceHost.Executable, StringComparison.OrdinalIgnoreCase);
            if (serviceTarget && !service.Exists) throw new IOException("The installed Glide service is unavailable.");
            if (service.Exists)
            {
                ServiceHost.RejectReparsePoints(ServiceHost.InstallDirectory);
                if (File.ReadAllText(Path.Combine(ServiceHost.InstallDirectory, "Owner.sid")).Trim() != identity.User!.Value)
                    throw new InvalidOperationException("Run the update as the Windows account enrolled in the Glide service.");
            }
            stage = CreateProtectedStage();
            using var client = ReleaseUpdate.CreateClient();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var release = ReleaseUpdate.LatestAsync(client, deadline.Token).GetAwaiter().GetResult();
            if (release.Tag != args[1] || release.Version <= CurrentVersion)
                throw new IOException("The selected release changed or is no longer newer. Check for updates again.");
            string zip = Path.Combine(stage, "release.zip"), package = Path.Combine(stage, "package");
            release.DownloadAsync(client, zip, deadline.Token).GetAwaiter().GetResult();
            ReleaseUpdate.ExtractPackage(zip, package);
            string[] destinations = service.Exists ? [Path.GetDirectoryName(target)!, ServiceHost.InstallDirectory] : [Path.GetDirectoryName(target)!];
            using var paths = new PathGuard(destinations);
            using var transaction = new UpdateTransaction(package, destinations);
            bool wasRunning = service.Running;
            // All validation/download/preparation finishes before asking the UI to
            // release input and exit. No forced termination of a portable process.
            if (parent.HasExited) throw new IOException("The requesting Glide app closed before the update was ready.");
            ready.Set();
            if (!parent.WaitForExit(15000)) throw new IOException("Glide did not exit within 15 seconds. No installed files were replaced.");
            // Use the verified release's installer while the service is stopped.
            // The helper already has the enrolled user's administrator token.
            UpdateLifecycle.Apply(transaction, service, service.Exists ? () => ServiceInstaller.Run(package) : null);
            Native.MessageBox(0, $"Glide was updated to {release.Tag}. Your settings and pairing were preserved.", "Glide update complete", 0x40);
            if (wasRunning) Process.Start(new ProcessStartInfo(ServiceHost.Executable, "--open-service") { UseShellExecute = true });
            else if (!serviceTarget) RestartPortable(target);
            return 0;
        }
        catch (Exception ex)
        {
            Native.MessageBox(0, "Update could not complete: " + ex.Message + "\n\nYour Glide.ini was not replaced. Reopen Glide to continue.", "Glide update", 0x10);
            return 1;
        }
        finally
        {
            if (stage is not null) { try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private static void RestartPortable(string target)
    {
        // Explorer launches with the desktop user's ordinary token; do not turn
        // a portable session into an elevated one after a one-time update.
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{target}\"") { UseShellExecute = true });
    }

    private static string CreateProtectedStage()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Glide", "Updates");
        for (string? path = root; path is not null; path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update staging paths cannot contain reparse points.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        // Only create beneath a trusted parent. Existing directories must already
        // be owned by SYSTEM/administrators, matching the service installer.
        string dataRoot = Path.GetDirectoryName(root)!;
        using var parentGuard = new PathGuard([Path.GetDirectoryName(dataRoot)!]);
        foreach (string path in new[] { dataRoot, root })
        {
            if (!Directory.Exists(path)) new DirectoryInfo(path).Create(security);
            using var directoryGuard = new PathGuard([path]);
            string? owner = new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            if (owner is not ("S-1-5-18" or "S-1-5-32-544")) throw new IOException("Update staging directory has an untrusted owner.");
            new DirectoryInfo(path).SetAccessControl(security);
        }
        string stage = Path.Combine(root, Guid.NewGuid().ToString("N"));
        new DirectoryInfo(stage).Create(security);
        return stage;
    }

    internal static void TestPaths(string directory)
    {
        string package = Path.Combine(directory, "update-package"), target = Path.Combine(directory, "update-target");
        Directory.CreateDirectory(package); Directory.CreateDirectory(target);
        foreach (string name in ReleaseUpdate.PackageFiles)
        {
            File.WriteAllText(Path.Combine(package, name), "new " + name);
            File.WriteAllText(Path.Combine(target, name), "old " + name);
        }
        try
        {
            using (var guard = new PathGuard([target]))
            {
                bool renameDenied = false;
                try { Directory.Move(target, target + "-moved"); } catch (IOException) { renameDenied = true; }
                if (!renameDenied) throw new Exception("Update path guard did not prevent directory replacement.");
                using var transaction = new UpdateTransaction(package, [target]);
                transaction.Apply(); transaction.Commit();
            }
            if (File.ReadAllText(Path.Combine(target, "Glide.exe")) != "new Glide.exe"
                || File.ReadAllText(Path.Combine(target, "Glide.ini")) != "old Glide.ini")
                throw new Exception("Protected path replacement did not preserve settings.");
        }
        finally
        {
            Directory.Delete(package, true);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            if (Directory.Exists(target + "-moved")) Directory.Delete(target + "-moved", true);
        }
    }

    // Hold every directory component without delete sharing while changing files.
    // Renames/replacements of those components fail until the update completes.
    internal sealed class PathGuard : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        internal PathGuard(IEnumerable<string> directories)
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string directory in directories)
                {
                    string? path = Path.GetFullPath(directory);
                    if (path.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Update destinations must be on a local drive.");
                    var ancestors = new Stack<string>();
                    while (path is not null) { ancestors.Push(path); path = Path.GetDirectoryName(path); }
                    foreach (string ancestor in ancestors)
                    {
                        if (!seen.Add(ancestor)) continue;
                        var handle = CreateFile(ancestor, 0x80000000, 3, 0, 3, 0x02200000, 0);
                        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Protect update path " + ancestor); }
                        handles.Add(handle);
                        if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Update paths cannot contain reparse points.");
                    }
                    foreach (string name in ReleaseUpdate.PackageFiles)
                    {
                        string file = Path.Combine(directory, name);
                        if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Update files cannot be reparse points.");
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in handles) handle.Dispose(); handles.Clear(); }
    }

    private sealed class InstalledService : IDisposable, IUpdateService
    {
        private readonly nint manager, handle;
        internal bool Exists => handle != 0;
        internal InstalledService()
        {
            manager = OpenSCManager(null, null, 1);
            ServiceNative.Check(manager != 0, "Open service manager");
            handle = OpenService(manager, ServiceHost.Name, 0x0034 | 1);
            if (handle == 0 && Marshal.GetLastWin32Error() != 1060) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Open Glide service"); }
            if (Exists)
            {
                QueryServiceConfig(handle, 0, 0, out uint needed);
                nint buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    ServiceNative.Check(QueryServiceConfig(handle, buffer, needed, out _), "Read Glide service configuration");
                    // QUERY_SERVICE_CONFIGW: three DWORDs, then aligned pointer.
                    string? binary = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, 16));
                    if (binary != $"\"{ServiceHost.Executable}\" --service") throw new IOException("Glide service points to an unexpected executable or diagnostic mode.");
                }
                catch { Dispose(); throw; }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        private uint State
        {
            get { if (!Exists) return 1; ServiceNative.Check(QueryServiceStatus(handle, out var status), "Read Glide service state"); return status.State; }
        }
        public bool Running => State == 4;
        public void Stop()
        {
            if (State == 1) return;
            ServiceNative.Check(ControlService(handle, 1, out _), "Stop Glide service");
            Wait(1);
        }
        public void Start()
        {
            if (State == 4) return;
            ServiceNative.Check(StartService(handle, 0, 0), "Start Glide service");
            Wait(4);
        }
        private void Wait(uint state)
        {
            var timer = Stopwatch.StartNew();
            while (State != state)
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new IOException("Glide service did not reach the requested state within 20 seconds.");
                Thread.Sleep(100);
            }
        }
        public void Dispose() { if (handle != 0) CloseServiceHandle(handle); if (manager != 0) CloseServiceHandle(manager); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(nint process, nint job, out bool result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command,
        nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref ServiceNative.StartupInfo startup, out ServiceNative.ProcessInfo process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string file, uint access, uint share, nint attributes, uint creation, uint flags, nint template);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(nint service, nint config, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out ServiceNative.Status status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(nint service, uint control, out ServiceNative.Status status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartService(nint service, uint count, nint args);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
}
