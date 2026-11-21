// Exercise the actual GDScript adapter in a temporary, game-free project.
import { spawnSync } from 'node:child_process';
import { copyFile, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { homedir, tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import { parseArgs } from 'node:util';

const { values } = parseArgs({ options: { godot: { type: 'string' } } });
if (!values.godot) throw new Error('Expected --godot <executable>');

const project = await mkdtemp(join(tmpdir(), 'sts2-stagehand-registration-'));
try {
  await writeFile(
    join(project, 'project.godot'),
    `config_version=5\n[application]\nconfig/name="Stagehand Registration Fixture"\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name="stagehand-tests/${basename(project)}"\n`,
  );
  await copyFile(new URL('../mod/sts2.gd', import.meta.url), join(project, 'sts2.gd'));
  await copyFile(new URL('./registration.gd', import.meta.url), join(project, 'registration.gd'));
  const child = spawnSync(
    values.godot,
    ['--headless', '--path', project, '--script', 'registration.gd'],
    { stdio: 'inherit', timeout: 30_000 },
  );
  if (child.error) throw child.error;

  process.exitCode = child.status ?? 1;
} finally {
  await rm(project, { recursive: true, force: true });
  const data =
    process.platform === 'darwin'
      ? join(homedir(), 'Library/Application Support')
      : process.platform === 'win32'
        ? process.env.APPDATA
        : (process.env.XDG_DATA_HOME ?? join(homedir(), '.local/share'));
  if (data)
    await rm(join(data, 'stagehand-tests', basename(project)), { recursive: true, force: true });
}
