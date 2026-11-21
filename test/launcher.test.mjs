import assert from 'node:assert/strict';
import { test } from 'node:test';
import { chmod, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { hasIsolatedUserdir } from '../tools/launch-isolated.mjs';

const valid =
  '[application]\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name="stagehand-sts2-test" ; isolated profile\n';

test('deployment requires an explicit isolated user directory', () => {
  for (const settings of [
    '[application]\nconfig/use_custom_user_dir=false\nconfig/custom_user_dir_name="stagehand-sts2-test"\n',
    '; stagehand-sts2-stale-comment\n[application]\n',
    '[application]\n; config/use_custom_user_dir=true\nconfig/custom_user_dir_name="stagehand-sts2-test"\n',
    valid.replace('stagehand-sts2-test', 'ordinary-profile'),
    valid.replace('stagehand-sts2-test', 'stagehand-sts2-test/../../ordinary'),
    valid.replace('[application]', '[DEFAULT]\n[application]'),
    valid.replaceAll('=', ': '),
    valid + 'config/use_custom_user_dir=false\n',
  ])
    assert.equal(hasIsolatedUserdir(settings), false, settings);

  assert.equal(hasIsolatedUserdir(valid), true);
  assert.equal(hasIsolatedUserdir(valid.replace('stagehand-sts2-', 'runtime-control-sts2-')), true);
});

test('launcher refuses unsafe settings before deploying or creating a session', async (t) => {
  const root = await mkdtemp(join(tmpdir(), 'stagehand-launch-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const macos = join(root, 'SlayTheSpire2.app/Contents/MacOS');
  await mkdir(macos, { recursive: true });
  await writeFile(join(macos, 'override.cfg'), valid.replace('true', 'false'));
  const result = spawnSync(
    process.execPath,
    [fileURLToPath(new URL('../tools/launch-isolated.mjs', import.meta.url)), '--directory', root],
    { encoding: 'utf8' },
  );
  assert.notEqual(result.status, 0);
  await assert.rejects(readFile(join(root, 'session.json')), { code: 'ENOENT' });
  await assert.rejects(readFile(join(macos, 'mods/RuntimeControl/RuntimeControl.dll')), {
    code: 'ENOENT',
  });
});

test(
  'valid isolation deploys only explicit artifacts and launches the fixture executable',
  { skip: process.platform === 'win32' },
  async (t) => {
    const root = await mkdtemp(join(tmpdir(), 'stagehand-launch-test-'));
    t.after(() => rm(root, { recursive: true, force: true }));
    const macos = join(root, 'SlayTheSpire2.app/Contents/MacOS');
    const artifacts = join(root, 'artifacts');
    await mkdir(macos, { recursive: true });
    await mkdir(artifacts);
    await writeFile(join(macos, 'override.cfg'), valid);
    const executable = join(macos, 'Slay the Spire 2');
    await writeFile(executable, '#!/bin/sh\nexit 0\n');
    await chmod(executable, 0o700);
    for (const name of [
      'RuntimeControl.dll',
      'RuntimeControl.pck',
      'RuntimeControl.json',
      'client.mjs',
      'README.md',
      'LICENSE',
    ])
      await writeFile(join(artifacts, name), name);

    await mkdir(join(artifacts, 'docs'));

    const result = spawnSync(
      process.execPath,
      [
        fileURLToPath(new URL('../tools/launch-isolated.mjs', import.meta.url)),
        '--directory',
        root,
        '--artifacts',
        artifacts,
      ],
      { encoding: 'utf8', timeout: 5000 },
    );
    assert.equal(result.status, 0, result.stderr);
    const session = JSON.parse(await readFile(join(root, 'session.json'), 'utf8'));
    assert.ok(session.pid > 0);
    assert.ok(session.token.length > 20);
    for (const name of ['RuntimeControl.pck', 'client.mjs'])
      assert.equal(await readFile(join(macos, 'mods/RuntimeControl', name), 'utf8'), name);
  },
);
