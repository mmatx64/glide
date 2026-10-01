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
            string path = System.IO.Path.Combine(directory, "test.ini");
            var settings = new Settings(path) { Host = "192.168.1.10", Role = "Controller", RemoteOnRight = false };
            using var identity = new PairingIdentity();
            settings.StoreIdentity(identity); settings.PairingCode = identity.Invitation; settings.Save();
            var loaded = new Settings(path);
            using var loadedIdentity = loaded.Identity();
            if (loaded.PairingCode != identity.Invitation || loadedIdentity.Invitation != identity.Invitation || loaded.Host != settings.Host || loaded.RemoteOnRight)
                throw new Exception("Portable settings did not round-trip.");
            if (File.ReadAllText(path).Contains(identity.Invitation, StringComparison.Ordinal)) throw new Exception("Credential saved in cleartext.");
            File.Delete(path);
            lines.Add("PASS INI round-trip and DPAPI credential protection");
            TransportTest().GetAwaiter().GetResult();
            lines.Add("PASS native AOT TLS authentication, input echo, and disconnect");
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
