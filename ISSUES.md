# Glide work tracker

## BUG-001: Fast remote wheel scrolling lags

Open; reported October 1, 2026. Fast/accelerated wheel scrolling up and down has a noticeable delay on the receiving PC. Slow scrolling appears normal. Buffering is a hypothesis, not a confirmed cause; the affected version, mouse/driver, target apps, and network conditions still need recording.

Reproduce in the same application locally and remotely: slow scrolling, fast same-direction bursts, rapid direction reversals, and stopping abruptly after a burst. Include vertical/horizontal wheels, browser smooth scrolling on/off, and portable/service mode. Record whether scrolling continues after the physical wheel stops and whether keyboard or pointer input also falls behind.

Investigation: `InputWorker.Mouse` forwards every wheel delta as a Button packet (flags 2048/4096); `Outbox` only coalesces adjacent Move packets. TLS writes already batch up to 32 queued packets without a batching timer. The receiver posts one Windows message per packet and calls `SendInput` separately. Measure queue depth/age and receive-to-inject timing before deciding whether transport, receiver dispatch, mouse-driver acceleration, or application animation dominates.

Acceptance: fast bursts and reversals respond promptly without a growing playback tail; slow/high-resolution scrolling retains its distance and direction; wheel changes do not cross key/button/position ordering barriers; no dropped key-up/button-up or weakened overflow/emergency-stop behavior. Any wheel-delta aggregation requires evidence that target applications retain the intended scrolling behavior.

## FEAT-001: User-initiated GitHub updates

Implemented in v0.6.0: Check for updates, public stable-release lookup, administrator helper, verified download, portable/service replacement, settings preservation, service restart, and rollback. See [README](README.md#click-to-update) and [validation](VALIDATION.md).

v0.6.0 uses the regular GitHub release channel: the earlier latest-release 404 was caused by every existing release being marked as a prerelease. Live UAC/SCM acceptance remains outstanding; automated validation covers verified downloads, mocked service failures, and a real Windows process-job lifetime test.
