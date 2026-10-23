# Release readiness — 6.0

**Release candidate. Commercial release acceptance is pending.** Implementation, executables, per-user installer, portable regression checks, and controlled component measurements are included. The original validation environment was Linux and did not execute Explorer, native Win32 behavior, physical USB scenarios, shortcuts, registry operations, or the installer's interface.

## Completed checks

- C# 5/.NET Framework 4.8 compilation of the application and installer, with final resources verified.
- Calculation, refresh, partial-result, identity, cancellation, process, and queue tests.
- Persistent-cache tests covering corruption, atomicity, generations, media/policy changes, and late restoration.
- Installer transaction, rollback, recovery, path handling, and data-preservation tests.
- Visual-association algorithm regressions, including ambiguity, immutable snapshots, bounds, and cadence.
- Reproducible 5.1/6.0 comparisons, reporting both improvements and the initial unordered-index regression.
- Source, documentation, tests, evidence, and integrity hashes included in the distribution.

## Checks requiring real Windows systems

| Check | Required evidence | Status |
| --- | --- | --- |
| Native reader | Worker and junction regression scripts, with the exact executable SHA-256 | Pending |
| Explorer and desktop | Windows regression script and completed visual matrix; scroll, selection, rename, tabs, and clicks | Pending |
| Devices and power | USB removal during scanning, different media at the same drive letter, sleep/resume, and Explorer restart | Pending |
| Cache after restart | Old value marked as refreshing; correct new total; deleted/recreated folders and replacement volumes do not inherit unrelated totals | Pending |
| Installation | Standard account, update, rollback, uninstall, startup behavior, locked files, and interruption | Pending |
| Whole-application resource use | CPU, RAM, disk, GDI, and USER counts for the application and readers; first scan, idle, prolonged use, and comparison with 5.1 | Pending |
| Supported environments | Explicitly accepted Windows builds, languages, DPI/monitor configurations, filesystems, and devices | Pending |
| Publisher signing | Valid Authenticode certificate, timestamp, and verification of the signed application and installer | Not applied |

The checklist is not automatic approval. A compiled fixture does not count as an executed test. Surfaces outside supported desktop/Explorer views and resources not exposed by the Shell remain outside scope. Do not advertise universal coverage of every folder on every Windows screen.

## Acceptance sequence

1. Preserve the preceding version and install the candidate using a standard account.
2. Execute the three native test scripts in [the Windows guide](testes-windows/README.md); retain their JSON results and executable hash.
3. Complete [the visual matrix](testes-windows/VISUAL_TEST_MATRIX.md), including the additional 6.0 cases, elapsed time, and environment.
4. Compare resource use using the performance panel/CSV, separate reader measurements, and appropriate tracing. Avoid concurrent benchmarks.
5. Investigate persistent growth in memory or handles, totals attached to the wrong folder, orphaned labels, and hangs before approving a build.
6. Once a legitimate publisher certificate is available, sign the application, rebuild the installer payload with the signed application, and sign the installer. Recreate hashes and inspect embedded resources. Signing changes the final hashes.
7. Publish only the exact build and supported-environment combination whose evidence was accepted. Define support, distribution, and update arrangements before commercial release.

There is no remote update service. Installation preserves one previous installed version for rollback; a first installation does not automatically import an old portable copy. SHA-256 verifies integrity, not publisher identity.

See [the verification record](VERIFICATION.md), [performance methodology](PERFORMANCE.md), and [machine-readable release status](evidencias/estado-de-lancamento.json).
