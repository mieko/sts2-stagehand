import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ServiceSts2 as Sts2 } from './support/service.mjs';
import { fixture, uiFixture, button } from './support/fixtures.mjs';
import { EventEmitter } from 'node:events';
import { isSettledObservation, observationBoundary } from '../spectator.mjs';

test('a service work-order settles at its own stop, including inventory choices without a native action', () => {
  const position = { screen: 'NRewardsScreen', ready: true, choices: [] };
  assert.equal(isSettledObservation({ ...position, operation: { status: 'running' } }), false);
  const stopped = { ...position, operation: { status: 'choice_required', atChoiceStop: true } };
  assert.equal(isSettledObservation(stopped), true);
  assert.equal(observationBoundary(stopped), 'choice_required');
});

test('a historical service choice does not label later human-driven positions as that stop', async () => {
  const initial = {
    screen: 'NRewardsScreen',
    ready: true,
    text: [],
    choices: [button('cards', 'NRewardButton', 'Cards', { category: 'CardReward' })],
  };
  const { game, update } = uiFixture(initial, () =>
    update({
      screen: 'NCardRewardSelectionScreen',
      ready: true,
      text: [],
      choices: [button('card', 'NGridCardHolder', 'Strike')],
    }),
  );
  const result = await game.collectLoot();
  assert.equal(result.status, 'choice_required');
  const first = await game.spectate();
  assert.equal(first.boundary, 'choice_required');
  const next = game.spectate({ after: first, timeoutMs: 100 });
  // Human input progresses without submitting another service operation.
  update({ screen: 'NMapScreen', ready: true, text: [], choices: [], map: [] });
  const frame = await next;
  assert.equal(frame.view.kind, 'map');
  assert.equal(frame.boundary, 'settled_position');
  assert.equal((await game.state()).operation.status, 'choice_required');
});

test('spectate is passive and waits past text/layout churn for a new settled human-driven position', async () => {
  const initial = {
    screen: 'NEventRoom',
    ready: true,
    player: { gold: 10 },
    text: ['Story'],
    event: { title: 'An event', description: 'Flavour', finished: false },
    choices: [button('pick', 'NEventOptionButton', 'Take', { description: 'Gain gold' })],
  };
  const { game, update, calls } = uiFixture(initial, () =>
    assert.fail('Spectating must not activate input'),
  );
  const first = await game.spectate();
  const waiting = game.spectate({ after: first, timeoutMs: 100 });
  update({
    ...initial,
    text: ['Typing...'],
    event: { ...initial.event, description: 'More flavour' },
    dialogue: { line: 2 },
    choices: initial.choices.map((c) => ({ ...c, ref: 'recreated-widget', rect: [2, 3, 4, 5] })),
  });
  let delivered = false;
  waiting.then(() => {
    delivered = true;
  });
  await new Promise((resolve) => setTimeout(resolve, 5));
  assert.equal(delivered, false);
  // No adapter-tracked pending action: ordinary human interaction is observable.
  update({ ...initial, player: { gold: 20 } });
  const second = await waiting;
  assert.equal(second.position, first.position + 1);
  assert.equal(second.view.player.gold, 20);
  assert.equal(second.boundary, 'settled_position');
  assert.deepEqual(calls, []);
});

test('spectate exposes the host-reported enemy-turn choice on its original endTurn', async () => {
  const { game } = uiFixture(
    {
      screen: 'NChooseACardSelectionScreen',
      ready: true,
      text: [],
      pending: { action: 'end', kind: 'endTurn', status: 'choice_required' },
      combat: { ready: false, side: 'Enemy' },
      choices: [button('curse', 'NGridCardHolder', 'Disintegration')],
    },
    () => assert.fail('No choice may be made'),
  );
  const frame = await game.spectate();
  assert.equal(frame.boundary, 'choice_required');
  assert.equal(frame.view.pending.action, 'end');
  assert.equal(frame.view.kind, 'card_selection');
});

test('spectate waits through accepted/running action states and reports per-action settlement, not a batch', async () => {
  const initial = {
    screen: 'NCombatRoom',
    ready: true,
    player: { energy: 3 },
    text: [],
    combat: { ready: true, hand: [], enemies: [] },
    choices: [],
  };
  const { game, update } = uiFixture(initial, () => assert.fail());
  const first = await game.spectate();
  let delivered = false;
  const next = game.spectate({ after: first, timeoutMs: 100 });
  next.then(() => {
    delivered = true;
  });
  update({
    ...initial,
    player: { energy: 2 },
    pending: { action: 'play', kind: 'play', status: 'running' },
  });
  await new Promise((resolve) => setTimeout(resolve, 5));
  assert.equal(delivered, false);
  update({
    ...initial,
    player: { energy: 2 },
    pending: { action: 'play', kind: 'play', status: 'completed' },
  });
  const result = await next;
  assert.equal(result.view.player.energy, 2);
  assert.equal(result.boundary, 'settled_position');
  assert.equal(result.batchComplete, undefined);
});

test('spectate does not normalize forced presentation and keeps one latest in-memory continuation', async () => {
  const { game, update, calls } = uiFixture(
    {
      screen: 'NTreasureRoom',
      ready: true,
      text: [],
      choices: [button('chest', 'NTreasureButton', 'Open')],
    },
    () => assert.fail('An observer must not open the mandatory chest'),
  );
  const first = await game.spectate();
  const waiting = game.spectate({ after: first, timeoutMs: 100 });
  await assert.rejects(game.spectate({ after: first }), /already waiting/);
  update({ screen: 'NMapScreen', ready: true, choices: [], text: [], map: [] });
  const second = await waiting;
  await assert.rejects(game.spectate({ after: first }), /last frame/);
  assert.equal(second.view.kind, 'map');
  assert.deepEqual(calls, []);
});

test('spectate idleness returns null; cancellation and disconnect reject, all cleaning listeners without stopping the action queue', async () => {
  for (const mode of ['timeout', 'abort', 'disconnect']) {
    const { game, client, listeners } = fixture();
    await game.spectate();
    const control = new AbortController();
    const waiting = game.spectate({ timeoutMs: 15, signal: control.signal });
    await new Promise((resolve) => setTimeout(resolve, 1));
    if (mode === 'abort') control.abort();
    if (mode === 'disconnect') client.emit('close');
    if (mode === 'timeout') assert.equal(await waiting, null);
    else
      await assert.rejects(
        waiting,
        mode === 'disconnect' ? /disconnected/ : { name: 'AbortError' },
      );
    assert.equal(listeners.size, 0);
    assert.equal(client.listenerCount('close'), 0);
    if (mode !== 'disconnect') assert.equal((await game.endTurn()).status, 'completed');
  }
});

test('idle spectating retains its continuation and does not hide request failures', async () => {
  const initial = {
    screen: 'NEventRoom',
    ready: true,
    player: { gold: 10 },
    choices: [],
    text: [],
  };
  const { game, update, calls } = uiFixture(initial, () => assert.fail('No spectator input'));
  const first = await game.spectate();
  assert.equal(await game.spectate({ after: first, timeoutMs: 5 }), null);
  const successor = game.spectate({ after: first, timeoutMs: 100 });
  update({ ...initial, player: { gold: 20 } });
  const second = await successor;
  assert.equal(second.position, first.position + 1);
  assert.equal(second.view.player.gold, 20);
  assert.deepEqual(calls, []);
  const broken = new Sts2(new EventEmitter(), {
    async subscribe() {
      return async () => {};
    },
    async observe() {
      throw new Error('RPC request timed out');
    },
  });
  await assert.rejects(broken.spectate(), /RPC request timed out/);
});

test('aborting an observation with an in-flight read releases its subscription promptly', async () => {
  const client = new EventEmitter();
  let unsubscribed = 0;
  const endpoint = {
    async subscribe() {
      return async () => {
        unsubscribed++;
      };
    },
    observe() {
      return new Promise(() => {});
    },
  };
  const game = new Sts2(client, endpoint),
    control = new AbortController();
  const waiting = game.waitFor(() => false, { signal: control.signal, timeoutMs: 1000 });
  setTimeout(() => control.abort(), 5);
  await assert.rejects(waiting, { name: 'AbortError' });
  assert.equal(unsubscribed, 1);
  assert.equal(client.listenerCount('close'), 0);
});

test('aborting during subscription setup cleans a subscription that becomes ready later', async () => {
  const client = new EventEmitter();
  let release,
    active = 0;
  const endpoint = {
    subscribe() {
      return new Promise((resolve) => {
        release = () => {
          active++;
          resolve(async () => {
            active--;
          });
        };
      });
    },
    async observe() {
      assert.fail('Aborted setup must not start observation');
    },
  };
  const game = new Sts2(client, endpoint),
    control = new AbortController();
  const waiting = game.spectate({ signal: control.signal, timeoutMs: 100 });
  control.abort();
  await assert.rejects(waiting, { name: 'AbortError' });
  release();
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(active, 0);
  assert.equal(client.listenerCount('close'), 0);
  assert.equal(client.listenerCount('notification'), 0);
});

test('removed observation endpoints terminate the wait and clean the subscription', async () => {
  const client = new EventEmitter();
  let unsubscribed = 0;
  const endpoint = {
    id: 'adapter',
    async subscribe() {
      return async () => {
        unsubscribed++;
      };
    },
    async observe() {
      return { value: { screen: 'NGame', ready: false, choices: [], text: [] } };
    },
  };
  const game = new Sts2(client, endpoint);
  const waiting = game.spectate({ timeoutMs: 100 });
  client.emit('notification', { method: 'objects.removed', params: { object: 'adapter' } });
  await assert.rejects(waiting, /endpoint was removed/);
  assert.equal(unsubscribed, 1);
  assert.equal(client.listenerCount('notification'), 0);
});

test('spectator idle result is not extended by a delayed unsubscribe acknowledgment', async () => {
  const client = new EventEmitter();
  let release,
    cleanupStarted = false;
  const endpoint = {
    async subscribe() {
      return () => {
        cleanupStarted = true;
        return new Promise((resolve) => {
          release = resolve;
        });
      };
    },
    async observe() {
      return { value: { screen: 'NGame', ready: false, choices: [], text: [] } };
    },
  };
  const game = new Sts2(client, endpoint);
  const waiting = game.spectate({ timeoutMs: 5 });
  let settled = false;
  waiting.then(() => {
    settled = true;
  });
  try {
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.equal(cleanupStarted, true);
    assert.equal(settled, true);
    assert.equal(await waiting, null);
  } finally {
    release?.();
    await waiting.catch(() => {});
  }
  assert.equal(client.listenerCount('close'), 0);
  assert.equal(client.listenerCount('notification'), 0);
});
