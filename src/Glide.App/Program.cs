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
        if (args.Length > 0 && args[0] == "--apply-update") return UpdateInstaller.Run(args);
        if (args.Length > 0 && args[0] is "--update-job-probe" or "--update-lifetime-probe") return UpdateInstaller.RunJobProbe(args);
        if (args.SequenceEqual(new[] { "--service" })) return ServiceHost.Run();
        if (args.SequenceEqual(new[] { "--service-test" })) return ServiceHost.Run(true);
        if (args.SequenceEqual(new[] { "--service-login-test" })) return ServiceHost.Run(true, true);
        if (args.SequenceEqual(new[] { "--enroll-login-control" })) return LoginEnrollment.Enroll();
        if (args.SequenceEqual(new[] { "--make-login-probe" })) return LoginEnrollment.CreateProbe();
        if (args.Length > 0 && args[0] is "--login-receiver" or "--login-probe") return LoginReceiver.Run(args, args[0] == "--login-probe");
        if (args.SequenceEqual(new[] { "--open-service" }) || (args.Length == 0 &&
            string.Equals(Environment.ProcessPath, ServiceHost.Executable, StringComparison.OrdinalIgnoreCase)))
            return ServiceHost.OpenInstalled();
        SetProcessDpiAwarenessContext(-4);
        if (args.Length == 2 && args[0] == "--idle-saver-test") return IdleSaverTest.Run(args[1]);
        if (args.Length == 2 && args[0] == "--system-saver-test") return IdleSaverTest.Run(args[1], true);
        if (args.Contains("--self-test")) return NativeTests.Run();
        if (args.Contains("--service-probe"))
        {
            try { using var serviceProbe = new ServiceSession(args); serviceProbe.RunProbe(); return 0; }
            catch (Exception ex) { ServiceHost.Log("Probe failed: " + ex.Message); return 1; }
        }
        // Rendering diagnostics have no networking or input hooks and can coexist with Glide.
        bool diagnostic = args.Contains("--preview") || args.Contains("--profile");
        using var mutex = new Mutex(true, diagnostic ? null : "Local\\Glide.Portable.Desktop", out bool first);
        if (!first) { if (args.Contains("--service-user")) return 2; MessageBox(0, "Glide is already running. Open it from the system tray.", "Glide", 0x40); return 0; }
        try
        {
            using var service = args.Contains("--service-user") ? new ServiceSession(args) : null;
            using var window = new MainWindow(args, service); return window.Run();
        }
        catch (Exception ex) { MessageBox(0, ex.Message, "Glide could not start", 0x10); return 1; }
    }
}

internal sealed partial class MainWindow : IDisposable
{
    private const uint Background = 0x10151c, Surface = 0x19212c, Field = 0x111923, Ink = 0xe9eff5,
        Muted = 0x91a0b3, Accent = 0x72e2c4;
    private const int ClientWidth = 1000, ClientHeight = 640;
    private const uint Style = 0x00c00000 | 0x00080000 | 0x00020000 | 0x02000000;
    private readonly WindowProc proc;
    private readonly Settings settings;
    private readonly Engine engine = new();
    private readonly Dictionary<int, nint> controls = new();
    private readonly Dictionary<int, string> captions = new();
    private readonly Dictionary<int, bool> controlVisibility = new();
    private readonly Dictionary<(int, int), nint> fonts = new();
    private readonly string[] args;
    private readonly ServiceSession? service;
    private readonly nint fieldBrush = CreateSolidBrush(Rgb((int)Field));
    private PairingIdentity? identity;
    private nint window;
    private double scale = 1;
    private volatile bool controller;
    private bool trayAdded, codeVisible, closed, emergencyHotkey;
    private string message = "";
    private string addresses = "";
    private NotifyIcon tray;
    private uint taskbarCreated;
    private const int RoleControl = 101, RoleReceive = 102, Address = 103, Code = 104,
        Start = 105, Side = 106, Copy = 107, Hide = 108, Quit = 109, Reveal = 110, ResetPair = 111,
        Manual = 112, NextPeer = 113, ApprovePair = 114, RejectPair = 115, Update = 116;
    private string? previewPath;
    private string? profilePath;
    private bool Diagnostic => previewPath is not null || profilePath is not null;
    private TimeSpan initialCpu;
    private readonly Stopwatch profileWatch = new();
    private nint icon;
    private string lastVisualState = "";
    private int paintCount;
    private int refreshCount, visualInvalidationCount;

    internal MainWindow(string[] args, ServiceSession? service = null)
    {
        this.args = args; this.service = service;
        proc = Procedure;
        int index = Array.IndexOf(args, "--preview");
        if (index >= 0 && index + 1 < args.Length) previewPath = System.IO.Path.GetFullPath(args[index + 1]);
        index = Array.IndexOf(args, "--profile");
        if (index >= 0 && index + 1 < args.Length) profilePath = System.IO.Path.GetFullPath(args[index + 1]);
        settings = new Settings(service is null ? null : ServiceHost.SettingsPath, loadFromDisk: previewPath is null && profilePath is null);
        controller = settings.Role != "Receiver";
        if (args.Contains("--receiver-preview")) controller = false;
        if (previewPath is not null) ConfigurePreview();
    }
    internal int Run()
    {
        icon = LoadIcon(GetModuleHandle(null), 32512);
        if (icon == 0) icon = LoadIcon(0, 32512);
        var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = proc,
            Instance = GetModuleHandle(null), Cursor = LoadCursor(0, 32512), Icon = icon, Name = "Glide.Main" };
        if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException("Could not register the app window.");
        window = CreateWindowEx(0, wc.Name, $"Glide  |  {(service is null ? "PORTABLE" : "SERVICE")} / v{UpdateInstaller.VersionText}", Style, unchecked((int)0x80000000), unchecked((int)0x80000000), ClientWidth, ClientHeight, 0, 0, wc.Instance, 0);
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
        Button(Update, "Check for updates");
        SwitchRole(controller, false);
        Layout();
        tray = new NotifyIcon { Size = (uint)Marshal.SizeOf<NotifyIcon>(), Window = window, Id = 1,
            Flags = 1 | 2 | 4, Callback = WM_APP + 1, Icon = icon, Tip = "Glide · double-click to open", Info = "", Title = "" };
        if (!Diagnostic) trayAdded = Shell_NotifyIcon(0, ref tray);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        SetTimer(window, 1, 500, 0);
        ShowWindow(window, service is not null && settings.PairingCode.Length > 0 ? 0 : 5); UpdateWindow(window);
        // First pairing has no input worker yet. The UI still needs the global
        // emergency shortcut; an active worker also recognizes it in its hook.
        if (!Diagnostic) emergencyHotkey = RegisterHotKey(window, 1, 0x4003, 0x7b);
        if (!Diagnostic) StartNetworking();
        Refresh();
        if (previewPath is not null && !args.Contains("--preview-interactive")) SetTimer(window, 2, 700, 0);
        if (profilePath is not null)
        {
            initialCpu = Process.GetCurrentProcess().TotalProcessorTime; profileWatch.Start();
            SetTimer(window, 3, 6000, 0);
        }
        int result;
        while ((result = GetMessage(out var m, 0, 0, 0)) > 0)
        {
            if (!IsDialogMessage(window, ref m)) { TranslateMessage(ref m); DispatchMessage(ref m); }
        }
        if (result < 0) throw new InvalidOperationException("Windows message loop failed.");
        return 0;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    private int S(int n) => (int)Math.Round(n * scale);
    private void FitWindow(double dpiScale)
    {
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor))
            monitor.Work = new Rect(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
        scale = Math.Min(previewScale ?? dpiScale, Math.Min((monitor.Work.Width - 24) / (double)ClientWidth, (monitor.Work.Height - 70) / (double)ClientHeight));
        scale = Math.Max(.5, scale);
        var rect = new Rect(0, 0, S(ClientWidth), S(ClientHeight)); AdjustWindowRectEx(ref rect, Style, false, 0);
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
        Place(RoleControl, 672, 112, 288, 46); Place(RoleReceive, 672, 168, 288, 46);
        Place(Side, 154, 319, 160, 34);
        Place(Address, 56, 454, 170, 25); Place(Code, 264, 454, 266, 25);
        Place(Reveal, 546, 450, 54, 33); Place(Copy, 342, 502, 124, 32);
        Place(Start, 672, 504, 288, 44); Place(ResetPair, 476, 502, 124, 32);
        Place(Hide, 772, 599, 130, 32); Place(Quit, 914, 599, 70, 32);
        Place(Update, 604, 599, 156, 32);
        Place(Manual, 486, 392, 114, 32); Place(NextPeer, 490, 437, 110, 32);
        Place(ApprovePair, 672, 450, 288, 44); Place(RejectPair, 672, 506, 288, 38);
    }
    private void SwitchRole(bool useController, bool remember = true)
    {
        if (remember) Remember();
        controller = useController;
        settings.Role = controller ? "Controller" : "Receiver";
        InvalidateRect(controls[RoleControl], 0, false); InvalidateRect(controls[RoleReceive], 0, false);
        codeVisible = false;
        if (!controller)
        {
            if (!Diagnostic) identity ??= settings.Identity();
            addresses = previewPath is not null ? "192.168.1.10" : string.Join("  ·  ", Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a => a.ToString()));
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
        message = previewPath is not null && args.Contains("--preview-error") ? "Peer unavailable. Check its address, listener, and private-network firewall rule." : "";
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
    }
    private void Caption(int id, string text)
    {
        if (captions.GetValueOrDefault(id) == text) return;
        captions[id] = text; SetWindowText(controls[id], text); InvalidateRect(controls[id], 0, false);
    }
    private void EnableControl(int id, bool enabled)
    {
        if (IsWindowEnabled(controls[id]) != enabled) EnableWindow(controls[id], enabled);
    }
    private async void Click(int id)
    {
        try
        {
        if (uiBusy && id is not (ApprovePair or RejectPair or Hide or Quit)) return;
        switch (id)
        {
            case RoleControl: await ChangeRole(true); break;
            case RoleReceive: await ChangeRole(false); break;
            case Side: settings.RemoteOnRight = !settings.RemoteOnRight; Remember(); SaveSettings(); InvalidateRect(window, 0, false); break;
            case Reveal:
                codeVisible = !codeVisible;
                SendMessage(controls[Code], 0xcc, codeVisible ? 0u : 0x25cfu, 0);
                Caption(Reveal, codeVisible ? "Hide" : "Show"); InvalidateRect(controls[Code], 0, true); break;
            case Copy: CopyText(Text(Code)); message = "Pairing code copied. Treat it like a password."; InvalidateRect(window, 0, false); break;
            case ResetPair:
                if (!Diagnostic) await ResetIdentity();
                else message = "Preview only · no pairing identity is created.";
                break;
            case Manual: manualSetup = !manualSetup; UpdateSetupControls(); break;
            case NextPeer: SelectNextPeer(); break;
            case ApprovePair: confirmation?.Answer.TrySetResult(true); message = "Verified · connecting both PCs…"; break;
            case RejectPair: confirmation?.Answer.TrySetResult(false); break;
            case Start:
                if (Diagnostic) return;
                await StartOrPause(); break;
            case Update: await CheckForUpdate(); break;
            case Hide: HideWindow(); break;
            case Quit:
                if (operations.IsStopped) settings.AutoConnect = false;
                StopSharing();
                try { Remember(); SaveSettings(); }
                catch (Exception ex) { MessageBox(window, "Sharing stopped. Settings could not be saved: " + ex.Message, "Glide", 0x30); }
                finally { DestroyWindow(window); }
                break;
        }
        }
        catch (OperationCanceledException) when (operations.IsStopped) { }
        catch (Exception ex) { message = ex is OperationCanceledException ? "Pairing canceled or timed out. Try again when both PCs are ready." : ex.Message; }
        finally { Refresh(); }
    }
    private void HideWindow()
    {
        if (!trayAdded) { message = "Tray unavailable. Use Minimize to keep Glide running."; InvalidateRect(window, 0, false); return; }
        Remember(); SaveSettings(); ShowWindow(window, 0); UpdatePairingPolicy();
    }
    private void Refresh()
    {
        refreshCount++;
        DiscoverPeers();
        UpdatePairingPolicy();
        TryAutoConnect();
        bool running = SessionRunning;
        bool busy = uiBusy || confirmation is not null || pairingServer?.IsPairing == true;
        foreach (int id in new[] { RoleControl, RoleReceive }) EnableControl(id, !busy);
        foreach (int id in new[] { Address, Code, ResetPair, NextPeer, Manual }) EnableControl(id, !busy && !running);
        EnableControl(Side, !busy && !running && controller);
        EnableControl(Start, !busy);
        EnableControl(Update, !busy && !updateBusy);
        Caption(Start, running ? "Pause sharing" : uiBusy ? "Pairing…" : controller && !manualSetup && selectedPeer is not null && !IsTrusted(selectedPeer) && !previewRemembered ? "Pair & connect" : controller ? "Start sharing" : "Start receiving");
        UpdateSetupControls();
        // Network timers still run, but an unchanged screen does not need repainting.
        string visualState = $"{controller}|{running}|{SessionConnected}|{engine.ControllingRemote}|{engine.Status}|{SessionLatency:0.0}|{message}|{discoveryError}|{manualSetup}|{settings.RemoteOnRight}|{settings.AutoConnect}|{settings.PeerName}|{selectedPeer?.Info}|{selectedPeer?.Address}|{nearby.Length}|{confirmation?.Prompt}|{captions[Start]}";
        if (lastVisualState != visualState)
        {
            visualInvalidationCount++;
            lastVisualState = visualState;
            InvalidateRect(window, 0, false);
        }
    }
    private nint Procedure(nint w, uint m, nuint p, nint l)
    {
        try
        {
            if (m == taskbarCreated && taskbarCreated != 0) { trayAdded = Shell_NotifyIcon(0, ref tray); return 0; }
            switch (m)
            {
                case WM_COMMAND: if (((p >> 16) & 65535) == 0) Click((int)(p & 65535)); return 0;
                case WM_HOTKEY:
                    if (p == 1 && !Diagnostic)
                    {
                        StopSharing("Emergency stop · sharing is off");
                        RememberPause("Emergency stop · sharing remains paused until you press Start.");
                        Refresh();
                    }
                    return 0;
                case WM_APP + 2: while (uiActions.TryDequeue(out var action)) action(); return 0;
                case WM_PAINT: PaintWindow(w); return 0;
                case 0x318: DrawContent(w, (nint)p); return 0; // WM_PRINTCLIENT
                case 0x14: return 1;
                case 0x2b: DrawButton(Marshal.PtrToStructure<DrawItem>(l)); return 1;
                case 0x133:
                case 0x138: SetTextColor((nint)p, Rgb((int)Ink)); SetBkColor((nint)p, Rgb((int)Field)); return fieldBrush;
                case WM_TIMER:
                    if (service?.Stopping == true) { DestroyWindow(w); return 0; }
                    if (service?.ShowRequested == true) { ShowWindow(w, 9); SetForegroundWindow(w); UpdatePairingPolicy(); }
                    if (p == 2 && previewPath is not null) { KillTimer(w, 2); Capture(previewPath); DestroyWindow(w); }
                    else if (p == 3 && profilePath is not null)
                    {
                        KillTimer(w, 3); using var process = Process.GetCurrentProcess(); process.Refresh();
                        var title = new StringBuilder(64); GetWindowText(w, title, title.Capacity);
                        File.WriteAllText(profilePath, $"Standby profile; sharing off; native Release build\nElapsedSeconds={profileWatch.Elapsed.TotalSeconds:F3}\nCpuPercentOfOneCore={(process.TotalProcessorTime - initialCpu).TotalMilliseconds / profileWatch.Elapsed.TotalMilliseconds * 100:F3}\nWorkingSetBytes={process.WorkingSet64}\nPrivateBytes={process.PrivateMemorySize64}\nPaintCount={paintCount}\nRefreshCount={refreshCount}\nVisualInvalidations={visualInvalidationCount}\nWindowTitle={title}\n");
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
                    closed = true; KillTimer(w, 1); windowLifetime.Cancel(); StopSharing();
                    if (emergencyHotkey) { UnregisterHotKey(w, 1); emergencyHotkey = false; }
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
        var brush = CreateSolidBrush(Rgb((int)color));
        if (radius <= 0)
        {
            var bounds = new Rect(S(x), S(y), Math.Max(1, S(width)), Math.Max(1, S(height)));
            FillRect(dc, ref bounds, brush); DeleteObject(brush); return;
        }
        var oldBrush = SelectObject(dc, brush); var oldPen = SelectObject(dc, GetStockObject(8));
        RoundRect(dc, S(x), S(y), S(x + width), S(y + height), S(radius), S(radius));
        SelectObject(dc, oldBrush); SelectObject(dc, oldPen); DeleteObject(brush);
    }
    private void PaintWindow(nint w)
    {
        var dc = BeginPaint(w, out var paint);
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            GetClientRect(w, out var bounds);
            memory = CreateCompatibleDC(dc);
            bitmap = CreateCompatibleBitmap(dc, bounds.Width, bounds.Height);
            if (memory == 0 || bitmap == 0) { DrawContent(w, dc); return; }
            previous = SelectObject(memory, bitmap);
            DrawContent(w, memory);
            BitBlt(dc, 0, 0, bounds.Width, bounds.Height, memory, 0, 0, 0x00cc0020);
            paintCount++;
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            EndPaint(w, ref paint);
        }
    }
    private void DrawButton(DrawItem item)
    {
        int id = (int)item.Id;
        bool selected = id is Start or ApprovePair || (id == RoleControl && controller) || (id == RoleReceive && !controller);
        bool disabled = (item.State & 4) != 0, pressed = (item.State & 1) != 0;
        bool textOnly = id is Manual or NextPeer;
        uint color = selected ? pressed ? 0x4abda0u : Accent : pressed ? 0x304154u : id is RoleControl or RoleReceive ? Field : Surface;
        if (disabled) color = Surface;
        var backing = CreateSolidBrush(Rgb((int)(id is Side or Hide or Quit or Update ? Background : Surface)));
        var bounds = item.Rect; FillRect(item.Dc, ref bounds, backing); DeleteObject(backing);
        if (!textOnly)
        {
            Panel(item.Dc, 0, 0, (int)Math.Round(item.Rect.Width / scale), (int)Math.Round(item.Rect.Height / scale), color, 9);
        }
        var oldFont = SelectObject(item.Dc, Font(14, selected ? 600 : 500)); SetBkMode(item.Dc, 1);
        SetTextColor(item.Dc, Rgb((int)(disabled ? Muted : selected ? Background : textOnly ? Accent : Ink)));
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
        StopSharing();
        windowLifetime.Cancel(); pairingServer?.Dispose(); discovery?.Dispose();
        engine.Dispose(); identity?.Dispose();
        if (window != 0 && !closed) DestroyWindow(window);
        foreach (var font in fonts.Values) DeleteObject(font);
        DeleteObject(fieldBrush);
        operations.Dispose(); windowLifetime.Dispose();
    }
    private void SaveSettings() { if (!Diagnostic) settings.Save(); }
}
