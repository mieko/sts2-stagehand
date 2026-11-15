#!/usr/bin/env python3
"""Exercise the actual GDScript registration in a temporary, game-free project."""
import argparse
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--godot', required=True)
args = parser.parse_args()
repo = Path(__file__).resolve().parent.parent
with tempfile.TemporaryDirectory(prefix='sts2-stagehand-registration-') as temporary:
    project = Path(temporary)
    (project / 'project.godot').write_text(
        'config_version=5\n[application]\nconfig/name="Stagehand Registration Fixture"\n'
        'config/use_custom_user_dir=true\n'
        f'config/custom_user_dir_name="stagehand-tests/{project.name}"\n'
    )
    shutil.copy2(repo / 'sts2.gd', project / 'sts2.gd')
    shutil.copy2(repo / 'test/registration.gd', project / 'registration.gd')
    try:
        subprocess.run([
            args.godot, '--headless', '--path', str(project), '--script', 'registration.gd',
        ], check=True, timeout=30)
    finally:
        if sys.platform == 'darwin':
            shutil.rmtree(
                Path.home() / 'Library/Application Support/stagehand-tests' / project.name,
                ignore_errors=True,
            )
