// Convenience for the isolated development copy; never prints launch credentials.
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { connectSts2 } from './sts2.mjs';

const require = createRequire(import.meta.url);
const defaultDirectory = fileURLToPath(new URL('./.local/instance/', import.meta.url));

export async function connectLocal({
  directory = defaultDirectory,
  waitMs = 10_000,
  ...options
} = {}) {
  const session = JSON.parse(await readFile(`${directory}/session.json`, 'utf8'));
  let url = session.url;
  if (!url) {
    if (!session.log) throw new Error('Session has no URL/log. Relaunch with launch-isolated.py.');

    const deadline = Date.now() + waitMs;
    let port;
    while (!port) {
      const log = await readFile(`${directory}/${session.log}`, 'utf8');
      port = log.match(/STAGEHAND_READY (\d+)/)?.[1];
      if (port) break;
      if (Date.now() >= deadline)
        throw new Error('The isolated game has not opened its connector yet');

      await new Promise((resolve) => setTimeout(resolve, 100));
    }
    url = `ws://127.0.0.1:${port}`;
  }
  return connectSts2(url, {
    token: session.token,
    WebSocket: globalThis.WebSocket ?? require('ws'),
    ...options,
  });
}
