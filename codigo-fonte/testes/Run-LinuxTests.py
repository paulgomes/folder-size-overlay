#!/usr/bin/env python3
"""Rebuild the portable suites from a delivered codigo-fonte/testes directory.
Requires Python3 and an installed .NET8 SDK on Linux. Does not execute Windows UI.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--dotnet', default='dotnet', help='Path to the installed .NET8 dotnet host')
parser.add_argument('--only', help='Run one named suite; omit to run all portable suites')
args = parser.parse_args()
if not sys.platform.startswith('linux'):
    sys.exit('This runner records Linux portable evidence. Use testes-windows for native Windows checks.')
dotnet = shutil.which(args.dotnet)
if not dotnet: sys.exit('Install the .NET8 SDK, or provide --dotnet /path/to/dotnet.')
sdks = subprocess.check_output([dotnet, '--list-sdks'], text=True)
matches = re.findall(r'^(8\.0\.[^ ]+) \[(.+)\]$', sdks, re.M)
if not matches: sys.exit('An installed .NET8 SDK is required.')
version, sdk_parent = matches[-1]
runtime_root = Path(sdk_parent).parent
compiler = Path(sdk_parent) / version / 'Roslyn/bincore/csc.dll'
reference_dirs = sorted((runtime_root / 'packs/Microsoft.NETCore.App.Ref').glob('8.*/ref/net8.0'))
runtimes = sorted((runtime_root / 'shared/Microsoft.NETCore.App').glob('8.*'))
if not reference_dirs or not runtimes: sys.exit('The .NET8 reference pack and runtime must be installed together.')
tests = Path(__file__).resolve().parent
source = tests.parent
if not (source / 'Scanner.cs').exists(): sys.exit('Keep this runner inside codigo-fonte/testes.')
output = Path(tempfile.mkdtemp(prefix='PastaGB-tests-'))
scanner = ['Scanner.cs', 'DirectoryIdentity.cs']
states = ['SizePresentation.cs', 'SizeRefreshPolicy.cs', 'SizeResultPolicy.cs', 'AppPreferences.cs', 'PathRelations.cs', 'VolumeRevisions.cs']
cases = [
    ('ScannerTests', scanner), ('ScannerV5Tests', scanner),
    ('VisualV5Tests', ['LabelLayout.cs', 'LabelAppearance.cs', 'BoundedMtaWorker.cs', 'VisualSnapshotPolicy.cs', 'MetadataMemoryBudget.cs', 'ViewSnapshot.cs']),
    ('LabelLayoutReviewTests', ['LabelLayout.cs']), ('ViewChangePolicyTests', ['ViewChangePolicy.cs']),
    ('VolumeRevisionsTests', ['VolumeRevisions.cs']), ('ScanQueuePolicyTests', ['ScanQueuePolicy.cs']),
    ('FolderResolutionStateTests', ['FolderResolutionState.cs']),
    ('CoreV5Tests', states + ['ManagedWorker.cs'] + scanner),
    ('CoordinatorV5Tests', ['IconCoordinator.cs', 'CoordinatorPolicy.cs', 'ScanQueuePolicy.cs'] + states + scanner),
    ('FolderChangeTrackerV5Tests', ['FolderChangeTracker.cs']), ('FolderTrackerRecoveryTests', ['FolderChangeTracker.cs']),
    ('ScannerPerformanceTests', scanner),
    ('VisualPerformanceTests', ['VisualSnapshotPolicy.cs', 'LabelLayout.cs']),
    ('CoordinatorPerformanceTests', ['PathRelations.cs', 'SizeRefreshPolicy.cs', 'SizePresentation.cs', 'AppPreferences.cs', 'FolderChangeTracker.cs']),
    ('LayoutPlanCacheTests', ['LayoutPlanCache.cs', 'LabelLayout.cs', 'ViewSnapshot.cs', 'VisualSnapshotPolicy.cs', 'SizePresentation.cs', 'AppPreferences.cs']),
    ('FolderWatcherRaceTests', ['FolderChangeTracker.cs']),
    ('V60RefreshTests', ['AdaptiveRefreshPolicy.cs', 'SurfaceSnapshotPolicy.cs', 'RuntimeMetrics.cs', 'ViewSnapshot.cs', 'VisualSnapshotPolicy.cs']),
    ('V60IdentityProtocolTests', scanner + ['ScanProtocol.cs']),
    ('V60PersistentIntegrationTests', ['App.Persistent.cs', 'PersistentSizeCache.cs', 'PersistentSizeIdentity.cs', 'SizePresentation.cs', 'AppPreferences.cs', 'VolumeRevisions.cs'] + scanner),
    ('V60InstallerTests', ['../instalador/Setup.cs']),
    ('PersistentSizeCacheTests', ['PersistentSizeCache.cs', 'PersistentSizeIdentity.cs', 'SizePresentation.cs', 'AppPreferences.cs'] + scanner),
    ('V60VisualTests', ['ProbeNameIndex.cs', 'DesktopItemIndex.cs']),
    ('V60CoordinatorTests', ['IconCoordinator.cs', 'CoordinatorPolicy.cs', 'ScanQueuePolicy.cs'] + states + scanner),
    ('V60CoordinatorPolicyTests', ['CoordinatorPolicy.cs', 'SizePresentation.cs', 'AppPreferences.cs']),
    ('V60CoordinatorWatchTests', ['FolderChangeTracker.cs']),
    ('ScanProcessTests', scanner + ['ScanProcess.cs', 'ScanProtocol.cs'])
]
environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1', PASTAGB_TEST_DOTNET=dotnet)
if args.only:
    cases = [case for case in cases if case[0] == args.only]
    if not cases: sys.exit('Unknown suite: ' + args.only)
records = []
for name, dependencies in cases:
    assembly = output / (name + '.dll')
    inputs = [tests / (name + '.cs')] + [source / x for x in dependencies]
    if name == 'V60VisualTests': inputs.append(tests / 'V60VisualBaseline.cs')
    if name == 'V60CoordinatorTests': inputs.append(tests / 'CoordinatorV5Tests.cs')
    command = [dotnet, str(compiler), '-nologo', '-noconfig', '-nostdlib+', '-langversion:5',
               '-utf8output', '-optimize+', '-target:exe', '-out:' + str(assembly)]
    command.extend('-r:' + str(x) for x in reference_dirs[-1].glob('*.dll'))
    if name == 'V60InstallerTests': command.append('-define:SETUP_TESTS')
    if name == 'PersistentSizeCacheTests': command.append('-define:PERSISTENT_CACHE_TESTS')
    if name == 'V60CoordinatorTests': command.append('-define:V60_COORDINATOR_TESTS')
    if name == 'ScanProcessTests': command.append('-define:SCAN_PROCESS_TESTS')
    if name == 'FolderWatcherRaceTests': command.append('-define:WATCHER_RACE_TESTS')
    command.extend(map(str, inputs))
    build = subprocess.run(command, capture_output=True, text=True, env=environment)
    stdout, stderr, code = build.stdout, build.stderr, build.returncode
    if code == 0:
        assembly.with_suffix('.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {
            'tfm': 'net8.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': runtimes[-1].name}}}))
        try:
            execution = subprocess.run([dotnet, str(assembly)], capture_output=True, text=True, env=environment, timeout=180)
            stdout += execution.stdout; stderr += execution.stderr; code = execution.returncode
        except subprocess.TimeoutExpired:
            stderr += 'Suite exceeded180seconds.'; code = 124
    record = {'name': name, 'exit_code': code, 'stdout': stdout, 'stderr': stderr,
              'input_sha256': {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in inputs}}
    records.append(record)
    (output / (name + '.json')).write_text(json.dumps(record, ensure_ascii=False, indent=2))
    print(name + ': ' + ('PASS' if code == 0 else 'FAIL'), flush=True)
    if code != 0: print(stdout + stderr, file=sys.stderr)
passed = all(x['exit_code'] == 0 for x in records)
(output / 'resultado.json').write_text(json.dumps({'passed': passed, 'linux_execution': True,
    'windows_native_executed': False, 'windows_ui_executed': False, 'suites': records}, ensure_ascii=False, indent=2))
print('Evidence: ' + str(output))
sys.exit(0 if passed else 1)
