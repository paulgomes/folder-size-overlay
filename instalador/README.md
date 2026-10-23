# Per-user installer

`Setup.cs` is the version 6.0 installer for Folder Size Overlay, originally named Pasta GB Icons. It uses WinForms, C# 5, and .NET Framework 4.8, with no additional runtime dependencies, requested elevation, or network access.

The installation destination is fixed:

```text
%LOCALAPPDATA%\Programs\PastaGBIcones
```

Preferences and cache remain under `%LOCALAPPDATA%\PastaGBIcones`. The installer does not read, migrate, or delete them. Existing executable names and Portuguese interface text are retained in this release.

## Installation and recovery behavior

| Operation | Behavior |
| --- | --- |
| Install or update | Stage files in `.PastaGBIcones.stage`, verify hashes, rename the current installation to `.PastaGBIcones.previous`, then move the staged directory into place. All three directories are siblings on the same volume. |
| Roll back | Copy and verify the previous version into a new staging directory, then perform the same replacement. The replaced version becomes the previous version, allowing the rollback itself to be reversed. |
| Uninstall | After interface confirmation, remove only the four recognized files and their empty directories, including the retained previous version. Preserve preferences and cache. |
| Interrupted replacement | If the second directory move fails, attempt to restore the previous version immediately. If restoration fails or the process stops between moves, the next installation can recover the verified previous version. Recognized incomplete staging is discarded; unknown directories are preserved. |
| Windows integration | Register uninstall information under HKCU. Offer an optional per-user Start menu shortcut. Do not create a service, scheduled task, Explorer-loaded extension, or resident installer component. |
| Automatic startup | Install, update, and rollback preserve the existing HKCU Run value. An entry still pointing to a portable copy remains unchanged, with guidance in the interface. Uninstall removes it only if it exactly matches the installed executable. |

Operations run on one STA worker to keep the installer interface responsive. The installer acquires `Local\PastaGB.Setup.1` and the application's existing mutex, `Local\PastaGB.Icones.2`. If the application is running, it asks the user to select **Encerrar** (Exit) and leaves the installation unchanged. It does not terminate processes.

## Build from this repository on Windows

First compile the application from the repository root:

```bat
codigo-fonte\Compilar.cmd
```

Then run the installer script in PowerShell under the execution policy permitted on the machine:

```powershell
.\instalador\Compilar-Instalador.ps1
```

The script reads the final `PastaGBIcones.exe` and `PastaGBIcones.exe.config` from the repository root, generates `instalador/payload-sha256.txt`, embeds the exact payload, writes `PastaGB-Instalar.exe` to the root, and copies its `.config` sidecar. It uses the installed .NET Framework compiler and does not change PowerShell execution policy.

The script was prepared and reviewed in the original Linux development environment, but was not executed there. The preserved Linux cross-compilation records are separate from executing this Windows build script.

The three embedded resource names must be exactly:

```text
PastaGBIcones.exe
PastaGBIcones.exe.config
payload-sha256.txt
```

The payload manifest has two lines. Each line contains a 64-character SHA-256 hexadecimal digest, two spaces, and the exact payload filename. Generate it from the final application bytes. Rebuilding the application after the installer requires rebuilding and checking the installer again. A compile-only installer without these embedded resources is not a usable distribution.

The `.config` sidecar declares the framework requirement. Before operating, the installer also checks that the installed .NET Framework release value is at least `528040`. The installed `Desinstalar.exe` and its temporary copy do not have that sidecar; they use the assembly's CLR 4 target and repeat the framework check. The installer uses the manifest's System DPI mode and `AutoScaleMode.Dpi`.

## Paths and integrity

The payload manifest accepts only `PastaGBIcones.exe` and `PastaGBIcones.exe.config`; it cannot select destination paths. An owned installation contains these files, `Desinstalar.exe`, and `installation.txt`. The ownership record stores version and file hashes, not destination paths.

Existing path components and managed files are checked for reparse points. Unexpected files or changed hashes cause the operation to stop and preserve the affected contents. Removal does not use recursive deletion. Staging files use `CreateNew` and `Flush(true)`. Explicit size limits are 16 MiB for the application, 64 KiB for its configuration, 64 MiB for the installer, and 2 KiB for the payload manifest. Directory inspection stops at the first unexpected filename.

SHA-256 detects corruption and payload/manifest mismatches. **It does not authenticate the publisher or replace Authenticode.** No certificate or signature is generated. These checks protect against operational mistakes, links, and unexpected filesystem state; the installer is not a security boundary against a hostile process already running as the same user.

## Automated evidence

The portable transaction suite can be run on Linux with Python 3 and the .NET 8 SDK:

```bash
python3 codigo-fonte/testes/Run-LinuxTests.py --only V60InstallerTests --dotnet /path/to/dotnet
```

It exercises real Linux files for install, update, rollback, failed replacement, failed restoration, interruption recovery, missing/corrupt/oversized payloads, links, unrelated files, interrupted uninstall, and preservation of data. `SETUP_TESTS` excludes Windows integration and enables the fixture's failure injection; that injection is absent from production builds.

`codigo-fonte/testes/V60InstallerPayloadSmoke.cs` is supplied for additional resource inspection. When compiled as a fixture with `Setup.cs` and `SETUP_TESTS`, it takes the installer executable, application executable, and application configuration as its three arguments. It checks resource names, versions, embedded bytes, and manifest hashes. It does not execute Windows installation. It is not included in the portable runner's 27 selected suites.

Repeat payload inspection after the final application and installer builds, against the exact files to distribute. Original results are preserved in [evidence](../evidencias/README.md).

## Windows acceptance still required

Validate the interface and install/update/rollback/uninstall on real Windows, including a standard account, a running application, locked files, antivirus behavior, Start menu shortcuts, Installed apps, accented paths, display scaling from 100% to 200%, and automatic startup. Compilation and embedded-resource inspection do not substitute for those checks. The `Local\` mutexes coordinate one session; concurrent interactive sessions for the same user require separate validation.

When launched from within its own installation, the installer copies itself to a unique `PastaGBSetup-<guid>` directory under TEMP and starts that copy so the original executable can be removed or replaced. The temporary copy may remain until normal TEMP cleanup. It does not launch a shell command to delete a running executable or remove user data to work around a locked file.

Directory moves and recovery cover the operational failures and process interruptions exercised by the tests. They do not promise filesystem ACID transactions or guaranteed recovery from disk corruption or power loss. See [Release readiness](../RELEASE_READINESS.md).

## Platform references

- [Directory.Move](https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.move?view=netframework-4.8)
- [Mutex](https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex?view=netframework-4.8)
- [Application manifests and asInvoker](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests)
- [.NET Framework version detection](https://learn.microsoft.com/en-us/dotnet/framework/install/how-to-determine-which-versions-are-installed)
