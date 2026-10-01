# Glide

A small, portable Windows mouse and keyboard bridge for **two PCs**. Native dark interface, tray operation, encrypted direct connections, and a warm connection before you cross a screen edge. Version 0.1 is an initial working build, not yet validated on two physical PCs.

**[Download Glide for Windows x64](https://github.com/mmatx64/glide/releases/download/v0.1.0/Glide-0.1.0-win-x64.zip)** · [Release notes](https://github.com/mmatx64/glide/releases/tag/v0.1.0)

Download the portable ZIP from the release, extract it on both PCs, and follow the steps below. The automatically generated “Source code” downloads are for building the app yourself.

## Run it

1. Extract the ZIP into a writable folder on each PC. Run **Glide.exe**. Windows 10/11 x64; no installer, .NET runtime, administrator rights, cloud account, or service required for ordinary use.
2. On the **second PC**, select **This PC receives**, then **Start listening**. Allow private-network access if Windows asks. Note its IPv4 address and click **Copy code**.
3. On the PC with your physical mouse and keyboard, select **This PC controls**. Enter the receiver's address and paste its pairing code. Use **Swap sides** to match your desk. Click **Start sharing**.
4. Wait for **Connected**, then move through the adjoining outer screen edge. Move back across the same boundary to return. Release held keys/buttons before leaving the controlling PC.
5. **Ctrl + Alt + F12** on either PC stops sharing and restores local control. **Pause sharing** also stops it. Start again to resume. Closing the window hides it to the tray; double-click or right-click its tray icon to reopen. **Quit** exits.

Turn off Mouse Without Borders while trying Glide so both apps do not intercept the same input. Start with an ordinary application on the receiver. Connections are never started automatically.

### Firewall

The receiver listens on **TCP 24819**. The app does not change firewall rules. If Windows does not offer a prompt, run the included `Allow-PrivateNetwork.ps1` in an Administrator PowerShell window on the receiving PC. The script limits access to this executable, private networks, and the local subnet. Review the script before running it. No router port forwarding is needed.

The helper's remove command is:

```powershell
Remove-NetFirewallRule -Name Glide-Portable-Private-24819
```

## Portable settings

All app settings, receiver identity, and remembered pairing credentials are in **Glide.ini beside Glide.exe**. Nothing is saved to an application settings folder or the registry. Changes are written atomically through a temporary sibling file. The app reports an error if its folder is read-only.

`Role`, `PeerAddress`, and `RemoteSide` are human-readable. `ReceiverIdentity` and `PeerCredential` are encrypted with Windows DPAPI for the current Windows account on the current PC. Copy the pristine ZIP to a new PC; a configured INI's credentials cannot be transferred to a different account/computer. To start fresh, exit Glide and replace the INI with the supplied template. **New code** on the receiver revokes the previous pairing.

Windows manages DPAPI master keys and temporary TLS private-key material itself; these are OS facilities, not additional Glide settings. The TLS key is not installed into a certificate store and is released when the identity is disposed.

Keep pairing codes private: anyone with the code and network access can control a listening receiver. Codes persist until rotated; they are not one-time invitations. The sender pins the receiver certificate before transmitting the 256-bit pairing secret over TLS. Input never travels in cleartext. Glide does not log keystrokes, install a background service, or enable startup entries.

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
- Use on trusted local networks. There is no discovery service or WAN relay. The listener binds IPv4; use the receiver's IPv4 address or a hostname that resolves to it.
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

Self-test writes results under `self-test` beside the executable, checks Win32 structures, DPAPI INI round-tripping, NativeAOT TLS echo, hook startup, emergency handling, and hook teardown. It does not inject remote keystrokes. Preview renders the actual native controls without starting sharing or saving settings. Profile measures six seconds of standby CPU/memory and exits. Neither mode is a two-PC benchmark.

### Two-PC acceptance pass

1. Verify pairing, and verify an incorrect code is refused.
2. Make 50 crossings in both directions. Observe the first few movements after each crossing and the RTT readout, first on Ethernet and then on your usual network.
3. Type mixed case and shortcuts in a disposable text document; test click, double-click, scroll, and dragging within the receiving PC.
4. Hold a key on the remote PC, disconnect its network, and verify local control returns and the receiver releases held input. Test Ctrl+Alt+F12 on both PCs.
5. Test sleep/wake, pause/resume, display changes, differing display scales, and reconnect after closing/reopening the receiving app.

## Code map

- `src/Glide.Core`: pinned TLS pairing, fixed-size wire protocol, bounded/coalescing outbox, heartbeat and coordinate conversion.
- `src/Glide.App`: native Win32 UI, dedicated input thread, connection lifecycle, portable INI/DPAPI storage.
- `tests/Glide.Tests`: dependency-free protocol, queue, authentication, loopback latency, and recovery checks.

Reference behavior was checked against Microsoft's [Mouse Without Borders overview](https://learn.microsoft.com/en-us/windows/powertoys/mouse-without-borders), [low-level hook guidance](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc), and [SendInput restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
