#!/usr/bin/env python3
"""Load built mod artifacts into the existing isolated copy and launch Steam-off."""
import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import time
from datetime import datetime

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--directory', type=Path, default=Path(__file__).resolve().parent / '.local/instance')
parser.add_argument('--artifacts', type=Path, default=Path(__file__).resolve().parent / 'build')
args = parser.parse_args()
root = args.directory.resolve()
app = root / 'SlayTheSpire2.app'
executable = app / 'Contents/MacOS/Slay the Spire 2'
settings = app / 'Contents/MacOS/override.cfg'
if not executable.is_file() or not settings.is_file() or not any(
        prefix in settings.read_text() for prefix in ('stagehand-sts2-', 'runtime-control-sts2-')):
    raise SystemExit('Expected the isolated app copy with its Stagehand custom userdir override.')
if not app.resolve().is_relative_to(root):
    raise SystemExit('The app must be inside the selected isolated directory.')
session_file = root / 'session.json'
if session_file.exists():
    old = json.loads(session_file.read_text())
    deadline = time.monotonic() + 3
    while True:
        try:
            os.kill(old['pid'], 0)
        except ProcessLookupError:
            break
        if time.monotonic() >= deadline:
            raise SystemExit('The previous isolated process is still running. Quit it normally first.')
        time.sleep(0.1)
mod = app / 'Contents/MacOS/mods/RuntimeControl'
mod.mkdir(parents=True, exist_ok=True)
for name in ('RuntimeControl.dll', 'RuntimeControl.pck'):
    shutil.copy2(args.artifacts / name, mod / name)
shutil.copy2(Path(__file__).with_name('RuntimeControl.json'), mod / 'RuntimeControl.json')
stamp = datetime.now().strftime('%Y%m%d-%H%M%S')
log_name = f'game-stdout-{stamp}.log'
token = secrets.token_hex(32)
env = os.environ.copy()
env['STAGEHAND_TOKEN'] = token
with (root / log_name).open('wb') as log:
    process = subprocess.Popen([str(executable), '--force-steam=off', '--log-file', str(root / f'game-engine-{stamp}.log')],
                               env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
fd = os.open(session_file, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
os.fchmod(fd, 0o600)
with os.fdopen(fd, 'w') as stream:
    json.dump({'pid': process.pid, 'token': token, 'log': log_name}, stream)
print(f'Isolated copy PID {process.pid}; log {root / log_name}')
