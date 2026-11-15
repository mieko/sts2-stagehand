import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Sts2, connectSts2 } from '../client/sts2.mjs';
import { once } from 'node:events';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { WebSocket, WebSocketServer } = require('ws');
const { connect } = await import(require.resolve('godot-stagehand'));

async function wrapperServer(t, { kind = 'Sts2', missing = false } = {}) {
  const server = new WebSocketServer({ host: '127.0.0.1', port: 0 });
  t.after(async () => {
    for (const socket of server.clients) socket.terminate();
    await new Promise((resolve) => server.close(resolve));
  });
  const objects = [
    ...(missing ? [] : [{ id: 'sts', label: 'Sts2', kind }]),
    { id: 'other', label: 'Other', kind: 'Other' },
  ];
  server.on('connection', (socket) =>
    socket.on('message', (bytes) => {
      const { id, method, params } = JSON.parse(bytes.toString());
      const result =
        method === 'session.hello'
          ? { protocol: 1, objects }
          : method === 'objects.list'
            ? objects
            : method === 'object.observe'
              ? { value: { screen: 'NCardRewardSelectionScreen', ready: true } }
              : method === 'test.reference'
                ? { $ref: 'sts', $type: 'Sts2' }
                : params;
      socket.send(JSON.stringify({ jsonrpc: '2.0', id, result }));
    }),
  );
  await once(server, 'listening');
  return `ws://127.0.0.1:${server.address().port}`;
}

test('StS connection uses the core wrapper factory and keeps one stateful facade across discovery and references', async (t) => {
  const url = await wrapperServer(t);
  const game = await connectSts2(url, {
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
  const echoed = await game.client.request('test.echo', { target: game });
  assert.equal(echoed.target, game);
  const independent = await connectSts2(url, { token: 'test', WebSocket });
  t.after(() => independent.close());
  assert.notEqual(independent, game);
  assert.equal(await independent.client.findObject('Sts2'), independent);
});

test('StS factory supports an existing client without replacing its wrappers or closing it on discovery failure', async (t) => {
  const url = await wrapperServer(t);
  const client = await connect(url, {
    token: 'test',
    WebSocket,
    wrappers: { Other: (remote) => ({ id: remote.id, custom: true }) },
  });
  t.after(() => client.close());
  const game = await connectSts2(null, { client });
  assert.ok(game instanceof Sts2);
  assert.equal(await connectSts2(null, { client }), game);
  assert.equal((await client.findObject('Other')).custom, true);

  const absent = await connect(await wrapperServer(t, { missing: true }), {
    token: 'test',
    WebSocket,
  });
  t.after(() => absent.close());
  await assert.rejects(connectSts2(null, { client: absent }), /Sts2 host is not loaded/);
  assert.equal((await absent.objects()).length, 1);
});

test('StS connection reports an outdated untyped host rather than returning a generic remote object', async (t) => {
  const url = await wrapperServer(t, { kind: 'game' });
  await assert.rejects(connectSts2(url, { token: 'test', WebSocket }), /Sts2.*type|typed.*Sts2/i);
});
