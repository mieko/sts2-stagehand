#!/usr/bin/env node
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { BlobReader, BlobWriter, ZipWriter } from '@zip.js/zip.js';
import { coreLicense, packagedFiles, repository } from './build.mjs';

export async function packageMod({
  artifacts = join(repository, 'build'),
  output = join(repository, 'dist'),
} = {}) {
  const { version } = JSON.parse(await readFile(join(repository, 'package.json'), 'utf8'));
  const manifest = JSON.parse(await readFile(join(artifacts, 'RuntimeControl.json'), 'utf8'));
  if (manifest.version !== version)
    throw new Error('Build version differs from package.json; rebuild first.');

  try {
    await readFile(join(artifacts, coreLicense));
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;

    throw new Error(
      'Build has no Stagehand core LICENSE. Build against a licensed Stagehand checkout.',
    );
  }

  await mkdir(output, { recursive: true });
  const zip = new ZipWriter(new BlobWriter('application/zip'));
  for (const file of packagedFiles)
    await zip.add(
      'RuntimeControl/' + file,
      new BlobReader(new Blob([await readFile(join(artifacts, file))])),
    );

  const target = join(output, `sts2-stagehand-${version}.zip`);
  await writeFile(target, new Uint8Array(await (await zip.close()).arrayBuffer()));
  return target;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const { values } = parseArgs({
    options: { artifacts: { type: 'string' }, output: { type: 'string' } },
  });
  console.log(await packageMod(values));
}
