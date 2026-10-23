# Contributing

Folder Size Overlay 6.0 is a release candidate. Its current priority is reliable
folder-to-label association and bounded resource use on real Windows systems.

Before reporting a problem, read [the compatibility scope](README.md) and
[the release acceptance criteria](RELEASE_READINESS.md). Provide the Windows
build, display scaling, application version, steps to reproduce, and the
observed result. Remove private paths and filenames from shared diagnostics.

For code changes:

1. Keep changes focused and explain the user-visible problem they address.
2. Follow the existing C# 5 / .NET Framework 4.8 constraints for production code.
3. Run the relevant [portable tests](codigo-fonte/testes/README.md).
4. For native, display, device, or installer changes, document the actual
   [Windows checks](testes-windows/README.md) performed. Compilation alone is
   not a Windows test result.
5. Report performance measurements with the workload, platform, and limitations.

Keep the frozen `benchmarks/baseline-v51/` source and the original records in
`evidencias/` unchanged. Add new evidence separately so results remain traceable.
Do not commit user caches, private diagnostics, credentials, or signing keys.

The maintainer has not selected an open-source license. Contact the maintainer
about licensing before submitting contributions that depend on a license choice.
