using static Glide.Native;

namespace Glide;

// Invoked only for authenticated, active receiver input, never heartbeats or
// idle timers. Some savers ignore SendInput; ask the standard saver window to
// close as well. Keep the user's timeout/password settings intact.
internal sealed class ScreenSaver
{
    private readonly Func<bool> desktopActive, running, secure;
    private readonly Func<nint> find;
    private readonly Action<nint> close;
    private long nextCheck;

    internal ScreenSaver(Func<bool>? desktopActive = null, Func<bool>? running = null,
        Func<bool>? secure = null, Func<nint>? find = null, Action<nint>? close = null)
    {
        this.desktopActive = desktopActive ?? (() =>
            GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 6, out int active, sizeof(int), out _) && active != 0);
        this.running = running ?? (() => SystemParametersInfo(0x72, 0, out int active, 0) && active != 0);
        // Fail closed if Windows cannot tell us whether password protection is on.
        this.secure = secure ?? (() => !SystemParametersInfo(0x76, 0, out int password, 0) || password != 0);
        this.find = find ?? (() => FindWindow("WindowsScreenSaverClass", null));
        this.close = close ?? (window => PostMessage(window, WM_CLOSE, 0, 0));
    }

    internal void Wake() => Wake(Environment.TickCount64);
    internal void Wake(long now)
    {
        // Bound Win32 queries during fast movement/scrolling. Post rather than
        // send: an unresponsive saver must not stall input or emergency work.
        if (now < nextCheck) return;
        nextCheck = now + 250;
        if (!desktopActive() || !running() || secure()) return;
        nint window = find();
        if (window != 0) close(window);
    }
}
