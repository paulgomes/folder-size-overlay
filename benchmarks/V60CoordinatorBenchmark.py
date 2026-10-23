#!/usr/bin/env python3
"""Alternate frozen 5.1/current scheduling loops with identical fixed adapters.

This measures coordinator work per pass on Linux/.NET 8, with intentional waits
disabled in the preferences adapter. It is not Windows CPU, Explorer latency,
disk throughput, whole-app memory, or an end-to-end filesystem benchmark.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
QA = ROOT / 'qa'
BASE = QA / 'baseline-v51'
CURRENT = ROOT / 'pasta-gb-icones'
DOTNET = QA / 'tooling/dotnet/dotnet'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--samples', type=int, default=5)
parser.add_argument('--iterations', type=int, default=200)
parser.add_argument('--folders', type=int, default=2048)
parser.add_argument('--out', type=Path, default=QA / 'V60CoordinatorBenchmark.json')
args = parser.parse_args()
if not 3 <= args.samples <= 15 or not 2 <= args.folders <= 2048 or not 10 <= args.iterations <= 2000:
    parser.error('samples=3..15; folders=2..2048; iterations=10..2000')
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
           DOTNET_TieredCompilation='0', COMPlus_TieredCompilation='0')
files = ['IconCoordinator.cs', 'PathRelations.cs', 'SizeRefreshPolicy.cs']
# Stable data/queue definitions isolate the coordinator change. Scanner types
# are dependencies only: the fake transport never launches a filesystem scan.
stable = ['SizePresentation.cs', 'SizeResultPolicy.cs', 'ScanQueuePolicy.cs', 'VolumeRevisions.cs',
          'Scanner.cs', 'DirectoryIdentity.cs']
executables = {}
hashes = {}
for version, path in [('baseline_5_1', BASE), ('optimized', CURRENT)]:
    sources = [QA / 'CoordinatorPerformanceBench.cs'] + [path / x for x in files] + [BASE / x for x in stable]
    if version == 'optimized':
        sources.append(CURRENT / 'CoordinatorPolicy.cs')
    output = QA / 'V60CoordinatorBenchmark' / (version + '.dll')
    subprocess.run([sys.executable, str(QA / 'tooling/compile_net8.py'), '--out', str(output)] +
                   list(map(str, sources)), cwd=ROOT, env=env, check=True)
    executables[version] = output
    hashes[version] = {str(x.relative_to(ROOT)): hashlib.sha256(x.read_bytes()).hexdigest() for x in sources}
trials = []
for scenario in ['fresh', 'monitored', 'busy']:
    for sample in range(args.samples):
        order = ['baseline_5_1', 'optimized'] if sample % 2 == 0 else ['optimized', 'baseline_5_1']
        for version in order:
            process = subprocess.run([str(DOTNET), str(executables[version]), scenario,
                str(args.folders), str(args.iterations)], capture_output=True, text=True, env=env, timeout=60)
            if process.returncode:
                raise RuntimeError(version + '/' + scenario + ': ' + process.stdout + process.stderr)
            result = json.loads(process.stdout)
            result.update(version=version, sample=sample)
            trials.append(result)
summary = {}
for scenario in ['fresh', 'monitored', 'busy']:
    group = summary[scenario] = {}
    for version in executables:
        values = [r for r in trials if r['scenario'] == scenario and r['version'] == version]
        group[version] = {key: statistics.median(r[key] for r in values if r[key] is not None)
            if any(r[key] is not None for r in values) else None
            for key in ['elapsed_ms', 'cpu_ms', 'allocated_bytes', 'coverage_checks', 'reads_started']}
    before, after = group['baseline_5_1'], group['optimized']
    group['elapsed_reduction_percent'] = round(100 * (1 - after['elapsed_ms'] / before['elapsed_ms']), 2)
    group['allocation_reduction_percent'] = round(100 * (1 - after['allocated_bytes'] / before['allocated_bytes']), 2)
unchanged = all(hashlib.sha256((ROOT / path).read_bytes()).hexdigest() == digest
                for version in hashes.values() for path, digest in version.items())
report = {'environment': sys.platform, 'framework': '.NET 8', 'baseline': '5.1',
          'native_windows_executed': False, 'description': __doc__,
          'finished_utc': datetime.now(timezone.utc).isoformat(),
          'cpu_measurement': 'Omitted: process CPU accounting is not reliable in this sandbox.',
          'sources_unchanged': unchanged, 'passed': unchanged and all(r['correctness_passed'] for r in trials),
          'source_sha256': hashes, 'summary': summary, 'trials': trials}
args.out.parent.mkdir(parents=True, exist_ok=True)
args.out.write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps(summary, indent=2))
if not report['passed']:
    raise SystemExit('Benchmark rejected: source mutation or correctness failure')
