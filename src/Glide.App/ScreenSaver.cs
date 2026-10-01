using static Glide.Native;

namespace Glide;

// Invoked only for authenticated, active receiver input, never heartbeats or
// idle timers. Some savers ignore SendInput; ask the standard saver window to
// close as well. Keep the user's timeout/password settings intact.
internal sealed class ScreenSaver
{
    private readonly Func<bool> desktopAvailable, running, secure;
    private readonly Func<nint> find;
    private readonly Action<nint> close;
    private long nextCheck;

    internal ScreenSaver(Func<bool>? desktopAvailable = null, Func<bool>? running = null,
        Func<bool>? secure = null, Func<nint>? find = null, Action<nint>? close = null)
    {
        this.desktopAvailable = desktopAvailable ?? InputDesktopAvailable;
        this.running = running ?? (() => SystemParametersInfo(0x72, 0, out int active, 0) && active != 0);
        // Fail closed if Windows cannot tell us whether password protection is on.
        this.secure = secure ?? (() => !SystemParametersInfo(0x76, 0, out int password, 0) || password != 0);
        this.find = find ?? FindInputSaverWindow;
        this.close = close ?? (window => PostMessage(window, WM_CLOSE, 0, 0));
    }

    internal void Wake() => Wake(Environment.TickCount64);
    internal void Wake(long now)
    {
        // Bound Win32 queries during fast movement/scrolling. Post rather than
        // send: an unresponsive saver must not stall input or emergency work.
        if (now < nextCheck) return;
        nextCheck = now + 250;
        if (!desktopAvailable() || !running() || secure()) return;
        nint window = find();
        if (window != 0) close(window);
    }

    // A non-password saver can use its own desktop. Do not require Glide's
    // hook thread to be on the input desktop, and never switch that thread.
    internal static bool AllowedDesktop(string name) => name.Equals("Default", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Screen-saver", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ScreenSaver", StringComparison.OrdinalIgnoreCase);
    private static bool AllowedDesktop(nint handle)
    {
        var name = new System.Text.StringBuilder(128);
        return GetUserObjectInformation(handle, 2, name, (uint)(name.Capacity * sizeof(char)), out _)
            && AllowedDesktop(name.ToString());
    }
    private static bool InputDesktopAvailable()
    {
        nint handle = OpenInputDesktop(0, false, 0x81); // READOBJECTS | WRITEOBJECTS
        if (handle == 0) return false;
        try { return AllowedDesktop(handle); }
        finally { CloseDesktop(handle); }
    }
    private static nint FindInputSaverWindow()
    {
        nint handle = OpenInputDesktop(0, false, 0x81);
        if (handle == 0) return 0;
        try { return AllowedDesktop(handle) ? FindSaverWindow(handle) : 0; }
        finally { CloseDesktop(handle); }
    }
    internal static nint FindSaverWindow(nint desktop)
    {
        nint found = 0;
        var name = new System.Text.StringBuilder(256);
        EnumWindowProc callback = (window, _) =>
        {
            if (GetClassName(window, name, name.Capacity) > 0 && name.ToString() is
                "WindowsScreenSaverClass" or "Blank Screen Saver" or "D3DSaverWndClass" or "Default Screen Saver")
            { found = window; return false; }
            return true;
        };
        EnumDesktopWindows(desktop, callback, 0);
        GC.KeepAlive(callback);
        return found;
    }
}
