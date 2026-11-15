import assert from 'node:assert/strict';
import { test } from 'node:test';
import { copyFile, mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

test('default local connection path supports spaces without reading any real session', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'stagehand checkout with spaces '));
  try {
    await mkdir(join(directory, 'client'));
    await copyFile(
      new URL('../client/local.mjs', import.meta.url),
      join(directory, 'client/local.mjs'),
    );
    await writeFile(
      join(directory, 'client/sts2.mjs'),
      'export const connectSts2 = async (url, options) => ({url, token: options.token});',
    );
    await mkdir(join(directory, '.local/instance'), { recursive: true });
    await writeFile(
      join(directory, '.local/instance/session.json'),
      JSON.stringify({ url: 'ws://127.0.0.1:1234', token: 'fixture-only' }),
    );
    const { connectLocal } = await import(pathToFileURL(join(directory, 'client/local.mjs')).href);
    assert.deepEqual(await connectLocal(), { url: 'ws://127.0.0.1:1234', token: 'fixture-only' });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
