# Verification record — 6.0

This page translates the original 6.0 verification record. It does not report a new Windows test run.

- **Original run:** `2026-10-21T23:15:05.380410+00:00`
- **Execution environment:** Linux/.NET 8
- **Compiled target:** C# 5/.NET Framework 4.8/Windows/AnyCPU
- **Source and test hashes stable during the run:** yes
- **Executed groups:** 29, all passed
- **Reported checks:** 26,320
- **Compilation checks:** 5, all passed

Reported counts include repeated checks, randomized data, and progress assertions. They are not independent test scenarios or Windows environments.

## Executable identity

| File | SHA-256 |
| --- | --- |
| `PastaGBIcones.exe` | `3453764f329e3cb6d631a1b8ec6802e5c33ab9a7dac54481ffbbd83eda48dc52` |
| `PastaGB-Instalar.exe` | `d33e39eb09bbdba563ed7f15964fe482b2edc0b4e622cb35f81b886e31841277` |

The installer embeds the same application executable and configuration bytes validated for the original package. Rebuilding or signing changes artifact hashes and requires updated verification.

## Executed groups

| Group | Result | Reported checks |
| --- | --- | ---: |
| coordinator-performance-regression | PASS | 1,710 |
| coordinator-policy-v60 | PASS | 29 |
| coordinator-v5 | PASS | 24 |
| coordinator-v60 | PASS | 47 |
| core-v5 | PASS | 67 |
| folder-resolution | PASS | 14 |
| folder-watcher-v5 | PASS | 15 |
| installer-payload-v60 | PASS | 7 |
| installer-transaction-v60 | PASS | 66 |
| layout-cache-regression | PASS | 197 |
| layout-regression | PASS | 71 |
| media-revisions | PASS | 24 |
| package-metadata | PASS | 6 |
| persistent-cache-v60 | PASS | 105 |
| persistent-identity-ipc-v60 | PASS | 11 |
| persistent-integration-v60 | PASS | 23 |
| probe-index-v60 | PASS | 22,470 |
| process-ipc | PASS | 114 |
| queue-policy | PASS | 34 |
| refresh-v60 | PASS | 45 |
| scanner-legacy | PASS | 163 |
| scanner-performance-regression | PASS | 20 |
| scanner-v5 | PASS | 110 |
| view-events | PASS | 62 |
| visual-performance-regression | PASS | 693 |
| visual-v5 | PASS | 171 |
| watcher-recovery | PASS | 5 |
| watcher-registration-race | PASS | 7 |
| watcher-signals-v60 | PASS | 10 |

## Compilation without native execution

| Check | Result |
| --- | --- |
| build-installer-net48 | PASS |
| build-net48 | PASS |
| windows-junction-compile | PASS |
| windows-visual-compile | PASS |
| windows-worker-compile | PASS |

Compiling the three Windows fixtures does not approve their execution. **Native Windows, UI, physical USB, and Windows installer validation remain pending.** Authenticode signing has not been applied.

## Reproduction and evidence

The delivered portable runner contains **27 groups**. The original development run additionally included package-metadata and final-payload checks tied to that development layout. An extracted-package smoke check passed six suites; see [its record](evidencias/runner-extraido.json).

Read [portable test instructions](codigo-fonte/testes/README.md), [the original verification JSON](evidencias/verification.json), and [the evidence guide](evidencias/README.md). Historical JSON records retain the original commands, paths, and hashes. The [release checklist](RELEASE_READINESS.md) defines the remaining acceptance work.
