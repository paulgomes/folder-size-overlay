# Folder Size Overlay

**Folder sizes beside your folders, while you browse Windows.**

Folder Size Overlay, originally named **Pasta GB Icons**, displays calculated folder sizes on the Windows desktop and in compatible File Explorer windows. Background readers calculate the totals, while caching and adaptive updates reduce repeated work.

**Version 6.0.0 — release candidate.** The application and installer have been compiled, and portable tests have passed. Visual behavior, native integration, and whole-application performance still require validation on real Windows systems. Authenticode signing is pending. See [release readiness](RELEASE_READINESS.md).

## Features

- **Automatic size labels** in supported desktop and Explorer views.
- **Adaptive updates** that slow down while a view is stable and respond to relevant changes.
- **Incremental recalculation** that combines bursts of filesystem changes and reuses valid results.
- **Persistent cache** that can show the last known size while a fresh calculation runs.
- **Bounded background work**, including reader concurrency, queues, caches, and scan limits.
- **Explicit result states** for waiting, calculating, refreshing, partial, and unavailable values.
- **Junction and link policies** with safeguards against traversal cycles.
- **Display preferences**, including an option to always show GB.
- **Performance diagnostics** for the main process, with CSV export.
- **Per-user installation** with payload verification, recovery, and rollback to a previous installed version.

The application runs outside the Explorer process. It does not install a Windows service or inject a DLL into Explorer. The repository documentation is in English; the existing application and installer interfaces remain in Portuguese.

## Install

Download the repository using **Code → Download ZIP**, then extract the entire archive.

1. Exit any running copy using **“Encerrar”** in its tray menu.
2. Run **`PastaGB-Instalar.exe`** from the extracted directory.
3. Choose **“Instalar / atualizar”**.
4. Open a supported desktop or File Explorer view.

The installer uses `%LOCALAPPDATA%\Programs\PastaGBIcones`. A portable option is also available: run `PastaGBIcones.exe` with `PastaGBIcones.exe.config` beside it.

Read [installation and usage](INSTALL.md) for updates, rollback, startup behavior, result states, and resource profiles.

## Compatibility and boundaries

The application targets **Windows, .NET Framework 4.8, C# 5, and AnyCPU**, in an interactive user session. Windows 10/11 validation scripts are included, but each intended Windows build, device, display scale, and Explorer layout still needs acceptance testing.

Supported scenarios include accessible filesystem folders on the desktop and in compatible Explorer windows. Local disks, external drives, and USB drives use the same reader when they expose drive-letter paths and the required folder identity and display information.

Coverage does not extend to every Windows surface. Open and Save dialogs, third-party file managers, virtual folders, MTP devices, UNC paths, and mapped network locations are outside the current scope. Some cloud-backed folders and unfamiliar reparse points may be skipped or produce partial results.

Labels require a safe, unambiguous position. A label can be hidden when the application cannot verify its association or avoid overlapping existing content.

Sizes represent **logical file sizes**, not physical disk allocation or deduplicated hard-link storage. The “always GB” option uses **1 GB = 1,000,000,000 bytes**. A first scan must inspect metadata, so a folder containing many files can take time even if its total size is small.

## Build from source

On Windows, with the .NET Framework compiler available:

```powershell
.\codigo-fonte\Compilar.cmd
.\instalador\Compilar-Instalador.ps1
```

The application build writes `PastaGBIcones.exe` to the repository root. Keep its configuration file beside it. Build the installer after the application: its embedded payload and hashes must match the application files being distributed. Follow the execution policy already configured on your machine.

See the [installer build instructions](instalador/README.md) and [test instructions](codigo-fonte/testes/README.md).

## Tests and performance evidence

The original 6.0 verification run passed **29 executed Linux test groups and five compilation checks**. An extracted-package smoke check also passed six suites. The portable runner included here contains **27 groups**; two checks from the original development layout are recorded in the historical evidence.

```bash
python3 codigo-fonte/testes/Run-LinuxTests.py --dotnet /path/to/dotnet
python3 benchmarks/Run-Performance.py --dotnet /path/to/dotnet
```

These commands require Linux, Python 3, and the .NET 8 SDK. They exercise portable components; the application itself targets Windows/.NET Framework 4.8.

Isolated component benchmarks showed less repeated processing and fewer temporary allocations. They do not measure total Windows CPU usage, disk throughput, or Explorer responsiveness. A large initially unordered name index also has a documented construction cost. Read the [full measurements and tradeoffs](PERFORMANCE.md), [verification record](VERIFICATION.md), and [Windows test guide](testes-windows/README.md).

## Repository contents

| Path | Contents |
| --- | --- |
| `codigo-fonte/` | Application source and Windows build command |
| `codigo-fonte/testes/` | Test fixtures and portable runner |
| `instalador/` | Installer source, manifest, and build script |
| `testes-windows/` | Native fixtures and visual acceptance matrix |
| `benchmarks/` | Reproducible comparisons with the preserved 5.1 baseline |
| `evidencias/` | Historical verification records, measurements, and hashes |
| [CODE_REVIEW.md](CODE_REVIEW.md) | Architecture and implementation notes |
| [RELEASE_READINESS.md](RELEASE_READINESS.md) | Remaining release acceptance work |

## Local data and diagnostics

Preferences and cached sizes stay under `%LOCALAPPDATA%\PastaGBIcones`. There is no automatic telemetry upload or remote updater. The metrics CSV contains no folder paths, but **“Copiar diagnóstico”** includes visible paths and local events; review it before sharing.

## License

No open-source license has been selected for this project.
