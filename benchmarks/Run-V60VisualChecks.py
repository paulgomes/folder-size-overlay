#!/usr/bin/env python3
"""Regressions and opt-in microbenchmarks for the v6 visual hot paths."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def method(text, signature):
    start = text.index(signature)
    opening = text.index("{", start)
    level, end = 1, opening + 1
    while level:
        level += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


def verify_baseline():
    desktop = (ROOT / "qa/baseline-v51/DesktopProbe.cs").read_text()
    explorer = (ROOT / "qa/baseline-v51/ExplorerProbe.cs").read_text()
    adapter = (ROOT / "qa/V60VisualBaseline.cs").read_text()
    expected = method(desktop, "private static AccessibleItem FindMatch(")
    expected = expected.replace("private static", "internal static", 1)
    actual = method(adapter, "internal static AccessibleItem FindMatch(")
    if expected != actual:
        raise RuntimeError("Desktop baseline adapter differs from the actual v5.1 method")
    start = explorer.index("HashSet<string> relevantNames =")
    end = explorer.index("GeometryRequest request =", start)
    if explorer[start:end].rstrip() not in adapter:
        raise RuntimeError("Explorer baseline adapter differs from the actual v5.1 block")
    return {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in (
        "qa/baseline-v51/DesktopProbe.cs", "qa/baseline-v51/ExplorerProbe.cs",
        "pasta-gb-icones/ProbeNameIndex.cs", "pasta-gb-icones/DesktopItemIndex.cs",
        "qa/V60VisualBaseline.cs", "qa/V60VisualTests.cs", "qa/V60VisualBench.cs")}


def compile_and_run(name, evidence):
    output = evidence / (name + ".dll")
    command = [sys.executable, "qa/tooling/compile_net8.py", "--out", str(output),
               "qa/" + name + ".cs", "qa/V60VisualBaseline.cs",
               "pasta-gb-icones/ProbeNameIndex.cs", "pasta-gb-icones/DesktopItemIndex.cs"]
    compilation = subprocess.run(command, cwd=ROOT, text=True, capture_output=True)
    (evidence / (name + "-compile.log")).write_text(compilation.stdout + compilation.stderr)
    if compilation.returncode:
        sys.stdout.write(compilation.stdout + compilation.stderr)
        raise RuntimeError("Compilation failed: " + name)
    execution = subprocess.run([str(ROOT / "qa/tooling/dotnet/dotnet"), str(output)],
                               cwd=ROOT, text=True, capture_output=True, timeout=120)
    (evidence / (name + "-run.log")).write_text(execution.stdout + execution.stderr)
    if execution.returncode:
        sys.stdout.write(execution.stdout + execution.stderr)
        raise RuntimeError("Execution failed: " + name)
    result = json.loads(execution.stdout.strip())
    (evidence / (name + ".json")).write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--benchmark", action="store_true",
                        help="Also time managed helper paths; run without other benchmarks in parallel.")
    args = parser.parse_args()
    evidence = ROOT / "qa/v60-visual-evidence"
    evidence.mkdir(exist_ok=True)
    hashes = verify_baseline()
    compile_and_run("V60VisualTests", evidence)
    if args.benchmark:
        compile_and_run("V60VisualBench", evidence)
    (evidence / "source-hashes.json").write_text(json.dumps(hashes, indent=2) + "\n")


if __name__ == "__main__":
    main()
