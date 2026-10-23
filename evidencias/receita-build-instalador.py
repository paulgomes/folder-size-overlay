#!/usr/bin/env python3
"""Build the per-user setup with the exact, already-built production payload."""
from pathlib import Path
import hashlib
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
project = root / 'pasta-gb-icones'
installer = root / 'installer'
names = ['PastaGBIcones.exe', 'PastaGBIcones.exe.config']
manifest = installer / 'payload-sha256.txt'
manifest.write_text(''.join(hashlib.sha256((project / name).read_bytes()).hexdigest() + '  ' + name + '\n' for name in names), encoding='ascii')
command = [sys.executable, str(root / 'qa/tooling/compile_net48.py'),
           '--out', str(installer / 'PastaGB-Instalar.exe'), '--target', 'winexe',
           '--manifest', str(installer / 'app.manifest')]
for name in names:
    command.extend(['--resource', str(project / name) + ',' + name])
command.extend(['--resource', str(manifest) + ',payload-sha256.txt', str(installer / 'Setup.cs')])
sys.exit(subprocess.run(command, cwd=root).returncode)
