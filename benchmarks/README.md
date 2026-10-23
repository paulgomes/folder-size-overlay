# Version 5.1 / 6.0 performance comparisons

These benchmarks reproduce isolated managed-component measurements on **Linux with Python 3 and the .NET 8 SDK**. They do not run the Windows application, UI Automation, GDI, or native Explorer integration, and they do not measure physical disk performance.

Run from the repository root, keeping `benchmarks/` beside `codigo-fonte/`:

```bash
python3 benchmarks/Run-Performance.py --dotnet /path/to/dotnet
```

Use `--only coordinator` or `--only visual` to select one comparison. Use `--out /path/to/results` to choose the result directory; otherwise it is `resultado-desempenho` beneath the current working directory.

The runner copies the required sources into a temporary workspace, uses the installed SDK, and alternates the compared versions. The frozen 5.1 source is included under `baseline-v51/`. Temporary build files are cleaned up; logs, result JSON, and the visual comparison's source hashes are copied to the result directory.

Run comparisons without competing benchmarks or other heavy workloads. Keep the raw results and source hashes with any reported measurements. A lower allocation count in one loop is not a claim about total application RAM or CPU use.

See [Performance](../PERFORMANCE.md) for the measurements, methodology, and tradeoffs, including the additional initial cost of building an index from a large unordered name list. See [Windows validation](../testes-windows/README.md) for the separate native acceptance work.
