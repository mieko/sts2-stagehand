import assert from 'node:assert/strict';
import { test } from 'node:test';

test('compact output retains native parent correlation without dumping history or service tracking', async () => {
  const { present } = await import('../client/present.mjs');
  const value = {
    status: 'choice_required',
    operation: 'batch-id',
    action: 'action-id',
    results: [{ status: 'completed', action: 'step-id', state: { text: ['historical state'] } }],
    decision: {
      kind: 'card_selection',
      choices: [],
      selection: { min: 1, max: 1 },
      pending: { kind: 'endTurn', action: 'action-id', status: 'choice_required' },
      operation: { operation: 'batch-id', status: 'choice_required' },
    },
  };
  const before = JSON.stringify(value);
  const view = present(value);
  assert.deepEqual(view.pending, {
    kind: 'endTurn',
    action: 'action-id',
    status: 'choice_required',
  });
  assert.deepEqual(view.outcomes, [{ status: 'completed', action: 'step-id' }]);
  assert.doesNotMatch(JSON.stringify(view), /historical state|batch-id/);
  assert.equal(present(value, { diagnostics: true }).operation.operation, 'batch-id');
  assert.equal(JSON.stringify(value), before);
});

test('compact recovery marks retained state as historical when a new position could not be observed', async () => {
  const { present } = await import('../client/present.mjs');
  const view = present({
    status: 'unconfirmed',
    stateScope: 'last_observed',
    decision: { kind: 'rewards', choices: [], player: { hp: 20, gold: 30 } },
  });
  assert.equal(view.stateScope, 'last_observed');
  assert.equal(view.status, 'unconfirmed');
  assert.equal(view.player.gold, 30);
});
import { Sts2 } from '../client/sts2.mjs';
import { button } from './support/fixtures.mjs';

test('ordinary card views omit absent metadata while diagnostics preserve its meaning and raw history', () => {
  const card = {
    ref: 'offer-card',
    name: 'Strike',
    cost: 1,
    starCost: null,
    description: 'Deal 6 damage.',
    canPlay: null,
    legalTargets: null,
    deckRef: null,
    enchantment: null,
    affliction: null,
    replay: 0,
    canUpgrade: true,
    canRemove: true,
    costScope: 'current_card',
    valueContext: { source: 'cached_native_model', prospectiveDamage: false },
  };
  const decision = {
    kind: 'card_reward',
    player: { name: 'Ironclad', stars: 0 },
    choices: [{ ref: 'holder', kind: 'select_card', enabled: true, label: 'Strike', detail: card }],
  };
  const raw = {
    status: 'choice_required',
    decision,
    state: decision,
    results: [{ status: 'completed', state: decision }],
  };
  const before = JSON.stringify(raw);
  const game = new Sts2(null, null);
  const small = game.present(raw);
  for (const key of [
    'starCost',
    'canPlay',
    'legalTargets',
    'deckRef',
    'enchantment',
    'affliction',
    'replay',
    'canUpgrade',
    'canRemove',
    'costScope',
    'valueContext',
  ])
    assert.equal(Object.hasOwn(small.choices[0].model, key), false, key);
  assert.equal(small.choices[0].model.ref, 'offer-card');
  assert.equal(small.choices[0].ref, 'holder');
  assert.equal(small.player.stars, 0);
  const detailed = game.present(raw, { diagnostics: true });
  assert.equal(detailed.choices[0].model.canPlay, null);
  assert.equal(detailed.choices[0].model.replay, 0);
  assert.equal(detailed.choices[0].model.valueContext.prospectiveDamage, false);
  assert.equal(JSON.stringify(raw), before);
  assert.equal(raw.results[0].state.choices[0].detail.starCost, null);
});

test('observed star costs, zero budgets, pets and orbs survive outside their usual character', () => {
  const game = new Sts2(null, null);
  const raw = {
    kind: 'combat',
    choices: [],
    player: {
      name: 'Ironclad',
      hp: 30,
      energy: 0,
      stars: 0,
      relics: [{ name: 'Counter relic', counter: 0, usedUp: false }],
    },
    combat: {
      hand: [
        { ref: 'free', name: 'Free card', description: 'Gain stars.', cost: 0, starCost: 0 },
        {
          ref: 'star',
          name: 'Borrowed card',
          description: 'Deal damage.',
          cost: 0,
          starCost: 3,
          canPlay: false,
          reason: 'NotEnoughStars',
          legalTargets: [],
        },
      ],
      pets: [{ name: 'Osty', hp: 0, alive: false }],
      orbs: [{ name: 'Dark', damage: 0 }],
      orbSlots: 1,
    },
  };
  const small = game.present(raw);
  assert.equal(small.player.stars, 0);
  assert.equal(small.player.energy, 0);
  assert.equal(small.player.relics[0].counter, 0);
  assert.equal(small.player.relics[0].usedUp, false);
  assert.equal(small.combat.hand[0].cost, 0);
  assert.equal(small.combat.hand[0].starCost, 0);
  assert.equal(small.combat.hand[1].starCost, 3);
  assert.equal(small.combat.hand[1].canPlay, false);
  assert.deepEqual(small.combat.hand[1].legalTargets, []);
  assert.equal(small.combat.pets[0].alive, false);
  assert.equal(small.combat.orbs[0].damage, 0);
});

test('current costs and countdowns remain visible and card variants group before presentation', () => {
  const game = new Sts2(null, null);
  const card = {
    name: 'Frantic Escape',
    description: 'Gain Block. Add 1 Sandpit. Costs 1 more this combat.',
    cost: 0,
    starCost: null,
    replay: 0,
    canPlay: true,
    legalTargets: null,
    canUpgrade: true,
    values: { Block: { preview: 12, base: 12, unit: 'block' } },
  };
  const small = game.present({
    kind: 'combat',
    choices: [],
    player: { statuses: [{ name: 'Sandpit', amount: 4 }] },
    combat: {
      hand: [
        { ...card, ref: 'a' },
        { ...card, ref: 'b' },
        {
          ...card,
          ref: 'more',
          cost: 1,
          enchantment: { name: 'Swift', amount: 1, description: 'Draw 3 cards.' },
        },
        {
          ...card,
          ref: 'unknown',
          cost: null,
          replay: null,
          canPlay: false,
          reason: null,
          affliction: { name: 'Afflicted', amount: 0 },
        },
      ],
      enemies: [{ intents: [{ type: 'Unknown' }] }],
    },
  });
  assert.equal(small.combat.hand[0].count, 2);
  assert.deepEqual(small.combat.hand[0].refs, ['a', 'b']);
  assert.equal(small.combat.hand.length, 3);
  assert.equal(small.combat.hand[1].cost, 1);
  assert.equal(small.combat.hand[1].enchantment.name, 'Swift');
  assert.equal(small.combat.hand[2].cost, null);
  assert.equal(small.combat.hand[2].replay, null);
  assert.equal(small.combat.hand[2].reason, null);
  assert.equal(small.combat.hand[2].affliction.amount, 0);
  assert.equal(small.player.statuses[0].amount, 4);
  assert.equal(small.combat.incoming.totalDamage, null);
});

test('selection views hide incidental UI but retain meaningful blocked choices and eligibility', () => {
  const game = new Sts2(null, null);
  const raw = {
    kind: 'card_selection',
    selection: { min: 1, max: 1 },
    combat: { ready: false, hand: [], enemies: [] },
    choices: [
      { ...button('ping', 'NPingButton', 'Ping'), enabled: false },
      { ...button('end', 'NEndTurnButton', 'End Turn'), enabled: false },
      button('peek', 'NPeekButton', 'Peek'),
      {
        ...button('locked', 'NGridCardHolder', 'Curse', {
          name: 'Curse',
          cost: 0,
          canRemove: false,
        }),
        kind: 'select_card',
        enabled: false,
        blockedReason: 'Cannot remove this card',
      },
      {
        ...button('confirm', 'NConfirmButton', 'Confirm'),
        enabled: false,
        blockedReason: 'Select one card',
      },
    ],
  };
  const small = game.present(raw);
  assert.deepEqual(
    small.choices.map((c) => c.ref),
    ['locked', 'confirm'],
  );
  assert.equal(small.choices[0].model.canRemove, false);
  assert.equal(small.choices[0].enabled, false);
  assert.equal(small.choices[1].blockedReason, 'Select one card');
  assert.deepEqual(
    game.present({ ...raw, combat: null }).choices.map((c) => c.ref),
    ['locked', 'confirm'],
  );
  assert.equal(game.present(raw, { diagnostics: true }).choices.length, 5);
});

test('concise presenter groups real copies, preserves variants and includes prices and incoming uncertainty', async () => {
  const { present } = await import('../client/sts2.mjs');
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
  const { present } = await import('../client/sts2.mjs');
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
  const { present } = await import('../client/present.mjs');
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
  const { present } = await import('../client/present.mjs');
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
  const { present } = await import('../client/sts2.mjs');
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
  const { present } = await import('../client/sts2.mjs');
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
  const { deckChanges } = await import('../client/cards.mjs');
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

test('compact cards retain native values and variants with diagnostic context; facing stays visible', () => {
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
  const raw = {
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
  };
  const view = game.present(raw);
  assert.equal(view.combat.hand.length, 2);
  assert.deepEqual(view.combat.hand[0].values.Damage, {
    value: 12,
    base: 9,
    enchanted: 10,
    unit: 'damage',
  });
  assert.equal(view.combat.hand[0].valueContext, undefined);
  assert.equal(
    game.present(raw, { diagnostics: true }).combat.hand[0].valueContext.prospectiveDamage,
    false,
  );
  assert.equal(view.player.facing, 'Right');
  assert.equal(view.combat.enemies[0].relativePosition.behind, true);
});
