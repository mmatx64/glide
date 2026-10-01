# Glide work tracker

## BUG-001: Fast remote wheel scrolling lags

Open; **v0.6.1 produced a small improvement in user testing, with lag still reported across apps. v0.6.2 adds further receive-path improvements**, awaiting two-PC acceptance. Reported October 1, 2026. Fast/accelerated wheel scrolling up and down has a noticeable delay on the receiving PC. Slow scrolling appears normal. The affected mouse/driver and network conditions still need recording.

Reproduce in the same application locally and remotely: slow scrolling, fast same-direction bursts, rapid direction reversals, and stopping abruptly after a burst. Include vertical/horizontal wheels, browser smooth scrolling on/off, and portable/service mode. Record whether scrolling continues after the physical wheel stops and whether keyboard or pointer input also falls behind.

Fix: replace one Windows wake per received packet with a single outstanding wake and drains of at most 32 already-queued packets. Consecutive wheel events use a single `SendInput` array; each original delta is a separate INPUT record, with no summing, cancellation, batching timer or protocol change. Position, key, button, release and peer-identity changes end a wheel run. Each drain yields to queued stop/emergency work. Overflow still closes the sender; partial injection stops the session and releases held input.

Evidence: a 192-wheel queued burst needs six bounded drains instead of 192; 10,000 concurrent arrivals retain exact order without losing a wake. The actual receiver loop with mock injection preserves 193 wheel records in 12 calls, including small signed deltas, reversals, both axes and mixed-input barriers. Partial-injection cleanup and emergency preemption also pass. These are automated dispatch checks, not a measured two-PC/application latency result. If lag persists, measure queue depth/age and receive-to-inject timing to distinguish transport, receiver scheduling, mouse-driver acceleration and application animation.

v0.6.2: preserve up to 32 available packets through TLS reading and atomic receiver enqueue, so an early wake cannot split an existing network burst into tiny injection calls. Correctly carry partial stream frames without waiting to fill a batch. Only controllers install a mouse hook; receivers retain keyboard emergency handling. In the first managed local TLS-to-mock-injection comparison, the same 12,800 wheels required 1,198 calls before and 555 after. This measures dispatch efficiency, not actual Windows injection or remote display latency; no deltas are summed or dropped.

Acceptance: fast bursts and reversals respond promptly without a growing playback tail; slow/high-resolution scrolling retains its distance and direction; wheel changes do not cross key/button/position ordering barriers; no dropped key-up/button-up or weakened overflow/emergency-stop behavior. Any wheel-delta aggregation requires evidence that target applications retain the intended scrolling behavior.

## FEAT-001: User-initiated GitHub updates

Implemented in v0.6.0: Check for updates, public stable-release lookup, administrator helper, verified download, portable/service replacement, settings preservation, service restart, and rollback. See [README](README.md#click-to-update) and [validation](VALIDATION.md).

v0.6.0 uses the regular GitHub release channel: the earlier latest-release 404 was caused by every existing release being marked as a prerelease. Live UAC/SCM acceptance remains outstanding; automated validation covers verified downloads, mocked service failures, and a real Windows process-job lifetime test.
