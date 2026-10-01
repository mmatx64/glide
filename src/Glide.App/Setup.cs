using System.Collections.Concurrent;
using Glide.Core;
using static Glide.Native;

namespace Glide;

internal sealed partial class MainWindow
{
    private readonly ConcurrentQueue<Action> uiActions = new();
    private readonly CancellationTokenSource windowLifetime = new();
    private readonly SharingOperations operations = new();
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
        engine.EmergencyStopped += () =>
        {
            StopSharing("Emergency stop · sharing is off");
            PostToUi(() => RememberPause("Emergency stop · sharing remains paused until you press Start."));
        };
        StartDiscoveryServices();
        UpdatePairingPolicy();
    }
    private void StartDiscoveryServices()
    {
        if (!settings.DiscoveryEnabled) { discoveryError = "Discovery disabled in Glide.ini · use Manual setup."; return; }
        PairingServer? server = null;
        DiscoveryService? beacon = null;
        try
        {
            byte[] fingerprint = identity!.Fingerprint;
            server = new PairingServer(identity, ApprovePairing, () => !engine.Connected && !outgoingPair && !operations.IsStopped,
                fingerprint =>
                {
                    var policy = pairingPolicy;
                    return policy.Enabled && (policy.TrustedFingerprint is { } trusted
                        ? System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(trusted, fingerprint)
                        : policy.WindowOpen);
                });
            server.Paired += (peer, epoch) => ApplyPairing(server, peer, epoch);
            server.Failed += reason => PostToUi(() => { if (ReferenceEquals(pairingServer, server)) { confirmation = null; message = "Pairing stopped · " + reason; Refresh(); } });
            beacon = new DiscoveryService(fingerprint, () => new Announcement(Environment.MachineName, fingerprint, !controller, engine.Connected));
            pairingServer = server; discovery = beacon;
            discoveryError = "";
        }
        catch (Exception ex)
        {
            server?.Dispose(); beacon?.Dispose();
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
    private void UpdatePairingPolicy()
    {
        var trusted = TrustedInvitation()?.Fingerprint;
        // An elevated, unpaired service session must never accept first pairing
        // passively. Enroll by initiating and confirming locally, or import a pairing.
        operations.Update(stopped => pairingPolicy = new(AllowIncomingPairing(settings.AutoConnect && !stopped, service is not null, trusted is not null), trusted, IsWindowVisible(window)));
    }
    internal static bool AllowIncomingPairing(bool sharingEnabled, bool serviceMode, bool rememberedPeer) =>
        sharingEnabled && (!serviceMode || rememberedPeer);
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
        if (!networkingStarted || !settings.AutoConnect || operations.IsStopped || engine.Running || uiBusy || confirmation is not null || pairingServer?.IsPairing == true || Environment.TickCount64 < nextAutoAttempt) return;
        nextAutoAttempt = Environment.TickCount64 + 4000;
        try
        {
            var operation = operations.Begin(resume: false);
            operations.Commit(operation, () =>
            {
                if (!controller) engine.Start(false, "", null, identity, settings.RemoteOnRight);
                else if (TrustedInvitation() is { } trusted && settings.Host.Length > 0)
                    engine.Start(true, settings.Host, trusted, null, settings.RemoteOnRight);
            });
        }
        catch (OperationCanceledException) when (operations.IsStopped) { }
        catch (Exception ex) { message = ex.Message; }
    }
    private async Task ChangeRole(bool useController)
    {
        if (useController == controller && engine.Running) return;
        if (Diagnostic) { SwitchRole(useController); return; }
        var operation = operations.Begin();
        pairingServer?.CancelPairing();
        uiBusy = true;
        try
        {
            await engine.StopAsync("Switching role…");
            operations.Commit(operation, () =>
            {
                SwitchRole(useController);
                settings.AutoConnect = true; settings.Save();
                nextAutoAttempt = 0; message = "";
            });
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
                operations.Pause(() => RevokeSharing("Sharing paused · local control"),
                    () => RememberPause("Sharing paused · press Start to resume. This choice is remembered."));
                await engine.StopAsync();
                return;
            }
            var operation = operations.Begin();
            pairingServer?.CancelPairing();
            await engine.StopAsync(); // finish a previous failed/recently stopped session
            operation.Token.ThrowIfCancellationRequested();
            if (!controller)
            {
                operations.Commit(operation, () => { settings.AutoConnect = true; settings.Save(); engine.Start(false, "", null, identity, settings.RemoteOnRight); });
            }
            else if (manualSetup)
            {
                var invitation = Invitation.Parse(Text(Code));
                if (Text(Address).Length == 0) throw new InvalidOperationException("Enter the receiving PC's address.");
                operations.Commit(operation, () => { Remember(); settings.AutoConnect = true; settings.Save(); engine.Start(true, settings.Host, invitation, null, settings.RemoteOnRight); });
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
                    using var pending = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token, operation.Token);
                    invitation = await EasyPairing.RequestAsync(peer.Address.ToString(), EasyPairing.Port, peer.Info.Fingerprint,
                        Environment.MachineName, identity!, remembered ? (_, _) => Task.FromResult(true) : ApprovePairing, pending.Token);
                }
                finally { outgoingPair = false; }
                operations.Commit(operation, () =>
                {
                    settings.Host = peer.Address.ToString(); settings.PeerName = peer.Info.Name;
                    settings.PairingCode = invitation.Encode(); settings.AutoConnect = true; settings.Save();
                    SetWindowText(controls[Address], settings.Host); SetWindowText(controls[Code], invitation.Encode());
                    engine.Start(true, settings.Host, invitation, null, settings.RemoteOnRight);
                });
            }
            else if (TrustedInvitation() is { } saved && settings.Host.Length > 0)
            {
                operations.Commit(operation, () => { settings.AutoConnect = true; settings.Save(); engine.Start(true, settings.Host, saved, null, settings.RemoteOnRight); });
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
            if (ct.IsCancellationRequested || closed || confirmation is not null || (prompt.Incoming && (outgoingPair || engine.Connected || uiBusy)))
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
                    if (!operations.IsStopped) message = ct.IsCancellationRequested ? "Pairing ended or timed out. No new pairing was saved here." : "Waiting for the other PC to finish pairing…";
                    Refresh();
                }
            });
        }
    }
    private Task ApplyPairing(PairingServer server, PairedPeer peer, CancellationToken epoch)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PostToUi(async () =>
        {
            try
            {
                if (ReferenceEquals(pairingServer, server)) await ReceivePaired(peer, epoch);
                applied.TrySetResult();
            }
            catch (Exception ex) { applied.TrySetException(ex); }
        });
        return applied.Task;
    }
    private async Task ReceivePaired(PairedPeer peer, CancellationToken epoch)
    {
        if (epoch.IsCancellationRequested || closed || operations.IsStopped || !settings.AutoConnect || uiBusy) return;
        confirmation = null;
        uiBusy = true;
        try
        {
            epoch.ThrowIfCancellationRequested();
            var operation = operations.Begin(resume: false);
            await engine.StopAsync("Preparing to receive…");
            operations.Commit(operation, () =>
            {
                epoch.ThrowIfCancellationRequested();
                SwitchRole(false);
                settings.PeerName = peer.Name; settings.Host = peer.Address; settings.PairingCode = peer.Invitation.Encode();
                settings.AutoConnect = true; settings.Save();
                engine.Start(false, "", null, identity, settings.RemoteOnRight);
                message = $"Paired with {peer.Name} · this PC now receives automatically.";
            });
        }
        catch (OperationCanceledException) when (operations.IsStopped) { }
        catch (Exception ex) { message = ex.Message; }
        finally { uiBusy = false; Refresh(); }
    }
    private async Task ResetIdentity()
    {
        uiBusy = true;
        try
        {
            StopSharing(); settings.AutoConnect = false; await engine.StopAsync();
            pairingServer?.Dispose(); pairingServer = null; discovery?.Dispose(); discovery = null;
            identity?.Dispose(); identity = new PairingIdentity(); settings.StoreIdentity(identity); settings.Save();
            SetWindowText(controls[Code], identity.Invitation);
            StartDiscoveryServices();
            message = "New identity created. Previous access to this PC is revoked. Press Start when ready.";
        }
        finally { uiBusy = false; }
    }
    private void StopSharing(string reason = "Sharing paused · local control") => operations.Stop(() => RevokeSharing(reason));
    private void RevokeSharing(string reason)
    {
        pairingPolicy = new(false, pairingPolicy.TrustedFingerprint, false);
        pairingServer?.CancelPairing();
        engine.Stop(reason);
    }
    private void RememberPause(string success)
    {
        confirmation = null;
        settings.AutoConnect = false; UpdatePairingPolicy(); message = success;
        try { settings.Save(); }
        catch (Exception ex) { message = "Sharing is off for this session. Could not save pause: " + ex.Message; }
    }
    private bool SetupControlVisible(int id) => id switch
    {
        Address or Code or Reveal => manualSetup && confirmation is null,
        Copy or ResetPair => manualSetup && !controller && confirmation is null,
        Manual => confirmation is null,
        NextPeer => !manualSetup && controller && !SessionRunning && confirmation is null && nearby.Length > 1,
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
}
