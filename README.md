# Glide

A small, portable Windows mouse and keyboard bridge for **two PCs**. Native dark interface, automatic nearby-PC discovery, confirmed pairing without copying IPs or secrets, and encrypted direct connections. Version 0.2 remains a preview pending testing on two physical PCs.

**[Download Glide for Windows x64](https://github.com/mmatx64/glide/releases/download/v0.2.0/Glide-0.2.0-win-x64.zip)** · [Release notes](https://github.com/mmatx64/glide/releases/tag/v0.2.0)

Download the portable ZIP from the release, extract it on both PCs, and follow the steps below. The automatically generated “Source code” downloads are for building the app yourself.

## Run it

1. Extract the ZIP into a writable folder on each PC. Run **Glide.exe**. Windows 10/11 x64; no installer, .NET runtime, administrator rights, cloud account, or service required for ordinary use.
2. Open Glide on **both PCs** and allow access on private networks if Windows asks. The other PC should appear under **Nearby PCs** within a few seconds. If more than one appears, use **Next PC**.
3. On the PC with your physical mouse and keyboard, select **This PC controls**, select the nearby laptop, and click **Pair & connect**. Compare the short code on both screens and click **Codes match · Pair** on **both PCs**. Never approve different codes. The other PC automatically becomes the receiver; no IP or long pairing code needs to be copied.
4. Wait for **Connected**, then move through the adjoining outer screen edge. Move back across the same boundary to return. Release held keys/buttons before leaving the controlling PC.
5. Use **Swap sides** to place the other PC left or right (pause first if already sharing). Glide remembers the pairing, role, and layout, then reconnects when both apps are opened again. A paired PC's changed IP is detected without replacing its trusted identity.
6. **Ctrl + Alt + F12** on either PC stops sharing and restores local control. **Pause sharing** also stops it. The pause persists across launches. Start again or explicitly choose a role to resume. Closing the window hides it to the tray; double-click or right-click its tray icon to reopen. **Quit** exits.

Turn off Mouse Without Borders while trying Glide so both apps do not intercept the same input. Start with an ordinary application on the receiver. To reverse roles after pairing, select **This PC receives** on the old controller and **This PC controls** on the other PC. Pairing exchanges credentials both ways, so no new code is needed. Glide is not installed into Windows startup; automatic reconnection happens when you open it.

### If the other PC does not appear

Both PCs need version 0.2 for discovery. Check that they are on the same private LAN and not isolated by guest Wi-Fi. UDP broadcasts do not generally cross subnets or VPNs. **Manual setup** retains the original IP/code setup for those cases and for version 0.1 peers. The firewall helper below now handles discovery and pairing on both PCs.

### Firewall

Glide uses **TCP 24819** for input, **UDP 24820** for nearby discovery, and **TCP 24821** for confirmed pairing. The app does not change firewall rules. If Windows does not offer a prompt, run the included `Allow-PrivateNetwork.ps1` in an Administrator PowerShell window on **both PCs**. The script limits access to this executable, private networks, and the local subnet. Review it before running it. No router port forwarding is needed. Rerun the updated helper if you previously allowed only the v0.1 input port.

The helper's remove command is:

```powershell
Get-NetFirewallRule -Name 'Glide-Portable-*' | Remove-NetFirewallRule
```

## Portable settings

All app settings, receiver identity, and remembered pairing credentials are in **Glide.ini beside Glide.exe**. Nothing is saved to an application settings folder or the registry. Changes are written atomically through a temporary sibling file. The app reports an error if its folder is read-only.

`Role`, `PeerName`, `PeerAddress`, and `RemoteSide` are human-readable. `AutoConnect=True` remembers automatic reconnection; Pause and emergency stop set it to False. `DiscoveryEnabled=False` disables discovery and easy pairing after restarting, while manual setup remains available. Missing new keys in a v0.1 INI default to True. `ReceiverIdentity` and `PeerCredential` are encrypted with Windows DPAPI for the current Windows account on the current PC. Copy the pristine ZIP to a new PC; a configured INI's credentials cannot be transferred to a different account/computer. To start fresh, exit Glide and replace the INI with the supplied template. **Manual setup → New code** on the receiver revokes prior access to that PC.

To upgrade in place, quit Glide, replace **Glide.exe**, and keep your own **Glide.ini**. Update the optional firewall helper as well. Existing pairings work; both PCs need the new version for discovery. The release ZIP always contains a blank settings template, never private credentials.

Windows manages DPAPI master keys and temporary TLS private-key material itself; these are OS facilities, not additional Glide settings. The TLS key is not installed into a certificate store and is released when the identity is disposed.

Nearby discovery sends only a sanitized PC name, public certificate fingerprint, role, and connection status. The IP comes from the received packet; discovery is not proof of identity. First pairing pins the advertised TLS certificate and compares a fresh session code on both screens before exchanging credentials. A committed client nonce, random server nonce, and certificate fingerprint bind the comparison code. Rejecting, canceling, or timing out either prompt aborts the exchange. Paired reconnects verify the saved certificate before sending the secret, including after an IP change.

Manual pairing codes remain private credentials: anyone with the code and network access can control a listening receiver. They persist until rotated and are not one-time invitations. Input never travels in cleartext. Glide does not log keystrokes, install a background service, or enable startup entries.

## Addressing transition stutter

- TLS authentication and desktop negotiation complete **before** edge crossing.
- `TCP_NODELAY`, small socket buffers, and a 400 ms heartbeat avoid reconnecting at the edge and expose real round-trip latency in the window.
- A dedicated message-loop thread handles Windows hooks. It queues input; it never waits for a network write.
- Adjacent queued absolute mouse positions collapse into the latest position. Key/button transitions remain ordered barriers, so a click cannot overtake its position.
- A bounded queue fails the session rather than dropping a key-up or button-up. A silent-peer watchdog disconnects after approximately 1.6–2 seconds and releases tracked remote input.
- The client reconnects in the background after ordinary connection failures. Pause and emergency stop disable reconnecting until you press Start again.

These changes target plausible causes of handoff lag. They cannot eliminate Wi-Fi contention, TCP retransmission delays, receiver scheduling stalls, or network-adapter power saving. The displayed RTT measures the encrypted connection; it is not end-to-end cursor/display latency.

## Current scope and limits

- One controlling PC and one receiver, arranged left or right. Multiple monitors are treated as each PC's bounding desktop. Complex layouts with gaps, unusual DPI arrangements, and cross-PC speed matching still need physical validation.
- Mouse movement, left/right/middle/X buttons, vertical/horizontal wheel, and scan-code keyboard forwarding. No clipboard sharing, file transfer, audio, or touch/pen forwarding in this version.
- Control originates from the designated controlling PC. This is not automatic bidirectional ownership switching between both physical keyboards.
- Handoff begins only when no key/button is held. Dragging windows/files across PCs is unsupported. The controlling PC's pointer is parked on its main display while remotely controlling the other PC.
- Windows secure desktops, UAC prompts, lock screens, Ctrl+Alt+Delete, and higher-privilege applications are not remotely controlled. If Windows blocks injection, Glide disconnects. Use the local keyboard/mouse for those screens.
- Use on trusted local networks. Discovery broadcasts every two seconds and removes absent peers after seven seconds; it is bounded to 32 peers. There is no WAN relay. Networking is IPv4. Complex multi-adapter, VPN, and guest-network discovery still needs physical validation.
- Unsigned development executable; there is no code-signing certificate or automatic updater.

## Build and verify

Build prerequisites: .NET 10 SDK, Visual Studio C++ Build Tools, and a Windows SDK. These are **only needed to build**, not to run the supplied native executable. There are no third-party application packages.

```powershell
.\build.ps1
```

This runs the protocol/security tests, publishes NativeAOT, runs the native smoke test, and creates `dist/Glide` plus a ZIP with fresh settings. It never packages a user's saved credentials. Debug symbols stay in `artifacts/native`.

```powershell
dotnet run --project tests/Glide.Tests -c Release
.\dist\Glide\Glide.exe --self-test
.\dist\Glide\Glide.exe --preview C:\temp\glide-preview.bmp
.\dist\Glide\Glide.exe --profile C:\temp\glide-idle.txt
```

Self-test writes results under `self-test` beside the executable and checks Win32 structures, INI/DPAPI round-tripping, NativeAOT TLS echo, discovery, mutual pairing, awaited role transitions, hook startup/emergency handling/teardown. It does not inject remote keystrokes. Preview renders native controls without networking or saving settings; `--preview-nearby` and `--preview-confirm` add example states. Profile measures six seconds of standby with networking disabled and exits. Neither mode is a two-PC benchmark.

### Two-PC acceptance pass

1. Open both apps, verify discovery and matching-code pairing, and reject a request once. Reopen both apps and verify automatic reconnect; pause and restart to verify it stays paused. Change a PC's IP and verify it reconnects with its saved identity. Reverse Control/Receive roles without copying credentials.
2. Make 50 crossings in both directions. Observe the first few movements after each crossing and the RTT readout, first on Ethernet and then on your usual network.
3. Type mixed case and shortcuts in a disposable text document; test click, double-click, scroll, and dragging within the receiving PC.
4. Hold a key on the remote PC, disconnect its network, and verify local control returns and the receiver releases held input. Test Ctrl+Alt+F12 on both PCs.
5. Test sleep/wake, pause/resume, display changes, differing display scales, and reconnect after closing/reopening the receiving app.

## Code map

- `src/Glide.Core`: pinned TLS pairing, fixed-size wire protocol, bounded/coalescing outbox, heartbeat and coordinate conversion.
- `src/Glide.App`: native Win32 UI, dedicated input thread, connection lifecycle, portable INI/DPAPI storage.
- `tests/Glide.Tests`: dependency-free protocol, queue, authentication, loopback latency, and recovery checks.

Reference behavior was checked against Microsoft's [Mouse Without Borders overview](https://learn.microsoft.com/en-us/windows/powertoys/mouse-without-borders), [low-level hook guidance](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc), and [SendInput restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
