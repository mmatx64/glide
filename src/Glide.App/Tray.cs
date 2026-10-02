using System.ComponentModel;
using System.Runtime.InteropServices;
using static Glide.Native;

namespace Glide;

internal sealed partial class MainWindow
{
    private const int TrayOpen = 201, TrayStart = 202, TrayControl = 203, TrayReceive = 204,
        TrayLeft = 205, TrayRight = 206, TrayUpdate = 207, TrayAbout = 208;
    private bool trayMenuOpen;
    private bool TrayBusy => uiBusy || confirmation is not null || pairingServer?.IsPairing == true;

    private void ShowMainWindow()
    {
        ShowWindow(window, 9); SetForegroundWindow(window); UpdatePairingPolicy();
    }

    private void HandleTrayNotification(uint notification)
    {
        // Legacy NOTIFYICONDATA callbacks; no NOTIFYICON_VERSION_4 is requested.
        if (notification is 0x202 or 0x203 or 0x400 or 0x401) ShowMainWindow();
        else if (notification is 0x205 or 0x7b) ShowTrayMenu();
    }

    private nint CreateTrayMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint role = 0, side = 0;
        try
        {
            bool busy = TrayBusy, running = SessionRunning;
            Add(menu, TrayOpen, "Open Glide");
            SetMenuDefaultItem(menu, TrayOpen, 0);
            Add(menu, 0, SessionConnected ? $"Connected to {SessionPeerName.Replace("&", "&&")}" : busy ? "Pairing…" : running ? controller ? "Connecting…" : "Listening for your other PC" : settings.AutoConnect ? "Standby" : "Sharing paused", enabled: false);
            Separator(menu);
            Add(menu, TrayStart, running ? "Pause sharing" : controller ? "Start sharing" : "Start receiving", enabled: !busy && !Diagnostic);
            role = CreatePopupMenu();
            if (role == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Add(role, TrayControl, "This PC controls", checkedItem: controller);
            Add(role, TrayReceive, "This PC receives", checkedItem: !controller);
            Append(menu, 0x10 | (busy ? 1u : 0u), (nuint)role, "Role"); role = 0; // Parent owns the submenu.
            side = CreatePopupMenu();
            if (side == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Add(side, TrayLeft, "Other PC on the left", checkedItem: !settings.RemoteOnRight);
            Add(side, TrayRight, "Other PC on the right", checkedItem: settings.RemoteOnRight);
            Append(menu, 0x10 | (busy || running || !controller ? 1u : 0u), (nuint)side, "Screen arrangement"); side = 0;
            Separator(menu);
            Add(menu, TrayUpdate, "Check for updates", enabled: !busy && !updateBusy && !Diagnostic);
            Add(menu, TrayAbout, "About Glide");
            Separator(menu);
            Add(menu, Quit, "Quit");
            return menu;
        }
        catch { DestroyMenu(menu); throw; }
        finally { if (role != 0) DestroyMenu(role); if (side != 0) DestroyMenu(side); }

        static void Append(nint target, uint flags, nuint id, string? text)
        {
            if (!AppendMenu(target, flags, id, text)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        static void Add(nint target, int id, string text, bool enabled = true, bool checkedItem = false)
            => Append(target, (enabled ? 0u : 1u) | (checkedItem ? 8u : 0u), (nuint)id, text);
        static void Separator(nint target) => Append(target, 0x800, 0, null);
    }

    private void ShowTrayMenu()
    {
        if (trayMenuOpen || closed) return;
        trayMenuOpen = true;
        nint menu = 0;
        try
        {
            Refresh();
            menu = CreateTrayMenu();
            if (!GetCursorPos(out var position)) return;
            // Required for dismissing a tray menu by clicking elsewhere, even while hidden.
            SetForegroundWindow(window);
            int command = TrackPopupMenu(menu, 0x100 | 0x80 | 2, position.X, position.Y, 0, window, 0);
            PostMessage(window, 0, 0, 0); // WM_NULL permits the next invocation to stay open.
            if (command != 0 && !closed) PostMessage(window, WM_COMMAND, (nuint)command, 0);
        }
        finally { if (menu != 0) DestroyMenu(menu); trayMenuOpen = false; }
    }

    private bool HandleTrayCommand(int id)
    {
        switch (id)
        {
            case TrayOpen: ShowMainWindow(); return true;
            case TrayAbout:
                MessageBox(window, $"Glide v{UpdateInstaller.VersionText}\n{(service is null ? "Portable" : "Service")} mode\n\nEncrypted mouse and keyboard sharing between two PCs.\n\nClick the tray icon to open Glide.\nCtrl + Alt + F12 stops sharing and returns local control.", "About Glide", 0x40);
                return true;
            case TrayStart:
                if (!TrayBusy && !Diagnostic) { if (!SessionRunning) ShowMainWindow(); Click(Start); }
                return true;
            case TrayControl:
            case TrayReceive:
                if (!TrayBusy) { ShowMainWindow(); Click(id == TrayControl ? RoleControl : RoleReceive); }
                return true;
            case TrayLeft:
            case TrayRight:
                if (!TrayBusy && !SessionRunning && controller)
                {
                    settings.RemoteOnRight = id == TrayRight;
                    Remember(); SaveSettings(); InvalidateRect(window, 0, false);
                }
                return true;
            case TrayUpdate:
                if (!TrayBusy && !updateBusy && !Diagnostic) { ShowMainWindow(); Click(Update); }
                return true;
            default: return false;
        }
    }
}
