using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Glide.Core;
using static Glide.Native;

namespace Glide;

internal sealed class InputWorker : IDisposable
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly ConcurrentQueue<(Connection Peer, Packet Packet)> inbound = new();
    private readonly HookProc mouseProc, keyProc;
    private readonly bool[] physical = new bool[256], suppressed = new bool[256];
    private readonly Dictionary<int, Packet> heldKeys = new();
    private readonly Dictionary<int, Packet> heldButtons = new();
    private uint threadId;
    private nint mouseHook, keyHook;
    private nuint timer;
    private Connection? connection;
    private bool controller, right, receiving;
    private volatile bool remote;
    private Rect desktop;
    private Point lastLocal, anchor;
    private double remoteX, remoteY;
    private long cooldown;
    private Exception? startupError;
    internal bool IsRemote => remote;
    internal event Action<string>? Notice;
    private const nuint Tag = 0x474c4944;
    private const uint Work = WM_APP + 10, Incoming = WM_APP + 11;

    internal InputWorker()
    {
        mouseProc = Mouse; keyProc = Keyboard;
        thread = new Thread(Loop) { IsBackground = true, Name = "Glide input" };
        thread.Start(); ready.Wait();
        if (startupError is not null) throw new InvalidOperationException("Input setup failed.", startupError);
    }
    internal void Attach(Connection peer, bool isController, bool remoteRight) => Post(() =>
    {
        Reset(); connection = peer; controller = isController; right = remoteRight;
        desktop = Desktop; GetCursorPos(out lastLocal); cooldown = Environment.TickCount64 + 700;
    });
    internal void Detach() => Post(() => { Reset(); connection = null; });
    internal void Emergency() => Post(Panic);
    internal void Receive(Connection peer, Packet packet)
    {
        if (inbound.Count >= 256) { connection?.Dispose(); return; }
        inbound.Enqueue((peer, packet)); PostThreadMessage(threadId, Incoming, 0, 0);
    }
    private void Post(Action action) { commands.Enqueue(action); PostThreadMessage(threadId, Work, 0, 0); }
    private void Loop()
    {
        try
        {
            SetThreadDpiAwarenessContext(-4);
            threadId = GetCurrentThreadId(); PeekMessage(out _, 0, 0, 0, 0);
            desktop = Desktop;
            mouseHook = SetWindowsHookEx(14, mouseProc, GetModuleHandle(null), 0);
            keyHook = SetWindowsHookEx(13, keyProc, GetModuleHandle(null), 0);
            if (mouseHook == 0 || keyHook == 0) throw new InvalidOperationException("Windows refused the input hooks.");
            timer = SetTimer(0, 0, 50, 0);
            if (timer == 0) throw new InvalidOperationException("Windows refused the input watchdog timer.");
            RegisterHotKey(0, 1, 0x4003, 0x7b);
            ready.Set();
            while (GetMessage(out var message, 0, 0, 0))
            {
                try
                {
                    if (message.Id == Work) { while (commands.TryDequeue(out var command)) command(); }
                    else if (message.Id == Incoming) { if (inbound.TryDequeue(out var item) && ReferenceEquals(item.Peer, connection)) Apply(item.Packet); }
                    else if (message.Id == WM_HOTKEY) Panic();
                    else if (message.Id == WM_TIMER)
                    {
                        if (connection is { IsAlive: false }) { Reset(); connection = null; }
                        var current = Desktop;
                        if (current.Left != desktop.Left || current.Top != desktop.Top || current.Width != desktop.Width || current.Height != desktop.Height)
                        { connection?.Dispose(); Reset(); desktop = current; Notice?.Invoke("Display layout changed. Reconnecting."); }
                    }
                    else { TranslateMessage(ref message); DispatchMessage(ref message); }
                }
                catch (Exception ex) { connection?.Dispose(); Reset(); Notice?.Invoke(ex.Message); }
            }
        }
        catch (Exception ex) { startupError = ex; ready.Set(); }
        finally
        {
            Reset();
            if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
            if (keyHook != 0) UnhookWindowsHookEx(keyHook);
            if (timer != 0) KillTimer(0, timer);
            UnregisterHotKey(0, 1);
        }
    }
    private nint Mouse(int code, nuint wParam, nint lParam)
    {
        if (code < 0) return CallNextHookEx(0, code, wParam, lParam);
        try
        {
            var data = Marshal.PtrToStructure<MouseHook>(lParam);
            if ((data.Flags & 1) != 0 || data.Extra == Tag || !controller || connection is not { IsAlive: true })
                return CallNextHookEx(0, code, wParam, lParam);
            uint message = (uint)wParam;
            if (message == 0x200)
            {
                if (remote)
                {
                    int dx = data.Point.X - anchor.X, dy = data.Point.Y - anchor.Y;
                    // Let our recenter warp reach Windows; suppressing it can leave the
                    // physical cursor at the edge and destroy subsequent motion deltas.
                    if (dx == 0 && dy == 0) return CallNextHookEx(0, code, wParam, lParam);
                    remoteX += dx; remoteY = Math.Clamp(remoteY + dy, 0, connection.RemoteHeight - 1);
                    if ((right && remoteX < 0) || (!right && remoteX >= connection.RemoteWidth))
                    { ReturnLocal(); return 1; }
                    remoteX = Math.Clamp(remoteX, 0, connection.RemoteWidth - 1);
                    connection.Send(new Packet(MessageKind.Move, Coordinates.Normalize(remoteX, connection.RemoteWidth), Coordinates.Normalize(remoteY, connection.RemoteHeight)));
                    SetCursorPos(anchor.X, anchor.Y);
                    return 1;
                }
                bool crossing = right ? data.Point.X >= desktop.Right - 1 && data.Point.X > lastLocal.X : data.Point.X <= desktop.Left && data.Point.X < lastLocal.X;
                lastLocal = data.Point;
                if (crossing && Environment.TickCount64 >= cooldown && !AnyHeld())
                {
                    remoteX = right ? 2 : connection.RemoteWidth - 3;
                    remoteY = Coordinates.Denormalize(Coordinates.Normalize(data.Point.Y - desktop.Top, desktop.Height), connection.RemoteHeight);
                    if (connection.Send(new Packet(MessageKind.Activate, Coordinates.Normalize(remoteX, connection.RemoteWidth), Coordinates.Normalize(remoteY, connection.RemoteHeight))))
                    {
                        // Center inside a real monitor; a virtual desktop can contain gaps.
                        anchor = new Point(GetSystemMetrics(0) / 2, GetSystemMetrics(1) / 2);
                        remote = true; SetCursorPos(anchor.X, anchor.Y);
                    }
                    return 1;
                }
            }
            else if (remote)
            {
                (int flags, int mouseData) = message switch
                {
                    0x201 => (2, 0), 0x202 => (4, 0), 0x204 => (8, 0), 0x205 => (16, 0),
                    0x207 => (32, 0), 0x208 => (64, 0),
                    0x20b => (128, (int)(data.Data >> 16)), 0x20c => (256, (int)(data.Data >> 16)),
                    0x20a => (2048, (short)(data.Data >> 16)), 0x20e => (4096, (short)(data.Data >> 16)), _ => (0, 0)
                };
                if (flags != 0) { connection.Send(new Packet(MessageKind.Button, flags, mouseData)); return 1; }
            }
        }
        catch { connection?.Dispose(); remote = false; }
        return CallNextHookEx(0, code, wParam, lParam);
    }
    private nint Keyboard(int code, nuint wParam, nint lParam)
    {
        if (code < 0) return CallNextHookEx(0, code, wParam, lParam);
        try
        {
            var data = Marshal.PtrToStructure<KeyHook>(lParam);
            if ((data.Flags & 0x10) != 0 || data.Vk >= 256) return CallNextHookEx(0, code, wParam, lParam);
            int vk = (int)data.Vk;
            bool up = (data.Flags & 0x80) != 0;
            physical[vk] = !up;
            if (!up && vk == 0x7b && (physical[0xa2] || physical[0xa3] || physical[0x11]) && (physical[0xa4] || physical[0xa5] || physical[0x12]))
            { suppressed[vk] = true; Panic(); return 1; }
            if (controller && remote && connection is { IsAlive: true })
            {
                suppressed[vk] = !up;
                connection.Send(new Packet(MessageKind.Key, vk, (int)data.Scan, ((data.Flags & 1) != 0 ? 1 : 0) | (up ? 2 : 0)));
                return 1;
            }
            if (suppressed[vk]) { if (up) suppressed[vk] = false; return 1; }
        }
        catch { connection?.Dispose(); remote = false; }
        return CallNextHookEx(0, code, wParam, lParam);
    }
    private bool AnyHeld()
    {
        for (int key = 1; key < 256; key++) if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }
    private void ReturnLocal()
    {
        connection?.Send(new Packet(MessageKind.Release));
        remote = false; cooldown = Environment.TickCount64 + 350;
        int y = desktop.Top + (int)Coordinates.Denormalize(Coordinates.Normalize(remoteY, connection?.RemoteHeight ?? desktop.Height), desktop.Height);
        lastLocal = new Point(right ? desktop.Right - 4 : desktop.Left + 3, y);
        SetCursorPos(lastLocal.X, lastLocal.Y);
    }
    private void Panic()
    {
        if (remote) ReturnLocal();
        connection?.Send(new Packet(MessageKind.Release));
        connection?.Dispose(); Reset();
        Notice?.Invoke("Emergency stop. Press Start to resume sharing.");
        EmergencyStopped?.Invoke();
    }
    internal event Action? EmergencyStopped;
    private void Reset()
    {
        if (remote) ReturnLocal();
        remote = false; receiving = false;
        foreach (var packet in heldKeys.Values) InjectKey(packet with { C = packet.C | 2 }, false);
        foreach (var packet in heldButtons.Values) InjectMouse(packet.A, packet.B, false);
        heldKeys.Clear(); heldButtons.Clear();
        cooldown = Environment.TickCount64 + 700;
    }
    private void Apply(Packet packet)
    {
        if (connection is not { IsAlive: true }) return;
        if (packet.Kind == MessageKind.Release) { if (controller && remote) ReturnLocal(); Reset(); return; }
        if (controller) throw new InvalidDataException("Peer sent unexpected input.");
        if (packet.Kind == MessageKind.Activate)
        { Reset(); receiving = true; Move(packet.A, packet.B); return; }
        if (!receiving) return;
        switch (packet.Kind)
        {
            case MessageKind.Move: Move(packet.A, packet.B); break;
            case MessageKind.Key:
                if (packet.A is < 1 or > 255 || packet.B is < 0 or > 255 || packet.C is < 0 or > 3) throw new InvalidDataException("Invalid key.");
                InjectKey(packet, true);
                if ((packet.C & 2) != 0) heldKeys.Remove(packet.A); else heldKeys[packet.A] = packet;
                break;
            case MessageKind.Button:
                if (packet.A is not (2 or 4 or 8 or 16 or 32 or 64 or 128 or 256 or 2048 or 4096)) throw new InvalidDataException("Invalid mouse button.");
                if (packet.A is 128 or 256 && packet.B is not (1 or 2)) throw new InvalidDataException("Invalid extra button.");
                InjectMouse(packet.A, packet.B, true);
                int id = packet.A is 128 or 256 ? 100 + packet.B : packet.A is 2 or 4 ? 1 : packet.A is 8 or 16 ? 2 : 3;
                if (packet.A is 2 or 8 or 32 or 128) heldButtons[id] = packet with { A = packet.A * 2 };
                else if (packet.A is 4 or 16 or 64 or 256) heldButtons.Remove(id);
                break;
            default: throw new InvalidDataException("Unexpected input message.");
        }
    }
    private static void Move(int x, int y)
    {
        if (x is < 0 or > 65535 || y is < 0 or > 65535) throw new InvalidDataException("Invalid cursor position.");
        var input = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Flags = 0xc001, Extra = Tag } } };
        Inject(input, true);
    }
    private static void InjectKey(Packet packet, bool required)
    {
        var input = new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput
        { Vk = packet.B == 0 ? (ushort)packet.A : (ushort)0, Scan = (ushort)packet.B, Flags = (uint)packet.C | (packet.B == 0 ? 0u : 8u), Extra = Tag } } };
        Inject(input, required);
    }
    private static void InjectMouse(int flags, int data, bool required)
    {
        var input = new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = (uint)flags, Data = unchecked((uint)data), Extra = Tag } } };
        Inject(input, required);
    }
    private static void Inject(Input input, bool required)
    {
        if (SendInput(1, in input, Marshal.SizeOf<Input>()) != 1 && required)
            throw new InvalidOperationException("Windows blocked input. Elevated apps and secure desktops require local control.");
    }
    public void Dispose()
    {
        PostThreadMessage(threadId, 0x12, 0, 0);
        thread.Join(TimeSpan.FromSeconds(2)); ready.Dispose();
    }
}
