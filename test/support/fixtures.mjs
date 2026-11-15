import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { ServiceSts2 as Sts2 } from './service.mjs';

export function fixture({ finish = true, finishStatus = 'completed' } = {}) {
  const client = new EventEmitter();
  const listeners = new Set(),
    order = [],
    cards = [];
  let pending,
    next = 0;
  const endpoint = {
    async observe() {
      return {
        value: {
          screen: 'NCombatRoom',
          text: [],
          ready: pending?.status !== 'running',
          combat: {
            ready: pending?.status !== 'running',
            hand: [{ ref: 'card' + next, name: 'Strike', cost: '1', canPlay: true }],
            enemies: [{ ref: 'enemy', name: 'Dummy' }],
          },
          pending,
          choices: [],
        },
      };
    },
    async subscribe(name, listener) {
      listeners.add(listener);
      return async () => listeners.delete(listener);
    },
    async invoke(method, { kind, card }) {
      if (card) cards.push(card);
      const action = String(++next);
      order.push('start:' + kind);
      pending = { action, kind, status: 'running' };
      if (finish)
        setTimeout(() => {
          order.push('finish:' + kind);
          pending = { action, kind, status: finishStatus };
          for (const listener of listeners) listener();
        }, 15);
      return { action, status: 'accepted' };
    },
  };
  return { game: new Sts2(client, endpoint), order, cards, listeners, client, endpoint };
}

export function uiFixture(initial, activate) {
  const client = new EventEmitter(),
    listeners = new Set(),
    calls = [];
  let current = initial;
  const update = (state) => {
    current = state;
    for (const notify of listeners) notify();
  };
  client.object = (id) => ({
    id,
    async inspect() {
      return { methods: [{ name: 'ForceClick' }] };
    },
    async call(method) {
      assert.equal(method, 'ForceClick');
      calls.push(id);
      activate(id, update);
    },
  });
  const endpoint = {
    async observe() {
      return { value: current };
    },
    async subscribe(event, listener) {
      listeners.add(listener);
      return async () => listeners.delete(listener);
    },
  };
  return { game: new Sts2(client, endpoint), calls, update, endpoint };
}

export const button = (ref, type, label, detail) => ({ ref, type, label, detail, enabled: true });
