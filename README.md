# Glide

A small, portable Windows mouse and keyboard bridge for **two PCs**. Native dark interface, automatic nearby-PC discovery, pairing from one PC without copying IPs or secrets, and encrypted direct connections. Version **0.6.0** adds user-initiated GitHub updates with verified downloads, administrator app/service replacement, settings preservation, and rollback. The optional service starts Glide elevated at sign-in for credential dialogs and administrator applications on the normal desktop. This is preview software; service mode still needs two-PC testing.

**[Download Glide v0.6.0 for Windows x64](https://github.com/mmatx64/glide/releases/download/v0.6.0/Glide-0.6.0-win-x64.zip)** · [Release notes](https://github.com/mmatx64/glide/releases/tag/v0.6.0)

Download the portable ZIP from the release, extract it on both PCs, and follow the steps below. The automatically generated “Source code” downloads are for building the app yourself.

The ZIP supports ordinary portable use and the optional service installation below.

## Run it

1. Extract the ZIP into a writable folder on each PC. Run **Glide.exe**. Windows 10/11 x64; no installer, .NET runtime, administrator rights, cloud account, or service required for ordinary use.
2. Open Glide on **both PCs** and allow access on private networks if Windows asks. The other PC should appear under **Nearby PCs** within a few seconds. If more than one appears, use **Next PC**.
3. On the PC with your physical mouse and keyboard, select **This PC controls**, select the nearby laptop, and click **Pair & connect**. Compare the short code on both screens and click **Codes match · Pair** **only on this initiating PC**. Never approve different codes. Nothing needs to be clicked on the other PC: it displays the code, becomes the receiver, and connects automatically. Either PC can initiate as the controller.
4. Wait for **Connected**, then move through the adjoining outer screen edge. Move back across the same boundary to return. Release held keys/buttons before leaving the controlling PC.
5. Use **Swap sides** to place the other PC left or right (pause first if already sharing). Glide remembers the pairing, role, and layout, then reconnects when both apps are opened again. A paired PC's changed IP is detected without replacing its trusted identity.
6. **Ctrl + Alt + F12** on either PC stops sharing and restores local control. **Pause sharing** also stops it. The pause persists across launches. Start again or explicitly choose a role to resume. Closing the window hides it to the tray; double-click or right-click its tray icon to reopen. **Quit** exits.

Turn off Mouse Without Borders while trying Glide so both apps do not intercept the same input. Start with an ordinary application on the receiver. To reverse roles after pairing, select **This PC receives** on the old controller and **This PC controls** on the other PC. Pairing exchanges credentials both ways, so no new code is needed. Portable mode does not add Windows startup entries; automatic reconnection happens when you open it.

## Click-to-update

Click **Check for updates** at the bottom of the window. Glide checks the latest public, stable Windows x64 release in `mmatx64/glide`; it does not poll in the background or require a GitHub account. If a newer version exists, Glide shows the affected paths and asks whether to install. Approve the administrator prompt as the Windows account using Glide; service mode already runs elevated and needs no second prompt.

The separate updater downloads into an administrator-protected staging folder, verifies GitHub's published SHA-256 digest and the expected ZIP contents, then asks Glide to release input and exit. It updates the portable folder you launched from (including `C:\Programs\Glide`) and any installed service copy in `%ProgramFiles%\Glide`. The desktop app and helper service currently share `Glide.exe`, so both are replaced together. Service mode retains its existing protected path; no installation is relocated.

Each PC's `Glide.ini`, pairing, enrolled service account, and Pause state are retained. A running service is stopped and restarted; a stopped service stays stopped. The service UI reopens when its service was running; otherwise the portable app reopens through the desktop shell. Installation/startup failures trigger file rollback and restoration of the previous service state. If recovery itself fails, the error dialog identifies the problem and remaining `.bak` files are retained beside the installation.

The updater only handles public releases outside GitHub's prerelease channel, with a matching `Glide-VERSION-win-x64.zip` asset and SHA-256 digest. Private releases and prereleases are unsupported. v0.6.0 uses the regular release channel so GitHub's latest-release endpoint can find it; this does not remove the preview limitations below. Install v0.6.0 manually once to gain the updater, then use the button for later releases. Update each PC separately.

## Optional service mode

Service mode automatically starts Glide elevated at sign-in, so normal-desktop Windows credential dialogs and administrator applications can receive shared input without relaunching Glide as administrator. Install it on the receiving PC, or on both PCs if you reverse roles. It supports **one enrolled Windows administrator account and the active physical console session** per PC. It does not control lock screens, UAC secure desktops, Ctrl+Alt+Delete, other users' sessions, or RDP sessions.

1. Quit the portable Glide instance. Extract the v0.6.0 package into a writable folder.
2. From PowerShell in that folder, run `.\Install-Service.ps1`. Approve the one-time Windows administrator prompt **as the same Windows account that will use Glide**. To retain a portable pairing, instead run `.\Install-Service.ps1 -SettingsPath 'C:\path\to\existing\Glide.ini'`. A first installation otherwise imports the blank INI beside the installer. Updates preserve the installed settings.
3. Open **Glide (service)** from the Start menu, or use its tray icon. No administrator prompt is needed for normal reopening. The title says **SERVICE**. A paired app starts hidden at sign-in; a fresh installation opens for setup. For a fresh pairing, initiate **Pair & connect from the service PC** and confirm the code there, then reverse roles if needed. An unpaired service session does not accept unattended incoming first-pair requests. If installing service mode on both unpaired PCs, pair them in portable mode first, then import each PC's own INI during installation.

**Pause sharing** and the emergency shortcut stay paused across sign-ins. **Quit** keeps the desktop app closed until the shortcut is used, the service is restarted, or you sign in again. Closing the window just hides it. Stop the service to stop its elevated child process; a child that cannot exit is terminated after a five-second grace period.

Installation creates the automatic **GlideSessionService** under LocalSystem, a protected `%ProgramFiles%\Glide\Glide.exe`, a Start menu shortcut, and private-LAN firewall rules restricted to that executable and the local subnet. Use `-SkipFirewall` if those rules are managed separately. The service launches only that fixed executable using the enrolled user's elevated token; **the desktop app runs as the user, not SYSTEM**. The service has no network listener, credential store, or arbitrary-command interface. The paired PC can control administrator applications in the elevated user session, so only enroll a PC you trust with that access.

Service-mode settings are in **`%ProgramData%\Glide\Service\Glide.ini`**, protected for administrators/SYSTEM and still encrypted with that user's DPAPI. `service.log` in the same folder records bounded lifecycle/error messages, never input contents or credentials. Portable INI files are not modified. Use Glide's UI for normal settings changes; direct edits to service settings need elevation.

For an update, extract the new ZIP elsewhere and rerun its installer as the enrolled account; it stops the old service copy, replaces the protected executable, keeps the INI, and restarts. Run `.\Install-Service.ps1 -Action Status` to inspect installation, or `.\Install-Service.ps1 -Action Uninstall` to remove the service, shortcut, and its firewall rules. Uninstall retains installed files and settings for recovery. To return to portable mode, use the original portable folder, or copy the service INI back under the same account using an administrator file operation.

### If the other PC does not appear

Both PCs need version 0.3 for discovery and one-sided pairing. Check that they are on the same private LAN and not isolated by guest Wi-Fi. UDP broadcasts do not generally cross subnets or VPNs. **Manual setup** retains the original IP/code setup for those cases and older peers. The firewall helper below handles discovery and pairing on both PCs.

### Smart App Control

This preview executable is **unsigned**. Windows Smart App Control can block an unknown unsigned app; changing the filename, icon, or ZIP does not solve that. A release signed with a certificate from a trusted code-signing provider (or Microsoft Artifact Signing) is needed to address signing-based blocks. There is no signing account/certificate configured for this project yet, and this release does not claim to fix the warning. Smart App Control has no per-app allow exception. See [Microsoft's signing guidance](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control) and [Smart App Control FAQ](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions).

### Firewall

Glide uses **TCP 24819** for input, **UDP 24820** for nearby discovery, and **TCP 24821** for confirmed pairing. The app does not change firewall rules. If Windows does not offer a prompt, run the included `Allow-PrivateNetwork.ps1` in an Administrator PowerShell window on **both PCs**. The script limits access to this executable, private networks, and the local subnet. Review it before running it. No router port forwarding is needed. Rerun the updated helper if you previously allowed only the v0.1 input port.

The helper's remove command is:

```powershell
Get-NetFirewallRule -Name 'Glide-Portable-*' | Remove-NetFirewallRule
```

## Portable settings

In portable mode, all app settings, receiver identity, and remembered pairing credentials are in **Glide.ini beside Glide.exe**. Nothing is saved to an application settings folder or the registry. Changes are written atomically through a temporary sibling file. The app reports an error if its folder is read-only. Installed service mode uses the protected location above.

`Role`, `PeerName`, `PeerAddress`, and `RemoteSide` are human-readable. `AutoConnect=True` remembers automatic reconnection; Pause and emergency stop set it to False. `DiscoveryEnabled=False` disables discovery and easy pairing after restarting, while manual setup remains available. Missing new keys in a v0.1 INI default to True. `ReceiverIdentity` and `PeerCredential` are encrypted with Windows DPAPI for the current Windows account on the current PC. Copy the pristine ZIP to a new PC; a configured INI's credentials cannot be transferred to a different account/computer. To start fresh, exit Glide and replace the INI with the supplied template. **Manual setup → New code** on the receiver revokes prior access to that PC.

To upgrade in place, quit Glide, replace **Glide.exe**, and keep your own **Glide.ini**. Update the optional firewall helper as well. Existing pairings work; both PCs need the new version for discovery. The release ZIP always contains a blank settings template, never private credentials.

Windows manages DPAPI master keys and temporary TLS private-key material itself; these are OS facilities, not additional Glide settings. The TLS key is not installed into a certificate store and is released when the identity is disposed.

Nearby discovery sends only a sanitized PC name, public certificate fingerprint, role, and connection status. The IP comes from the received packet; discovery is not proof of identity. First pairing pins the advertised TLS certificate and displays a fresh session code on both screens before exchanging credentials. A committed client nonce, random server nonce, and certificate fingerprint bind the comparison code. Only the initiator confirms; rejection or timeout aborts the exchange. Both endpoints prove possession of their TLS private keys, and the exchanged identity must match that proof. Paired reconnects verify the saved certificate before sending the secret, including after an IP change.

An unpaired portable PC accepts incoming first pairing automatically while its Glide window is open and sharing is not paused. Use this convenience on a trusted LAN: another local user can initiate first pairing without a receiving-side approval. Once paired, incoming easy-pairing requests must prove the remembered PC's identity; an unrelated PC cannot replace it by copying its name or discovery announcement. Pause/emergency stop blocks incoming easy pairing too. A remembered PC can reconnect while the window is hidden. Reset the saved pairing before switching to a different workstation.

Manual pairing codes remain private credentials: anyone with the code and network access can control a listening receiver. They persist until rotated and are not one-time invitations. Input never travels in cleartext. Glide does not log keystrokes. Service installation and startup are opt-in through the installer.

## Addressing transition stutter

- TLS authentication and desktop negotiation complete **before** edge crossing.
- `TCP_NODELAY`, small socket buffers, and a 400 ms heartbeat avoid reconnecting at the edge and expose real round-trip latency in the window.
- A dedicated message-loop thread handles Windows hooks. It queues input; it never waits for a network write.
- UI polling repaints only when displayed state changes. Buffered window painting and unchanged-control checks avoid the previous half-second refresh flicker.
- Adjacent queued absolute mouse positions collapse into the latest position. Key/button transitions remain ordered barriers, so a click cannot overtake its position.
- A preallocated queue sends up to 32 already-queued events in one TLS write. There is no batching timer or wait to fill a batch. This reduces allocation and encryption/write overhead during bursts; TLS authentication, encryption, event order, and the 256-event queue limit remain in place.
- A bounded queue fails the session rather than dropping a key-up or button-up. A silent-peer watchdog disconnects after approximately 1.6–2 seconds and releases tracked remote input.
- The client reconnects in the background after ordinary connection failures. Pause and emergency stop disable reconnecting until you press Start again.

These changes target plausible causes of handoff lag. They cannot eliminate Wi-Fi contention, TCP retransmission delays, receiver scheduling stalls, or network-adapter power saving. The displayed RTT measures the encrypted connection; it is not end-to-end cursor/display latency.

## Current scope and limits

- One controlling PC and one receiver, arranged left or right. Multiple monitors are treated as each PC's bounding desktop. Complex layouts with gaps, unusual DPI arrangements, and cross-PC speed matching still need physical validation.
- Mouse movement, left/right/middle/X buttons, vertical/horizontal wheel, and scan-code keyboard forwarding. No clipboard sharing, file transfer, audio, or touch/pen forwarding in this version.
- Control originates from the designated controlling PC. This is not automatic bidirectional ownership switching between both physical keyboards.
- Handoff begins only when no key/button is held. Dragging windows/files across PCs is unsupported. The controlling PC's pointer is parked on its main display while remotely controlling the other PC.
- Portable mode cannot control higher-privilege applications unless launched elevated. Service mode supports those windows on the normal desktop. Windows secure desktops, UAC elevation prompts, lock screens, and Ctrl+Alt+Delete still require local control. If Windows blocks injection, Glide disconnects.
- Use on trusted local networks. Discovery broadcasts every two seconds and removes absent peers after seven seconds; it is bounded to 32 peers. There is no WAN relay. Networking is IPv4. Complex multi-adapter, VPN, and guest-network discovery still needs physical validation.
- Unsigned development executable; there is no code-signing certificate. Updates in v0.6.0 are explicitly initiated by the user.
- Fast/accelerated remote wheel scrolling has a reported delay; slow scrolling appears normal. Tracked as [BUG-001](https://github.com/mmatx64/glide/blob/main/ISSUES.md); buffering is not yet a confirmed cause.

## Build and verify

Version **0.4.0** preserves the controlling keyboard's interpretation of keypad digits/decimal and navigation even when the receiver has a different Num Lock state. Num Lock presses also update the controlling keyboard's state and light during remote control. Update both executables for the complete fix. Cursor parking behavior is unchanged.

Build prerequisites: .NET 10 SDK, Visual Studio C++ Build Tools, and a Windows SDK. These are **only needed to build**, not to run the supplied native executable. There are no third-party application packages.

```powershell
.\build.ps1
```

This runs the protocol/security tests, publishes NativeAOT, runs the native smoke test, and creates `dist/Glide` plus a ZIP with fresh settings. It never packages a user's saved credentials. Debug symbols stay in `artifacts/native`.

The build also runs offline service-cleanup tests with mocked Windows service commands. They check that failed validation restores normal service configuration without touching the installed service. Core and native checks exercise input overflow, cancellation, and receiver shutdown without injecting remote input.

Use `.\build.ps1 -SkipLocalCopy` to build, test, and package a release while `dist/Glide/Glide.exe` is running; the running app and its settings are left in place.

For real SCM/session validation, run `tests\Glide.Tests\Validate-Service.ps1` in an Administrator PowerShell window after building. This installs/stops/restarts the service and launches a diagnostic child with no network or input injection. It checks the user token, session, wakeup, graceful shutdown, and restart, then restores normal configuration but leaves the service stopped. `-EnableAfterTest` starts normal sharing afterward; `-SettingsPath` selects an existing INI for first installation only. This test changes the local service installation and should be run when sharing can be interrupted.

```powershell
dotnet run --project tests/Glide.Tests -c Release
dotnet run --project tests/Glide.Tests -c Release -- --benchmark
.\dist\Glide\Glide.exe --self-test
.\dist\Glide\Glide.exe --preview C:\temp\glide-preview.bmp
.\dist\Glide\Glide.exe --profile C:\temp\glide-idle.txt
```

Self-test writes results under `self-test` beside the executable and checks Win32 structures, Unicode titles/text, INI/DPAPI round-tripping, NativeAOT TLS echo, discovery, one-sided verified pairing, awaited role transitions, hook startup/emergency handling/teardown. It does not inject remote keystrokes. Preview uses sample data without loading saved credentials, networking, or saving settings. Add `--preview-connected`, `--preview-nearby`, `--preview-manual`, `--preview-paused`, or `--preview-reconnecting` for those states. `--receiver-preview` selects the receiving role; `--preview-confirm` shows incoming pairing, with `--preview-outgoing` for the initiating screen. Use `--preview-left`, `--preview-long-name`, `--preview-error`, or `--preview-scale 1.5` for layout checks. `--preview-interactive` keeps the sample window open until Quit; Start does not connect. Profile measures six seconds of standby with networking disabled, reports repaint/refresh counts, and exits. Neither mode is a two-PC benchmark.

The optional `--benchmark` runs synthetic encrypted loopback traffic without input injection or saved credentials. It reports sequential and 64-event burst RTT distributions, TLS write counts, process CPU, and allocations. These are same-PC transport measurements, not Wi-Fi or end-to-end cursor latency. Native self-test also checks keypad INPUT construction and Windows character translation with synthetic Num Lock states; actual remote typing still requires the pass below.

### Two-PC acceptance pass

1. Open both apps, verify discovery, initiate pairing on one PC and verify the code there only. Leave the receiving PC untouched and check that both connect. Reject a request once on the initiator. Reopen both apps and verify automatic reconnect; pause and restart to verify it stays paused. Change a PC's IP and verify it reconnects with its saved identity. Reverse Control/Receive roles without copying credentials.
2. Make 50 crossings in both directions. Observe the first few movements after each crossing and the RTT readout, first on Ethernet and then on your usual network.
3. Type mixed case and shortcuts in a disposable text document; test click, double-click, scroll, and dragging within the receiving PC. Start with opposite Num Lock states on the PCs: check keypad 0–9 and decimal with the controller's Num Lock on, then keypad navigation with it off. Toggle Num Lock while remote and check the controlling keyboard's light, Shift+keypad, dedicated arrows/Home/End/Delete, and keypad Enter/operators. Hold a keypad key across a Num Lock/Shift change, then release it and return locally; check that no key stays held or gets swallowed.
4. Hold a key on the remote PC, disconnect its network, and verify local control returns and the receiver releases held input. Test Ctrl+Alt+F12 on both PCs.
5. Test sleep/wake, pause/resume, display changes, differing display scales, and reconnect after closing/reopening the receiving app.

## Code map

- `src/Glide.Core`: pinned TLS pairing, fixed-size wire protocol, bounded/coalescing outbox, heartbeat and coordinate conversion.
- `src/Glide.App`: native Win32 UI (`Interface.cs` draws the desk/session panels), dedicated input thread, connection lifecycle, portable INI/DPAPI storage.
- `tests/Glide.Tests`: dependency-free protocol, queue, authentication, loopback latency, and recovery checks.

Reference behavior was checked against Microsoft's [Mouse Without Borders overview](https://learn.microsoft.com/en-us/windows/powertoys/mouse-without-borders), [low-level hook guidance](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc), and [SendInput restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
