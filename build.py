#!/usr/bin/env python3
"""Build the managed binding and resource-only PCK without modifying a game install."""
import argparse
from pathlib import Path
import subprocess
import tempfile

repo = Path(__file__).resolve().parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--game-data', type=Path, required=True, help='Directory containing sts2.dll and GodotSharp.dll')
parser.add_argument('--godot', required=True, help='Godot 4.5 executable used only to create the resource pack')
parser.add_argument('--core', type=Path, default=repo.parent / 'stagehand')
parser.add_argument('--output', type=Path, default=repo / 'build')
args = parser.parse_args()
output = args.output.resolve()
addon = args.core.resolve() / 'addons/stagehand'
for item in [args.game_data / 'sts2.dll', args.game_data / 'GodotSharp.dll', addon / 'bridge.gd']:
    if not item.is_file():
        parser.error(f'Missing required input: {item}')
output.mkdir(parents=True, exist_ok=True)
subprocess.run(['dotnet', 'build', str(repo / 'Stagehand.Sts2.csproj'), '--configuration', 'Release',
                '--output', str(output), f'-p:GameDataDir={args.game_data.resolve()}',
                f'-p:BaseIntermediateOutputPath={repo / "obj"}/', '-p:RuntimeFrameworkVersion=9.0.14'], check=True)
with tempfile.TemporaryDirectory(prefix='sts2-stagehand-pack-') as temporary:
    project = Path(temporary)
    (project / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="Stagehand Pack"\n')
    subprocess.run([args.godot, '--headless', '--path', str(project), '--script', str(repo / 'pack.gd'),
                    '--', str(addon), str(output / 'RuntimeControl.pck')], check=True)
print(f'Built mod artifacts in {output}; nothing deployed.')
