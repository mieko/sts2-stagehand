import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { BlobReader, ZipReader } from '@zip.js/zip.js';
import * as core from 'godot-stagehand';
import { packageMod } from '../tools/package.mjs';
import { prepareFiles } from '../tools/build.mjs';

// Build outputs contain dependency DLLs; the release must contain only our install slice.
test('release ZIP is versioned, self-contained and excludes game/build/session files', async (t) => {
  const root = await mkdtemp(join(tmpdir(), 'stagehand-package-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const artifacts = join(root, 'artifacts');
  await mkdir(artifacts);
  const { version } = JSON.parse(
    await readFile(new URL('../package.json', import.meta.url), 'utf8'),
  );
  await prepareFiles(artifacts, fileURLToPath(new URL('../stagehand/', import.meta.url)), version);
  for (const name of [
    'RuntimeControl.dll',
    'RuntimeControl.pck',
    'sts2.dll',
    'GodotSharp.dll',
    'session.json',
  ])
    await writeFile(join(artifacts, name), 'fixture');

  const license = join(artifacts, 'docs/stagehand-LICENSE');
  await rm(license, { force: true });
  await assert.rejects(packageMod({ artifacts, output: root }), /no Stagehand core LICENSE/);
  await writeFile(license, 'Core license fixture');

  await writeFile(join(artifacts, 'docs/private-plan.md'), 'local notes');
  await mkdir(join(artifacts, 'client'));
  await writeFile(join(artifacts, 'client/local.mjs'), 'private launcher');
  await writeFile(join(artifacts, 'client/sts2.mjs'), 'old split client');
  const path = await packageMod({ artifacts, output: root });
  assert.equal(path, join(root, `sts2-stagehand-${version}.zip`));
  const zip = new ZipReader(new BlobReader(new Blob([await readFile(path)])));
  const entries = (await zip.getEntries()).map((entry) => entry.filename);
  await zip.close();
  const expected = [
    'RuntimeControl.dll',
    'RuntimeControl.pck',
    'RuntimeControl.json',
    'client.mjs',
    'docs/stagehand-LICENSE',
    'docs/stagehand.md',
    'docs/api.md',
    'docs/operations.md',
    'docs/install.md',
    'docs/security.md',
    'docs/development.md',
    'README.md',
    'LICENSE',
  ];
  assert.deepEqual(entries.sort(), expected.map((name) => `RuntimeControl/${name}`).sort());

  const client = await import(pathToFileURL(join(artifacts, 'client.mjs')).href);
  for (const name of [...Object.keys(core), 'connectSts2', 'Sts2', 'sts2Wrappers', 'present'])
    assert.ok(Object.hasOwn(client, name), name);
  const manifest = JSON.parse(await readFile(join(artifacts, 'RuntimeControl.json'), 'utf8'));
  assert.equal(manifest.id, 'RuntimeControl');
  assert.equal(manifest.version, version);
});

test('packaging rejects stale artifact versions instead of relabeling an old build', async (t) => {
  const artifacts = await mkdtemp(join(tmpdir(), 'stagehand-old-package-'));
  t.after(() => rm(artifacts, { recursive: true, force: true }));
  await writeFile(join(artifacts, 'RuntimeControl.json'), JSON.stringify({ version: '0.0.0' }));
  await assert.rejects(packageMod({ artifacts, output: artifacts }), /rebuild first/);
});
