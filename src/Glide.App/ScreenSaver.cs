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
    private readonly bool native;
    private long nextCheck;
    private bool pending;
    private int waking;

    internal ScreenSaver(Func<bool>? desktopAvailable = null, Func<bool>? running = null,
        Func<bool>? secure = null, Func<nint>? find = null, Action<nint>? close = null)
    {
        native = desktopAvailable is null && running is null && secure is null && find is null && close is null;
        this.desktopAvailable = desktopAvailable ?? InputDesktopAvailable;
        this.running = running ?? (() => SystemParametersInfo(0x72, 0, out int active, 0) && active != 0);
        // Fail closed if Windows cannot tell us whether password protection is on.
        this.secure = secure ?? (() => !SystemParametersInfo(0x76, 0, out int password, 0) || password != 0);
        this.find = find ?? FindInputSaverWindow;
        this.close = close ?? (window => PostMessage(window, WM_CLOSE, 0, 0));
    }

    internal bool Wake() => Wake(Environment.TickCount64);
    internal bool Wake(long now)
    {
        if (native && pending)
        {
            // Once a wake is in flight, check only readiness on subsequent
            // packets/timer retries. Release the held batch promptly when
            // Default returns, rather than waiting for the 250 ms retry gate.
            if (!NativePending()) { pending = false; nextCheck = now + 250; return false; }
            if (now < nextCheck) return true;
            nextCheck = now + 250;
            if (running() && !secure()) WakeNative();
            return true;
        }
        // Bound Win32 queries during fast movement/scrolling. Post rather than
        // send: an unresponsive saver must not stall input or emergency work.
        if (now < nextCheck) return pending;
        nextCheck = now + 250;
        pending = false;
        if (!desktopAvailable() || !running() || secure()) return false;
        if (native) return pending = WakeNative();
        nint window = find();
        if (window != 0) close(window);
        return pending = window != 0;
    }

    private bool NativePending()
    {
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return true;
        try
        {
            if (!DesktopName(desktop).Equals("Default", StringComparison.OrdinalIgnoreCase)) return true;
            return running() && FindSaverWindow(desktop) != 0;
        }
        finally { CloseDesktop(desktop); }
    }

    private bool WakeNative()
    {
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return false;
        try
        {
            string name = DesktopName(desktop);
            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                nint window = FindSaverWindow(desktop);
                if (window == 0) return false;
                close(window); return true;
            }
            if (!SaverDesktop(name)) return false;
        }
        finally { CloseDesktop(desktop); }
        // A thread with existing windows/hooks cannot change desktop. Use a
        // fresh, short-lived thread; leave the normal input hook thread intact.
        if (Interlocked.CompareExchange(ref waking, 1, 0) == 0)
        {
            try { new Thread(WakeSaverDesktop) { IsBackground = true, Name = "Glide saver wake" }.Start(); }
            catch { Interlocked.Exchange(ref waking, 0); throw; }
        }
        return true;
    }
    private unsafe void WakeSaverDesktop()
    {
        nint desktop = 0, original = GetThreadDesktop(GetCurrentThreadId());
        try
        {
            // READOBJECTS | WRITEOBJECTS | JOURNALPLAYBACK. Merely opening the
            // desktop for enumeration (0x81) is insufficient for SendInput.
            desktop = OpenInputDesktop(0, false, 0xa1);
            if (desktop == 0 || !SaverDesktop(DesktopName(desktop)) || !running() || secure()
                || !SetThreadDesktop(desktop) || !SaverInputActive()) return;
            // Only a harmless wake movement goes to the saver. Actual packets
            // stay queued for Default; never send text or clicks to this desktop.
            var input = new Input { Data = new InputUnion { Mouse = new MouseInput { X = 1, Flags = 1, Extra = 0x474c4944 } } };
            SendInput(1, &input, sizeof(Input));
            // Windows owns this dedicated saver desktop. Custom savers may use
            // arbitrary classes; close visible saver windows here only. Default
            // still requires the recognized saver classes, and Winlogon is denied.
            EnumWindowProc callback = (window, _) =>
            {
                if (!SaverInputActive() || secure()) return false;
                if (IsWindowVisible(window)) close(window);
                return true;
            };
            EnumDesktopWindows(desktop, callback, 0); GC.KeepAlive(callback);
        }
        catch (Exception) { /* The receiver's bounded wake deadline reports failure. */ }
        finally
        {
            if (desktop != 0) { SetThreadDesktop(original); CloseDesktop(desktop); }
            Interlocked.Exchange(ref waking, 0);
        }
    }
    private static bool SaverInputActive()
    {
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return false;
        try { return SaverDesktop(DesktopName(desktop)); }
        finally { CloseDesktop(desktop); }
    }
    private static bool SaverDesktop(string name) => name.Equals("Screen-saver", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ScreenSaver", StringComparison.OrdinalIgnoreCase);
    private static string DesktopName(nint handle)
    {
        var name = new System.Text.StringBuilder(128);
        return GetUserObjectInformation(handle, 2, name, (uint)(name.Capacity * sizeof(char)), out _) ? name.ToString() : "";
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
        nint handle = OpenInputDesktop(0, false, 1); // inspect without requiring write/playback rights
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
