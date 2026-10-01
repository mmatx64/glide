using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Glide.Core;
using static Glide.Native;

namespace Glide;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(-4);
        if (args.Contains("--self-test")) return NativeTests.Run();
        using var mutex = new Mutex(true, "Local\\Glide.Portable.Desktop", out bool first);
        if (!first) { MessageBox(0, "Glide is already running. Open it from the system tray.", "Glide", 0x40); return 0; }
        try { using var window = new MainWindow(args); return window.Run(); }
        catch (Exception ex) { MessageBox(0, ex.Message, "Glide could not start", 0x10); return 1; }
    }
}

internal sealed partial class MainWindow : IDisposable
{
    private const uint Background = 0x10151c, Surface = 0x19212c, Field = 0x111923, Ink = 0xe9eff5,
        Muted = 0x91a0b3, Accent = 0x72e2c4;
    private const uint Style = 0x00c00000 | 0x00080000 | 0x00020000 | 0x02000000;
    private readonly WindowProc proc;
    private readonly Settings settings;
    private readonly Engine engine = new();
    private readonly Dictionary<int, nint> controls = new();
    private readonly Dictionary<int, string> captions = new();
    private readonly Dictionary<(int, int), nint> fonts = new();
    private readonly string[] args;
    private readonly nint fieldBrush = CreateSolidBrush(Rgb((int)Field));
    private PairingIdentity? identity;
    private nint window;
    private double scale = 1;
    private volatile bool controller;
    private bool trayAdded, codeVisible, closed;
    private string message = "";
    private string addresses = "";
    private NotifyIcon tray;
    private uint taskbarCreated;
    private const int RoleControl = 101, RoleReceive = 102, Address = 103, Code = 104,
        Start = 105, Side = 106, Copy = 107, Hide = 108, Quit = 109, Reveal = 110, ResetPair = 111,
        Manual = 112, NextPeer = 113, ApprovePair = 114, RejectPair = 115;
    private string? previewPath;
    private string? profilePath;
    private TimeSpan initialCpu;
    private readonly Stopwatch profileWatch = new();
    private nint icon;

    internal MainWindow(string[] args)
    {
        this.args = args;
        settings = new Settings(); controller = settings.Role != "Receiver";
        proc = Procedure;
        int index = Array.IndexOf(args, "--preview");
        if (index >= 0 && index + 1 < args.Length) previewPath = System.IO.Path.GetFullPath(args[index + 1]);
        index = Array.IndexOf(args, "--profile");
        if (index >= 0 && index + 1 < args.Length) profilePath = System.IO.Path.GetFullPath(args[index + 1]);
        if (args.Contains("--receiver-preview")) controller = false;
        if (previewPath is null) settings.Save();
    }
    internal int Run()
    {
        icon = LoadIcon(GetModuleHandle(null), 32512);
        if (icon == 0) icon = LoadIcon(0, 32512);
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = proc,
            Instance = GetModuleHandle(null), Cursor = LoadCursor(0, 32512), Icon = icon, Name = "Glide.Main" };
        if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("Could not register the app window.");
        window = CreateWindowEx(0, wc.Name, "Glide", Style, unchecked((int)0x80000000), unchecked((int)0x80000000), 900, 760, 0, 0, wc.Instance, 0);
        if (window == 0) throw new InvalidOperationException("Could not create the app window.");
        SynchronizationContext.SetSynchronizationContext(new WindowContext(this));
        FitWindow(GetDpiForWindow(window) / 96.0);
        int dark = 1; DwmSetWindowAttribute(window, 20, ref dark, 4);
        int color = (int)Rgb((int)Background); DwmSetWindowAttribute(window, 35, ref color, 4);
        Button(RoleControl, "This PC controls"); Button(RoleReceive, "This PC receives");
        Edit(Address, false); Edit(Code, true);
        Button(Start, "Start sharing"); Button(Side, "Swap sides"); Button(Copy, "Copy code");
        Button(Hide, "Hide to tray"); Button(Quit, "Quit"); Button(Reveal, "Show"); Button(ResetPair, "New code");
        Button(Manual, "Manual setup"); Button(NextPeer, "Next PC");
        Button(ApprovePair, "Codes match · Pair"); Button(RejectPair, "Reject");
        SwitchRole(controller, false);
        Layout();
        tray = new NotifyIcon { Size = (uint)Marshal.SizeOf<NotifyIcon>(), Window = window, Id = 1,
            Flags = 1 | 2 | 4, Callback = WM_APP + 1, Icon = icon, Tip = "Glide · double-click to open", Info = "", Title = "" };
        if (previewPath is null) trayAdded = Shell_NotifyIcon(0, ref tray);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        SetTimer(window, 1, 500, 0);
        ShowWindow(window, 5); UpdateWindow(window);
        if (previewPath is null && profilePath is null) StartNetworking();
        if (previewPath is not null && args.Contains("--preview-nearby"))
        {
            selectedPeer = new NearbyPeer(new Announcement("YOUR-LAPTOP", new byte[32], true, false), IPAddress.Parse("192.168.1.24"), Environment.TickCount64);
            nearby = [selectedPeer];
        }
        if (previewPath is not null && args.Contains("--preview-confirm"))
            confirmation = new Confirmation(new PairingPrompt("YOUR-LAPTOP", "192.168.1.24", "A1B2 C3D4 E5F6", true), new TaskCompletionSource<bool>());
        Refresh();
        if (previewPath is not null) SetTimer(window, 2, 700, 0);
        if (profilePath is not null)
        {
            initialCpu = Process.GetCurrentProcess().TotalProcessorTime; profileWatch.Start();
            SetTimer(window, 3, 6000, 0);
        }
        while (GetMessage(out var m, 0, 0, 0))
        {
            if (!IsDialogMessage(window, ref m)) { TranslateMessage(ref m); DispatchMessage(ref m); }
        }
        return 0;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    private int S(int n) => (int)Math.Round(n * scale);
    private void FitWindow(double dpiScale)
    {
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor))
            monitor.Work = new Rect(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
        scale = Math.Min(dpiScale, Math.Min((monitor.Work.Width - 24) / 900.0, (monitor.Work.Height - 70) / 750.0));
        scale = Math.Max(.5, scale);
        var rect = new Rect(0, 0, S(900), S(750)); AdjustWindowRectEx(ref rect, Style, false, 0);
        MoveWindow(window, monitor.Work.Left + Math.Max(0, (monitor.Work.Width - rect.Width) / 2),
            monitor.Work.Top + Math.Max(0, (monitor.Work.Height - rect.Height) / 2), rect.Width, rect.Height, false);
    }
    private nint Font(int size, int weight = 400)
    {
        if (fonts.TryGetValue((size, weight), out var font)) return font;
        return fonts[(size, weight)] = CreateFont(-S(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, weight >= 600 ? "Segoe UI Semibold" : "Segoe UI");
    }
    private void Button(int id, string text)
    {
        controls[id] = CreateWindowEx(0, "BUTTON", text, 0x5001000b, 0, 0, 0, 0, window, id, GetModuleHandle(null), 0);
        captions[id] = text;
    }
    private void Edit(int id, bool secret)
    {
        controls[id] = CreateWindowEx(0, "EDIT", "", 0x50010080 | (secret ? 0x20u : 0u), 0, 0, 0, 0, window, id, GetModuleHandle(null), 0);
        SendMessage(controls[id], 0xc5, 512, 0); // bounded edit length
    }
    private void Layout()
    {
        void Place(int id, int x, int y, int w, int h)
        {
            MoveWindow(controls[id], S(x), S(y), S(w), S(h), true);
            SendMessage(controls[id], 0x30, (nuint)Font(14, 500), 1);
        }
        Place(RoleControl, 36, 167, 180, 42); Place(RoleReceive, 224, 167, 180, 42);
        Place(Side, 714, 173, 150, 32);
        Place(Address, 58, 470, 260, 25); Place(Code, 358, 470, 378, 25);
        Place(Reveal, 752, 464, 88, 36); Place(Copy, 710, 517, 130, 35);
        Place(Start, 58, 573, 220, 44); Place(ResetPair, 580, 517, 118, 35);
        Place(Hide, 654, 697, 132, 32); Place(Quit, 796, 697, 68, 32);
        Place(Manual, 708, 409, 132, 32); Place(NextPeer, 708, 465, 132, 35);
        Place(ApprovePair, 58, 573, 220, 44); Place(RejectPair, 292, 573, 130, 44);
    }
    private void SwitchRole(bool useController, bool save = true)
    {
        if (save) Remember();
        controller = useController;
        settings.Role = controller ? "Controller" : "Receiver";
        codeVisible = false;
        if (!controller)
        {
            if (previewPath is null && profilePath is null) identity ??= settings.Identity();
            addresses = string.Join("  ·  ", Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a => a.ToString()));
            if (addresses.Length == 0) addresses = "No IPv4 network detected";
            SetWindowText(controls[Address], addresses.Split("  ·  ")[0]);
            SetWindowText(controls[Code], identity?.Invitation ?? "Preview only");
        }
        else
        {
            SetWindowText(controls[Address], settings.Host);
            SetWindowText(controls[Code], settings.PairingCode);
        }
        SendMessage(controls[Address], 0xcf, controller ? 0u : 1u, 0);
        SendMessage(controls[Code], 0xcf, controller ? 0u : 1u, 0);
        SendMessage(controls[Code], 0xcc, 0x25cf, 0);
        EnableWindow(controls[Side], controller);
        Caption(Reveal, "Show");
        Caption(Start, controller ? "Start sharing" : "Start listening");
        message = "";
        if (save && previewPath is null) settings.Save();
        UpdateSetupControls();
        InvalidateRect(window, 0, false);
    }
    private string Text(int id)
    {
        var text = new StringBuilder(1024); GetWindowText(controls[id], text, text.Capacity); return text.ToString().Trim();
    }
    private void Remember()
    {
        if (controller && manualSetup) { settings.Host = Text(Address); settings.PairingCode = Text(Code); }
        settings.Role = controller ? "Controller" : "Receiver";
        if (previewPath is null) settings.Save();
    }
    private void Caption(int id, string text) { captions[id] = text; SetWindowText(controls[id], text); InvalidateRect(controls[id], 0, false); }
    private async void Click(int id)
    {
        try
        {
        if (uiBusy && id is not (ApprovePair or RejectPair or Hide or Quit)) return;
        switch (id)
        {
            case RoleControl: await ChangeRole(true); break;
            case RoleReceive: await ChangeRole(false); break;
            case Side: settings.RemoteOnRight = !settings.RemoteOnRight; Remember(); InvalidateRect(window, 0, false); break;
            case Reveal:
                codeVisible = !codeVisible;
                SendMessage(controls[Code], 0xcc, codeVisible ? 0u : 0x25cfu, 0);
                Caption(Reveal, codeVisible ? "Hide" : "Show"); InvalidateRect(controls[Code], 0, true); break;
            case Copy: CopyText(Text(Code)); message = "Pairing code copied. Treat it like a password."; InvalidateRect(window, 0, false); break;
            case ResetPair:
                await ResetIdentity(); break;
            case Manual: manualSetup = !manualSetup; UpdateSetupControls(); break;
            case NextPeer: SelectNextPeer(); break;
            case ApprovePair: confirmation?.Answer.TrySetResult(true); message = "Confirmed here · waiting for the other PC…"; break;
            case RejectPair: confirmation?.Answer.TrySetResult(false); break;
            case Start:
                if (previewPath is not null) return;
                await StartOrPause(); break;
            case Hide: HideWindow(); break;
            case Quit: Remember(); DestroyWindow(window); break;
        }
        }
        catch (Exception ex) { message = ex is OperationCanceledException ? "Pairing canceled or timed out. Try again when both PCs are ready." : ex.Message; }
        finally { Refresh(); }
    }
    private void HideWindow()
    {
        if (!trayAdded) { message = "Tray unavailable. Use Minimize to keep Glide running."; InvalidateRect(window, 0, false); return; }
        Remember(); ShowWindow(window, 0);
    }
    private void Refresh()
    {
        DiscoverPeers();
        TryAutoConnect();
        bool running = engine.Running;
        bool busy = uiBusy || confirmation is not null || pairingServer?.IsPairing == true;
        foreach (int id in new[] { RoleControl, RoleReceive }) EnableWindow(controls[id], !busy);
        foreach (int id in new[] { Address, Code, ResetPair, NextPeer, Manual }) EnableWindow(controls[id], !busy && !running);
        EnableWindow(controls[Side], !running && controller);
        EnableWindow(controls[Start], !busy);
        Caption(Start, running ? "Pause sharing" : uiBusy ? "Pairing…" : controller && !manualSetup && selectedPeer is not null && !IsTrusted(selectedPeer) ? "Pair & connect" : controller ? "Start sharing" : "Start receiving");
        UpdateSetupControls();
        InvalidateRect(window, 0, false);
    }
    private nint Procedure(nint w, uint m, nuint p, nint l)
    {
        try
        {
            if (m == taskbarCreated && taskbarCreated != 0) { trayAdded = Shell_NotifyIcon(0, ref tray); return 0; }
            switch (m)
            {
                case WM_COMMAND: if (((p >> 16) & 65535) == 0) Click((int)(p & 65535)); return 0;
                case WM_APP + 2: while (uiActions.TryDequeue(out var action)) action(); return 0;
                case WM_PAINT: PaintWindow(w); return 0;
                case 0x318: DrawContent(w, (nint)p); return 0; // WM_PRINTCLIENT
                case 0x14: return 1;
                case 0x2b: DrawButton(Marshal.PtrToStructure<DrawItem>(l)); return 1;
                case 0x133:
                case 0x138: SetTextColor((nint)p, Rgb((int)Ink)); SetBkColor((nint)p, Rgb((int)Field)); return fieldBrush;
                case WM_TIMER:
                    if (p == 2 && previewPath is not null) { KillTimer(w, 2); Capture(previewPath); DestroyWindow(w); }
                    else if (p == 3 && profilePath is not null)
                    {
                        KillTimer(w, 3); using var process = Process.GetCurrentProcess(); process.Refresh();
                        File.WriteAllText(profilePath, $"Standby profile; sharing off; native Release build\nElapsedSeconds={profileWatch.Elapsed.TotalSeconds:F3}\nCpuPercentOfOneCore={(process.TotalProcessorTime - initialCpu).TotalMilliseconds / profileWatch.Elapsed.TotalMilliseconds * 100:F3}\nWorkingSetBytes={process.WorkingSet64}\nPrivateBytes={process.PrivateMemorySize64}\n");
                        DestroyWindow(w);
                    }
                    else Refresh();
                    return 0;
                case 0x2e0:
                    foreach (var font in fonts.Values) DeleteObject(font); fonts.Clear();
                    var rect = Marshal.PtrToStructure<Rect>(l); MoveWindow(w, rect.Left, rect.Top, rect.Width, rect.Height, false);
                    FitWindow((p & 65535) / 96.0); if (controls.Count > 0) Layout(); InvalidateRect(w, 0, false); return 0;
                case WM_APP + 1:
                    if ((uint)l is 0x203 or 0x205) { ShowWindow(w, 9); SetForegroundWindow(w); } return 0;
                case WM_CLOSE: HideWindow(); return 0;
                case WM_DESTROY:
                    closed = true; KillTimer(w, 1); windowLifetime.Cancel(); engine.Stop();
                    if (trayAdded) { Shell_NotifyIcon(2, ref tray); trayAdded = false; }
                    PostQuitMessage(0); return 0;
            }
        }
        catch (Exception ex) { message = ex.Message; InvalidateRect(w, 0, false); }
        return DefWindowProc(w, m, p, l);
    }
    private void TextAt(nint dc, string text, int x, int y, int width, int height, int size, uint color, int weight = 400, uint flags = 0x20)
    {
        var old = SelectObject(dc, Font(size, weight)); SetTextColor(dc, Rgb((int)color)); SetBkMode(dc, 1);
        var rect = new Rect(S(x), S(y), S(width), S(height)); DrawText(dc, text, text.Length, ref rect, flags | 0x800);
        SelectObject(dc, old);
    }
    private void Box(nint dc, int x, int y, int width, int height, uint color, int radius = 14)
    {
        var brush = CreateSolidBrush(Rgb((int)color)); var oldBrush = SelectObject(dc, brush); var oldPen = SelectObject(dc, GetStockObject(8));
        RoundRect(dc, S(x), S(y), S(x + width), S(y + height), S(radius), S(radius));
        SelectObject(dc, oldBrush); SelectObject(dc, oldPen); DeleteObject(brush);
    }
    private void PaintWindow(nint w)
    {
        var dc = BeginPaint(w, out var paint);
        try { DrawContent(w, dc); }
        finally { EndPaint(w, ref paint); }
    }
    private void DrawContent(nint w, nint dc)
    {
            GetClientRect(w, out var rect); var bg = CreateSolidBrush(Rgb((int)Background)); FillRect(dc, ref rect, bg); DeleteObject(bg);
            Box(dc, 36, 30, 35, 35, Accent, 10); TextAt(dc, "g", 45, 29, 26, 34, 28, Background, 700);
            TextAt(dc, "glide", 82, 29, 140, 38, 28, Ink, 650);
            TextAt(dc, "TWO PCs. ONE FLOW.", 36, 87, 500, 23, 11, Accent, 650);
            TextAt(dc, "Your desk. One cursor.", 34, 111, 680, 44, 32, Ink, 600);
            TextAt(dc, "PORTABLE  /  v0.2", 708, 38, 160, 26, 11, Muted, 500, 2 | 0x20);
            bool connected = engine.Connected;
            Box(dc, 728, 111, 136, 30, connected ? 0x1d3c36u : Surface, 20);
            TextAt(dc, connected ? "●  Connected" : engine.Running ? "●  Connecting" : "○  Standby", 740, 116, 118, 22, 12, connected ? Accent : Muted, 500);
            bool remoteFirst = controller && !settings.RemoteOnRight;
            ScreenCard(dc, 36, remoteFirst, connected); ScreenCard(dc, 492, !remoteFirst, connected);
            TextAt(dc, "↔", 427, 265, 46, 42, 29, connected ? Accent : Muted, 400, 1 | 0x20);
            TextAt(dc, controller ? "Move across the adjoining edge to switch PCs." : "Use the mouse and keyboard attached to your other PC.", 36, 355, 830, 25, 13, Muted);
            DrawSetup(dc);
            string detail = message.Length > 0 ? message : discoveryError.Length > 0 ? discoveryError : engine.Status;
            TextAt(dc, detail, 38, 646, 822, 39, 12, message.Length > 0 ? 0xffcc8a : Muted, 400, 0x10);
            TextAt(dc, "Ctrl + Alt + F12", 36, 703, 137, 24, 12, Ink, 600);
            TextAt(dc, "returns control & stops sharing", 177, 703, 260, 24, 12, Muted);
            TextAt(dc, connected && engine.Latency > 0 ? $"{engine.Latency:0.0} ms RTT" : "LAN ONLY", 466, 703, 165, 24, 11, connected ? Accent : Muted, 500);
    }
    private void DrawManualSetup(nint dc)
    {
            bool connected = engine.Connected;
            TextAt(dc, controller ? "OTHER PC'S ADDRESS" : "THIS PC'S ADDRESS", 58, 446, 270, 19, 10, Muted, 600);
            TextAt(dc, controller ? "PAIRING CODE FROM THE OTHER PC" : "YOUR PRIVATE PAIRING CODE", 358, 446, 460, 19, 10, Muted, 600);
            Box(dc, 50, 465, 281, 39, Field, 8); Box(dc, 350, 465, 394, 39, Field, 8);
            TextAt(dc, controller ? "On the other PC, choose “This PC receives” and start listening.\nCopy its address and pairing code here." : "Paste this address and code into your controlling PC.\nAllow Glide on private networks if Windows asks.", 58, 518, controller ? 755 : 510, 47, 13, Muted, 400, 0x10);
            TextAt(dc, engine.ControllingRemote ? "Controlling your second PC" : connected ? "Secure connection is ready" : "Encrypted · direct over your local network", 296, 585, 542, 24, 13, connected ? Accent : Muted);
    }
    private void ScreenCard(nint dc, int x, bool other, bool connected)
    {
        Box(dc, x, 230, 372, 113, Surface, 16);
        bool active = connected && (engine.ControllingRemote == other);
        Box(dc, x + 20, 252, 65, 47, active ? 0x315e55u : 0x303f51u, 8);
        Box(dc, x + 25, 257, 55, 35, Field, 3);
        Box(dc, x + 43, 300, 20, 4, Muted, 2);
        TextAt(dc, other ? "SECOND PC" : "THIS PC", x + 107, 250, 245, 21, 10, active ? Accent : Muted, 600);
        TextAt(dc, other ? selectedPeer?.Info.Name ?? (settings.PeerName.Length > 0 ? settings.PeerName : "Your other Windows PC") : Environment.MachineName, x + 107, 274, 245, 28, 17, Ink, 600);
        TextAt(dc, other ? connected ? "Paired and connected" : "Waiting to connect" : controller ? "Mouse + keyboard" : "Receives mouse + keyboard", x + 107, 305, 245, 21, 12, Muted);
    }
    private void DrawButton(DrawItem item)
    {
        int id = (int)item.Id;
        bool selected = id is Start or ApprovePair || (id == RoleControl && controller) || (id == RoleReceive && !controller);
        bool disabled = (item.State & 4) != 0, pressed = (item.State & 1) != 0;
        uint color = selected ? pressed ? 0x4abda0u : Accent : pressed ? 0x304154u : Surface;
        if (disabled) color = Surface;
        var backing = CreateSolidBrush(Rgb((int)(id is Start or Reveal or Copy or ResetPair or Manual or NextPeer or ApprovePair or RejectPair ? Surface : Background)));
        var bounds = item.Rect; FillRect(item.Dc, ref bounds, backing); DeleteObject(backing);
        var brush = CreateSolidBrush(Rgb((int)color)); var oldBrush = SelectObject(item.Dc, brush); var oldPen = SelectObject(item.Dc, GetStockObject(8));
        RoundRect(item.Dc, item.Rect.Left, item.Rect.Top, item.Rect.Right, item.Rect.Bottom, S(9), S(9));
        SelectObject(item.Dc, oldBrush); SelectObject(item.Dc, oldPen); DeleteObject(brush);
        var oldFont = SelectObject(item.Dc, Font(13, selected ? 600 : 500)); SetBkMode(item.Dc, 1);
        SetTextColor(item.Dc, Rgb((int)(disabled ? Muted : selected ? Background : Ink)));
        string text = captions.GetValueOrDefault(id, "");
        var rect = item.Rect; DrawText(item.Dc, text, text.Length, ref rect, 1 | 4 | 0x20 | 0x800);
        if ((item.State & 16) != 0) { rect.Left += S(3); rect.Top += S(3); rect.Right -= S(3); rect.Bottom -= S(3); DrawFocusRect(item.Dc, ref rect); }
        SelectObject(item.Dc, oldFont);
    }
    [DllImport("user32.dll")] private static extern bool DrawFocusRect(nint dc, ref Rect rect);
    private void CopyText(string text)
    {
        if (!OpenClipboard(window)) throw new InvalidOperationException("Clipboard is busy. Try Copy code again.");
        try
        {
            var bytes = Encoding.Unicode.GetBytes(text + '\0'); var memory = GlobalAlloc(2, (nuint)bytes.Length);
            if (memory == 0) throw new OutOfMemoryException();
            var data = GlobalLock(memory);
            if (data == 0) { GlobalFree(memory); throw new InvalidOperationException("Cannot access clipboard memory."); }
            Marshal.Copy(bytes, 0, data, bytes.Length); GlobalUnlock(memory);
            EmptyClipboard();
            if (SetClipboardData(13, memory) == 0) { GlobalFree(memory); throw new InvalidOperationException("Could not copy the code."); }
        }
        finally { CloseClipboard(); }
    }
    private void Capture(string path)
    {
        GetClientRect(window, out var rect);
        var dc = GetDC(window); var memory = CreateCompatibleDC(dc);
        var info = new BitmapInfo { Size = 40, Width = rect.Width, Height = -rect.Height, Planes = 1, BitCount = 32, ImageSize = (uint)(rect.Width * rect.Height * 4) };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var pixels, 0, 0); var old = SelectObject(memory, bitmap);
        try
        {
            DrawContent(window, memory);
            foreach (var (id, control) in controls)
            {
                if (!SetupControlVisible(id)) continue;
                GetWindowRect(control, out var childRect);
                var point = new Point(childRect.Left, childRect.Top); ScreenToClient(window, ref point);
                int saved = SaveDC(memory);
                SetViewportOrgEx(memory, point.X, point.Y, 0);
                SendMessage(control, id is Address or Code ? 0x318u : 0x317u, (nuint)memory, 4 | 8);
                RestoreDC(memory, saved);
            }
            var bytes = new byte[info.ImageSize]; Marshal.Copy(pixels, bytes, 0, bytes.Length);
            using var file = new BinaryWriter(File.Create(path));
            file.Write((ushort)0x4d42); file.Write(54 + bytes.Length); file.Write(0); file.Write(54);
            file.Write(40); file.Write(info.Width); file.Write(info.Height); file.Write((ushort)1); file.Write((ushort)32);
            file.Write(0); file.Write(bytes.Length); file.Write(0); file.Write(0); file.Write(0); file.Write(0); file.Write(bytes);
        }
        finally { SelectObject(memory, old); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(window, dc); }
    }
    public void Dispose()
    {
        windowLifetime.Cancel(); pairingServer?.Dispose(); discovery?.Dispose();
        engine.Dispose(); identity?.Dispose();
        if (window != 0 && !closed) DestroyWindow(window);
        foreach (var font in fonts.Values) DeleteObject(font);
        DeleteObject(fieldBrush);
    }
}
