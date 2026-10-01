# Glide validation — October 1, 2026

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
