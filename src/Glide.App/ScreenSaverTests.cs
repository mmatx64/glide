using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Glide.Core;
using static Glide.Native;

namespace Glide;

internal static class ScreenSaverTests
{
    internal static void Run(List<string> lines)
    {
        bool active = true, running = true, secure = false;
        nint target = 123;
        int searches = 0, closes = 0;
        var saver = new ScreenSaver(() => active, () => running, () => secure,
            () => { searches++; return target; }, window =>
            { if (window != target) throw new Exception("Wrong saver window."); closes++; });
        saver.Wake(0); saver.Wake(249);
        if (closes != 1 || searches != 1) throw new Exception("Screensaver wake queries are not bounded.");
        saver.Wake(250);
        if (closes != 2) throw new Exception("Screensaver wake did not retry.");
        active = false; saver.Wake(500);
        active = true; secure = true; saver.Wake(750);
        secure = false; running = false; saver.Wake(1000);
        if (closes != 2 || searches != 2) throw new Exception("Inactive/secure/absent screensaver was targeted.");
        running = true; target = 0; saver.Wake(1250);
        if (closes != 2) throw new Exception("Missing saver window was targeted.");
        lines.Add("PASS screensaver wake is bounded to 250 ms retries, ignores inactive desktops/password protection/absent savers, and never closes a missing window");

        // A uniquely named hidden window stands in for the saver. Exercise real
        // Win32 lookup/PostMessage and the production receiver thread without
        // activating a saver or sending any input to the user's desktop.
        int closed = 0;
        WindowProc proc = (window, message, w, l) =>
        {
            if (message == WM_CLOSE) closed++;
            return DefWindowProc(window, message, w, l);
        };
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = proc,
            Instance = GetModuleHandle(null), Name = "Glide.SaverTest." + Guid.NewGuid().ToString("N") };
        if (RegisterClassEx(ref wc) == 0) throw new Exception("Saver test class failed.");
        nint window = CreateWindowEx(0, wc.Name, "Hidden saver stand-in", 0, 0, 0, 10, 10, 0, 0, wc.Instance, 0);
        if (window == 0) throw new Exception("Saver test window failed.");
        try
        {
            ReceiverWake(wc.Name).GetAwaiter().GetResult();
            while (PeekMessage(out var message, 0, 0, 0, 1)) DispatchMessage(ref message);
            if (closed != 1 || FindWindow(wc.Name, null) != 0) throw new Exception("Receiver wake did not close the test window.");
        }
        finally { DestroyWindow(window); GC.KeepAlive(proc); }
        lines.Add("PASS active receiver movement/keys/buttons/batched wheels request wake; idle/Release/inactive/stale/controller input does not; real posted WM_CLOSE closes only the hidden test window (no desktop input)");
    }

    private static async Task ReceiverWake(string className)
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
            using var rejected = new ManualResetEventSlim();
            int wakes = 0;
            long now = 0;
            var saver = new ScreenSaver(running: () => true, secure: () => false,
                find: () => FindWindow(className, null));
            using var worker = new InputWorker(inputs =>
            {
                if (inputs[^1].Type == 0 && inputs[^1].Data.Mouse.X == 9000) finished.Set();
                return (uint)inputs.Length;
            }, () => { Interlocked.Increment(ref wakes); saver.Wake(now); now += 250; });
            worker.Notice += _ => rejected.Set();
            worker.Attach(server, false, true);
            worker.Receive(client, new(MessageKind.Activate, 500, 500)); // stale identity
            worker.ReceiveBatch(server, new Packet[]
            {
                new(MessageKind.Move, 100, 100), new(MessageKind.Button, 2048, 120),
                new(MessageKind.Activate, 1000, 2000), new(MessageKind.Move, 2000, 3000),
                new(MessageKind.Button, 2), new(MessageKind.Button, 4),
                new(MessageKind.Button, 2048, 1), new(MessageKind.Button, 2048, -120), new(MessageKind.Button, 4096, 7),
                new(MessageKind.Key, 65, 30), new(MessageKind.Key, 65, 30, 2),
                new(MessageKind.Release), new(MessageKind.Move, 3000, 3000), new(MessageKind.Key, 65, 30),
                new(MessageKind.Button, 2048, 120), new(MessageKind.Activate, 4000, 5000), new(MessageKind.Move, 9000, 5000)
            });
            if (!finished.Wait(TimeSpan.FromSeconds(3)) || Volatile.Read(ref wakes) != 9 || !server.IsAlive)
                throw new Exception("Receiver wake input gating failed.");
            await Task.Delay(300, timeout.Token);
            if (Volatile.Read(ref wakes) != 9) throw new Exception("Idle timer woke the screensaver.");
            worker.Attach(server, true, true);
            worker.Receive(server, new(MessageKind.Release));
            worker.Receive(server, new(MessageKind.Activate, 1000, 1000));
            if (!rejected.Wait(TimeSpan.FromSeconds(3)) || server.IsAlive || Volatile.Read(ref wakes) != 9)
                throw new Exception("Controller accepted a remote screensaver wake.");
        }
        finally { listener.Stop(); }
    }
}
