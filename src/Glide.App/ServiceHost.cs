using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using static Glide.ServiceNative;

namespace Glide;

// LocalSystem is only a launcher. No network listener, credentials, input packets,
// client-supplied command lines, or SYSTEM desktop UI exist in this process.
internal static class ServiceHost
{
    internal const string Name = "GlideSessionService";
    internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Glide");
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Glide", "Service");
    internal static string SettingsPath => Path.Combine(DataDirectory, "Glide.ini");
    internal static string Executable => Path.Combine(InstallDirectory, "Glide.exe");
    internal static string RequestName(string sid) => "Global\\Glide.Service.Request." + new SecurityIdentifier(sid).Value;
    private static readonly ServiceMain main = RunService;
    private static readonly Handler handler = Control;
    private static readonly ManualResetEventSlim stopping = new();
    private static nint statusHandle;
    private static readonly object statusLock = new();
    private static Status status;
    private static string lastLog = "";
    private static bool diagnostic;

    internal static int Run(bool test = false)
    {
        diagnostic = test;
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) return 5;
        return StartServiceCtrlDispatcher([new ServiceEntry { Name = Name, Main = main }, new ServiceEntry()]) ? 0 : Marshal.GetLastWin32Error();
    }
    private static void Report(uint state, uint error = 0)
    {
        lock (statusLock)
        {
            status = new Status { Type = 0x10, State = state, Accepted = state == 4 ? 5u : 0u,
                Error = error, WaitHint = state == 2 || state == 3 ? 10000u : 0u, Checkpoint = state == 2 || state == 3 ? 1u : 0u };
            SetServiceStatus(statusHandle, ref status);
        }
    }
    private static uint Control(uint control, uint type, nint data, nint context)
    {
        if (control is 1 or 5) { stopping.Set(); Report(3); }
        else if (control == 4) { lock (statusLock) SetServiceStatus(statusHandle, ref status); }
        return 0;
    }
    private static void RunService(uint count, nint args)
    {
        statusHandle = RegisterServiceCtrlHandlerEx(Name, handler, 0);
        if (statusHandle == 0) return;
        Report(2);
        uint error = 0;
        try
        {
            ValidateInstallLocation();
            string owner = new SecurityIdentifier(File.ReadAllText(Path.Combine(InstallDirectory, "Owner.sid")).Trim()).Value;
            using var request = new ServiceEvent(RequestName(owner), owner, true);
            Report(4); Log("Service running; waiting for the enrolled console account.");
            RunSessions(owner, request);
        }
        catch (Exception ex) { error = 1064; Log("Service failed: " + ex.Message); }
        finally { Report(1, error); }
    }
    internal static void ValidateInstallLocation()
    {
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Service must run from its protected Program Files installation.");
        RejectReparsePoints(Executable); RejectReparsePoints(Path.Combine(InstallDirectory, "Owner.sid"));
        RejectReparsePoints(DataDirectory);
    }
    internal static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Service paths cannot contain reparse points.");
    }
    private static void RunSessions(string owner, ServiceEvent request)
    {
        SessionProcess? child = null;
        string? currentLogon = null;
        bool quit = false;
        long retryAfter = 0;
        try
        {
            while (!stopping.IsSet)
            {
                bool requested = request.Poll();
                uint session = WTSGetActiveConsoleSessionId();
                nint token = 0;
                try
                {
                    if (session == uint.MaxValue || session == 0 || !WTSQueryUserToken(session, out token))
                    { child?.Dispose(); child = null; currentLogon = null; continue; }
                    using var identity = new WindowsIdentity(token);
                    if (identity.User?.Value != owner)
                    { child?.Dispose(); child = null; currentLogon = null; continue; }
                    // AuthenticationId, not just the recyclable Windows session id.
                    string logon = session + ":" + ReadLogonId(token);
                    if (currentLogon != logon)
                    { child?.Dispose(); child = null; currentLogon = logon; quit = false; retryAfter = 0; }
                    if (child is not null && child.HasExited(out uint exitCode))
                    {
                        child.Dispose(); child = null; quit = exitCode == 0;
                        retryAfter = Environment.TickCount64 + 10000;
                        Log(quit ? "Glide quit; waiting for shortcut or next sign-in." : "Glide exited; retrying in ten seconds.");
                    }
                    if (requested) { quit = false; retryAfter = 0; child?.Show(); }
                    if (child is null && !quit && Environment.TickCount64 >= retryAfter)
                    {
                        retryAfter = Environment.TickCount64 + 10000;
                        child = SessionProcess.Start(token, owner, session);
                        if (requested) child.Show();
                        Log("Glide started elevated as the enrolled user in console session " + session + ".");
                    }
                }
                catch (Exception ex) { Log("Session launch unavailable: " + ex.Message); }
                finally { if (token != 0) CloseHandle(token); stopping.Wait(1000); }
            }
        }
        finally { child?.Dispose(); }
    }
    internal static long ReadLogonId(nint token)
    {
        // TOKEN_STATISTICS starts with TokenId (8 bytes), then AuthenticationId.
        nint data = Marshal.AllocHGlobal(56);
        try { Check(GetTokenInformation(token, 10, data, 56, out _), "Read logon identity"); return Marshal.ReadInt64(data, 8); }
        finally { Marshal.FreeHGlobal(data); }
    }
    internal static void Log(string message)
    {
        if (message == lastLog) return;
        lastLog = message;
        try
        {
            string path = Path.Combine(DataDirectory, "service.log");
            if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch { /* Logging must not prevent stop or session cleanup. */ }
    }
    internal static int OpenInstalled()
    {
        using var identity = WindowsIdentity.GetCurrent();
        nint request = OpenEvent(2, false, RequestName(identity.User!.Value));
        if (request == 0) { Native.MessageBox(0, "The Glide service is not running for this Windows account. Install or start GlideSessionService first.", "Glide service", 0x40); return 1; }
        try { Check(SetEvent(request), "Open Glide service window"); return 0; }
        finally { CloseHandle(request); }
    }

    private sealed class SessionProcess : IDisposable
    {
        private nint process, job;
        private readonly ServiceEvent stop, show;
        private SessionProcess(nint process, nint job, ServiceEvent stop, ServiceEvent show)
        { this.process = process; this.job = job; this.stop = stop; this.show = show; }
        internal static SessionProcess Start(nint userToken, string owner, uint session)
        {
            nint linked = 0, primary = 0, environment = 0, job = 0;
            ProcessInfo process = default;
            ServiceEvent? stop = null, show = null;
            try
            {
                nint selected = userToken;
                if (TokenValue<int>(userToken, 18) == 3) { linked = TokenValue<nint>(userToken, 19); selected = linked; }
                if (TokenValue<int>(selected, 20) == 0)
                    throw new InvalidOperationException("The enrolled account must be an administrator with an elevated token.");
                Check(DuplicateTokenEx(selected, 0x02000000, 0, 2, 1, out primary), "Duplicate user primary token");
                using (var identity = new WindowsIdentity(primary))
                    if (identity.IsSystem || identity.User?.Value != owner || TokenValue<uint>(primary, 12) != session)
                        throw new InvalidOperationException("Refused a different user or session token.");
                Check(CreateEnvironmentBlock(out environment, primary, false), "Create user environment");
                stop = new ServiceEvent("Global\\Glide.Service.Stop." + Guid.NewGuid().ToString("N"), owner, false, true);
                show = new ServiceEvent("Global\\Glide.Service.Show." + Guid.NewGuid().ToString("N"), owner, false);
                job = CreateJobObject(0, null); Check(job != 0, "Create service session job");
                var limits = new JobLimits { Basic = new JobBasic { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
                Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()), "Configure session job");
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
                var command = new StringBuilder("\"" + Executable + (diagnostic ? "\" --service-probe " : "\" --service-user ") + stop.Name + " " + show.Name);
                Check(CreateProcessAsUser(primary, Executable, command, 0, 0, false, 0x404, environment, InstallDirectory, ref startup, out process), "Start user session Glide");
                Check(AssignProcessToJobObject(job, process.Process), "Attach session lifetime");
                Check(ResumeThread(process.Thread) != uint.MaxValue, "Resume session Glide");
                CloseHandle(process.Thread); process.Thread = 0;
                var result = new SessionProcess(process.Process, job, stop, show);
                process.Process = 0; job = 0; stop = null; show = null;
                return result;
            }
            finally
            {
                if (process.Thread != 0) CloseHandle(process.Thread);
                if (process.Process != 0) { TerminateProcess(process.Process, 1); CloseHandle(process.Process); }
                if (job != 0) CloseHandle(job);
                stop?.Dispose(); show?.Dispose();
                if (environment != 0) DestroyEnvironmentBlock(environment);
                if (primary != 0) CloseHandle(primary);
                if (linked != 0) CloseHandle(linked);
            }
        }
        internal bool HasExited(out uint code)
        {
            code = 0;
            if (WaitForSingleObject(process, 0) != 0) return false;
            Check(GetExitCodeProcess(process, out code), "Read session exit code"); return true;
        }
        internal void Show() => show.Signal();
        public void Dispose()
        {
            if (process == 0) return;
            stop.Signal(); WaitForSingleObject(process, 5000);
            CloseHandle(job); job = 0; // crash/timeout cleanup never leaves an orphan elevated UI
            CloseHandle(process); process = 0; stop.Dispose(); show.Dispose();
        }
    }
}

internal sealed class ServiceEvent : IDisposable
{
    internal string Name { get; }
    private nint handle;
    internal ServiceEvent(string name, string sid, bool userCanSignal, bool manualReset = false)
    {
        Name = name;
        string access = userCanSignal ? "0x00100002" : "0x00100000";
        string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;" + access + ";;;" + new SecurityIdentifier(sid).Value + ")S:(ML;;NW;;;ME)";
        Check(ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out nint descriptor, out _), "Create event permissions");
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            handle = CreateEvent(ref attributes, manualReset, false, name);
            int error = Marshal.GetLastWin32Error();
            Check(handle != 0, "Create service event");
            if (error == 183) { Dispose(); throw new InvalidOperationException("Service event already exists; refusing an untrusted object."); }
        }
        finally { LocalFree(descriptor); }
    }
    internal bool Poll() => WaitForSingleObject(handle, 0) == 0;
    internal void Signal() => Check(SetEvent(handle), "Signal service event");
    public void Dispose() { if (handle != 0) { CloseHandle(handle); handle = 0; } }
}

internal sealed class ServiceSession : IDisposable
{
    private nint stop, show;
    internal ServiceSession(string[] args)
    {
        ServiceHost.ValidateInstallLocation();
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.IsSystem || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Service UI requires an elevated user token, never SYSTEM.");
        string owner = File.ReadAllText(Path.Combine(ServiceHost.InstallDirectory, "Owner.sid")).Trim();
        if (identity.User?.Value != owner || args.Length != 3 || args[0] is not ("--service-user" or "--service-probe")
            || !ValidEvent(args[1], "Stop") || !ValidEvent(args[2], "Show"))
            throw new InvalidOperationException("Invalid service session launch.");
        stop = OpenEvent(0x00100000, false, args[1]); show = OpenEvent(0x00100000, false, args[2]);
        if (stop == 0 || show == 0) { Dispose(); throw new InvalidOperationException("Service session lifetime is unavailable."); }
    }
    internal static bool ValidEvent(string value, string purpose) =>
        value.StartsWith("Global\\Glide.Service." + purpose + ".", StringComparison.Ordinal)
        && Guid.TryParseExact(value[(value.LastIndexOf('.') + 1)..], "N", out _);
    internal bool Stopping => WaitForSingleObject(stop, 0) == 0;
    internal bool ShowRequested => WaitForSingleObject(show, 0) == 0;
    internal void RunProbe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string path = Path.Combine(ServiceHost.DataDirectory, "session-probe.txt");
        File.WriteAllText(path, $"PID={Environment.ProcessId}\nSession={Process.GetCurrentProcess().SessionId}\nUser={identity.User!.Value}\nElevated={ServiceNative.TokenValue<int>(identity.Token, 20)}\nSystem={identity.IsSystem}\n");
        while (!Stopping)
        {
            if (ShowRequested) File.AppendAllText(path, "ShowRequest=True\n");
            Thread.Sleep(50);
        }
        File.AppendAllText(path, "GracefulStop=True\n");
    }
    public void Dispose() { if (stop != 0) CloseHandle(stop); if (show != 0) CloseHandle(show); stop = show = 0; }
}
