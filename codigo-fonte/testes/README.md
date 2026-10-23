# Verification tests

The product targets **Windows and .NET Framework 4.8**. The portable tests were executed on **Linux and .NET 8**. Successful compilation or a portable test result does not establish that the Windows interface or native APIs work correctly on a particular Windows build.

## Build the application on Windows

From the repository root, run:

```bat
codigo-fonte\Compilar.cmd
```

The script explicitly lists the production sources; test fixtures are not included in the application executable. It writes `PastaGBIcones.exe` to the root, where `PastaGBIcones.exe.config` must remain beside it. The script uses the .NET Framework compiler installed on the machine and pauses before exiting.

To build the installer, follow the [installer instructions](../../instalador/README.md). The Windows scripts were prepared and reviewed in the original Linux development environment; they were not executed there.

## Run the portable suites

Install Python 3 and the .NET 8 SDK on Linux, preserve the repository layout, and run from the repository root:

```bash
python3 codigo-fonte/testes/Run-LinuxTests.py --dotnet /path/to/dotnet
```

If `dotnet` is already on `PATH`, `--dotnet` may be omitted. To run one suite:

```bash
python3 codigo-fonte/testes/Run-LinuxTests.py --only V60PersistentIntegrationTests
```

The delivered runner contains **27 portable suites**. The original development run recorded **29 groups**, including two additional metadata and payload checks tied to its development layout. See [Verification](../../VERIFICATION.md) and the [preserved evidence](../../evidencias/README.md) for the distinction.

Each run creates a unique temporary output directory, printed as `Evidence: ...`. It contains the compiled assemblies, per-suite JSON records with input SHA-256 hashes, and `resultado.json`. The exit code is nonzero when any selected suite fails. The runner does not execute Windows UI tests.

Identity fixtures use injected resolvers. Cache and installer tests exercise real Linux filesystem I/O; the IPC tests use real child processes, and filesystem watchers receive Linux events. Test compilation symbols enable specific fixtures and injections and are not part of the production build. Reported assertion and progress counts include repetitions and do not represent distinct Windows acceptance scenarios.

`V60VisualBaseline.cs` is the reference algorithm checked against the frozen 5.1 source. `V60InstallerPayloadSmoke.cs` is supplied as an additional resource-inspection fixture; it is not one of the 27 suites selected by this runner.

## Native Windows acceptance

Follow the [Windows regression instructions](../../testes-windows/README.md), complete the [visual test matrix](../../testes-windows/VISUAL_TEST_MATRIX.md), and use the criteria in [Release readiness](../../RELEASE_READINESS.md).

The historical scripts in `evidencias/` describe the isolated SDK and paths used for the original run. They are preserved records, not the supported entry point for a fresh checkout. Use `Run-LinuxTests.py` for the portable suites and `benchmarks/Run-Performance.py` for component comparisons.
