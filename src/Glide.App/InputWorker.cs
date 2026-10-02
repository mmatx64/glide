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
    private readonly Inbox inbound = new();
    private readonly (Connection Peer, Packet Packet)[] incomingBatch = new (Connection, Packet)[32];
    internal delegate uint InputSender(ReadOnlySpan<Input> inputs);
    private readonly InputSender sendInput;
    private readonly Func<bool> wakeScreenSaver;
    private readonly Func<bool> inputAllowed;
    private readonly Func<bool> releaseAllowed;
    private readonly HookProc mouseProc, keyProc;
    private readonly bool[] physical = new bool[256], suppressed = new bool[768];
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
    private int disposing;
    private int pendingCount, pendingIndex;
    private long wakeDeadline;
    internal bool IsRemote => remote;
    internal bool CapturingMouse => Volatile.Read(ref mouseHook) != 0;
    internal string ThreadDesktop { get; private set; } = "";
    internal event Action<string>? Notice;
    private const nuint Tag = 0x474c4944;
    private const uint Work = WM_APP + 10, Incoming = WM_APP + 11;

    internal InputWorker(InputSender? sendInput = null, Action? wakeScreenSaver = null, Func<bool>? inputAllowed = null, Func<bool>? releaseAllowed = null, Func<bool>? wakePending = null)
    {
        this.sendInput = sendInput ?? SendNativeInput;
        // Mock injection must also avoid changing the user's real screensaver.
        this.wakeScreenSaver = wakePending ?? (wakeScreenSaver is null && sendInput is null
            ? new ScreenSaver().Wake : () => { wakeScreenSaver?.Invoke(); return false; });
        this.inputAllowed = inputAllowed ?? (() => true);
        this.releaseAllowed = releaseAllowed ?? this.inputAllowed;
        mouseProc = Mouse; keyProc = Keyboard;
        thread = new Thread(Loop) { IsBackground = true, Name = "Glide input" };
        thread.Start(); ready.Wait();
        if (startupError is not null)
        {
            thread.Join(); ready.Dispose();
            throw new InvalidOperationException("Input setup failed.", startupError);
        }
    }
    internal void Attach(Connection peer, bool isController, bool remoteRight) => Post(() =>
    {
        Reset(); connection = peer; controller = isController; right = remoteRight;
        SetMouseCapture(isController);
        desktop = Desktop; GetCursorPos(out lastLocal); cooldown = Environment.TickCount64 + 700;
    });
    internal void Detach() => Post(() => { Reset(); connection = null; controller = false; SetMouseCapture(false); });
    internal void Emergency() => Post(Panic);
    internal void Receive(Connection peer, Packet packet)
        => ReceiveBatch(peer, new ReadOnlySpan<Packet>(in packet));
    internal void ReceiveBatch(Connection peer, ReadOnlySpan<Packet> packets)
    {
        if (Volatile.Read(ref disposing) != 0) { peer.Stop(); return; }
        if (!inbound.TryAddBatch(peer, packets, out bool wake)) return;
        if (wake && !PostThreadMessage(threadId, Incoming, 0, 0)) peer.Stop();
    }
    private void Post(Action action)
    {
        if (Volatile.Read(ref disposing) != 0) return;
        commands.Enqueue(action); PostThreadMessage(threadId, Work, 0, 0);
    }
    private void Loop()
    {
        try
        {
            SetThreadDpiAwarenessContext(-4);
            threadId = GetCurrentThreadId(); PeekMessage(out _, 0, 0, 0, 0);
            ThreadDesktop = LoginReceiver.DesktopName(GetThreadDesktop(threadId));
            desktop = Desktop;
            keyHook = SetWindowsHookEx(13, keyProc, GetModuleHandle(null), 0);
            if (keyHook == 0) throw new InvalidOperationException("Windows refused the keyboard hook.");
            timer = SetTimer(0, 0, 50, 0);
            if (timer == 0) throw new InvalidOperationException("Windows refused the input watchdog timer.");
            RegisterHotKey(0, 1, 0x4003, 0x7b);
            ready.Set();
            int result;
            while ((result = GetMessage(out var message, 0, 0, 0)) > 0)
            {
                try
                {
                    if (message.Id == Work) { while (commands.TryDequeue(out var command)) command(); }
                    else if (message.Id == Incoming) DrainIncoming();
                    else if (message.Id == WM_HOTKEY) Panic();
                    else if (message.Id == WM_TIMER)
                    {
                        if (connection is { IsAlive: false }) { Reset(); connection = null; }
                        var current = Desktop;
                        if (current.Left != desktop.Left || current.Top != desktop.Top || current.Width != desktop.Width || current.Height != desktop.Height)
                        { connection?.Stop(); Reset(); desktop = current; Notice?.Invoke("Display layout changed. Reconnecting."); }
                        if (pendingCount != 0) DrainIncoming();
                    }
                    else { TranslateMessage(ref message); DispatchMessage(ref message); }
                }
                catch (Exception ex) { connection?.Stop(); Reset(); Notice?.Invoke(ex.Message); }
            }
            if (result < 0) throw new InvalidOperationException("Windows input message loop failed.");
        }
        catch (Exception ex) { startupError = ex; ready.Set(); }
        finally
        {
            Reset();
            Array.Clear(incomingBatch);
            if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
            if (keyHook != 0) UnhookWindowsHookEx(keyHook);
            if (timer != 0) KillTimer(0, timer);
            UnregisterHotKey(0, 1);
        }
    }
    private void SetMouseCapture(bool enabled)
    {
        // Only a controller needs to intercept the physical mouse. A receiver
        // otherwise takes a needless low-level-hook callback for injected input.
        if (enabled && mouseHook == 0)
        {
            mouseHook = SetWindowsHookEx(14, mouseProc, GetModuleHandle(null), 0);
            if (mouseHook == 0) throw new InvalidOperationException("Windows refused the mouse hook.");
        }
        else if (!enabled && mouseHook != 0)
        {
            if (!UnhookWindowsHookEx(mouseHook)) throw new InvalidOperationException("Windows could not release the mouse hook.");
            mouseHook = 0;
        }
    }
    private void DrainIncoming()
    {
        if (pendingCount == 0) { pendingCount = inbound.TakeBatch(incomingBatch); pendingIndex = 0; }
        bool deferred = false;
        try
        {
            Span<Input> wheels = stackalloc Input[incomingBatch.Length];
            while (pendingIndex < pendingCount)
            {
                int i = pendingIndex;
                var item = incomingBatch[i++];
                if (!ReferenceEquals(item.Peer, connection) || !item.Peer.IsAlive) { pendingIndex = i; continue; }
                if (!controller && receiving && IsWheel(item.Packet))
                {
                    int length = 0;
                    wheels[length++] = BuildMouseInput(item.Packet.A, item.Packet.B);
                    while (i < pendingCount && ReferenceEquals(incomingBatch[i].Peer, item.Peer) && IsWheel(incomingBatch[i].Packet))
                    {
                        var packet = incomingBatch[i++].Packet;
                        wheels[length++] = BuildMouseInput(packet.A, packet.B);
                    }
                    // Separate INPUT records retain small deltas, reversals and
                    // wheel axes. Do not sum them or cross a key/button/move barrier.
                    if (wakeScreenSaver()) { Defer(); deferred = true; return; }
                    Inject(wheels[..length], true);
                }
                else if (!Apply(item.Packet)) { Defer(); deferred = true; return; }
                pendingIndex = i;
            }
        }
        finally
        {
            if (!deferred) FinishBatch();
        }
        void Defer()
        {
            if (wakeDeadline == 0) wakeDeadline = Environment.TickCount64 + 3000;
            if (Environment.TickCount64 >= wakeDeadline)
                throw new InvalidOperationException("Screensaver did not dismiss. Use the receiving PC's local mouse.");
        }
        void FinishBatch()
        {
            Array.Clear(incomingBatch, 0, pendingCount);
            pendingCount = pendingIndex = 0; wakeDeadline = 0;
            // Yield to Work/hotkey messages after at most 32 packets. No timer
            // or wait to fill a batch. Only an actual saver transition defers
            // this bounded batch; commands/emergency work keep running meanwhile.
            if (inbound.CompleteBatch() && !PostThreadMessage(threadId, Incoming, 0, 0)) connection?.Stop();
        }
    }
    private static bool IsWheel(Packet packet) => packet.Kind == MessageKind.Button && packet.A is 2048 or 4096;
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
        catch { connection?.Stop(); remote = false; }
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
            int keyId = KeyIdentity(vk, (int)data.Scan, (int)data.Flags);
            bool up = (data.Flags & 0x80) != 0;
            physical[vk] = !up;
            if (!up && vk == 0x7b && (physical[0xa2] || physical[0xa3] || physical[0x11]) && (physical[0xa4] || physical[0xa5] || physical[0x12]))
            { suppressed[keyId] = true; Panic(); return 1; }
            if (controller && remote && connection is { IsAlive: true })
            {
                // Keep the source Num Lock state (and keyboard LED) authoritative.
                // Otherwise Windows keeps reporting the old keypad meaning here.
                bool localNumLock = vk == 0x90;
                suppressed[keyId] = !up && !localNumLock;
                connection.Send(new Packet(MessageKind.Key, vk, (int)data.Scan,
                    KeyFlags(vk, (int)data.Scan, ((data.Flags & 1) != 0 ? 1 : 0) | (up ? 2 : 0))));
                return localNumLock ? CallNextHookEx(0, code, wParam, lParam) : 1;
            }
            if (suppressed[keyId]) { if (up) suppressed[keyId] = false; return 1; }
        }
        catch { connection?.Stop(); remote = false; }
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
        connection?.Stop(); Reset();
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
    private bool Apply(Packet packet)
    {
        if (connection is not { IsAlive: true }) return true;
        if (packet.Kind == MessageKind.Release) { if (controller && remote) ReturnLocal(); Reset(); return true; }
        if (controller) throw new InvalidDataException("Peer sent unexpected input.");
        if (packet.Kind == MessageKind.Activate || (receiving && packet.Kind is MessageKind.Move or MessageKind.Key or MessageKind.Button))
            if (wakeScreenSaver()) return false;
        if (packet.Kind == MessageKind.Activate)
        { Reset(); receiving = true; Move(packet.A, packet.B); return true; }
        if (!receiving) return true;
        switch (packet.Kind)
        {
            case MessageKind.Move: Move(packet.A, packet.B); break;
            case MessageKind.Key:
                if (packet.A is < 1 or > 255 || packet.B is < 0 or > 255 || packet.C is < 0 or > 3) throw new InvalidDataException("Invalid key.");
                // Num Lock/Shift can change the reported VK while a keypad key is
                // held. Release the key we actually pressed, using physical identity.
                int keyId = KeyIdentity(packet.A, packet.B, packet.C);
                if (heldKeys.TryGetValue(keyId, out var pressed)) packet = packet with { A = pressed.A };
                InjectKey(packet, true);
                if ((packet.C & 2) != 0) heldKeys.Remove(keyId); else heldKeys[keyId] = packet;
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
        return true;
    }
    private void Move(int x, int y)
    {
        if (x is < 0 or > 65535 || y is < 0 or > 65535) throw new InvalidDataException("Invalid cursor position.");
        var input = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Flags = 0xc001, Extra = Tag } } };
        Inject(input, true);
    }
    private void InjectKey(Packet packet, bool required)
    {
        Inject(BuildKeyInput(packet), required);
    }
    internal static int KeyIdentity(int vk, int scan, int flags) =>
        scan == 0 ? 0x200 | vk : (scan & 0xff) | ((KeyFlags(vk, scan, flags) & 1) << 8);
    private static int KeyFlags(int vk, int scan, int flags) =>
        // Hook metadata can mark right Shift extended, but neither Shift scan
        // code has an E0 prefix. Keep down/up and cleanup on the same identity.
        vk is 0x10 or 0xa0 or 0xa1 && scan is 0x2a or 0x36 ? flags & ~1 : flags;
    internal static Input BuildKeyInput(Packet packet)
    {
        // Also normalize older controllers' packets at the receiver.
        int flags = KeyFlags(packet.A, packet.B, packet.C);
        // Non-extended keypad digits/decimal share scan codes with navigation.
        // Preserve the source's resolved VK so the receiver's Num Lock state
        // cannot reinterpret a digit as End/Down/Delete (or the reverse).
        bool keypad = (flags & 1) == 0 && packet.B is
            (>= 0x47 and <= 0x49) or (>= 0x4b and <= 0x4d) or (>= 0x4f and <= 0x53);
        bool scanCode = packet.B != 0 && !keypad;
        return new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput
        { Vk = scanCode ? (ushort)0 : (ushort)packet.A, Scan = (ushort)packet.B,
            Flags = (uint)flags | (scanCode ? 8u : 0u), Extra = Tag } } };
    }
    internal static Input BuildMouseInput(int flags, int data) =>
        new() { Data = new InputUnion { Mouse = new MouseInput { Flags = (uint)flags, Data = unchecked((uint)data), Extra = Tag } } };
    private void InjectMouse(int flags, int data, bool required)
    {
        Inject(BuildMouseInput(flags, data), required);
    }
    private void Inject(Input input, bool required) => Inject(new ReadOnlySpan<Input>(in input), required);
    private void Inject(ReadOnlySpan<Input> inputs, bool required)
    {
        if (!(required ? inputAllowed() : releaseAllowed()))
        {
            if (required) throw new InvalidOperationException("Sign-in desktop changed. Reconnecting.");
            return;
        }
        if (sendInput(inputs) != inputs.Length && required)
            throw new InvalidOperationException("Windows blocked input. Elevated apps and secure desktops require local control.");
    }
    private static unsafe uint SendNativeInput(ReadOnlySpan<Input> inputs)
    {
        fixed (Input* pointer = inputs) return SendInput((uint)inputs.Length, pointer, sizeof(Input));
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposing, 1) != 0) return;
        PostThreadMessage(threadId, 0x12, 0, 0);
        thread.Join(); ready.Dispose();
    }
}
