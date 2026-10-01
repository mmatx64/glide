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
            }
            lines.Add("PASS awaited role transitions can stop and restart the input listener");
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
            using var client = await Connection.ConnectAsync("127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, Invitation.Parse(identity.Invitation), 2560, 1440, timeout.Token);
            using var server = await accept;
            var echo = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Input += packet => server.Send(packet);
            client.Input += packet => echo.TrySetResult(packet);
            var clientRun = client.RunAsync(timeout.Token); var serverRun = server.RunAsync(timeout.Token);
            var sent = new Packet(MessageKind.Move, 32000, 16000);
            client.Send(sent);
            if (await echo.Task.WaitAsync(timeout.Token) != sent) throw new Exception("Native encrypted echo failed.");
            client.Dispose(); server.Dispose();
            try { await clientRun; } catch (Exception) { }
            try { await serverRun; } catch (Exception) { }
            client.Finish(); server.Finish();
        }
        finally { listener.Stop(); }
    }
}
