using System.Collections.Concurrent;
using Glide.Core;
using static Glide.Native;

namespace Glide;

internal sealed partial class MainWindow
{
    private readonly ConcurrentQueue<Action> uiActions = new();
    private readonly CancellationTokenSource windowLifetime = new();
    private DiscoveryService? discovery;
    private PairingServer? pairingServer;
    private NearbyPeer[] nearby = [];
    private NearbyPeer? selectedPeer;
    private bool manualSetup, uiBusy;
    private volatile bool outgoingPair;
    private bool networkingStarted;
    private long nextAutoAttempt;
    private string discoveryError = "";
    private string cachedCode = "";
    private Invitation? cachedInvitation;
    private Confirmation? confirmation;
    private sealed record PairingPolicy(bool Enabled, byte[]? TrustedFingerprint, bool WindowOpen);
    private volatile PairingPolicy pairingPolicy = new(false, null, false);
    private sealed record Confirmation(PairingPrompt Prompt, TaskCompletionSource<bool> Answer);
    private sealed class WindowContext(MainWindow owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => owner.PostToUi(() => callback(state));
    }
    private void PostToUi(Action action)
    {
        if (closed) return;
        uiActions.Enqueue(action); PostMessage(window, WM_APP + 2, 0, 0);
    }
    private void StartNetworking()
    {
        if (networkingStarted) return;
        identity ??= settings.Identity(); settings.Save();
        networkingStarted = true;
        engine.EmergencyStopped += () => PostToUi(() =>
        {
            settings.AutoConnect = false;
            UpdatePairingPolicy();
            try { settings.Save(); } catch (Exception ex) { message = ex.Message; }
            message = "Emergency stop · sharing remains paused until you press Start.";
        });
        StartDiscoveryServices();
        UpdatePairingPolicy();
    }
    private void StartDiscoveryServices()
    {
        if (!settings.DiscoveryEnabled) { discoveryError = "Discovery disabled in Glide.ini · use Manual setup."; return; }
        try
        {
            byte[] fingerprint = identity!.Fingerprint;
            pairingServer = new PairingServer(identity, ApprovePairing, () => !engine.Connected && !outgoingPair,
                fingerprint =>
                {
                    var policy = pairingPolicy;
                    return policy.Enabled && (policy.TrustedFingerprint is { } trusted
                        ? System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(trusted, fingerprint)
                        : policy.WindowOpen);
                });
            pairingServer.Paired += name => PostToUi(() => ReceivePaired(name));
            pairingServer.Failed += reason => PostToUi(() => { confirmation = null; message = "Pairing stopped · " + reason; Refresh(); });
            discovery = new DiscoveryService(fingerprint, () => new Announcement(Environment.MachineName, fingerprint, !controller, engine.Connected));
            discoveryError = "";
        }
        catch (Exception ex)
        {
            discoveryError = "Nearby discovery unavailable: " + ex.Message + " Use Manual setup.";
        }
    }
    private Invitation? TrustedInvitation()
    {
        string code = settings.PairingCode;
        if (code != cachedCode)
        {
            cachedCode = code; cachedInvitation = null;
            if (code.Length > 0) { try { cachedInvitation = Invitation.Parse(code); } catch (FormatException) { } }
        }
        return cachedInvitation;
    }
    private bool IsTrusted(NearbyPeer peer) => TrustedInvitation() is { } trusted && trusted.Fingerprint.AsSpan().SequenceEqual(peer.Info.Fingerprint);
    private void UpdatePairingPolicy() => pairingPolicy = new(settings.AutoConnect, TrustedInvitation()?.Fingerprint, IsWindowVisible(window));
    private void DiscoverPeers()
    {
        if (discovery is null) return;
        nearby = discovery.Peers.Snapshot(Environment.TickCount64);
        selectedPeer = nearby.FirstOrDefault(p => p.Info.Id == selectedPeer?.Info.Id)
            ?? nearby.FirstOrDefault(IsTrusted) ?? nearby.FirstOrDefault();
        var remembered = nearby.FirstOrDefault(IsTrusted);
        if (controller && !engine.Connected && remembered is not null && settings.Host != remembered.Address.ToString())
        {
            settings.Host = remembered.Address.ToString();
            engine.UpdateAddress(settings.Host);
            SetWindowText(controls[Address], settings.Host);
            try { settings.Save(); } catch (Exception ex) { message = ex.Message; }
        }
        if (discovery.Error is not null) discoveryError = "Discovery stopped: " + discovery.Error + " Use Manual setup.";
    }
    private void SelectNextPeer()
    {
        if (nearby.Length == 0) return;
        int index = Array.FindIndex(nearby, p => p.Info.Id == selectedPeer?.Info.Id);
        selectedPeer = nearby[(index + 1) % nearby.Length];
        message = "";
    }
    private void TryAutoConnect()
    {
        if (!networkingStarted || !settings.AutoConnect || engine.Running || uiBusy || confirmation is not null || pairingServer?.IsPairing == true || Environment.TickCount64 < nextAutoAttempt) return;
        nextAutoAttempt = Environment.TickCount64 + 4000;
        try
        {
            if (!controller) engine.Start(false, "", null, identity, settings.RemoteOnRight);
            else if (TrustedInvitation() is { } trusted && settings.Host.Length > 0)
                engine.Start(true, settings.Host, trusted, null, settings.RemoteOnRight);
        }
        catch (Exception ex) { message = ex.Message; }
    }
    private async Task ChangeRole(bool useController)
    {
        if (useController == controller && engine.Running) return;
        if (previewPath is not null) { SwitchRole(useController); return; }
        uiBusy = true;
        try
        {
            await engine.StopAsync("Switching role…");
            SwitchRole(useController);
            settings.AutoConnect = true; settings.Save();
            nextAutoAttempt = 0; message = "";
        }
        finally { uiBusy = false; }
    }
    private async Task StartOrPause()
    {
        uiBusy = true;
        try
        {
            if (engine.Running)
            {
                settings.AutoConnect = false; settings.Save();
                await engine.StopAsync();
                message = "Sharing paused · press Start to resume. This choice is remembered.";
                return;
            }
            await engine.StopAsync(); // finish a previous failed/recently stopped session
            if (!controller)
            {
                settings.AutoConnect = true; settings.Save();
                engine.Start(false, "", null, identity, settings.RemoteOnRight);
            }
            else if (manualSetup)
            {
                var invitation = Invitation.Parse(Text(Code));
                if (Text(Address).Length == 0) throw new InvalidOperationException("Enter the receiving PC's address.");
                Remember(); settings.AutoConnect = true; settings.Save();
                engine.Start(true, settings.Host, invitation, null, settings.RemoteOnRight);
            }
            else if (selectedPeer is { } peer)
            {
                if (peer.Info.Connected) throw new InvalidOperationException("That PC is already sharing. Pause it there first.");
                Invitation invitation;
                bool remembered = IsTrusted(peer);
                if (pairingServer?.IsPairing == true) throw new InvalidOperationException("Finish the incoming pairing request first.");
                outgoingPair = true; message = "Opening secure pairing…"; Refresh();
                try
                {
                    invitation = await EasyPairing.RequestAsync(peer.Address.ToString(), EasyPairing.Port, peer.Info.Fingerprint,
                        Environment.MachineName, identity!, remembered ? (_, _) => Task.FromResult(true) : ApprovePairing, windowLifetime.Token);
                }
                finally { outgoingPair = false; }
                settings.Host = peer.Address.ToString(); settings.PeerName = peer.Info.Name;
                settings.PairingCode = invitation.Encode(); settings.AutoConnect = true; settings.Save();
                SetWindowText(controls[Address], settings.Host); SetWindowText(controls[Code], invitation.Encode());
                engine.Start(true, settings.Host, invitation, null, settings.RemoteOnRight);
            }
            else if (TrustedInvitation() is { } saved && settings.Host.Length > 0)
            {
                settings.AutoConnect = true; settings.Save();
                engine.Start(true, settings.Host, saved, null, settings.RemoteOnRight);
            }
            else throw new InvalidOperationException("Open Glide on the other PC. If it does not appear, allow private-network access or use Manual setup.");
            message = "";
        }
        finally { uiBusy = false; }
    }
    private async Task<bool> ApprovePairing(PairingPrompt prompt, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new Confirmation(prompt, answer);
        using var registration = ct.Register(() => answer.TrySetCanceled(ct));
        PostToUi(() =>
        {
            if (ct.IsCancellationRequested || closed || confirmation is not null || (prompt.Incoming && (outgoingPair || engine.Connected)))
            { answer.TrySetResult(false); return; }
            confirmation = request;
            message = prompt.Incoming ? "Verify this code on the initiating PC. Nothing to click here."
                : "Compare both screens, then confirm here. Nothing to click on the other PC.";
            if (prompt.Incoming) answer.TrySetResult(true);
            else { ShowWindow(window, 9); SetForegroundWindow(window); }
            Refresh();
        });
        try { return await answer.Task.ConfigureAwait(false); }
        finally
        {
            PostToUi(() =>
            {
                if (!prompt.Incoming && ReferenceEquals(confirmation, request))
                {
                    confirmation = null;
                    message = ct.IsCancellationRequested ? "Pairing ended or timed out. No new pairing was saved here." : "Waiting for the other PC to finish pairing…";
                    Refresh();
                }
            });
        }
    }
    private async void ReceivePaired(PairedPeer peer)
    {
        confirmation = null;
        uiBusy = true;
        try
        {
            if (!settings.AutoConnect) throw new OperationCanceledException("Sharing was paused before pairing completed.");
            await engine.StopAsync("Preparing to receive…");
            SwitchRole(false);
            settings.PeerName = peer.Name; settings.Host = peer.Address; settings.PairingCode = peer.Invitation.Encode();
            settings.AutoConnect = true; settings.Save();
            engine.Start(false, "", null, identity, settings.RemoteOnRight);
            message = $"Paired with {peer.Name} · this PC now receives automatically.";
        }
        catch (Exception ex) { message = ex.Message; }
        finally { uiBusy = false; Refresh(); }
    }
    private async Task ResetIdentity()
    {
        uiBusy = true;
        try
        {
            settings.AutoConnect = false; await engine.StopAsync();
            pairingServer?.Dispose(); pairingServer = null; discovery?.Dispose(); discovery = null;
            identity?.Dispose(); identity = new PairingIdentity(); settings.StoreIdentity(identity); settings.Save();
            SetWindowText(controls[Code], identity.Invitation);
            StartDiscoveryServices();
            message = "New identity created. Previous access to this PC is revoked. Press Start when ready.";
        }
        finally { uiBusy = false; }
    }
    private bool SetupControlVisible(int id) => id switch
    {
        Address or Code or Reveal => manualSetup && confirmation is null,
        Copy or ResetPair => manualSetup && !controller && confirmation is null,
        Manual => confirmation is null,
        NextPeer => !manualSetup && controller && confirmation is null && nearby.Length > 1,
        Start => confirmation is null,
        ApprovePair or RejectPair => confirmation is { Prompt.Incoming: false },
        _ => true
    };
    private void UpdateSetupControls()
    {
        if (!controls.ContainsKey(Manual)) return;
        foreach (int id in new[] { Address, Code, Reveal, Copy, ResetPair, Manual, NextPeer, Start, ApprovePair, RejectPair })
        {
            bool visible = SetupControlVisible(id);
            if (!controlVisibility.TryGetValue(id, out bool previous) || previous != visible)
            {
                controlVisibility[id] = visible;
                ShowWindow(controls[id], visible ? 5 : 0);
            }
        }
        Caption(Manual, manualSetup ? "Nearby PCs" : "Manual setup");
    }
    private void DrawSetup(nint dc)
    {
        Box(dc, 36, 398, 828, 236, Surface, 18);
        if (confirmation is { } request)
        {
            TextAt(dc, request.Prompt.Incoming ? "Pairing from your other PC" : "Confirm your receiving PC", 58, 414, 770, 30, 19, Ink, 600);
            TextAt(dc, request.Prompt.Code, 58, 458, 770, 41, 29, Accent, 600);
            TextAt(dc, $"Compare this code on both screens. Only pair if it matches.\n{request.Prompt.PeerName}  ·  {request.Prompt.Address}", 58, 512, 770, 49, 13, Muted, 400, 0x10);
            if (request.Prompt.Incoming) TextAt(dc, "Confirm on the initiating PC · this PC connects automatically", 58, 585, 770, 24, 13, Accent);
            return;
        }
        TextAt(dc, manualSetup ? "Manual connection" : controller ? "Nearby PCs" : "Ready for your other PC", 58, 414, 640, 29, 18, Ink, 600);
        if (manualSetup) { DrawManualSetup(dc); return; }
        if (controller)
        {
            TextAt(dc, selectedPeer?.Info.Name ?? (settings.PeerName.Length > 0 ? settings.PeerName : "Looking for Glide on your network…"), 58, 460, 640, 34, 21, Ink, 600);
            string detail = selectedPeer is { } peer ? $"{peer.Address}  ·  {(IsTrusted(peer) ? "Remembered pairing" : "Ready to pair")}  ·  {(peer.Info.Receiver ? "Receives" : "Controls")}" : "Open Glide on both PCs. Nearby PCs appear automatically.";
            TextAt(dc, detail, 58, 498, 770, 25, 13, Accent);
            TextAt(dc, selectedPeer is { Info.Connected: true } ? "This PC is already sharing. Pause it there to make a new connection." : selectedPeer is { Info.Receiver: false } p && IsTrusted(p) ? "Both PCs are set to control. Choose “This PC receives” on the other PC." : "Pair once by comparing a short code. No IP or secret to copy.", 58, 532, 770, 25, 13, Muted);
        }
        else
        {
            TextAt(dc, Environment.MachineName, 58, 460, 770, 34, 21, Ink, 600);
            TextAt(dc, "Choose this PC from the nearby list on your controlling PC.", 58, 502, 770, 25, 13, Accent);
            TextAt(dc, "No approval needed here. Verify the code on the initiating PC.", 58, 534, 770, 25, 13, Muted);
        }
        TextAt(dc, engine.Connected ? "Encrypted connection ready" : settings.AutoConnect ? "Remembers your pairing and role" : "Automatic connection paused", 296, 585, 540, 24, 13, engine.Connected ? Accent : Muted);
    }
}
