import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Sts2 } from '../sts2.mjs';
import { EventEmitter } from 'node:events';

test('card inspection accepts observed reward, shop, selection and compact models without driving or rereading', async () => {
  const calls = [];
  const projected = {
    ref: 'card:known',
    name: 'Know Thy Place',
    cost: 0,
    description: 'Apply 1 Weak and 1 Vulnerable. Exhaust.',
    upgrade: {
      ref: null,
      name: 'Know Thy Place+',
      cost: 0,
      description: 'Apply 1 Weak and 1 Vulnerable.',
    },
  };
  const game = new Sts2(new EventEmitter(), {
    async invoke(method, params) {
      calls.push({ method, params });
      return projected;
    },
    async observe() {
      throw new Error('Inspection must not normalize or reread state');
    },
  });
  const card = { ref: 'card:known', name: 'Know Thy Place' };
  for (const source of [
    card,
    'card:known',
    { ref: 'reward-widget', cardRef: card.ref, detail: card },
    { ref: 'shop-widget', detail: { item: card } },
    { ref: 'selection-widget', detail: card },
    { ref: 'compact-choice', model: card },
  ]) {
    assert.equal(await game.inspectCard(source, { upgrades: true }), projected);
  }
  assert.deepEqual(
    calls,
    Array.from({ length: 6 }, () => ({
      method: 'inspect',
      params: { kind: 'card', card: 'card:known', upgrades: true },
    })),
  );
  assert.equal(await game.inspectCard(card), projected);
  assert.equal(calls.at(-1).params.upgrades, false);
});

test('failed card inspection stays outside the mutation queue and preserves native reference rejection', async () => {
  const game = new Sts2(new EventEmitter(), {
    async invoke() {
      throw new Error('Card reference is unknown');
    },
    async observe() {
      return { value: { pending: null, choices: [] } };
    },
  });
  await assert.rejects(
    game.inspectCard({ ref: 'old-card' }, { upgrades: true }),
    /Card reference is unknown/,
  );
  await assert.rejects(game.inspectCard({ name: 'Know Thy Place' }), /card or offer.*reference/i);
  await game.resumeQueue();
});
