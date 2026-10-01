using System.Runtime.InteropServices;

namespace Glide;

internal static class Native
{
    internal const uint WM_DESTROY = 2, WM_PAINT = 15, WM_CLOSE = 16, WM_COMMAND = 0x111,
        WM_TIMER = 0x113, WM_HOTKEY = 0x312, WM_APP = 0x8000;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint HookProc(int code, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public Rect(int x, int y, int width, int height) { Left = x; Top = y; Right = x + width; Bottom = y + height; }
        public int Width => Right - Left; public int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WindowClass
    {
        public uint Size, Style; public WindowProc Proc; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? Menu; public string Name; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseHook { public Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyHook { public uint Vk, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal unsafe struct Paint { public nint Dc; public int Erase; public Rect Rectangle; public int Restore, Update; public fixed byte Reserved[32]; }
    [StructLayout(LayoutKind.Sequential)] internal struct DrawItem { public uint Type, Id, Item, Action, State; public nint Window, Dc; public Rect Rect; public nuint Data; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct NotifyIcon
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint ex, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] internal static extern nint DefWindowProc(nint w, uint m, nuint p, nint l);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint w);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint w, int command);
    [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint w);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint w);
    [DllImport("user32.dll")] internal static extern bool GetMessage(out Message message, nint w, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message, nint w, uint min, uint max, uint remove);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern bool IsDialogMessage(nint w, ref Message message);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint w, uint m, nuint p, nint l);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint thread, uint m, nuint p, nint l);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint p, nint l);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint w);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint w, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, in Input input, int size);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint w, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint w, int id);
    [DllImport("user32.dll")] internal static extern nuint SetTimer(nint w, nuint id, uint interval, nint proc);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint w, nuint id);
    [DllImport("user32.dll")] internal static extern nint LoadCursor(nint instance, nint resource);
    [DllImport("user32.dll")] internal static extern nint LoadIcon(nint instance, nint resource);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBox(nint w, string text, string title, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetWindowText(nint w, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint w, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern bool EnableWindow(nint w, bool enable);
    [DllImport("user32.dll")] internal static extern nint SendMessage(nint w, uint m, nuint p, nint l);
    [DllImport("user32.dll")] internal static extern bool MoveWindow(nint w, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint w, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint w, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(nint w, ref Point point);
    [DllImport("gdi32.dll")] internal static extern int SaveDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern bool RestoreDC(nint dc, int saved);
    [DllImport("gdi32.dll")] internal static extern bool SetViewportOrgEx(nint dc, int x, int y, nint previous);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint w, nint rect, bool erase);
    [DllImport("user32.dll")] internal static extern bool AdjustWindowRectEx(ref Rect rect, uint style, bool menu, uint ex);
    [DllImport("user32.dll")] internal static extern nint BeginPaint(nint w, out Paint paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(nint w, ref Paint paint);
    [DllImport("user32.dll")] internal static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int DrawText(nint dc, string text, int length, ref Rect rect, uint flags);
    [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] internal static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] internal static extern uint SetBkColor(nint dc, uint color);
    [DllImport("gdi32.dll")] internal static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] internal static extern bool RoundRect(nint dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] internal static extern nint GetStockObject(int obj);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint w, uint attr, ref int value, int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIcon data);
    [DllImport("user32.dll")] internal static extern bool OpenClipboard(nint w);
    [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")] internal static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("kernel32.dll")] internal static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] internal static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] internal static extern nint GlobalFree(nint memory);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(nint w, nint dc, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint w);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint w, nint dc);
    [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    internal static Rect Desktop => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
    internal static uint Rgb(int hex) => (uint)(((hex & 255) << 16) | (hex & 0xff00) | ((hex >> 16) & 255));
}
