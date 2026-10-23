# Installation and usage

Folder Size Overlay / Pasta GB Icons **6.0.0** is a release candidate for Windows/.NET Framework 4.8. Native Windows, visual, device, and installer acceptance testing is still pending. The supplied executables are not Authenticode-signed. See [verification](VERIFICATION.md) and [release readiness](RELEASE_READINESS.md).

## Install or update

1. In the old version, select **“Encerrar”** from the icon near the Windows clock.
2. Extract the entire downloaded ZIP before running its executables.
3. Open **`PastaGB-Instalar.exe`** and choose **“Instalar / atualizar”**.
4. The application is installed for the current account under `%LOCALAPPDATA%\Programs\PastaGBIcones`.
5. Check **“Como funciona · versão 6.0”** in the application's menu.

The installer offers a Start menu shortcut, launch after installation, and rollback to a previous installed version. **The first installation does not import an old portable copy into its rollback backup.** Keep that portable directory if you want to retain it.

The startup preference is preserved. If it still points to a portable copy, open the newly installed application and enable **“Iniciar com o Windows”** in that copy's menu. The installer reports this situation when it detects it.

Uninstall through Windows Installed apps or `Desinstalar.exe` in the installed directory. Preferences and cached data under `%LOCALAPPDATA%\PastaGBIcones` are preserved.

## Portable use

Run `PastaGBIcones.exe` from the extracted directory, keeping `PastaGBIcones.exe.config` beside it. To update, exit the running copy and replace the files in the same directory. Keeping the path unchanged preserves the path configured for startup.

## Reading the labels

The existing user interface is in Portuguese.

| Display | Meaning |
| --- | --- |
| `aguardando` | Queued for a reader |
| `calculando` | First calculation in progress |
| `↻` beside a size | An earlier value is being checked or refreshed |
| `*` beside a size | Partial total |
| `indisponível` | Unavailable; retries use backoff |

The first calculation inspects filesystem metadata. Millions of files can take time even when they occupy relatively few GB. The reader does not open file contents to sum their logical sizes. A directory changing during traversal is not a transactional snapshot; subsequent events and reconciliation handle later updates.

The persistent cache accepts only complete results with strong Windows folder and volume identity and no additional dependencies. It revalidates the root before presenting a previous total, always marked as refreshing. A cached total is not proof that the contents remained unchanged while the application was closed.

Folders with links or aliases that do not satisfy the cache's identity requirements use the normal reader. Corrupted cache data does not prevent a new scan. A blocked native call can occupy the single cache worker until it returns; the program does not create unlimited replacement workers or wait for that I/O when closing the interface.

## Resource profiles

| Profile | Concurrent readers | Requested CPU ceiling per reader | Metadata-entry budget per reader |
| --- | ---: | ---: | ---: |
| Balanced / `Equilibrado` | Up to 2 | 15% | 12,000 entries/second |
| Economical / `Econômico` | 1 | 10% | 3,000 entries/second |

Economical mode reduces resource contention and can increase initial calculation time. Diagnostics report whether Windows accepted the native limits. Entries per second are not a physical disk-byte limit. The reader also requests a 384 MiB committed-memory limit and uses a reactive memory safeguard when the native limit is not confirmed.

Open **“Desempenho…”** to inspect the main process and export its local metric history. Separate reader processes are excluded from that panel. Measure them separately when evaluating the whole application. CPU is reported as a percentage of the machine's total capacity; metrics CSV files contain no folder paths.

## Capacity limits

| Component | Limit |
| --- | --- |
| Coordinator | 2,048 requested paths and 2,048 in-memory results |
| Labels | 1,024 simultaneous label objects |
| Explorer indices | Estimated 96 MiB aggregate and 48 MiB per index; up to 100,000 Shell items and 10,000 accessible items per capture |
| Layout cache | 16 surfaces, 2,048 items, estimated 2 MiB budget |
| Persistent cache | 1 worker, 64 requests, 1,024 entries, 262,144 text characters, file up to 4 MiB, retention up to 30 days |
| Metrics | 720 samples, minimum 2-second interval |
| Default reader | 100,000 directories, 2,000,000 entries, depth 256; reaching a limit produces an explicitly partial result |

## Display compatibility

The application operates in an interactive Windows session, outside Explorer. It has no service or injected Explorer DLL. Test scripts cover Windows 10/11 scenarios but must be executed for every build, device, and display configuration intended for support.

Local folders, external drives, and USB drives with drive letters use the same reader. The user must have access, and the Shell must expose usable geometry and identity. Network/UNC paths, mapped network drives, virtual folders, MTP phones, third-party windows, and universal Open/Save dialog coverage are outside scope. Cloud-backed or unknown reparse points may be skipped or cause partial totals.

In Details view, the native Size cell is used only if it is identifiable and empty. Other modes require free space without conflicts. Labels can be hidden when a safe position cannot be established. Relevant events invalidate positions immediately; periodic geometry validity is capped at ten seconds. A blocked native provider can leave a surface without labels until it recovers.

## Data and troubleshooting

**“Copiar diagnóstico”** includes visible paths and local events, as its menu warning explains. Review it before sharing. Cache and metrics are local, with no automatic telemetry or remote updates.

The displayed size is logical size. Compression, sparse files, and hard links can make it differ from physical space used on disk. The always-GB preference uses 1 GB = 1,000,000,000 bytes.

For acceptance testing and unresolved release requirements, use the [Windows test guide](testes-windows/README.md), [visual matrix](testes-windows/VISUAL_TEST_MATRIX.md), and [release checklist](RELEASE_READINESS.md).
