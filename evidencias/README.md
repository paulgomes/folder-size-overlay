# Preserved version 6.0 evidence

This directory contains the original verification and benchmark records for the 6.0 application and installer. JSON records, source hashes, runtime configurations, and original build recipes are retained as evidence; translating repository documentation does not change the scope of those recorded results.

## Start here

| File | What it records |
| --- | --- |
| `verification.json` | Original Linux verification run, source/test hashes, executable hashes, and results |
| `estado-de-lancamento.json` | Release status and the explicit pending Windows, performance, and signing fields |
| `runner-extraido.json` | Smoke checks of six suites from the extracted distribution |
| `build-net48.json` and `build-installer-net48.json` | Production application and installer compilation evidence |
| `windows-visual-compile.json`, `windows-worker-compile.json`, `windows-junction-compile.json` | Windows fixture compilation only |
| `desempenho-coordenador.json` | Isolated coordinator comparison |
| `desempenho-visual.json` and `desempenho-visual-fontes.json` | Managed visual-helper comparisons and their source hashes |

The original development run recorded **29 passing groups and five successful compilation checks**. The source-distribution runner exposes **27 portable suites**; the two additional original checks depend on the former development layout. The six extracted-package smoke checks provide additional packaging evidence, not six new Windows acceptance scenarios.

**Native Windows execution, visual Windows execution, whole-application Windows performance measurement, and Authenticode signing remain pending.** A fixture compile record is not a native test pass. Repeated assertion/progress counts are not counts of independent Windows scenarios.

## Historical paths and fresh runs

Some records include temporary Linux paths and the former directory names `pasta-gb-icones`, `installer`, and `qa`. These are provenance from the recorded run. They are not paths that must exist on a user's Windows machine, and the old build recipes are not directly runnable instructions for a fresh checkout.

For current instructions, use:

- [Portable test runner](../codigo-fonte/testes/README.md)
- [Component benchmarks](../benchmarks/README.md)
- [Installer build](../instalador/README.md)
- [Windows acceptance checks](../testes-windows/README.md)
- [Verification summary](../VERIFICATION.md)
- [Release readiness](../RELEASE_READINESS.md)

Keep fresh run reports separate from these historical records and associate every result with the exact executable and source hashes that were tested.

## Publication integrity

`original-package-sha256.txt` is the manifest of the original Portuguese 6.0
distribution. Its documentation paths and hashes intentionally describe that
historical archive, not this English checkout. Use the root `SHA256.txt` for
the current published files. `publication-audit.json` records the documentation
preparation checks and confirms that source, build scripts, tests, and binaries
were preserved. It does not claim a new native Windows test run.
