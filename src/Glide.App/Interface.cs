// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Glide contributors

using System.Globalization;
using System.Net;
using Glide.Core;
using static Glide.Native;

namespace Glide;

internal sealed partial class MainWindow
{
    private const uint Border = 0x293645;
    private const uint Centered = 1 | 0x20 | 0x8000;
    private bool previewConnected, previewRunning, previewRemembered;
    private double? previewScale;
    private bool SessionConnected => previewConnected || engine.Connected;
    private bool SessionRunning => previewRunning || engine.Running;
    private double SessionLatency => previewConnected ? 5.2 : engine.Latency;
    private bool HasRememberedPeer => previewRemembered || (selectedPeer is not null && !SessionRunning && confirmation is null
        ? IsTrusted(selectedPeer) : TrustedInvitation() is not null);
    // Discovery selection is not necessarily the peer in the active session.
    private string SessionPeerName => confirmation?.Prompt.PeerName
        ?? (SessionRunning && settings.PeerName.Length > 0 ? settings.PeerName : selectedPeer?.Info.Name)
        ?? (settings.PeerName.Length > 0 ? settings.PeerName : "Your other PC");
    private string SessionPeerAddress => confirmation?.Prompt.Address
        ?? (SessionRunning && settings.Host.Length > 0 ? settings.Host : selectedPeer?.Address.ToString())
        ?? settings.Host;

    private void ConfigurePreview()
    {
        // Entirely synthetic display state. Never open a connection or load saved credentials.
        previewConnected = args.Contains("--preview-connected");
        previewRunning = previewConnected || args.Contains("--preview-reconnecting");
        previewRemembered = previewRunning || args.Contains("--preview-paused");
        manualSetup = args.Contains("--preview-manual");
        settings.AutoConnect = !args.Contains("--preview-paused");
        settings.RemoteOnRight = !args.Contains("--preview-left");
        if (previewRemembered)
        {
            settings.PeerName = "LENOVO66";
            settings.Host = "192.168.1.20";
        }
        if (previewRemembered || args.Contains("--preview-nearby"))
        {
            string name = args.Contains("--preview-long-name") ? "DESIGN-STUDIO-WORKSTATION-UPSTAIRS" : "LENOVO66";
            selectedPeer = new NearbyPeer(new Announcement(name, new byte[32], true, previewConnected), IPAddress.Parse("192.168.1.20"), Environment.TickCount64);
            nearby = [selectedPeer];
            if (previewRemembered) settings.PeerName = name;
        }
        if (args.Contains("--preview-confirm"))
        {
            bool incoming = !args.Contains("--preview-outgoing");
            controller = !incoming;
            confirmation = new Confirmation(new PairingPrompt("LENOVO66", "192.168.1.20", "A1B2 C3D4 E5F6", incoming), new TaskCompletionSource<bool>());
        }
        int index = Array.IndexOf(args, "--preview-scale");
        if (index >= 0 && index + 1 < args.Length && double.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double factor)
            && factor >= .5 && factor <= 3) previewScale = factor;
    }

    private void Panel(nint dc, int x, int y, int width, int height, uint fill, int radius = 12)
    {
        Box(dc, x, y, width, height, Border, radius);
        Box(dc, x + 1, y + 1, width - 2, height - 2, fill, Math.Max(2, radius - 2));
    }

    private void DrawContent(nint w, nint dc)
    {
        GetClientRect(w, out var rect);
        var background = CreateSolidBrush(Rgb((int)Background));
        FillRect(dc, ref rect, background); DeleteObject(background);
        Panel(dc, 16, 16, 616, 552, Background);
        Panel(dc, 648, 16, 336, 552, Surface);

        bool remoteFirst = controller && !settings.RemoteOnRight;
        DrawScreen(dc, 64, remoteFirst);
        DrawScreen(dc, 350, !remoteFirst);
        TextAt(dc, "↔", 300, 91, 48, 46, 35, SessionConnected ? Accent : Muted, 400, Centered);
        TextAt(dc, controller ? "Move across the adjoining edge to switch PCs." : "Use the mouse and keyboard attached to your other PC.",
            36, 284, 576, 24, 14, Muted, 400, Centered);
        // Button + separator + helper form one centered group in the left panel.
        Box(dc, 334, 323, 1, 26, Border, 0);
        TextAt(dc, SessionRunning ? "Pause to change sides" : !controller ? "Set on controlling PC" : settings.RemoteOnRight ? "Other PC on the right" : "Other PC on the left",
            354, 326, 140, 22, 13, Muted);
        Box(dc, 36, 366, 576, 1, Border, 0);
        DrawSetup(dc);
        DrawSession(dc);

        Box(dc, 0, 584, ClientWidth, 1, Border, 0);
        DrawKey(dc, "Ctrl", 24, 40); TextAt(dc, "+", 67, 605, 18, 20, 13, Muted, 400, Centered);
        DrawKey(dc, "Alt", 88, 36); TextAt(dc, "+", 127, 605, 18, 20, 13, Muted, 400, Centered);
        DrawKey(dc, "F12", 148, 42);
        TextAt(dc, "Returns control & stops sharing", 208, 605, 400, 22, 13, Muted);
    }

    private void DrawKey(nint dc, string text, int x, int width)
    {
        Panel(dc, x, 599, width, 32, Surface, 8);
        TextAt(dc, text, x, 599, width, 32, 12, Ink, 600, Centered | 4);
    }

    private void DrawScreen(nint dc, int x, bool other)
    {
        bool active = SessionConnected && (controller ? engine.ControllingRemote == other : !other);
        uint bezel = active ? 0x315e55u : 0x303f51u;
        Box(dc, x + 27, 48, 190, 106, bezel, 8);
        Box(dc, x + 33, 54, 178, 94, Field, 2);
        Box(dc, x + 108, 154, 28, 14, bezel, 0);
        Box(dc, x + 76, 168, 92, 6, active ? 0x315e55u : Muted, 4);
        TextAt(dc, other ? "SECOND PC" : "THIS PC", x, 192, 244, 22, 12, active ? Accent : Muted, 500, Centered);
        TextAt(dc, other ? SessionPeerName : previewPath is not null ? "CORSAIRONE" : Environment.MachineName,
            x, 216, 244, 30, 20, Ink, 600, Centered);
        TextAt(dc, other ? controller ? "Receives" : "Mouse + keyboard" : controller ? "Mouse + keyboard" : "Receives",
            x, 248, 244, 23, 14, Muted, 400, Centered);
    }

    private void DrawSession(nint dc)
    {
        bool connected = SessionConnected;
        TextAt(dc, "Session", 672, 36, 164, 35, 26, Ink, 600);
        string state = connected ? "●  Connected" : confirmation is not null || uiBusy ? "○  Pairing" : SessionRunning ? controller ? "○  Connecting" : "○  Listening" : settings.AutoConnect ? "○  Standby" : "○  Paused";
        Box(dc, 842, 39, 118, 30, connected ? 0x1d3c36u : Field, 24);
        TextAt(dc, state, 842, 39, 118, 30, 12, connected ? Accent : Muted, 500, Centered | 4);
        TextAt(dc, "THIS PC", 672, 86, 288, 21, 12, Muted, 500);
        TextAt(dc, connected ? "CONNECTED TO" : confirmation is not null ? "PAIRING WITH" : "OTHER PC", 672, 244, 288, 22, 12, Muted, 500);
        TextAt(dc, SessionPeerName, 672, 273, 288, 33, 22, Ink, 600, 0x20 | 0x8000);
        TextAt(dc, SessionPeerAddress.Length > 0 ? SessionPeerAddress : "Choose a nearby PC to begin", 672, 312, 288, 24, 14, Muted, 400, 0x20 | 0x8000);
        TextAt(dc, HasRememberedPeer ? "Remembered pairing" : confirmation is not null ? "Compare the code on both PCs" : "Pair once to remember this PC", 672, 342, 288, 22, 13, Muted);
        Box(dc, 672, 378, 288, 1, Border, 0);
        if (confirmation is not null)
        {
            TextAt(dc, confirmation.Prompt.Incoming ? "Verify on the initiating PC.\nThis PC connects automatically." : "Only approve matching codes.\nReject if the codes differ.",
                672, 398, 288, 44, 13, Muted, 400, 0x10);
            return;
        }
        // Simple line-style lock, using the same palette as the controls.
        Box(dc, 678, 403, 12, 15, Muted, 10); Box(dc, 680, 405, 8, 13, Surface, 8);
        Box(dc, 674, 413, 20, 15, Muted, 4); Box(dc, 676, 415, 16, 11, Surface, 2);
        TextAt(dc, connected ? "Encrypted connection" : "Encrypted when connected", 708, 405, 252, 25, 14, Muted);
        for (int i = 0; i < 4; i++) Box(dc, 675 + i * 5, 456 - i * 4, 3, 6 + i * 4, Muted, 0);
        TextAt(dc, "Round-trip time", 708, 440, 166, 26, 14, Muted);
        TextAt(dc, connected && SessionLatency > 0 ? $"{SessionLatency:0.0} ms" : "—", 874, 440, 86, 26, 14, connected ? Accent : Muted, 600, 2 | 0x20);
    }

    private string SetupNotice(string fallback)
    {
        if (message.Length > 0) return message;
        if (discoveryError.Length > 0) return discoveryError;
        if (previewRunning && !previewConnected) return "Reconnecting · Peer unavailable. Check the other PC and its private-network firewall rule.";
        if (!SessionConnected && (engine.Running || engine.Status != "Ready when you are")) return engine.Status;
        return fallback;
    }

    private void DrawSetupNotice(nint dc, string fallback, int x = 48, int y = 506, int width = 552, int height = 44, uint flags = 0x10)
    {
        TextAt(dc, SetupNotice(fallback), x, y, width, height, 12, Muted, 400, flags);
    }

    private void DrawSetup(nint dc)
    {
        Panel(dc, 32, 382, 584, 170, Surface);
        if (confirmation is { } request)
        {
            TextAt(dc, request.Prompt.Incoming ? "Pairing from your other PC" : "Confirm your receiving PC", 48, 396, 552, 29, 19, Ink, 600);
            TextAt(dc, request.Prompt.Code, 48, 433, 552, 37, 28, Accent, 600);
            TextAt(dc, $"{request.Prompt.PeerName}  ·  {request.Prompt.Address}", 48, 478, 552, 22, 13, Muted, 400, 0x20 | 0x8000);
            TextAt(dc, request.Prompt.Incoming ? "Compare this code on both screens. Confirm on the initiating PC.\nThis PC will connect automatically." : "Compare this code on both screens. Only pair if it matches.\nUse “Codes match · Pair” to continue, or Reject if it differs.",
                48, 506, 552, 42, 12, Muted, 400, 0x10);
            return;
        }
        TextAt(dc, manualSetup ? "Manual connection" : controller || SessionConnected ? "Nearby PCs" : "Ready for your other PC", 48, 396, 430, 29, 19, Ink, 600);
        if (manualSetup) { DrawManualSetup(dc); return; }
        if (SessionConnected)
        {
            TextAt(dc, "Pause sharing to connect to another PC.", 48, 435, 552, 24, 13, Muted);
            int others = nearby.Count(peer => !IsTrusted(peer) && !(previewConnected && ReferenceEquals(peer, selectedPeer)));
            DrawSetupNotice(dc, others == 0 ? "No other PCs found right now." : $"{others} other {(others == 1 ? "PC" : "PCs")} nearby. Pause sharing to choose one.",
                48, 489, 552, 52, 1 | 0x10);
        }
        else if (controller)
        {
            TextAt(dc, selectedPeer?.Info.Name ?? (settings.PeerName.Length > 0 ? settings.PeerName : "Looking for Glide on your network…"), 48, 438, SetupControlVisible(NextPeer) ? 432 : 552, 28, 18, Ink, 600, 0x20 | 0x8000);
            string detail = selectedPeer is { } peer ? $"{peer.Address}  ·  {(IsTrusted(peer) || previewRemembered ? "Remembered pairing" : "Ready to pair")}  ·  {(peer.Info.Receiver ? "Receives" : "Controls")}" : "Open Glide on both PCs. Nearby PCs appear automatically.";
            TextAt(dc, detail, 48, 476, 552, 23, 12, Accent);
            DrawSetupNotice(dc, !settings.AutoConnect ? "Automatic connection paused. Press Start to resume." : selectedPeer is { Info.Connected: true } ? "That PC is already sharing. Pause it there to make a new connection." : selectedPeer is { Info.Receiver: false } p && IsTrusted(p) ? "Both PCs are set to control. Choose “This PC receives” on the other PC." : "Pair once by comparing a short code. No IP or secret to copy.");
        }
        else
        {
            TextAt(dc, "Choose this PC on your controlling PC.", 48, 439, 552, 28, 16, Ink, 500);
            TextAt(dc, "No approval needed here. Verify the code on the initiating PC.", 48, 477, 552, 23, 12, Accent);
            DrawSetupNotice(dc, settings.AutoConnect ? "Keep Glide open on both PCs to pair." : "Automatic connection paused. Press Start to resume.");
        }
    }

    private void DrawManualSetup(nint dc)
    {
        TextAt(dc, controller ? "OTHER PC'S ADDRESS" : "THIS PC'S ADDRESS", 48, 430, 190, 19, 10, Muted, 600);
        TextAt(dc, controller ? "PAIRING CODE FROM THE OTHER PC" : "YOUR PRIVATE PAIRING CODE", 256, 430, 344, 19, 10, Muted, 600);
        Box(dc, 48, 449, 186, 34, Field, 8); Box(dc, 256, 449, 282, 34, Field, 8);
        DrawSetupNotice(dc, controller ? "On the other PC, choose “This PC receives” and start receiving.\nCopy its address and pairing code here."
            : "Copy this address and code to your controlling PC. Keep the code private.",
            48, 496, controller ? 552 : 278, 52);
    }
}
