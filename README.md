# Glide

One mouse and keyboard, two Windows PCs. Glide is a small, portable app that lets you move across a screen edge and keep working on the other computer.

**[Download v0.7.4 for Windows x64](https://github.com/mmatx64/glide/releases/download/v0.7.4/Glide-0.7.4-win-x64.zip)** · [Release notes](https://github.com/mmatx64/glide/releases/tag/v0.7.4)

## Features

- Mouse, keyboard, extra mouse buttons, and vertical/horizontal scrolling.
- Nearby-PC discovery and pairing with confirmation on just one PC.
- Encrypted direct connections, remembered pairing, and automatic reconnection.
- Left/right screen arrangement, tray controls, and an emergency stop shortcut.
- Click-to-update with verified downloads and preserved settings.
- Optional service mode for elevated apps, plus opt-in Windows sign-in/unlock control.

## Get started

1. Extract the ZIP on both PCs and run **Glide.exe**. Windows 10/11 x64; no .NET installation or administrator rights needed for portable use.
2. Put both PCs on the same private network and allow Glide through Windows Firewall.
3. On the PC with your mouse and keyboard, choose **This PC controls**, select the other PC, and click **Pair & connect**. Compare the codes on both screens; confirm **Codes match · Pair** on the initiating PC only.
4. Once **Connected**, move through the adjoining screen edge. Cross back to return. Use **Swap sides** if the arrangement is reversed.

Release held keys/buttons before crossing. **Ctrl + Alt + F12** stops sharing on either PC. **Pause sharing** stays paused until you resume. Closing the window hides Glide to the tray; click the icon to reopen, or right-click for controls and **Quit**.

## Updates and settings

Choose **Check for updates** on each PC. Updates preserve your pairing and `Glide.ini`; installed service copies are updated too. The updater uses public stable releases and may request administrator approval.

Portable settings live beside `Glide.exe`. For a manual update, quit Glide and replace the executable, keeping your existing INI. Pairing credentials are protected for that Windows account and PC, so use the blank package INI when setting up a different computer.

## Optional service mode

Service mode starts Glide elevated at sign-in so it can control administrator apps on the normal desktop. Quit portable Glide, then run this from the extracted package as the Windows administrator account that will use Glide:

```powershell
.\Install-Service.ps1
```

Open **Glide (service)** from the Start menu. To keep an existing portable pairing, install with `-SettingsPath 'C:\path\to\Glide.ini'`. Service settings live in `%ProgramData%\Glide\Service\Glide.ini`. For a new service pairing, initiate from the service PC; if both PCs use service mode, pair in portable mode first and import each PC's own INI.

To enable sign-in/unlock input, first pair the service receiver and start sharing, then run `.\Install-Service.ps1 -EnableLoginControl` from the package folder. This is opt-in; Windows still checks your PIN/password, and Glide does not store it. Try remote **Win+L** unlock before relying on reboot/sign-out control. Use `-DisableLoginControl` to turn it off, or `-Action Uninstall` to remove the service.

## Gotchas

- Turn off Mouse Without Borders or similar input-sharing apps while using Glide.
- No clipboard, file transfer, audio sharing, or dragging between PCs. One designated PC controls the other; you can reverse roles in the app.
- Guest Wi-Fi, VPNs, or different subnets may prevent discovery. Try **Manual setup**. The included `Allow-PrivateNetwork.ps1` can add private-LAN firewall rules; ports are TCP 24819/24821 and UDP 24820.
- The executable is unsigned, so Windows security tools may warn or block it.
- Portable input cannot control elevated apps without elevation. Service mode supports one enrolled administrator account on the physical console; RDP, UAC secure prompts, Ctrl+Alt+Delete, and pre-Windows/BitLocker prompts require local input.
- Fast scrolling can still lag, and idle screensaver wake/sign-in behavior needs checking on your PCs. Keep local input available. Known issues are tracked in [ISSUES.md](https://github.com/mmatx64/glide/blob/main/ISSUES.md).
- Pair only computers you trust. An unpaired portable receiver accepts initial pairing while its window is open and sharing is not paused; always compare the codes. Manual pairing codes are reusable credentials—keep them private.

## Build

Requires the .NET 10 SDK, Visual Studio C++ Build Tools, and a Windows SDK. Run `.\build.ps1`; it builds a self-contained native executable, runs native self-tests, and creates the release ZIP in `dist/`. Use `-SkipLocalCopy` to leave your existing local copy alone. The optional local `tests/Glide.Tests` folder is not tracked in Git.

## License

Copyright © 2026 Glide contributors. Licensed under **GNU GPL version 3 or any later version** (`GPL-3.0-or-later`): you can use, study, modify, and share Glide, including commercially, under the GPL's terms. Distributed modified versions must preserve those freedoms and provide corresponding source. Glide comes without warranty. See the full [LICENSE](https://github.com/mmatx64/glide/blob/main/LICENSE).
