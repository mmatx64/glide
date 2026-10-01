using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Glide.Core;
using static Glide.ServiceNative;

namespace Glide;

// No windows, pairing server, updater, IPC command executor or controller mode.
// The fixed service child receives only existing TLS-authenticated input.
internal static class LoginReceiver
{
    private static readonly uint sessionId = CurrentSession();
    private static uint CurrentSession() { using var process = Process.GetCurrentProcess(); return (uint)process.SessionId; }
    internal static string DesktopName(nint desktop)
    {
        var name = new StringBuilder(128);
        return Native.GetUserObjectInformation(desktop, 2, name, (uint)(name.Capacity * sizeof(char)), out _) ? name.ToString() : "";
    }
    internal static string InputDesktopName()
    {
        nint desktop = Native.OpenInputDesktop(0, false, 1);
        if (desktop == 0) return "";
        try { return DesktopName(desktop); }
        finally { Native.CloseDesktop(desktop); }
    }
    internal static bool OnConsole() => sessionId == WTSGetActiveConsoleSessionId();
    private static bool InputAllowed(string owner)
    {
        if (!OnConsole() || !InputDesktopName().Equals("Winlogon", StringComparison.OrdinalIgnoreCase)) return false;
        nint token = 0;
        try
        {
            uint session = WTSGetActiveConsoleSessionId();
            if (!WTSQueryUserToken(session, out token)) return Marshal.GetLastWin32Error() == 1008;
            using var user = new WindowsIdentity(token);
            return user.User?.Value == owner && ServiceHost.IsLocked(session);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return false; }
        finally { if (token != 0) CloseHandle(token); }
    }
    private static bool ReleasesAllowed(string owner)
    {
        if (!OnConsole()) return false;
        if (InputAllowed(owner)) return true;
        if (!InputDesktopName().Equals("Default", StringComparison.OrdinalIgnoreCase)) return false;
        nint token = 0;
        try
        {
            if (!WTSQueryUserToken(WTSGetActiveConsoleSessionId(), out token)) return false;
            using var user = new WindowsIdentity(token);
            return user.User?.Value == owner;
        }
        finally { if (token != 0) CloseHandle(token); }
    }
    internal static int Run(string[] args, bool probe)
    {
        nint stop = 0;
        try
        {
            ServiceHost.ValidateInstallLocation();
            using var system = WindowsIdentity.GetCurrent();
            if (!system.IsSystem || !OnConsole() || Process.GetCurrentProcess().SessionId == 0
                || !DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId())).Equals("Winlogon", StringComparison.OrdinalIgnoreCase)
                || args.Length != 3 || !ServiceSession.ValidEvent(args[1], "Stop") || !ServiceSession.ValidEvent(args[2], "Show"))
                throw new InvalidOperationException("Refused an invalid sign-in receiver launch.");
            stop = OpenEvent(0x00100000, false, args[1]);
            if (stop == 0) throw new InvalidOperationException("Sign-in receiver has no service lifetime event.");
            string owner = new SecurityIdentifier(File.ReadAllText(Path.Combine(ServiceHost.InstallDirectory, "Owner.sid")).Trim()).Value;
            if (probe)
            {
                ServiceHost.RejectReparsePoints(LoginEnrollment.ProbeData);
                byte[] plaintext = Settings.Protect(File.ReadAllBytes(LoginEnrollment.ProbeData), false);
                try
                {
                    if (plaintext.Length is < 33 or > 65536) throw new InvalidDataException("Invalid DPAPI diagnostic fixture.");
                    using var probeIdentity = new PairingIdentity(plaintext[32..], plaintext[..32]);
                    ProbeTls(probeIdentity).GetAwaiter().GetResult();
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
                using var hooks = new InputWorker(inputs => throw new InvalidOperationException("Probe must never inject input."));
                if (!hooks.ThreadDesktop.Equals("Winlogon", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Input hook thread did not inherit the sign-in desktop.");
                string path = Path.Combine(ServiceHost.DataDirectory, "login-probe.txt");
                File.WriteAllText(path, $"PID={Environment.ProcessId}\nSession={Process.GetCurrentProcess().SessionId}\nSystem={system.IsSystem}\nDesktop=Winlogon\nInputDesktop={InputDesktopName()}\nMachineDPAPI=True\nLoopbackTLS=True\nHookDesktop={hooks.ThreadDesktop}\nNetwork=LoopbackOnly\nInput=False\n");
                while (WaitForSingleObject(stop, 100) != 0 && OnConsole()) { }
                File.AppendAllText(path, "GracefulStop=True\n");
                return 0;
            }
            if (!LoginEnrollment.Enabled()) return 0;
            if (new FileInfo(LoginEnrollment.Profile).Length > 65536) throw new InvalidDataException("Sign-in enrollment is too large.");
            var settings = new Settings(ServiceHost.SettingsPath);
            using var identity = LoginEnrollment.Decode(File.ReadAllBytes(LoginEnrollment.Profile), settings, owner);
            using var engine = new Engine(() => new InputWorker(wakeScreenSaver: () => { }, inputAllowed: () => InputAllowed(owner), releaseAllowed: () => ReleasesAllowed(owner)));
            engine.EmergencyStopped += () =>
            {
                settings.AutoConnect = false;
                try { settings.Save(); }
                catch { File.Delete(LoginEnrollment.Profile); }
                ServiceHost.Log("Sign-in sharing paused by emergency shortcut.");
            };
            engine.Start(false, "", null, identity, settings.RemoteOnRight);
            DiscoveryService? discovery = null;
            try
            {
                if (settings.DiscoveryEnabled)
                    discovery = new DiscoveryService(identity.Fingerprint,
                        () => new Announcement(Environment.MachineName, identity.Fingerprint, true, engine.Connected));
                ServiceHost.Log("Sign-in receiver listening for the existing paired PC.");
                while (WaitForSingleObject(stop, 100) != 0 && OnConsole() && engine.Running) { }
            }
            finally { discovery?.Dispose(); }
            return 0;
        }
        catch (Exception ex) { ServiceHost.Log("Sign-in receiver unavailable: " + ex.Message); return 1; }
        finally { if (stop != 0) CloseHandle(stop); }
    }
    private static async Task ProbeTls(PairingIdentity identity)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var connecting = Connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                Invitation.Parse(identity.Invitation), 1920, 1080, timeout.Token);
            var accepting = Connection.AcceptAsync(await listener.AcceptTcpClientAsync(timeout.Token), identity, 1920, 1080, timeout.Token);
            await using var client = await connecting;
            await using var server = await accepting;
        }
        finally { listener.Stop(); }
    }
}
