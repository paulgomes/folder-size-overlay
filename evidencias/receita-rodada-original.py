#!/usr/bin/env python3
"""Final source-bound verification. Runtime cases use Linux .NET8; native tests only compile."""
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import re
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parent.parent
QA = ROOT / 'qa'
SOURCE = ROOT / 'pasta-gb-icones'
EVIDENCE = QA / 'v60-evidence'
EVIDENCE.mkdir(exist_ok=True)
PYTHON = sys.executable

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

source_files = sorted(SOURCE.glob('*.cs')) + [SOURCE / 'app.manifest', SOURCE / 'PastaGBIcones.exe.config', SOURCE / 'Compilar.cmd', ROOT / 'installer/Setup.cs', ROOT / 'installer/app.manifest', ROOT / 'installer/PastaGB-Instalar.exe.config']
source_hashes = {str(p.relative_to(ROOT)): sha(p) for p in source_files}
test_files = sorted(QA.glob('*Tests.cs')) + sorted((QA / 'windows').glob('*.cs'))
test_hashes = {str(p.relative_to(ROOT)): sha(p) for p in test_files}
started = datetime.now(timezone.utc).isoformat()

def run(name, command, native_compile=False):
    before = time.monotonic()
    try:
        result = subprocess.run(command, cwd=ROOT, text=True, capture_output=True, timeout=180)
        code, stdout, stderr = result.returncode, result.stdout, result.stderr
    except subprocess.TimeoutExpired as exc:
        code, stdout, stderr = 124, str(exc.stdout or ''), 'TIMEOUT: ' + str(exc.stderr or '')
    record = {'name': name, 'command': command, 'exit_code': code,
              'seconds': round(time.monotonic() - before, 3), 'stdout': stdout, 'stderr': stderr,
              'windows_native_executed': False, 'windows_ui_executed': False,
              'kind': 'compile_only' if native_compile else 'executed_linux'}
    matches = re.findall(r'PASS:\s*(\d+)', stdout, re.I)
    assertion_count = sum(map(int, matches))
    for line in stdout.splitlines():
        try:
            item = json.loads(line)
            if isinstance(item, dict): assertion_count += int(item.get('assertions', 0))
        except (ValueError, TypeError): pass
    record['reported_assertions'] = assertion_count or None
    (EVIDENCE / (name + '.json')).write_text(json.dumps(record, ensure_ascii=False, indent=2) + '\n')
    print(name + ': ' + ('PASS' if code == 0 else 'FAIL') + (' — ' + stdout.strip().splitlines()[-1] if stdout.strip() else ''), flush=True)
    return record

build_command = [PYTHON, str(QA / 'tooling/compile_net48.py'), '--out', str(SOURCE / 'PastaGBIcones.exe'),
                 '--target', 'winexe', '--manifest', str(SOURCE / 'app.manifest'),
                 '--reference', 'UIAutomationClient', '--reference', 'UIAutomationTypes'] + [str(p) for p in sorted(SOURCE.glob('*.cs'))]
records = [run('build-net48', build_command, True)]
if records[0]['exit_code'] != 0:
    sys.stderr.write(records[0]['stdout'] + records[0]['stderr'])
    sys.exit(1)

records.append(run('build-installer-net48', [PYTHON, str(QA / 'build_v60_installer.py')], True))
if records[-1]['exit_code'] != 0:
    sys.stderr.write(records[-1]['stdout'] + records[-1]['stderr'])
    sys.exit(1)

def pure(name, harness, sources, define=None):
    return (name, [PYTHON, str(QA / 'tooling/compile_net8.py'), '--out', str(EVIDENCE / (name + '.dll')),
                   '--run'] + (['--define', define] if define else []) + [str(QA / item) for item in ([harness] if isinstance(harness, str) else harness)] + [str(SOURCE / item) for item in sources], False)

scanner = ['Scanner.cs', 'DirectoryIdentity.cs']
states = ['SizePresentation.cs', 'SizeRefreshPolicy.cs', 'SizeResultPolicy.cs', 'AppPreferences.cs', 'PathRelations.cs', 'VolumeRevisions.cs']
cases = [
    pure('scanner-legacy', 'ScannerTests.cs', scanner),
    pure('scanner-v5', 'ScannerV5Tests.cs', scanner),
    pure('visual-v5', 'VisualV5Tests.cs', ['LabelLayout.cs', 'LabelAppearance.cs', 'BoundedMtaWorker.cs', 'VisualSnapshotPolicy.cs', 'MetadataMemoryBudget.cs', 'ViewSnapshot.cs']),
    pure('layout-regression', 'LabelLayoutReviewTests.cs', ['LabelLayout.cs']),
    pure('view-events', 'ViewChangePolicyTests.cs', ['ViewChangePolicy.cs']),
    pure('media-revisions', 'VolumeRevisionsTests.cs', ['VolumeRevisions.cs']),
    pure('queue-policy', 'ScanQueuePolicyTests.cs', ['ScanQueuePolicy.cs']),
    pure('folder-resolution', 'FolderResolutionStateTests.cs', ['FolderResolutionState.cs']),
    pure('core-v5', 'CoreV5Tests.cs', states + ['ManagedWorker.cs'] + scanner),
    pure('coordinator-v5', 'CoordinatorV5Tests.cs', ['IconCoordinator.cs', 'CoordinatorPolicy.cs', 'ScanQueuePolicy.cs'] + states + scanner),
    pure('folder-watcher-v5', 'FolderChangeTrackerV5Tests.cs', ['FolderChangeTracker.cs']),
    pure('watcher-recovery', 'FolderTrackerRecoveryTests.cs', ['FolderChangeTracker.cs']),
    pure('scanner-performance-regression', 'ScannerPerformanceTests.cs', scanner),
    pure('visual-performance-regression', 'VisualPerformanceTests.cs', ['VisualSnapshotPolicy.cs', 'LabelLayout.cs']),
    pure('coordinator-performance-regression', 'CoordinatorPerformanceTests.cs', ['PathRelations.cs', 'SizeRefreshPolicy.cs', 'SizePresentation.cs', 'AppPreferences.cs', 'FolderChangeTracker.cs']),
    pure('layout-cache-regression', 'LayoutPlanCacheTests.cs', ['LayoutPlanCache.cs', 'LabelLayout.cs', 'ViewSnapshot.cs', 'VisualSnapshotPolicy.cs', 'SizePresentation.cs', 'AppPreferences.cs']),
    pure('watcher-registration-race', 'FolderWatcherRaceTests.cs', ['FolderChangeTracker.cs'], 'WATCHER_RACE_TESTS'),
    pure('package-metadata', 'PackageMetadataTests.cs', []),
    pure('installer-payload-v60', 'V60PackagePayloadTests.cs', []),
    pure('installer-transaction-v60', 'V60InstallerTests.cs', ['../installer/Setup.cs'], 'SETUP_TESTS'),
    pure('refresh-v60', 'V60RefreshTests.cs', ['AdaptiveRefreshPolicy.cs', 'SurfaceSnapshotPolicy.cs', 'RuntimeMetrics.cs', 'ViewSnapshot.cs', 'VisualSnapshotPolicy.cs']),
    pure('persistent-identity-ipc-v60', 'V60IdentityProtocolTests.cs', scanner + ['ScanProtocol.cs']),
    pure('persistent-integration-v60', 'V60PersistentIntegrationTests.cs', ['App.Persistent.cs', 'PersistentSizeCache.cs', 'PersistentSizeIdentity.cs', 'SizePresentation.cs', 'AppPreferences.cs', 'VolumeRevisions.cs'] + scanner),
    pure('persistent-cache-v60', 'PersistentSizeCacheTests.cs', ['PersistentSizeCache.cs', 'PersistentSizeIdentity.cs', 'SizePresentation.cs', 'AppPreferences.cs'] + scanner, 'PERSISTENT_CACHE_TESTS'),
    pure('probe-index-v60', ['V60VisualTests.cs', 'V60VisualBaseline.cs'], ['ProbeNameIndex.cs', 'DesktopItemIndex.cs']),
    pure('coordinator-v60', ['CoordinatorV5Tests.cs', 'V60CoordinatorTests.cs'], ['IconCoordinator.cs', 'CoordinatorPolicy.cs', 'ScanQueuePolicy.cs'] + states + scanner, 'V60_COORDINATOR_TESTS'),
    pure('coordinator-policy-v60', 'V60CoordinatorPolicyTests.cs', ['CoordinatorPolicy.cs', 'SizePresentation.cs', 'AppPreferences.cs']),
    pure('watcher-signals-v60', 'V60CoordinatorWatchTests.cs', ['FolderChangeTracker.cs']),
    ('process-ipc', [PYTHON, str(QA / 'run_scan_process_tests.py')], False)
]

def fixture(name, source, refs):
    command = [PYTHON, str(QA / 'tooling/compile_net48.py'), '--out', str(EVIDENCE / (name + '.exe')),
               '--target', 'exe', '--assembly-reference', str(SOURCE / 'PastaGBIcones.exe')]
    for item in refs: command.extend(['--reference', item])
    command.append(str(source))
    return name, command, True

cases.extend([
    fixture('windows-visual-compile', QA / 'windows/ExplorerRegressionHarness.cs', ['System.Web.Extensions', 'Microsoft.CSharp', 'UIAutomationClient', 'UIAutomationTypes']),
    fixture('windows-worker-compile', QA / 'windows/ScanWorkerWindowsTests.cs', ['System.Web.Extensions']),
    fixture('windows-junction-compile', QA / 'ScannerWindowsFixtureTests.cs', [])
])
with ThreadPoolExecutor(max_workers=3) as pool:
    jobs = [pool.submit(run, *case) for case in cases]
    for future in as_completed(jobs): records.append(future.result())

unchanged = source_hashes == {str(p.relative_to(ROOT)): sha(p) for p in source_files}
tests_unchanged = test_hashes == {str(p.relative_to(ROOT)): sha(p) for p in test_files}
passed = all(r['exit_code'] == 0 for r in records) and unchanged and tests_unchanged
report = {'version': '6.0.0.0', 'started_utc': started, 'finished_utc': datetime.now(timezone.utc).isoformat(),
          'environment': sys.platform, 'passed': passed, 'sources_unchanged': unchanged, 'tests_unchanged': tests_unchanged,
          'windows_native_executed': False, 'windows_ui_executed': False,
          'exe_sha256': sha(SOURCE / 'PastaGBIcones.exe'), 'installer_sha256': sha(ROOT / 'installer/PastaGB-Instalar.exe'), 'source_sha256': source_hashes,
          'tests_sha256': test_hashes, 'checks': sorted(records, key=lambda x: x['name'])}
(EVIDENCE / 'verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
print(json.dumps({key: report[key] for key in ['version', 'passed', 'sources_unchanged', 'tests_unchanged', 'windows_native_executed', 'windows_ui_executed', 'exe_sha256']}, indent=2), flush=True)
sys.exit(0 if passed else 1)
