# Glide validation — October 1, 2026

## v0.6.0: user-initiated updates and wheel-lag tracking

- Added **Check for updates** using the latest public stable GitHub release, with a user-confirmed administrator handoff. The helper independently fetches release metadata, enforces the repository/version/asset naming, bounds download size, checks GitHub's SHA-256 digest, and rejects unexpected ZIP contents before requesting shutdown. No credentials or GitHub token are stored.
- The helper updates the launching portable folder (including `C:\Programs\Glide`) and the fixed protected Program Files service copy when installed. Both use the same executable. Settings templates are excluded from replacement; service enrollment, configuration, and user settings are retained. File replacement is reversible until the replacement service starts. A stopped service remains stopped. Protected staging and directory handles reject reparse paths and prevent folder renames during installation.
- Service jobs now permit **explicit** breakaway for the elevated updater; normal descendants still die when the session job closes. Service-mode updates retain the enrolled user's elevated token and do not launch a SYSTEM desktop updater. Portable mode uses UAC and reopens through the desktop shell after replacement.
- **80 core checks passed**, including release parsing/version comparison, unavailable-public-release messaging, verified fixture downloads, corrupt/oversized download rejection, ZIP traversal rejection, both-copy replacement, preserved INIs, successful rollback, locked-file partial failure, service-start failure/recovery, and running/stopped service-state preservation. Service orchestration tests use a fake service; they do not claim live SCM update coverage. The three existing offline service-cleanup scenarios also passed.
- Release **NativeAOT publish and all native smoke checks passed**. A real Windows process-job test launched an attached diagnostic parent and a detached updater, confirmed the updater was outside that specific job, closed the job, observed parent termination, and verified updater completion. Native path tests confirmed directory renames are blocked while atomic replacement and INI preservation still work. These checks do not stop the installed production service or inject remote input.
- Inspected the connected UI capture at `artifacts/update-ui.bmp`: the new footer button fits alongside Hide/Quit without obscuring the emergency shortcut. Existing preview/profile modes cannot check or install updates.
- **Live UAC/SCM acceptance is outstanding:** test real download/UAC/install/restart end to end, UAC cancellation, the enrolled-account check, portable installation in `C:\Programs\Glide`, settings/ACL preservation in service mode, an unavailable service executable, and rollback after a real service-start failure. No existing running app was replaced in this pass. The initial public latest-release 404 was traced to all existing releases being prereleases; v0.6.0 uses the regular release channel for updater discovery.
- Packaged v0.6.0 at `dist/Glide-0.6.0-win-x64.zip`; checksums are in `dist/SHA256SUMS-0.6.0.txt`. The working `dist/Glide` copy was left intact using `-SkipLocalCopy`. `dotnet run --project tests/Glide.Tests -c Release -- --verify-release v0.6.0 artifacts/release-verification` checks the live public metadata, verified download, package contents, and pristine settings after publication without installing it.
- **Post-publication verification passed:** the unauthenticated updater client discovered v0.6.0 as GitHub's latest regular release, downloaded and verified the published ZIP, extracted the exact expected files, and confirmed the INI has no configured peer/identity credentials. Downloaded ZIP SHA-256 `cad050712026e31bc2c719967bf686b7190371650e5f5f61fa5b9397a6d30fb3`; executable SHA-256 `f60a1f819b1448507c05f150ba3b560feac108d4fabb38b69c1b9733dacce592`. Both match the local build and published checksums. Tag v0.6.0 identifies commit `93dc72d`.
- Recorded fast/accelerated remote-wheel delay as [BUG-001](ISSUES.md). Slow scrolling is reported normal; buffering remains an unconfirmed hypothesis. No wheel-forwarding behavior changed.

## v0.5.1: sharing lifecycle and listener fixes

- Emergency stop cancels pending setup work and rejects stale completion, including pairing results already queued for the UI. The shortcut also works during first pairing, before an input worker exists. Pause and Quit stop sharing before settings writes; failed pause persistence is reported. Pairing remains reserved until the UI applies the result.
- Inbound overflow closes the sending connection even before input attachment. Input and pairing listeners allow four bounded workers, so a stalled handshake does not serialize all clients. One active input session and one pairing confirmation remain enforced. Completed connection disposal awaits its I/O tasks; discovery and input teardown drain before disposing their state. No wire-format change.
- Removed the unused Win32 capture declaration and test-only single-packet dequeue API, consolidated settings saves, corrected Win32 message-loop error handling, and tightened service event-name validation. Preview/profile interaction stays isolated from networking and saved settings.
- **60 core checks, three offline service-cleanup scenarios, Release NativeAOT publish, and native smoke checks passed.** Coverage includes input overflow, concurrent send/disposal, bounded listeners, stalled-client coexistence, competing pairing, cancelled queued results, settings failure, and 250 concurrent setup-completion/emergency-stop races. The native receiver also rejects a second active session and drains shutdown. Service validation now restores production configuration in `finally`; offline checks exercise installer failure, service-start failure, and success with mocked SCM calls under PowerShell 7 and Windows PowerShell 5.1. The installed service was not interrupted by this pass.
- A single current loopback benchmark measured sequential median/p99 **0.044/0.099 ms** and 64-event burst median/p99 **0.078/0.176 ms**, with 6,254 TLS writes for 64,000 echoed events. These are synthetic transport measurements, not two-PC latency guarantees. Physical handoff and service acceptance still require the README's two-PC pass.
- Release package: `dist/Glide-0.5.1-win-x64.zip`, with executable and ZIP checksums in `dist/SHA256SUMS-0.5.1.txt`. It includes a pristine settings template and the optional service installer. Update both PCs and retain each PC's own settings; service updates use the installer as the enrolled account.

## v0.5.0: opt-in LocalSystem session service

- Added an automatic LocalSystem launcher for one enrolled administrator account on the active console session. It obtains that same user's elevated token and launches only the protected installed Glide executable on `winsta0\default`. No SYSTEM desktop UI, network listener, client-supplied executable/arguments, or service-owned pairing credentials. Other users and sessions are rejected. The service UI uses the existing network/pairing/input implementation; first incoming pairing without a remembered peer is disabled in service mode.
- Installer/update/uninstall script handles fixed protected Program Files and ProgramData paths, rejects reparse points/untrusted directory owners, resets file permissions, retains settings, quotes the SCM image path, and creates a shortcut plus executable-specific private-subnet firewall rules. Service data remains DPAPI-protected for the enrolled user. The local request event permits only the enrolled user, administrators, and SYSTEM; it accepts only a fixed wake/show request. Stop/show events have random names; pre-existing objects are rejected.
- **47 core tests and native smoke checks passed.** New native checks cover Win32 service/process/job ABI, token/logon metadata, event naming, wake/reset/stop lifecycle, object collision rejection, and rejection of an unprotected executable path.
- **Real SCM/session validation passed on this PC:** install, LocalSystem identity, correctly quoted executable path, launch into the interactive session as the enrolled user at elevated integrity (not SYSTEM), wake/show delivery, graceful child stop, no orphan, and relaunch after service restart. Normal service mode then started successfully with preserved pairing settings. The desktop app was independently observed at integrity **12288**, versus the credential dialog's previously observed **8202** and portable Glide's **8192**.
- A normal, non-elevated launcher successfully opened the service UI. The non-elevated process was denied read access to the protected service INI; Program Files permissions allow ordinary users read/execute only. Raw local results are in `artifacts/service-validation.txt`; the durable rerun script is `tests/Glide.Tests/Validate-Service.ps1`.
- Still needs two-PC service-mode acceptance: actual credential-prompt input, reboot/sign-out startup, fast user switching, Pause/Quit persistence across sign-in, standard-user rejection, crash cleanup, and uninstall/reinstall. Diagnostic session tests did not inject input or make network connections. The v0.5.0 preview package is `dist/Glide-0.5.0-win-x64.zip`, with checksums in `dist/SHA256SUMS-0.5.0.txt`. It includes the service installer and a pristine INI template.

## v0.4.0: Num Lock and transport fixes

- Keypad digits/decimal and keypad navigation now use the controller's resolved virtual key; other keys retain scan-code forwarding. Num Lock also reaches the controlling PC while remote, keeping its keyboard state/light current. Physical scan/extended identity tracks suppression and held-key releases across Num Lock/Shift changes. Cursor parking is unchanged, as requested.
- Replaced per-event linked-list allocations with a bounded preallocated ring. Each TLS write drains at most 32 already-queued packets, with no batching timer. Ordering, adjacent-motion coalescing, queue overflow failure, TLS authentication/encryption, wire format, and heartbeat timing/RTT calculation are unchanged.
- **47 core checks passed**, including ring wrap, partial drains, cancellation/wakeup, 10,000 concurrent ordered events, 480 mixed events echoed through batched TLS, existing security checks, and silent-peer recovery. Release NativeAOT publish and all native smoke checks passed. Keypad tests cover digits/decimal/navigation, extended/nonextended distinctions, key-up, physical identity, and Windows translation using synthetic Num Lock states. They do not inject input or prove two-PC typing behavior.
- Three baseline and three optimized Windows/.NET 10 loopback benchmark runs, each with warmup, 2,000 sequential echoes, and 1,000 bursts of 64 reliable synthetic events. Baseline burst median RTT **0.381–0.388 ms**, p99 **0.715–0.870 ms**; optimized **0.073–0.075 ms**, p99 **0.132–0.147 ms**. TLS writes for 64,000 echoed events fell from about **128,000 to 6,300** (about 95% fewer); process allocations fell from **32.8–33.2 MB to 12.9–13.4 MB**. Sequential medians stayed comparable: **0.042–0.044 ms before**, **0.042–0.045 ms after**. Raw output: `artifacts/transport-before*.txt` and `artifacts/transport-after*.txt`; rerun with the README benchmark command. Optimized repeat measurements were taken with the compiler idle.
- These measurements exclude input injection, display latency, and the physical LAN. The reported **56 ms spikes on an Ethernet/Wi-Fi link have not been reproduced or proven fixed**. Do not treat lower loopback overhead as a Wi-Fi latency guarantee. The displayed RTT remains the raw application heartbeat round trip, including queueing and scheduling; spikes are not smoothed away.
- Release package: `dist/Glide-0.4.0-win-x64.zip`, with checksums in `dist/SHA256SUMS-0.4.0.txt`. It contains the rebuilt executable and pristine settings template, not saved credentials. Quit Glide on each PC before upgrading. Keep each PC's existing INI to retain pairing, or extract to a fresh folder and use the supplied INI to pair again. `build.ps1 -SkipLocalCopy` packages the release without replacing the running local app.

## v0.3.1: compact desk and session layout

- Implemented the approved two-column layout with the existing navy/mint palette, version in the native title bar, and centered handoff instructions and Swap sides group. Connection controls and latency live in the Session panel. Manual setup and both pairing directions fit the smaller discovery panel; connected sessions no longer show the misleading selected-peer busy warning.
- Before the version bump, the Release NativeAOT build, **all 38 core checks, and all native smoke checks passed**. That layout candidate was **4,500,992 bytes**, SHA-256 `8746AE9C904A8E3A8A5C0A20413814F91EEE8DE46F1130C3220EB8250CCEA312`. Tests were not rerun for the version-only bump, as requested.
- Rendered 12 sample states using the built native executable: connected, standby, nearby, manual controller/receiver, outgoing/incoming confirmation, reconnecting, paused, long name, left arrangement at 150% scale, and error. Inspected the main layout, confirmation controls, manual field placement, truncation, and scaled arrangement. Preview images are under `artifacts/ui-validation/`; these are synthetic sessions, not evidence of two-PC connectivity.
- Before the version bump, the title read back as `Glide  |  PORTABLE / v0.3`; it now specifies `v0.3.1`. A six-second hidden standby profile had 12 refresh calls and 1 visual invalidation. Preview/profile modes do not load or overwrite saved settings. Native EDIT contents and the title bar are not part of the off-screen preview images; desktop inspection timed out waiting for app access, so live field interaction was not verified in this pass.
- Two-PC operation and visible connected-session repaint behavior remain subject to the physical acceptance pass below. No transport or input-forwarding code changed.

## v0.3.0: one-sided pairing and UI refresh fixes

Native executable: **4,470,784 bytes**; SHA-256 `F8F82E3728E61264CE0BEDDFCC8B4044347DB5AAF3409EB2B58AC535F42293D3`.

Portable ZIP: **2,082,327 bytes**; SHA-256 `0612381FEB4318378310911A65F410D7BE70580A0F4363BFBE1CFFFA381748F6`.

- Full Release NativeAOT build and **38 core checks passed**. New coverage verifies passive receiving-side confirmation, withholding credentials until the initiator verifies, rejecting a same-name stranger's certificate, initiator rejection, and revoking permission while verification is pending. Existing protocol, discovery, pinning, cancellation and altered-nonce checks pass.
- Native smoke tests passed, including mutual TLS client identity proof, one-sided verified pairing, Unicode window-title/control text, settings/DPAPI, input transport, listener restarts, and hook lifecycle. No remote keystrokes were injected.
- Both incoming and outgoing pairing previews were rendered and visually inspected. Incoming has no approval buttons; outgoing has Verify/Reject controls. These previews use sample peer data.
- The actual application window title read back as **Glide**, fixing the ANSI/Unicode mismatch that displayed only **G**.
- Six-second standby sample: **12 refresh calls, 1 initial state invalidation**, rather than an invalidation on every timer tick; 17,362,944-byte working set and 5,283,840 private bytes. CPU measured 0.000% of one core at Windows process-time resolution. The helper window was hidden and performed no paints, so this is an idle invalidation/resource check, not a visible-frame flicker benchmark. Changed-state painting now uses a temporary off-screen bitmap and one copy to the window.
- Final same-PC encrypted transport RTT: median **0.031 ms**, p95 **0.080 ms**, p99 **0.231 ms**. This does not measure two-PC input latency.
- ZIP entries verified against the intended executable, pristine INI, README and optional firewall helper. The packaged INI matches the source template exactly. Authenticode status remains **NotSigned**; no signing certificate/account is configured and Smart App Control blocking is not resolved by this update.

Update both PCs for v0.3 discovery/easy pairing; preserve their own INI files. First pairing automatically accepts a request on an open, unpaired, unpaused receiving app; use a trusted LAN. Remembered pairings require the saved peer's private-key proof. Latest one-sided workflow and visible connected-session repaint behavior still need verification on two physical PCs.

## v0.2.0: discovery and confirmed pairing

Native executable: **4,453,888 bytes**; SHA-256 `61D6E9432D7BB66D829FA06BA100669747767AB62AA48A116490176F0DA07F5B`.

Portable ZIP: **2,074,685 bytes**; SHA-256 `F108B2670E040D155E3168EB6D7939893C375D58BCB3900B6FB3FEEAB491B52F`.

- Full Release NativeAOT build passed with warnings treated as errors.
- **35 core checks passed**, including the 20 existing checks plus bounded discovery encoding, absence of pairing secrets in announcements, malformed packet rejection, self-filtering, IP refresh, peer expiry/capacity, real two-endpoint UDP discovery, certificate-bound commitments, per-session codes, withheld credentials before both approvals, matching codes, mutual credential exchange, declined-request cancellation, spoofed certificate rejection, and altered-nonce rejection before approval.
- Native executable checks passed: INI/DPAPI persistence including pause/discovery preferences; actual NativeAOT UDP discovery and confirmed pairing; TLS input echo; three awaited listener stop/restart cycles; input-hook startup/emergency/teardown. No remote input was injected by these smoke tests.
- Nearby-PC and confirmation screens were rendered with sample data and visually inspected. They are example UI states, not a claim of discovering a second physical PC.
- Updated firewall script parsed successfully; it was **not executed** and no firewall rules were installed by the agent.
- Same-PC encrypted transport RTT in the final test run: median **0.030 ms**, p95 **0.057 ms**, p99 **0.224 ms**. This is not cross-PC latency.
- First-pairing trust requires comparing the 12-hex-character session code on both PCs. Discovery is unauthenticated and contains only public identity/status; credential release requires both confirmations. Later connections use the saved certificate pin. This implementation has automated negative-path tests, not an independent security audit.

Still requires physical testing: broadcasts across the user's actual adapters/firewall, two-PC startup/reconnection/role reversal, changing DHCP addresses, sleep/wake, actual input behavior and handoff smoothness, and active-sharing resource use. See README.md for the acceptance pass.

## v0.1.0 archived baseline

Release executable: `dist/Glide/Glide.exe`, Windows x64 NativeAOT, **4,164,096 bytes**.

SHA-256: `E3D3D59C13566A04B6A767984C8D58718C6EDD4F359E3794296B751A715E9944`.

Portable ZIP: `dist/Glide-0.1.0-win-x64.zip`, **1,953,501 bytes** after adding GitHub download links to the README. Verified contents: executable, fresh INI template, README, optional private-LAN firewall script. No private pairing material or debug symbols included.

## Passed

- Full Release NativeAOT build with warnings treated as errors.
- All 20 core checks: packet serialization/rejection; coordinate endpoints, clamping and mixed-resolution conversion; 8,000-motion burst coalescing; click ordering barriers; reliable-event queue overflow; pairing-code validation; certificate export/import; authenticated TLS desktop negotiation; 250 input echoes; heartbeat telemetry; disconnect; wrong-secret rejection; wrong-certificate rejection; silent-peer timeout and bounded recovery.
- Native executable smoke checks: x64 Win32 structure layouts; portable INI and DPAPI round-trip without cleartext credentials; actual NativeAOT TLS authentication/echo/disconnect; dedicated hook-thread initialization, emergency path, and teardown. This smoke test does not inject keyboard/mouse events.
- Both native UI layouts rendered and inspected. Preview is a native off-screen render, not a screenshot of cross-PC operation. Native edit contents do not appear in the off-screen receiver preview.
- Executable dependency inspection: Windows system libraries and Windows Universal CRT; no separately installed .NET runtime or Visual C++ runtime required on Windows 10/11.

## Measurements and limits

Latest managed core loopback run: median **0.033 ms**, p95 **0.062 ms**, p99 **0.214 ms** over 250 sequential encrypted message round-trips. This measures same-PC loopback transport, not real network handoff, injection, rendering, or end-to-end input latency.

A six-second standby sample of the preceding build (before the final window-sizing change) recorded **16,404,480 bytes working set**, **5,111,808 private bytes**, and **0.000% of one CPU core** at Windows process-time resolution. Sharing was off and the window was hidden. This is a short idle sample, not a sustained connected-session benchmark or a guarantee of zero CPU usage.

The original ephemeral TLS key import failed on Windows Schannel. Switching to OS-managed temporary keys fixed authentication, and wrong-secret/wrong-certificate rejection was revalidated. DPAPI and SDK build access required execution outside the restricted tool sandbox; ordinary app use does not require elevation.

## Still requires physical verification

Two-PC edge handoff, cursor continuity at high mouse polling rates, receiver input behavior, stuck-input recovery under real packet loss, multi-monitor/DPI configurations, sleep/wake, and resource use during active sharing. Follow the acceptance pass in README.md. No claim is made that the user's intermittent lag has already been reproduced or eliminated.
