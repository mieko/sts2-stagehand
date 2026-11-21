#!/usr/bin/env node
// Build managed code and a resource-only pack; never deploy to a game installation.
import { execFileSync } from 'node:child_process';
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { build as bundle } from 'esbuild';

const documentation = [
  'README.md',
  'LICENSE',
  'docs/api.md',
  'docs/operations.md',
  'docs/install.md',
  'docs/security.md',
  'docs/development.md',
];
const coreApi = 'docs/stagehand.md';
export const coreLicense = 'docs/stagehand-LICENSE';
export const packagedFiles = [
  'RuntimeControl.dll',
  'RuntimeControl.pck',
  'RuntimeControl.json',
  'client.mjs',
  ...documentation,
  coreApi,
  coreLicense,
];

export const repository = fileURLToPath(new URL('../', import.meta.url));

export async function prepareFiles(output, core, version) {
  const addon = resolve(core, 'addons/stagehand');
  await mkdir(join(output, 'docs'), { recursive: true });
  for (const file of documentation) await cp(join(repository, file), join(output, file));
  await cp(join(addon, 'api.md'), join(output, coreApi));
  await bundle({
    entryPoints: [join(repository, 'client/sts2.mjs')],
    outfile: join(output, 'client.mjs'),
    alias: { 'godot-stagehand': join(addon, 'client.mjs') },
    bundle: true,
    format: 'esm',
    platform: 'node',
    target: 'node22',
  });

  try {
    await cp(join(core, 'LICENSE'), join(output, coreLicense));
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;

    await rm(join(output, coreLicense), { force: true });
  }

  const manifest = JSON.parse(await readFile(join(repository, 'mod/RuntimeControl.json'), 'utf8'));
  manifest.version = version;
  await writeFile(join(output, 'RuntimeControl.json'), JSON.stringify(manifest, null, 2) + '\n');
}

export async function build({
  gameData,
  godot,
  core = join(repository, 'stagehand'),
  output = join(repository, 'build'),
}) {
  gameData = resolve(gameData);
  core = resolve(core);
  output = resolve(output);
  for (const input of [
    join(gameData, 'sts2.dll'),
    join(gameData, 'GodotSharp.dll'),
    join(core, 'addons/stagehand/bridge.gd'),
  ])
    await readFile(input);

  const { version } = JSON.parse(await readFile(join(repository, 'package.json'), 'utf8'));
  const project = await mkdtemp(join(tmpdir(), 'sts2-stagehand-pack-'));
  const staged = join(project, 'artifacts');
  try {
    await prepareFiles(staged, core, version);
    await writeFile(
      join(project, 'project.godot'),
      'config_version=5\n[application]\nconfig/name="Stagehand Pack"\n',
    );
    const pack = join(project, 'RuntimeControl.pck');
    const runGodot = (script, ...args) =>
      execFileSync(
        godot,
        [
          '--headless',
          '--path',
          project,
          '--script',
          join(repository, 'tools', script),
          '--',
          ...args,
        ],
        { stdio: 'inherit', timeout: 30_000 },
      );
    runGodot('pack.gd', join(core, 'addons/stagehand'), staged, pack);
    runGodot('check-pack.gd', pack, staged);

    execFileSync(
      'dotnet',
      [
        'build',
        join(repository, 'mod/Stagehand.Sts2.csproj'),
        '--configuration',
        'Release',
        '--output',
        staged,
        `-p:GameDataDir=${gameData}`,
        '-p:RuntimeFrameworkVersion=9.0.14',
        `-p:Version=${version}`,
      ],
      { stdio: 'inherit' },
    );
    await cp(pack, join(staged, 'RuntimeControl.pck'));
    await rm(join(output, coreLicense), { force: true });
    await cp(staged, output, { recursive: true });
  } finally {
    await rm(project, { recursive: true, force: true });
  }
  console.log(`Built ${version} in ${output}; nothing deployed.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const { values } = parseArgs({
    options: {
      'game-data': { type: 'string' },
      godot: { type: 'string' },
      core: { type: 'string' },
      output: { type: 'string' },
    },
  });
  if (!values['game-data'] || !values.godot)
    throw new Error(
      'Usage: node tools/build.mjs --game-data <assemblies> --godot <executable> [--core <checkout>] [--output <directory>]',
    );

  await build({
    gameData: values['game-data'],
    godot: values.godot,
    core: values.core,
    output: values.output,
  });
}
