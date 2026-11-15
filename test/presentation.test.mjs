import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Sts2 } from '../sts2.mjs';
import { button } from './support/fixtures.mjs';

test('concise presenter groups real copies, preserves variants and includes prices and incoming uncertainty', async () => {
  const { present } = await import('../sts2.mjs');
  const dazed = { name: 'Dazed', description: 'Unplayable. Ethereal.', cost: 0, canPlay: false };
  const raw = {
    kind: 'combat',
    choices: [
      {
        ref: 'offer',
        label: 'Potion',
        kind: 'buy_potion',
        price: 51,
        detail: { price: 51, item: { description: 'Game potion description.' } },
        enabled: true,
      },
    ],
    player: { gold: 52 },
    combat: {
      hand: [
        { ...dazed, ref: 'a' },
        { ...dazed, ref: 'b' },
        { ...dazed, ref: 'c', enchantment: { name: 'Different', amount: 1 } },
      ],
      enemies: [
        {
          intents: [
            { type: 'Attack', isAttack: true, damagePerHit: 6, hits: 2, totalDamage: 12 },
            { type: 'Unknown' },
          ],
        },
      ],
    },
  };
  const result = present({
    status: 'completed',
    state: { huge: true },
    decision: raw,
    results: [{ state: {} }],
  });
  assert.equal(result.state, undefined);
  assert.equal(result.decision, undefined);
  assert.equal(result.results, undefined);
  assert.deepEqual(result.combat.hand[0].refs, ['a', 'b']);
  assert.equal(result.combat.hand[0].count, 2);
  assert.equal(result.combat.hand.length, 2);
  assert.equal(result.player.gold, 52);
  assert.equal(result.choices[0].price, 51);
  assert.equal(result.combat.incoming.knownDamage, 12);
  assert.equal(result.combat.incoming.totalDamage, null);
  assert.equal(raw.combat.hand.length, 3);
});

test('the presenter preserves native evoke-next positions without spatial reinterpretation', async () => {
  const { present } = await import('../sts2.mjs');
  const orbs = [
    { name: 'Lightning', queuePosition: 1, evokeNext: true },
    { name: 'Dark', queuePosition: 2, evokeNext: false },
  ];
  const result = present({
    kind: 'combat',
    choices: [],
    combat: { orbs, orbOrder: 'evoke_next_first' },
  });
  assert.equal(result.combat.orbOrder, 'evoke_next_first');
  assert.deepEqual(result.combat.orbs, orbs);
  assert.equal(result.combat.orbs[0].evokeNext, true);
  assert.deepEqual(
    present({ kind: 'combat', choices: [], combat: { orbs: [], orbOrder: 'evoke_next_first' } })
      .combat.orbs,
    [],
  );
});

test('compact map/inventory options retain next rooms, combat counters and offer effect definitions', async () => {
  const { present } = await import('../present.mjs');
  const map = [{ ref: 'r', col: 0, row: 1, type: 'Shop', leadsTo: [], state: 'Travelable' }];
  const raw = {
    kind: 'map',
    map,
    nextRooms: map,
    choices: [],
    player: {
      relics: [
        { name: 'Static', status: 'Normal' },
        { name: 'Ornamental Fan', counter: 3 },
      ],
    },
  };
  const small = present(raw);
  assert.equal(small.map, undefined);
  assert.deepEqual(small.nextRooms, map);
  assert.deepEqual(
    small.player.relics.map((r) => r.name),
    ['Ornamental Fan'],
  );
  assert.equal(present(raw, { map: true, inventory: true }).map[0].at, '0,1');
  assert.equal(present(raw, { inventory: true }).player.relics.length, 2);
  const offer = present({
    kind: 'event',
    choices: [
      {
        ...button('swift', 'NEventOptionButton', 'Swift'),
        kind: 'event_option',
        detail: {
          effects: [{ name: 'Swift', description: 'First play each combat draws 3 cards.' }],
        },
      },
    ],
  });
  assert.equal(
    offer.choices[0].model.effects[0].description,
    'First play each combat draws 3 cards.',
  );
});

test('compact Architect dialogue retains current spoken text once with no advance or extra read', async () => {
  const game = new Sts2(null, {
    observe() {
      assert.fail('Pure projection only');
    },
  });
  const raw = {
    kind: 'event',
    choices: [],
    event: { description: '' },
    text: ['Current Architect line'],
    dialogue: {
      source: 'speech_bubble',
      line: 2,
      text: ['Current Architect line'],
      canAdvance: false,
    },
  };
  const view = game.present(raw);
  assert.deepEqual(view.dialogue.text, ['Current Architect line']);
  assert.deepEqual(view.text, []);
  assert.equal(view.dialogue.canAdvance, false);
  assert.equal(raw.text[0], 'Current Architect line');
});

test('pure compact outcomes retain companions and related-card facts without duplicate hand/allied models', async () => {
  const card = {
    ref: 'card',
    name: 'Conqueror',
    cost: 1,
    mechanics: ['A native-backed rule.'],
    effects: [
      {
        name: 'Sovereign Blade',
        description: 'Retain. Deal 10 damage.',
        card: { name: 'Sovereign Blade', cost: 2, starCost: null },
      },
    ],
  };
  const player = {
    ref: 'me',
    hp: 50,
    gold: 12,
    relics: [
      { name: 'Static relic', status: 'Normal' },
      { name: 'Counter', counter: 2 },
    ],
  };
  const pet = {
    ref: 'osty',
    name: 'Osty',
    alive: false,
    hp: 0,
    maxHp: 20,
    statuses: [{ name: 'Doom', amount: 4 }],
  };
  const decision = {
    kind: 'card_selection',
    text: ['Repeated card labels'],
    choices: [
      {
        ...button('hand-widget', 'NHandCardHolder', 'Conqueror'),
        kind: 'select_card',
        cardRef: 'card',
        detail: card,
      },
    ],
    player,
    combat: {
      ready: false,
      hand: [card],
      pets: [pet],
      allies: [player, pet],
      ongoing: true,
      noLivingEnemies: true,
      inactiveEnemies: [{ name: 'Test Subject', alive: false }],
    },
  };
  const game = new Sts2(null, {
    observe() {
      assert.fail('present must be pure');
    },
  });
  const before = JSON.stringify(decision);
  const result = game.present({ status: 'choice_required', action: 'turn', decision });
  assert.deepEqual(result.combat.hand, []);
  assert.deepEqual(result.combat.handInChoices, ['card']);
  assert.deepEqual(result.combat.pets, [pet]);
  assert.deepEqual(result.combat.allies, []);
  assert.equal(result.combat.noLivingEnemies, true);
  assert.equal(result.combat.inactiveEnemies[0].name, 'Test Subject');
  assert.equal(result.text, undefined);
  assert.equal(result.choices[0].model.effects[0].card.cost, 2);
  assert.deepEqual(result.choices[0].model.mechanics, ['A native-backed rule.']);
  assert.deepEqual(
    result.player.relics.map((r) => r.name),
    ['Counter'],
  );
  assert.equal(JSON.stringify(decision), before);
});

test('compact Timeline summarizes history and retains every new explicit epoch decision', async () => {
  const { present } = await import('../present.mjs');
  const epochs = ['inspect_epoch', 'locked_epoch', 'reveal_epoch'].map((kind, i) => ({
    ref: String(i),
    label: kind,
    kind,
    enabled: kind !== 'locked_epoch',
  }));
  const raw = { kind: 'timeline', choices: epochs, timeline: { busy: false, epochs } };
  const compact = present(raw);
  assert.deepEqual(compact.timeline.history, { completed: 1, locked: 1 });
  assert.equal(compact.timeline.epochs, undefined);
  assert.deepEqual(
    compact.choices.map((c) => c.ref),
    ['2'],
  );
  assert.equal(raw.choices.length, 3);
});

test('compact sphere shows each visible cell once, full revealed items separately from fragments', async () => {
  const { present } = await import('../sts2.mjs');
  const cells = Array.from({ length: 121 }, (_, i) => ({
    ref: 'cell' + i,
    kind: 'divine_cell',
    type: 'NCrystalSphereCell',
    enabled: true,
    detail: {
      x: i % 11,
      y: Math.floor(i / 11),
      hidden: i !== 5,
      revealedItem: i === 5 ? 'CrystalSphereGold' : null,
    },
  }));
  const d = {
    kind: 'crystal_sphere',
    choices: cells,
    crystalSphere: { remaining: 2, cells, tools: [], revealedItems: [] },
  };
  const result = present(d);
  assert.equal(result.choices.length, 0);
  assert.equal(result.crystalSphere.cells, undefined);
  assert.equal(result.crystalSphere.grid.rows.length, 11);
  assert.equal(result.crystalSphere.grid.rows[0].cells[5], 'CrystalSphereGold');
  assert.equal(result.crystalSphere.grid.rows[0].cells[4], '?');
  assert.deepEqual(result.crystalSphere.revealedItems, []);
  assert.ok(JSON.stringify(result).length < JSON.stringify(d).length / 4);
});

test('compact combat retains actionable context and material choice without repeated effect/relic prose', async () => {
  const { present } = await import('../sts2.mjs');
  const card = {
    ref: 'c',
    name: 'Dazed',
    description: 'Ethereal.',
    effects: [{ name: 'Ethereal', description: 'Long effect definition.' }],
  };
  const choice = {
    ref: 'special',
    kind: 'special_card_reward',
    label: 'Take your stolen card back',
    detail: { category: 'SpecialCardReward', card },
  };
  const result = present({
    status: 'observed_change',
    selectedChoice: choice,
    deckChanges: { added: [card] },
    decision: {
      kind: 'combat',
      choices: [
        button('draw', 'NDrawPileButton', 'Draw Pile'),
        button('end', 'NEndTurnButton', 'End Turn'),
      ],
      player: {
        gold: 50,
        relics: [{ id: 'R', name: 'Relic', description: 'Long relic definition.', counter: 2 }],
      },
      combat: { hand: [card], enemies: [] },
    },
  });
  assert.deepEqual(
    result.choices.map((c) => c.ref),
    ['end'],
  );
  assert.equal(result.player.relics[0].description, undefined);
  assert.equal(result.player.relics[0].counter, 2);
  assert.deepEqual(result.combat.hand[0].effects, ['Ethereal']);
  assert.equal(result.selectedChoice.ref, 'special');
  assert.equal(result.deckChanges.added[0].name, 'Dazed');
});

test('deck snapshot comparison preserves distinct duplicate copies across combat refs', async () => {
  const { deckChanges } = await import('../cards.mjs');
  const before = [
    { ref: 'old1', deckRef: 'deck:1', name: 'Strike', description: 'Deal6.' },
    { ref: 'old2', deckRef: 'deck:2', name: 'Strike', description: 'Deal6.' },
  ];
  const after = [
    { ...before[1], ref: 'new2' },
    { ...before[0], ref: 'new1', name: 'Strike+', description: 'Deal9.' },
  ];
  const changes = deckChanges(before, after);
  assert.equal(changes.changed.length, 1);
  assert.equal(changes.changed[0].after.deckRef, 'deck:1');
  assert.deepEqual(changes.removed, []);
  assert.deepEqual(changes.added, []);
});

test('compact cards retain structured values and context without grouping different native effects; facing stays visible', () => {
  const card = {
    ref: 'a',
    name: 'Attack',
    description: 'Deal damage.',
    cost: 1,
    values: {
      Damage: { nativeType: 'DamageVar', unit: 'damage', base: 9, enchanted: 10, preview: 12 },
    },
    valueContext: {
      source: 'cached_native_model',
      modelPile: 'Hand',
      target: 'not_recorded',
      prospectiveDamage: false,
    },
  };
  const game = new Sts2(null, null);
  const view = game.present({
    kind: 'combat',
    player: { facing: 'Right' },
    choices: [],
    combat: {
      hand: [
        card,
        { ...card, ref: 'b', values: { Damage: { ...card.values.Damage, preview: 13 } } },
      ],
      enemies: [{ name: 'Crab', relativePosition: { side: 'Left', behind: true } }],
    },
  });
  assert.equal(view.combat.hand.length, 2);
  assert.deepEqual(view.combat.hand[0].values.Damage, {
    value: 12,
    base: 9,
    enchanted: 10,
    unit: 'damage',
  });
  assert.equal(view.combat.hand[0].valueContext.prospectiveDamage, false);
  assert.equal(view.player.facing, 'Right');
  assert.equal(view.combat.enemies[0].relativePosition.behind, true);
});
