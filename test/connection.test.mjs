import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Sts2, connect, connectSts2 } from '../client/sts2.mjs';
import { once } from 'node:events';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { WebSocket, WebSocketServer } = require('ws');

async function wrapperServer(t, { kind = 'Sts2', missing = false, developer = false } = {}) {
  const server = new WebSocketServer({ host: '127.0.0.1', port: 0, path: '/rpc' });
  t.after(async () => {
    for (const socket of server.clients) socket.terminate();
    await new Promise((resolve) => server.close(resolve));
  });
  const objects = [
    ...(missing ? [] : [{ id: 'sts', label: 'Sts2', kind }]),
    { id: 'other', label: 'Other', kind: 'Other' },
    ...(developer
      ? [
          { id: 'recorder', label: 'PropertyRecorder', kind: 'PropertyRecorder', version: '1.0.0' },
          { id: 'screen', label: 'Screen', kind: 'Screen' },
        ]
      : []),
  ];
  server.on('connection', (socket) =>
    socket.on('message', (bytes) => {
      const { id, method, params } = JSON.parse(bytes.toString());
      let result = params;
      switch (method) {
        case 'session.hello':
          result = { protocol: 1, objects };
          break;
        case 'objects.list':
          result = objects;
          break;
        case 'object.observe':
          result = { value: { screen: 'NCardRewardSelectionScreen', ready: true } };
          break;
        case 'test.reference':
          result = { $ref: 'sts', $type: 'Sts2' };
          break;
        case 'object.call':
          if (params.object === 'screen' && params.method === 'capture')
            result = { base64: 'AQID', width: 1, height: 1 };
          break;
      }
      socket.send(JSON.stringify({ jsonrpc: '2.0', id, result }));
    }),
  );
  await once(server, 'listening');
  return `http://127.0.0.1:${server.address().port}`;
}

test('StS connection uses the core wrapper factory and keeps one stateful facade across discovery and references', async (t) => {
  const url = await wrapperServer(t);
  const game = await connectSts2({
    url,
    token: 'test',
    WebSocket,
    wrappers: { Other: (remote) => ({ remote, custom: true }) },
  });
  t.after(() => game.close());
  assert.ok(game instanceof Sts2);
  assert.equal(await game.client.findObject('Sts2'), game);
  assert.equal(await game.client.request('test.reference'), game);
  assert.equal((await game.client.findObject('Other')).custom, true);
  assert.equal((await game.state()).screen, 'NCardRewardSelectionScreen');
  await assert.rejects(game.screenshot(), /Root is unavailable: Screen/);
  const echoed = await game.client.request('test.echo', { target: game });
  assert.equal(echoed.target, game);
  const independent = await connectSts2({ url, token: 'test', WebSocket });
  t.after(() => independent.close());
  assert.notEqual(independent, game);
  assert.equal(await independent.client.findObject('Sts2'), independent);
});

test('StS connection retains core developer wrappers alongside semantic and custom wrappers', async (t) => {
  const game = await connectSts2({
    url: await wrapperServer(t, { developer: true }),
    token: 'test',
    WebSocket,
    wrappers: { Other: (remote) => ({ remote, custom: true }) },
  });
  t.after(() => game.close());
  const recorder = await game.client.root('PropertyRecorder');
  assert.equal(recorder.remote.client, game.client);
  assert.equal((await game.client.findObject('Other')).custom, true);
  assert.equal(await game.client.findObject('Sts2'), game);
  assert.deepEqual(await recorder.read({ offset: 2 }), {
    object: 'recorder',
    method: 'read',
    params: { offset: 2 },
  });
  assert.deepEqual(await game.screenshot(), { width: 1, height: 1, image: Buffer.from([1, 2, 3]) });
});

test('StS factory supports an existing client without replacing its wrappers or closing it on discovery failure', async (t) => {
  const url = await wrapperServer(t);
  const client = await connect(url, {
    token: 'test',
    WebSocket,
    wrappers: { Other: (remote) => ({ id: remote.id, custom: true }) },
  });
  t.after(() => client.close());
  const game = await connectSts2({ client });
  assert.ok(game instanceof Sts2);
  assert.equal(await connectSts2({ client }), game);
  assert.equal((await client.findObject('Other')).custom, true);

  const absent = await connect(await wrapperServer(t, { missing: true }), {
    token: 'test',
    WebSocket,
  });
  t.after(() => absent.close());
  await assert.rejects(connectSts2({ client: absent }), /Sts2 host is not loaded/);
  assert.equal((await absent.objects()).length, 1);
});

test('StS connection reports an outdated untyped host rather than returning a generic remote object', async (t) => {
  const url = await wrapperServer(t, { kind: 'game' });
  await assert.rejects(connectSts2({ url, token: 'test', WebSocket }), /Sts2.*type|typed.*Sts2/i);
});
