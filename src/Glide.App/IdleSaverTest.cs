using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using Glide.Core;
using static Glide.Native;

namespace Glide;

// Explicit diagnostic only, never part of --self-test or normal startup.
// Temporarily shortens the runtime timeout, then restores it. The companion
// script selects/restores a built-in saver when testing built-in compatibility.
internal static class IdleSaverTest
{
    internal static int Run(string output)
    {
        var lines = new List<string>();
        int timeout = -1, result = 1;
        bool armed = false;
        var saver = new ScreenSaver();
        try
        {
            using var user = WindowsIdentity.GetCurrent();
            if (user.IsSystem || (uint)Process.GetCurrentProcess().SessionId != ServiceNative.WTSGetActiveConsoleSessionId()
                || LoginReceiver.InputDesktopName() != "Default"
                || !SystemParametersInfo(0x76, 0, out int secure, 0) || secure != 0
                || !SystemParametersInfo(0x72, 0, out int running, 0) || running != 0
                || !SystemParametersInfo(0x10, 0, out int active, 0) || active == 0
                || !SystemParametersInfo(0x0e, 0, out timeout, 0))
                throw new InvalidOperationException("Idle saver diagnostic requires an unlocked user console, enabled non-password saver and no already-running saver.");
            lines.Add($"OriginalTimeout={timeout}; original saver selection and password setting are unchanged by this executable.");
            if (!SetSystemParametersInfo(0x0f, 5, 0, 2)) throw new InvalidOperationException("Cannot arm the temporary runtime timeout.");
            armed = true;
            if (!SpinWait.SpinUntil(() => SystemParametersInfo(0x72, 0, out int runningNow, 0) && runningNow != 0
                && LoginReceiver.InputDesktopName() is "Screen-saver" or "ScreenSaver", TimeSpan.FromSeconds(25)))
                throw new InvalidOperationException("Windows did not start an idle saver on its dedicated desktop. Physical activity can postpone this test.");
            lines.Add("PASS Windows actually starts its idle saver on the active Screen-saver desktop; no direct /s launch or preview");
            if (!SpinWait.SpinUntil(() => VisibleSaverWindow() != 0, TimeSpan.FromSeconds(5)))
                throw new Exception("Windows switched to the saver desktop but no visible saver window appeared.");
            uint baseline = ProbeDefaultInput();
            Thread.Sleep(100);
            bool stillSaving = SystemParametersInfo(0x72, 0, out int baselineRunning, 0) && baselineRunning != 0
                && LoginReceiver.InputDesktopName() is "Screen-saver" or "ScreenSaver";
            lines.Add($"BaselineDefaultSendInput={baseline}; saver remains active={stillSaving}");
            if (!stillSaving) throw new Exception("Normal-desktop baseline dismissed this saver; rerun without baseline to test the separate-desktop wake case.");
            nint desktop = OpenInputDesktop(0, false, 1);
            if (desktop != 0)
            {
                try
                {
                    nint window = VisibleSaverWindow();
                    var name = new System.Text.StringBuilder(256);
                    if (window != 0) GetClassName(window, name, name.Capacity);
                    lines.Add("ActualSaverClass=" + name);
                    GetWindowThreadProcessId(window, out uint processId);
                    using var process = Process.GetProcessById((int)processId);
                    lines.Add("ActualSaverProcess=" + Path.GetFileName(process.MainModule!.FileName));
                }
                finally { CloseDesktop(desktop); }
            }
            Replay(saver, lines).GetAwaiter().GetResult();
            result = 0;
        }
        catch (Exception ex) { lines.Add("FAIL " + ex.Message); }
        finally
        {
            if (armed)
            {
                // Reset idle through the actual saver desktop before closing it;
                // otherwise the short timeout can immediately start another saver.
                bool restored = SpinWait.SpinUntil(() =>
                {
                    saver.Wake();
                    return SetSystemParametersInfo(0x0f, (uint)timeout, 0, 2);
                }, TimeSpan.FromSeconds(6));
                if (restored && SystemParametersInfo(0x0e, 0, out int actual, 0) && actual == timeout)
                    lines.Add($"PASS original runtime timeout restored to {timeout}; no stored timeout/password settings changed");
                else { lines.Add("FAIL restore timeout; dismiss the saver locally and restore Screen Saver Settings before further tests."); result = 1; }
            }
            File.WriteAllLines(Path.GetFullPath(output), lines);
        }
        return result;
    }
    private static unsafe uint ProbeDefaultInput()
    {
        // Reproduce the old input context with a harmless movement only. No
        // keystrokes/clicks, and a running saver has already been observed.
        var input = new Input { Data = new InputUnion { Mouse = new MouseInput { X = 1, Flags = 1, Extra = 0x474c4944 } } };
        return SendInput(1, &input, sizeof(Input));
    }
    private static nint VisibleSaverWindow()
    {
        if (LoginReceiver.InputDesktopName() is not ("Screen-saver" or "ScreenSaver")) return 0;
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return 0;
        nint found = 0;
        try
        {
            EnumWindowProc find = (window, _) => { if (!IsWindowVisible(window)) return true; found = window; return false; };
            EnumDesktopWindows(desktop, find, 0); GC.KeepAlive(find); return found;
        }
        finally { CloseDesktop(desktop); }
    }
    private static async Task Replay(ScreenSaver saver, List<string> lines)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var identity = new PairingIdentity();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var connecting = Connection.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
                Invitation.Parse(identity.Invitation), 1920, 1080, timeout.Token);
            var accepting = Connection.AcceptAsync(await listener.AcceptTcpClientAsync(timeout.Token), identity, 1920, 1080, timeout.Token);
            await using var client = await connecting; await using var server = await accepting;
            using var finished = new ManualResetEventSlim();
            var injected = new List<Input>();
            string? notice = null;
            using var worker = new InputWorker(inputs =>
            {
                if (LoginReceiver.InputDesktopName() != "Default") throw new Exception("Actual remote packets reached the saver desktop.");
                foreach (var input in inputs) injected.Add(input);
                if (inputs[^1].Type == 0 && inputs[^1].Data.Mouse.X == 9000) finished.Set();
                return (uint)inputs.Length;
            }, wakePending: saver.Wake);
            worker.Notice += text => notice = text;
            worker.Attach(server, false, true);
            server.InputBatch += packets => worker.ReceiveBatch(server, packets);
            var receiving = server.RunAsync(timeout.Token); var sending = client.RunAsync(timeout.Token);
            var packets = new Packet[]
            {
                new(MessageKind.Activate, 1000, 2000), new(MessageKind.Key, 65, 30), new(MessageKind.Key, 65, 30, 2),
                new(MessageKind.Button, 2), new(MessageKind.Button, 4),
                new(MessageKind.Button, 2048, 120), new(MessageKind.Button, 2048, -120), new(MessageKind.Move, 9000, 4000)
            };
            foreach (var packet in packets) if (!client.Send(packet)) throw new Exception("Idle test sender stopped.");
            try
            {
                if (!finished.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Receiver did not resume after idle saver wake: " + notice);
                if (!server.IsAlive || injected.Count != 8
                    || injected[0].Data.Mouse.X != 1000 || injected[1].Type != 1 || injected[1].Data.Keyboard.Flags != 8
                    || injected[2].Data.Keyboard.Flags != 10 || injected[3].Data.Mouse.Flags != 2 || injected[4].Data.Mouse.Flags != 4
                    || unchecked((int)injected[5].Data.Mouse.Data) != 120 || unchecked((int)injected[6].Data.Mouse.Data) != -120)
                    throw new Exception("Idle transition dropped/reordered input or disconnected the peer.");
                if (LoginReceiver.InputDesktopName() != "Default" || !SystemParametersInfo(0x72, 0, out int active, 0) || active != 0)
                    throw new Exception("The actual idle saver did not exit.");
                lines.Add("PASS production wake dismisses the actual idle saver; authenticated TLS packets wait for Default then replay exact move/key down-up/click down-up/wheel reversal order (mock replay injection)");
                lines.Add("PASS peer remains connected; no actual text/click/wheel input injected into normal applications; only a benign saver-desktop wake move");
            }
            finally
            {
                timeout.Cancel(); client.Stop(); server.Stop();
                try { await Task.WhenAll(receiving, sending); }
                catch (Exception) when (timeout.IsCancellationRequested) { }
            }
        }
        finally { listener.Stop(); }
    }
}
