# Windows visual acceptance matrix

**Initial status: PENDING.** A scenario may be marked as passed only after execution on Windows with recorded evidence. Compiling the application or fixture does not approve this matrix.

## Run details

- Date, reviewer, and run duration:
- Windows edition and build:
- Tested executable SHA-256, from `execucao.json`:
- Resolution and display scaling for each monitor:
- Explorer view mode and theme:
- Preferences: adaptive units or always GB; label scale:
- Power-saving state and connected drives:

## What the fixture automates

The fixture uses the discovery classes from the **selected executable**, verified by SHA-256, in a dedicated Explorer window. It navigates A → B with matching folder names → an empty folder → A. It requests icon, details, list, and thumbnail views; checks path correspondence, identity changes and restoration, Shell selection, absence of stale labels in the empty folder, mouse-transparent native window styles, and the `HTTRANSPARENT` response. The first two icon scenarios also require at least one visible label.

Fixture captures use the compatible discovery entry point without the application's private event tracker. The normal application runs alongside the fixture, which observes its native label windows. This does not automatically verify every rendering-coordinator transition, every pixel, or real mouse transparency on every Windows version.

Scenario files contain known logical byte counts. The report records expected totals to support review; it **does not automatically compare every drawn value with those totals**. Unit settings, refresh states, and partial scans affect displayed labels. Complete that comparison below.

Folder names such as `Fotos` and `Relatórios` are retained here because they are the exact names created by the fixture.

## Required manual checks

| Scenario | Acceptance criterion | Result / evidence |
| --- | --- | --- |
| A → B → empty → A | A/Fotos = 2,000,000 bytes; B/Fotos = 7,000,000 bytes. No A value appears on B. No stale labels in the empty folder. | PENDING |
| A/Relatórios and B/Relatórios | A = 3,500,000 bytes; B = 9,000,000 bytes. Correct association on every return. | PENDING |
| Single and multiple selection | Each size stays with its folder; mouse, keyboard, and selection-rectangle behavior remains usable. | PENDING |
| Click directly on a label | The click selects or opens the underlying folder; the label does not receive focus. Include double-clicking. | PENDING |
| Rename with F2 | The label does not cover the editor or interfere with typing. Cancelling with Esc and confirming a new name both restore the correct display. | PENDING |
| Mouse and keyboard scrolling | Labels follow items; no value remains floating over an old row. | PENDING |
| Drag a scenario folder | Native dragging works; the label follows the folder's new location. Use only test files. | PENDING |
| Multiple windows and tabs | Switching tabs or windows does not mix paths or retain another folder's labels. | PENDING |
| Small/large icons, thumbnails, list, details | Labels respect available space and do not cover names, native columns, or editors. Record views that do not expose sufficient geometry. | PENDING |
| Automatic, light, and dark themes | Text remains legible; automatic mode follows the interface color. | PENDING |
| High contrast | System colors are respected without decorative shadows; values remain legible. | PENDING |
| Label scales 85%, 100%, 115%, 130% | Text remains legible without covering names, columns, or editors. Check `indisponível`, `<0,01 GB* ↻`, and long totals. | PENDING |
| Two monitors with different DPI | Moving Explorer between monitors preserves correct position and legibility. Include a monitor to the left with negative coordinates. | PENDING |
| Change display scaling while running | Labels recover their position without restarting the application. | PENDING |
| Remove USB during a scan | A stale result does not return. A different device using the same letter receives its own scan. | PENDING |
| Sleep/resume and restart Explorer | Valid surfaces are rediscovered without orphaned label windows. | PENDING |
| Repeated opening and closing of windows | Record idle and post-cycle CPU, memory, and handles. Investigate continued growth. | PENDING |

A brief hide while geometry is invalid differs from displaying another folder's size. Record the duration and triggering action, preferably with video. An automatic pass does not establish universal coverage of every Windows provider or window.

## Additional version 6.0 checks

| Scenario | Acceptance criterion | Result / evidence |
| --- | --- | --- |
| Idle, stable windows | Labels remain visible; CPU and capture counters slow down; GDI/USER counts do not grow continually. | PENDING |
| Hidden desktop | Captures do not remain permanently at the active rate; visibility recovers without restart. | PENDING |
| Many windows and a slow capture round | Rotating visits do not introduce periodic blank intervals within the valid snapshot lifetime; expired geometry is not drawn. | PENDING |
| Switch between unrelated applications | Switching between applications without Explorer does not itself cause a burst of captures. | PENDING |
| Restart with a populated cache | The last confirmed value appears with ↻; a later scan updates it to current contents. | PENDING |
| Cached result arrives during a scan | The previous value appears without a duplicate reader; completion replaces it correctly. | PENDING |
| Delete/recreate a folder; reuse a drive letter | An old size is never assigned to a new filesystem object. | PENDING |
| Continuous changes | Recalculation becomes eligible within five seconds, subject to queue order and available readers. | PENDING |
| System clock moves backward | A capture with negative age does not remain valid. | PENDING |
| Theme, display, and power changes | Theme/display changes refresh geometry; switching battery/AC does not unnecessarily rebuild the index. | PENDING |
| Replace removable media during sleep | Resume revalidates media and cancels or rejects old reads. | PENDING |
| Install, update, roll back, uninstall | Works as a normal user; shortcuts, registry, and startup entries are correct; preferences/cache are preserved; a running app prevents replacement. | PENDING |
| Long session resource use | Measure the main process and reader processes separately through open/close/restart cycles. | PENDING |

## Keep the evidence

Retain `execucao.json`, `resultado-windows.json`, captured PNGs, and the completed matrix. The script leaves its files and scenario window available for review. After inspection, the reviewer may remove that run's own temporary directory.
