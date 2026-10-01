# Glide 0.1 validation — October 1, 2026

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
