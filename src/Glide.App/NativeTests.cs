using System.Runtime.InteropServices;
using Glide.Core;

namespace Glide;

internal static class NativeTests
{
    internal static int Run()
    {
        var lines = new List<string>();
        string directory = System.IO.Path.Combine(AppContext.BaseDirectory, "self-test");
        Directory.CreateDirectory(directory);
        try
        {
            if (Marshal.SizeOf<Native.Input>() != 40) throw new Exception("x64 INPUT layout is wrong.");
            if (Marshal.SizeOf<Native.MouseHook>() != 32 || Marshal.SizeOf<Native.KeyHook>() != 24) throw new Exception("Hook layout is wrong.");
            lines.Add("PASS Win32 x64 input structures");
            UpdateInstaller.TestPaths(directory);
            lines.Add("PASS update directory guard blocks rename while allowing atomic replacement and preserving settings");
            if (!Environment.ProcessPath!.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                UpdateInstaller.TestJobLifetime(directory);
                lines.Add("PASS detached updater survives service job closure while the attached parent is terminated");
            }
            ServiceTests();
            lines.Add("PASS service ABI, token/logon queries, bounded event names, event lifecycle, and untrusted install-path rejection");
            LoginTests.Run(directory, lines).GetAwaiter().GetResult();
            KeypadTest();
            lines.Add("PASS keypad digits/decimal/navigation, key-up flags, and Windows translation with either Num Lock state (no input injected)");
            ScreenSaverTests.Run(lines);
            WheelTests.Run(lines).GetAwaiter().GetResult();
            WheelPipelineTests.Run(lines).GetAwaiter().GetResult();
            WindowTextTest();
            lines.Add("PASS Unicode window title and edit text round-trip");
            string path = System.IO.Path.Combine(directory, "test.ini");
            var settings = new Settings(path) { Host = "192.168.1.10", Role = "Controller", RemoteOnRight = false, AutoConnect = false, DiscoveryEnabled = true, PeerName = "LAPTOP" };
            using var identity = new PairingIdentity();
            settings.StoreIdentity(identity); settings.PairingCode = identity.Invitation; settings.Save();
            var loaded = new Settings(path);
            using var loadedIdentity = loaded.Identity();
            if (loaded.PairingCode != identity.Invitation || loadedIdentity.Invitation != identity.Invitation || loaded.Host != settings.Host || loaded.RemoteOnRight || loaded.AutoConnect || !loaded.DiscoveryEnabled || loaded.PeerName != "LAPTOP")
                throw new Exception("Portable settings did not round-trip.");
            if (File.ReadAllText(path).Contains(identity.Invitation, StringComparison.Ordinal)) throw new Exception("Credential saved in cleartext.");
            File.Delete(path);
            lines.Add("PASS INI round-trip and DPAPI credential protection");
            LifecycleTests.Run(directory).GetAwaiter().GetResult();
            lines.Add("PASS stopped pairing cannot restart, automatic resume is blocked, settings failure cannot prevent Pause, and 250 concurrent start/stop races");
            TransportTest().GetAwaiter().GetResult();
            lines.Add("PASS native AOT TLS authentication, input echo, and disconnect");
            PairingTest().GetAwaiter().GetResult();
            lines.Add("PASS native AOT discovery and one-sided verified pairing with client identity proof");
            using (var engine = new Engine())
            {
                for (int i = 0; i < 3; i++)
                {
                    engine.Start(false, "", null, identity, true, 0);
                    Thread.Sleep(80); engine.StopAsync().GetAwaiter().GetResult();
                    if (engine.Running) throw new Exception("Role transition did not finish shutting down.");
                }
                for (int i = 0; i < 20; i++)
                {
                    engine.Start(false, "", null, identity, true, 0);
                    engine.StopAsync().GetAwaiter().GetResult();
                    if (engine.Running || engine.Connected) throw new Exception("Stop raced receiver startup.");
                }
            }
            lines.Add("PASS awaited role transitions can stop and restart the input listener");
            ReceiverLifecycle(identity).GetAwaiter().GetResult();
            lines.Add("PASS native receiver accepts beside a stalled TLS client, rejects a second active session, and drains shutdown");
            using (var worker = new InputWorker()) { Thread.Sleep(150); worker.Emergency(); Thread.Sleep(100); }
            lines.Add("PASS dedicated hook thread, emergency path, and clean teardown (no remote input injected)");
            lines.Add($"Desktop {Native.Desktop.Width} x {Native.Desktop.Height}");
            File.WriteAllLines(System.IO.Path.Combine(directory, "results.txt"), lines);
            return 0;
        }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex); File.WriteAllLines(System.IO.Path.Combine(directory, "results.txt"), lines); return 1;
        }
    }
    private static void ServiceTests()
    {
        if (MainWindow.AllowIncomingPairing(true, true, false)
            || MainWindow.AllowIncomingPairing(false, true, true)
            || MainWindow.AllowIncomingPairing(false, false, false)
            || !MainWindow.AllowIncomingPairing(true, true, true)
            || !MainWindow.AllowIncomingPairing(true, false, false))
            throw new Exception("Service pairing must require local enrollment or an existing trusted peer and honor Pause.");
        if (Marshal.SizeOf<ServiceNative.StartupInfo>() != 104 || Marshal.SizeOf<ServiceNative.ProcessInfo>() != 24
            || Marshal.SizeOf<ServiceNative.JobLimits>() != 144 || Marshal.SizeOf<ServiceNative.Status>() != 28
            || Marshal.SizeOf<ServiceNative.ServiceEntry>() != 16)
            throw new Exception("Service Win32 ABI layout mismatch.");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (ServiceHost.ReadLogonId(identity.Token) == 0 || ServiceNative.TokenValue<uint>(identity.Token, 12) != System.Diagnostics.Process.GetCurrentProcess().SessionId)
            throw new Exception("Session token metadata mismatch.");
        string stopName = "Global\\Glide.Service.Stop." + Guid.NewGuid().ToString("N");
        if (!ServiceSession.ValidEvent(stopName, "Stop") || ServiceSession.ValidEvent(stopName, "Show")
            || ServiceSession.ValidEvent(stopName + " --unexpected", "Stop")
            || ServiceSession.ValidEvent("Global\\Glide.Service.Stop.extra." + Guid.NewGuid().ToString("N"), "Stop"))
            throw new Exception("Service event name validation failed.");
        string eventName = "Local\\Glide.Service.Test." + Guid.NewGuid().ToString("N");
        using (var signal = new ServiceEvent(eventName, identity.User!.Value, true))
        {
            if (signal.Poll()) throw new Exception("Service event began signaled.");
            signal.Signal();
            if (!signal.Poll() || signal.Poll()) throw new Exception("Service wakeup did not auto-reset.");
            bool rejected = false;
            try { using var duplicate = new ServiceEvent(eventName, identity.User.Value, true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { rejected = true; }
            if (!rejected) throw new Exception("Service accepted a pre-existing event.");
        }
        using (var stop = new ServiceEvent(eventName, identity.User!.Value, false, true))
        {
            stop.Signal();
            if (!stop.Poll() || !stop.Poll()) throw new Exception("Service shutdown signal was lost.");
        }
        if (!string.Equals(Environment.ProcessPath, ServiceHost.Executable, StringComparison.OrdinalIgnoreCase))
        {
            bool rejected = false;
            try { ServiceHost.ValidateInstallLocation(); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("Service accepted an unprotected executable path.");
        }
    }
    private static void KeypadTest()
    {
        if (InputWorker.KeyIdentity(0x61, 0x4f, 0) != InputWorker.KeyIdentity(0x23, 0x4f, 2)
            || InputWorker.KeyIdentity(0x23, 0x4f, 0) == InputWorker.KeyIdentity(0x23, 0x4f, 1)
            || InputWorker.KeyIdentity(0x4f, 0, 0) == InputWorker.KeyIdentity(0x61, 0x4f, 0))
            throw new Exception("Key identity cannot track keypad release after Num Lock/Shift changes.");
        // Exercise the actual INPUT builder, then Windows character translation
        // with synthetic keyboard states. Do not type into the user's desktop.
        int[] scans = [0x52, 0x4f, 0x50, 0x51, 0x4b, 0x4c, 0x4d, 0x47, 0x48, 0x49, 0x53];
        int[] navigation = [0x2d, 0x23, 0x28, 0x22, 0x25, 0x0c, 0x27, 0x24, 0x26, 0x21, 0x2e];
        var layout = GetKeyboardLayout(0);
        for (int i = 0; i < scans.Length; i++)
        {
            int digit = i < 10 ? 0x60 + i : 0x6e;
            foreach (int vk in new[] { digit, navigation[i] })
            {
                foreach (int flags in new[] { 0, 2 })
                {
                    var input = InputWorker.BuildKeyInput(new Packet(MessageKind.Key, vk, scans[i], flags));
                    var key = input.Data.Keyboard;
                    if (input.Type != 1 || key.Vk != vk || key.Scan != scans[i] || key.Flags != flags || key.Extra == 0)
                        throw new Exception("Keypad meaning or key-up was lost during injection preparation.");
                }
                string? previous = null;
                foreach (byte numLock in new byte[] { 0, 1 })
                {
                    var state = new byte[256]; state[0x90] = numLock;
                    var text = new System.Text.StringBuilder(8);
                    var key = InputWorker.BuildKeyInput(new Packet(MessageKind.Key, vk, scans[i])).Data.Keyboard;
                    int count = ToUnicodeEx(key.Vk, key.Scan, state, text, text.Capacity, 4, layout);
                    string result = count > 0 ? text.ToString(0, count) : "";
                    if (vk == digit && (count <= 0 || (i < 10 && result != i.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                        throw new Exception("Keypad number did not translate to a digit.");
                    if (vk != digit && count != 0) throw new Exception("Keypad navigation unexpectedly produced text.");
                    if (previous is not null && result != previous) throw new Exception("Keypad translation depends on receiver Num Lock.");
                    previous = result;
                }
            }
        }
        // Dedicated navigation, keypad operators/Enter, letters and right-hand
        // modifiers must retain physical scan-code forwarding and extended flags.
        foreach (var packet in new[] {
            new Packet(MessageKind.Key, 0x23, 0x4f, 1), new Packet(MessageKind.Key, 0x2e, 0x53, 1),
            new Packet(MessageKind.Key, 0x6b, 0x4e), new Packet(MessageKind.Key, 0x6d, 0x4a),
            new Packet(MessageKind.Key, 0x6a, 0x37), new Packet(MessageKind.Key, 0x6f, 0x35, 1),
            new Packet(MessageKind.Key, 0x0d, 0x1c, 1), new Packet(MessageKind.Key, 0x41, 0x1e),
            new Packet(MessageKind.Key, 0xa3, 0x1d, 1), new Packet(MessageKind.Key, 0x90, 0x45, 1) })
        {
            foreach (int up in new[] { 0, 2 })
            {
                var key = InputWorker.BuildKeyInput(packet with { C = packet.C | up }).Data.Keyboard;
                if (key.Vk != 0 || key.Scan != packet.B || key.Flags != (uint)(packet.C | up | 8))
                    throw new Exception("Non-keypad scan-code forwarding changed.");
            }
        }
        var fallback = InputWorker.BuildKeyInput(new Packet(MessageKind.Key, 0xaf, 0, 2)).Data.Keyboard;
        if (fallback.Vk != 0xaf || fallback.Scan != 0 || fallback.Flags != 2)
            throw new Exception("Scanless virtual-key fallback changed.");
    }
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(
        uint vk, uint scan, byte[] state, System.Text.StringBuilder text, int count, uint flags, nint layout);
    private static void WindowTextTest()
    {
        Native.WindowProc proc = Native.DefWindowProc;
        var wc = new Native.WindowClass { Size = (uint)Marshal.SizeOf<Native.WindowClass>(), Proc = proc,
            Instance = Native.GetModuleHandle(null), Name = "Glide.UnicodeTest" };
        if (Native.RegisterClassEx(ref wc) == 0) throw new Exception("Test window registration failed.");
        nint window = Native.CreateWindowEx(0, wc.Name, "Glide · Δ", 0, 0, 0, 100, 100, 0, 0, wc.Instance, 0);
        if (window == 0) throw new Exception("Test window creation failed.");
        try
        {
            var text = new System.Text.StringBuilder(64);
            Native.GetWindowText(window, text, text.Capacity);
            if (text.ToString() != "Glide · Δ") throw new Exception("Unicode title was truncated.");
            Native.SetWindowText(window, "Glide – connected");
            Native.GetWindowText(window, text, text.Capacity);
            if (text.ToString() != "Glide – connected") throw new Exception("Unicode title update was truncated.");
            nint edit = Native.CreateWindowEx(0, "EDIT", "PC – 你好", 0x40000000, 0, 0, 80, 20, window, 0, wc.Instance, 0);
            Native.GetWindowText(edit, text, text.Capacity);
            if (text.ToString() != "PC – 你好") throw new Exception("Unicode control text was corrupted.");
        }
        finally { Native.DestroyWindow(window); GC.KeepAlive(proc); }
    }
    private static async Task PairingTest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var sender = new PairingIdentity(); using var receiver = new PairingIdentity();
        using var discovery = new DiscoveryService(receiver.Fingerprint, () => new("Receiver", receiver.Fingerprint, true, false), new(System.Net.IPAddress.Loopback, 0), []);
        using var beacon = new System.Net.Sockets.UdpClient();
        await beacon.SendAsync(new Announcement("Sender", sender.Fingerprint, false, false).Encode(), discovery.LocalEndpoint, timeout.Token);
        while (discovery.Peers.Snapshot(Environment.TickCount64).Length == 0) await Task.Delay(20, timeout.Token);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        try
        {
            string? serverCode = null, clientCode = null;
            var accept = Task.Run(async () => await EasyPairing.AcceptAsync(await listener.AcceptTcpClientAsync(timeout.Token), receiver,
                (prompt, _) => { serverCode = prompt.Code; return Task.FromResult(true); }, timeout.Token));
            var invitation = await EasyPairing.RequestAsync("127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port,
                receiver.Fingerprint, "Sender", sender, (prompt, _) => { clientCode = prompt.Code; return Task.FromResult(true); }, timeout.Token);
            var peer = await accept;
            if (invitation.Encode() != receiver.Invitation || peer.Invitation.Encode() != sender.Invitation || clientCode != serverCode)
                throw new Exception("Native confirmed pairing failed.");
        }
        finally { listener.Stop(); }
    }
    private static async Task TransportTest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var identity = new PairingIdentity();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accept = Task.Run(async () => await Connection.AcceptAsync(await listener.AcceptTcpClientAsync(timeout.Token), identity, 1920, 1080, timeout.Token));
            await using var client = await Connection.ConnectAsync("127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, Invitation.Parse(identity.Invitation), 2560, 1440, timeout.Token);
            await using var server = await accept;
            var echo = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Input += packet => server.Send(packet);
            client.Input += packet => echo.TrySetResult(packet);
            var clientRun = client.RunAsync(timeout.Token); var serverRun = server.RunAsync(timeout.Token);
            var sent = new Packet(MessageKind.Move, 32000, 16000);
            client.Send(sent);
            if (await echo.Task.WaitAsync(timeout.Token) != sent) throw new Exception("Native encrypted echo failed.");
            client.Stop(); server.Stop();
            try { await clientRun; } catch (Exception) { }
            try { await serverRun; } catch (Exception) { }
            await client.DisposeAsync(); await server.DisposeAsync();
        }
        finally { listener.Stop(); }
    }
    private static async Task ReceiverLifecycle(PairingIdentity identity)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); reservation.Start();
        int port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var engine = new Engine();
        engine.Start(false, "", null, identity, true, port);
        System.Net.Sockets.TcpClient stalled;
        while (true)
        {
            var candidate = new System.Net.Sockets.TcpClient();
            try { await candidate.ConnectAsync(System.Net.IPAddress.Loopback, port, ct); stalled = candidate; break; }
            catch (System.Net.Sockets.SocketException) { candidate.Dispose(); await Task.Delay(20, ct); }
            catch { candidate.Dispose(); throw; }
        }
        using var stalledLifetime = stalled;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct); handshake.CancelAfter(TimeSpan.FromSeconds(2));
        await using var client = await Connection.ConnectAsync("127.0.0.1", port, Invitation.Parse(identity.Invitation), 1920, 1080, handshake.Token);
        while (!engine.Connected) await Task.Delay(10, ct);
        bool rejected = false;
        try { await using var duplicate = await Connection.ConnectAsync("127.0.0.1", port, Invitation.Parse(identity.Invitation), 1920, 1080, ct); }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException) { rejected = true; }
        if (!rejected) throw new Exception("Receiver accepted a second active input session.");
        await engine.StopAsync().WaitAsync(ct);
        if (engine.Running || engine.Connected) throw new Exception("Receiver shutdown left an active session.");
    }
}
