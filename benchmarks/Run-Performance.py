#!/usr/bin/env python3
"""Reproduce the packaged Linux/.NET8 comparisons in a temporary workspace."""
from pathlib import Path
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--dotnet', default='dotnet', help='Installed .NET8 SDK host')
parser.add_argument('--only', choices=['visual', 'coordinator'])
parser.add_argument('--out', type=Path, default=Path.cwd() / 'resultado-desempenho')
args = parser.parse_args()
if not sys.platform.startswith('linux'):
    sys.exit('These comparisons reproduce Linux logic measurements. They do not measure native Windows UI or disk behavior.')
here = Path(__file__).resolve().parent
source = here.parent / 'codigo-fonte'
if not (source / 'Scanner.cs').is_file() or not (here / 'baseline-v51/Scanner.cs').is_file():
    sys.exit('Keep this runner in benchmarks beside codigo-fonte.')
dotnet = shutil.which(args.dotnet)
if not dotnet: sys.exit('Install the .NET8 SDK or provide --dotnet /path/to/dotnet.')
environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1')
installed = subprocess.check_output([dotnet, '--list-sdks'], text=True, env=environment)
matches = re.findall(r'^(8\.0\.[^ ]+) \[(.+)\]$', installed, re.M)
if not matches: sys.exit('An installed .NET8 SDK is required.')
sdk_root = Path(matches[-1][1]).parent
jobs = [
    ('coordinator', 'V60CoordinatorBenchmark.py', 'V60CoordinatorBenchmark.json'),
    ('visual', 'Run-V60VisualChecks.py', 'v60-visual-evidence/V60VisualBench.json')
]
if args.only: jobs = [job for job in jobs if job[0] == args.only]
args.out.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix='PastaGB-comparison-') as temporary:
    workspace = Path(temporary)
    qa = workspace / 'qa'
    qa.mkdir()
    shutil.copytree(source, workspace / 'pasta-gb-icones', ignore=shutil.ignore_patterns('testes'))
    shutil.copytree(here / 'baseline-v51', qa / 'baseline-v51')
    for file in here.iterdir():
        if file.suffix in {'.cs', '.py'} and file.name != Path(__file__).name:
            shutil.copy2(file, qa / file.name)
    tooling = qa / 'tooling'
    tooling.mkdir()
    shutil.copy2(here / 'compile_net8.py', tooling / 'compile_net8.py')
    os.symlink(sdk_root, tooling / 'dotnet', target_is_directory=True)
    results = []
    for name, script, report in jobs:
        print('Comparing ' + name + '...', flush=True)
        run = subprocess.run([sys.executable, str(qa / script)] + (['--benchmark'] if name == 'visual' else []), cwd=workspace, env=environment,
                             text=True, capture_output=True, timeout=600)
        (args.out / (name + '.log')).write_text(run.stdout + run.stderr)
        if run.returncode != 0:
            sys.exit('Comparison failed: ' + name + '. See ' + str(args.out / (name + '.log')))
        (args.out / report).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(qa / report, args.out / report)
        if name == 'visual':
            shutil.copy2(qa / 'v60-visual-evidence/source-hashes.json', args.out / 'v60-visual-evidence/source-hashes.json')
        results.append({'comparison': name, 'report': report, 'exit_code': run.returncode})
        print(name + ': PASS', flush=True)
    (args.out / 'resultado.json').write_text(json.dumps({'linux_execution': True,
        'windows_performance_measured': False, 'comparisons': results}, ensure_ascii=False, indent=2) + '\n')
print('Results: ' + str(args.out.resolve()))
