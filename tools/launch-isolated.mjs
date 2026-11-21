#!/usr/bin/env node
// Development only: deploy to an existing isolated macOS app and launch Steam-off.
import { spawn } from 'node:child_process';
import { chmod, cp, mkdir, readFile, realpath, writeFile, open } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { repository } from './build.mjs';

export function hasIsolatedUserdir(text) {
  let section;
  const entries = new Map();
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith(';')) continue;

    if (line.startsWith('[')) {
      if (!/^\[[^\]]+\]$/.test(line)) return false;

      section = line.slice(1, -1);
      if (section === 'DEFAULT') return false;

      continue;
    }
    const match = line.match(/^([^=]+)=(.*)$/);
    if (!match || !section) return false;

    if (section !== 'application') continue;
    if (entries.has(match[1].trim())) return false;

    entries.set(match[1].trim(), match[2].trim());
  }
  if (!/^true\s*(;.*)?$/.test(entries.get('config/use_custom_user_dir') ?? '')) return false;

  const match = entries
    .get('config/custom_user_dir_name')
    ?.match(/^("(?:[^"\\]|\\.)*")\s*(?:;.*)?$/);
  if (!match) return false;

  try {
    const name = JSON.parse(match[1]);
    return /^(stagehand-sts2-|runtime-control-sts2-)/.test(name) && !/[\/\\\0]/.test(name);
  } catch {
    return false;
  }
}

async function previousExited(sessionFile) {
  let session;
  try {
    session = JSON.parse(await readFile(sessionFile, 'utf8'));
  } catch (error) {
    if (error.code === 'ENOENT') return;

    throw error;
  }
  if (!Number.isSafeInteger(session.pid) || session.pid <= 0)
    throw new Error('Invalid previous session PID');

  const deadline = Date.now() + 3000;
  while (true) {
    try {
      process.kill(session.pid, 0);
    } catch (error) {
      if (error.code === 'ESRCH') return;

      throw error;
    }
    if (Date.now() >= deadline)
      throw new Error('The previous isolated process is still running. Quit it normally first.');

    await new Promise((done) => setTimeout(done, 100));
  }
}

export async function launch({
  directory = join(repository, '.local/instance'),
  artifacts = join(repository, 'build'),
} = {}) {
  const root = await realpath(directory);
  const app = await realpath(join(root, 'SlayTheSpire2.app'));
  const inside = relative(root, app);
  if (!inside || inside === '..' || inside.startsWith(`..${sep}`) || isAbsolute(inside))
    throw new Error('The app must be inside the selected isolated directory.');

  const macos = join(app, 'Contents/MacOS');
  const settings = await readFile(join(macos, 'override.cfg'), 'utf8');
  if (!hasIsolatedUserdir(settings))
    throw new Error('Expected the isolated app copy with its Stagehand custom userdir override.');

  const sessionFile = join(root, 'session.json');
  await previousExited(sessionFile);
  const mod = join(macos, 'mods/RuntimeControl');
  await mkdir(mod, { recursive: true });
  for (const name of [
    'RuntimeControl.dll',
    'RuntimeControl.pck',
    'RuntimeControl.json',
    'client.mjs',
    'docs',
    'LICENSE',
    'README.md',
  ])
    await cp(join(artifacts, name), join(mod, name), { recursive: true });

  const stamp = new Date().toISOString().replaceAll(/[:.]/g, '-');
  const logName = `game-stdout-${stamp}.log`;
  const log = await open(join(root, logName), 'wx', 0o600);
  const token = randomBytes(32).toString('hex');
  const child = spawn(
    join(macos, 'Slay the Spire 2'),
    ['--force-steam=off', '--log-file', join(root, `game-engine-${stamp}.log`)],
    {
      env: {
        ...process.env,
        STAGEHAND_TOKEN: token,
        STAGEHAND_PORT: '0',
        STAGEHAND_LISTEN: '127.0.0.1',
        STAGEHAND_DEVELOPER: 'true',
      },
      stdio: ['ignore', log.fd, log.fd],
      detached: true,
    },
  );
  try {
    await new Promise((done, reject) => {
      child.once('spawn', done);
      child.once('error', reject);
    });
    await writeFile(sessionFile, JSON.stringify({ pid: child.pid, token, log: logName }), {
      mode: 0o600,
    });
    await chmod(sessionFile, 0o600);
    child.unref();
  } finally {
    await log.close();
  }
  console.log(`Isolated copy PID ${child.pid}; log ${join(root, logName)}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const { values } = parseArgs({
    options: { directory: { type: 'string' }, artifacts: { type: 'string' } },
  });
  await launch(values);
}
