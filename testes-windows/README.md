# Windows regression checks

The C# fixtures were compiled during development. **These Windows scripts and visual scenarios have not been executed on Windows as part of the recorded 6.0 verification.** All native and visual acceptance results remain pending until they are run and reviewed on Windows.

## Requirements

- Windows 10 or 11 with .NET Framework 4.8 and its compiler.
- A normal user account and an unlocked, interactive desktop session.
- The extracted repository or distribution, with the executable and its `.config` file together.

Exit earlier instances of the application before starting. These checks open Explorer windows and create test data; run them when that activity will not interrupt other work. A service session or locked desktop cannot validate Explorer interaction.

## Explorer and overlay regression

From the repository root, run PowerShell under the execution policy permitted on the machine:

```powershell
.\testes-windows\Run-WindowsRegression.ps1 -Executable 'C:\path\PastaGBIcones.exe'
```

The script creates a unique directory under `%TEMP%\PastaGB-verificacoes`, copies the selected executable and configuration, checks the executable's SHA-256, compiles the fixture with the .NET Framework compiler, and performs controlled navigation. An optional `-OutputDirectory` changes the parent evidence directory. The fixture creates about 22 MB of scenario files. It does not install a service, elevate privileges, change global Windows preferences, or modify pre-existing user files.

After the automated run, complete the [visual test matrix](VISUAL_TEST_MATRIX.md) and retain it with the run's evidence. The existing script copies the compatibility filename `MATRIZ-VISUAL.md` into its output directory. Keep that file alongside the script.

An `automated_passed: true` result does not by itself approve visual positioning, actual clicks, dragging, mixed display scaling, or physical USB removal and reconnection. If a native query blocks the fixture, that run fails; the fixture does not create an unbounded series of replacement threads to conceal the problem.

## Isolated scan workers and junctions

Two independent scripts compile a fixture against a verified copy of the selected executable:

```powershell
.\testes-windows\Run-WorkerRegression.ps1 -Executable 'C:\path\PastaGBIcones.exe'
.\testes-windows\Run-JunctionRegression.ps1 -Executable 'C:\path\PastaGBIcones.exe'
```

The worker fixture checks real reader processes, totals, dependencies, subtotals, cancellation, slot release, and the reported native resource controls or their fallback. Confirmation of a configured CPU control does not measure the application's actual CPU consumption under load.

The junction fixture creates junctions only inside its own temporary scenario directories. It checks aliases, cycles, repeated destinations, and a new scan after a destination changes. Neither fixture substitutes for visual checks or physical removable-drive tests.

The C# fixtures were compiled against the packaged executable. The PowerShell scripts were reviewed but not run in the Linux development environment. Record a pass only after executing the applicable fixture on Windows.

## Performance and release acceptance

Use the application's **Desempenho…** menu (Performance) to record the main process's CPU, memory, handles, and rendering metrics, and export the CSV. Reader workers are separate processes and must also be measured.

Compare versions on the same machine without competing benchmarks. Record the first scan, subsequent navigation with a populated cache, idle operation, and folder changes separately. Complete the version 6.0 cases in the matrix.

The delivered version is a release candidate. Windows installation, registry entries, shortcuts, publisher signing, and native behavior have not been approved by these Linux verification records. See [Release readiness](../RELEASE_READINESS.md) for the remaining acceptance criteria.

## Platform references

- [UI Automation threading](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)
- [UI Automation property caching](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-cachingforclients)
- [Shell view modes](https://learn.microsoft.com/en-us/windows/win32/api/shldisp/nf-shldisp-ishellfolderviewdual2-put_currentviewmode)
- [Shell item selection](https://learn.microsoft.com/en-us/windows/win32/shell/shellfolderview-selectitem)
- [Bounded native message waits](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw)
