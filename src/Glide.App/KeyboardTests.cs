using System.Runtime.InteropServices;
using System.Text;
using Glide.Core;

namespace Glide;

internal static class KeyboardTests
{
    internal static async Task Run(List<string> lines)
    {
        var layout = GetKeyboardLayout(0);
        // Decode production INPUT with Windows, rather than assuming hook flags
        // describe an E0-prefixed scan code. No input reaches the user's desktop.
        foreach (var (vk, scan, expected) in new[] {
            (0xa0, 0x2a, 0xa0), (0x10, 0x2a, 0xa0),
            (0xa1, 0x36, 0xa1), (0x10, 0x36, 0xa1),
            (0xa3, 0x1d, 0xa3), (0xa5, 0x38, 0xa5) })
        {
            foreach (int extended in expected is 0xa3 or 0xa5 ? new[] { 1 } : new[] { 0, 1 })
            {
                var down = new Packet(MessageKind.Key, vk, scan, extended);
                var up = down with { C = extended | 2 };
                uint pressed = Decode(down), released = Decode(up);
                if (pressed != expected || released != expected)
                    throw new Exception($"Windows decoded modifier {vk:x}/{scan:x}/{extended} as {pressed:x}/{released:x}, expected {expected:x}.");
                if (InputWorker.KeyIdentity(vk, scan, extended) != InputWorker.KeyIdentity(vk, scan, up.C))
                    throw new Exception("Modifier release identity changed.");
                if (expected is 0xa0 or 0xa1)
                {
                    if (InputWorker.KeyIdentity(vk, scan, 0) != InputWorker.KeyIdentity(vk, scan, 1))
                        throw new Exception("Shift identity depends on hook extended metadata.");
                    var state = new byte[256]; state[0x10] = state[pressed] = 0x80;
                    Translate('?', state); Translate('A', state);
                    Array.Clear(state);
                    Translate('/', state); Translate('a', state);
                }
            }
        }
        if (InputWorker.KeyIdentity(0xa0, 0x2a, 0) == InputWorker.KeyIdentity(0xa1, 0x36, 1))
            throw new Exception("Left and right Shift identities collided.");
        lines.Add("PASS Windows scan-code decoding and shifted/unshifted ? / A a with either Shift, hook flag variants, and distinct right Ctrl/Alt (no input injected)");
        await ReceiverTest(lines);

        uint Decode(Packet packet)
        {
            var input = InputWorker.BuildKeyInput(packet);
            var key = input.Data.Keyboard;
            if (input.Type != 1 || key.Vk != 0 || (key.Flags & 8) == 0
                || (key.Flags & 2) != (packet.C & 2) || key.Extra == 0)
                throw new Exception("Modifier scan forwarding, release or loop-prevention tag was lost.");
            uint scan = key.Scan | ((key.Flags & 1) != 0 ? 0xe000u : 0u);
            return MapVirtualKeyEx(scan, 3, layout);
        }
        void Translate(char expected, byte[] state)
        {
            short mapped = VkKeyScanEx(expected, layout);
            if (mapped == -1 || (mapped >> 8) is < 0 or > 1)
                throw new Exception("Keyboard test needs a layout with ordinary Shift mappings for ? / A a.");
            uint vk = (uint)(mapped & 0xff);
            uint scan = MapVirtualKeyEx(vk, 0, layout);
            var text = new StringBuilder(8);
            int count = ToUnicodeEx(vk, scan, state, text, text.Capacity, 4, layout);
            if (count != 1 || text[0] != expected)
                throw new Exception($"Windows modifier translation expected {expected}, got {text}.");
        }
    }

    private static async Task ReceiverTest(List<string> lines)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var identity = new PairingIdentity();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var connecting = Connection.ConnectAsync("127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port,
                Invitation.Parse(identity.Invitation), 1920, 1080, timeout.Token);
            var accepting = Connection.AcceptAsync(await listener.AcceptTcpClientAsync(timeout.Token), identity, 1920, 1080, timeout.Token);
            await using var client = await connecting;
            await using var server = await accepting;
            var inputs = new List<Native.KeyboardInput>();
            using var drained = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            using var worker = new InputWorker(batch =>
            {
                foreach (var input in batch)
                    if (input.Type == 1) inputs.Add(input.Data.Keyboard);
                if (inputs.Count == 9) drained.Set();
                if (inputs.Count == 10) completed.Set();
                return (uint)batch.Length;
            });
            worker.Attach(server, false, true);
            worker.ReceiveBatch(server, new Packet[] {
                new(MessageKind.Activate, 1000, 2000),
                new(MessageKind.Key, 0xa1, 0x36, 1), new(MessageKind.Key, 0xbf, 0x35),
                new(MessageKind.Key, 0xbf, 0x35, 2), new(MessageKind.Key, 0xa1, 0x36, 2),
                new(MessageKind.Key, 0xa1, 0x36, 1), new(MessageKind.Key, 0x41, 0x1e),
                new(MessageKind.Key, 0x41, 0x1e, 2), new(MessageKind.Release),
                new(MessageKind.Activate, 1000, 2000), new(MessageKind.Key, 0xa1, 0x36, 1) });
            if (!drained.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Shift receiver sequence did not drain.");
            worker.Detach();
            if (!completed.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Shift receiver sequence did not drain.");
            ushort[] scans = [0x36, 0x35, 0x35, 0x36, 0x36, 0x1e, 0x1e, 0x36, 0x36, 0x36];
            uint[] flags = [8, 8, 10, 10, 8, 8, 10, 10, 8, 10];
            if (!inputs.Select(input => input.Scan).SequenceEqual(scans) || !inputs.Select(input => input.Flags).SequenceEqual(flags))
                throw new Exception("Shift receiver ordering or release/disconnect cleanup was lost.");
            lines.Add("PASS receiver right Shift combinations, differing down/up hook flags, Release and detach cleanup (mock injection)");
        }
        finally { listener.Stop(); }
    }

    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint MapVirtualKeyEx(uint code, uint type, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanEx(char character, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(
        uint vk, uint scan, byte[] state, StringBuilder text, int count, uint flags, nint layout);
}
