# Architecture and implementation notes — 6.0

This version reduces repeated queries, temporary allocations, and idle activity. Discovery runs in dedicated workers, calculations run in bounded child processes, and a WinForms UI thread controls labels outside the Explorer process.

## Discovery and drawing

`AdaptiveRefreshPolicy` increases the interval between identical captures. Events wake captures earlier, with a 50 ms window grouping bursts. Periodic checks remain necessary because providers can miss events.

The desktop registers its host with the tracker. Navigation and position changes invalidate drawing; content selection and state changes request a softer recapture. Switching between applications unrelated to desktop/Explorer does not wake both probes. Window `REORDER` events do not feed back into the overlay's own positioning.

Theme, display, and settings changes invalidate geometry without discarding the name index. Resume invalidates media and dependent results, with duplicate notifications grouped. Window, timer, and drawing operations remain on the UI thread.

`UiDispatchWindow` uses a coalesced `PostMessage` with at most one pending notification. Drawing is scheduled within 32 ms and returns to a one-second heartbeat when unchanged. An update arriving during drawing keeps another pass scheduled.

Visible snapshots are reused only when their signature, host, dimensions, DPI, and color agree. Identical invisible snapshots also enter idle mode. Negative age after a clock adjustment is rejected. Explorer's validity period includes the wait and two capture-round durations, because rotating order can move a window from first to last. The ceiling is ten seconds; relevant events revoke validity immediately.

`ProbeNameIndex` retains immutable names and per-request masks, protecting against late UI Automation responses. `DesktopItemIndex` compares positions among candidates with the same name. Duplicates, ambiguity, and DPI tolerance were compared with the 5.1 algorithm. Appearance data and up to 32 fonts are shared within each drawing pass, then temporary fonts are disposed. Full HWND/PID/class/ancestry validation remains in place; confirmation that a native cell is empty reads its current property.

## Coordinator and content changes

The coordinator waits for signals or deadlines, with a maximum one-second fallback. It reuses demand and watch plans and publishes changed results. Per-folder closures were removed from the path without overdue reads.

Changes use an 800 ms quiet interval, with eligibility no later than five seconds after the first event. Continuous writing therefore cannot postpone updates indefinitely. Nearby signals observe a 50 ms interval between rounds while preserving shorter deadlines and immediate wakeup from idle. Watcher maintenance every five seconds preserves an eight-second health lease.

Pause removes active watch demand and cancels readers through existing paths. Resume revalidates potentially changed content. Slot accounting, confirmed termination, quarantine, retry backoff, and starvation safeguards remain.

## Persistence and identity

The private protocol is version 3. A complete native Windows scan exports volume, folder, creation, and physical-path identity after revalidating its root. Errors, partial results, cancellation, work limits, root aliases, and additional dependencies do not authorize the simple persistent cache. The portable resolver never exports durable native identity proof.

The cache uses one worker, bounded queues and format sizes, SHA-256 integrity checks, and atomic replacement. Loading its file does not scan folders. Restoration confirms identity on demand and always presents the previous value with the refreshing indicator; it makes no claim that content stayed unchanged while the application was stopped.

`App.Persistent` binds each response to policy, generation, and a media snapshot captured before validation. A media change between validation and consumption rejects the response. Queue attempts are limited to 16 new attempts per 250 ms. A restored cache value arriving after a scan has started becomes the previous value of that same work item, without launching another reader.

A stuck native call occupies the single cache worker. Its deadline rejects a late result after the call returns; it does not interrupt the kernel call. Cache is optional, and shutdown does not wait for its I/O. Recovery from every blocked provider is not guaranteed.

## Distribution and diagnostics

The per-user installer verifies its exact payload, stages a separate directory, preserves the preceding installation, and rolls back failed swaps. It removes only known names and preserves account data. It does not forcibly terminate running applications. Transaction behavior was tested with real files on Linux; Windows registry operations, mutexes, shortcuts, UI, and file-locking behavior still require Windows testing.

The performance panel retains up to 720 samples without folder paths. It covers the main process; readers require separate measurement. Temporary allocations in benchmarks are not total application RAM.

## Evidence and limits

[VERIFICATION.md](VERIFICATION.md) and [the original verification record](evidencias/verification.json) contain final counts, commands, and hashes. Coverage includes earlier regressions, real Linux events and child processes, injected identities, media changes, recovery, limits, association equivalence, and final installer integrity.

**The original delivery did not execute native or visual Windows validation.** Compilation, review, and portable tests do not establish the absence of all defects. Remaining acceptance criteria are in [RELEASE_READINESS.md](RELEASE_READINESS.md).

## Platform references

- [UI Automation events](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-eventsforclients)
- [UI Automation property caching](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-cachingforclients)
- [PostMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew)
- [Power and resume notifications](https://learn.microsoft.com/en-us/windows/win32/power/wm-powerbroadcast)
- [Windows Performance Recorder profiles](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/recording-for-resource-based-analysis)
