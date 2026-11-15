import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { test } from 'node:test';
import { Sts2 } from './sts2.mjs';

test('card inspection accepts observed reward, shop, selection and compact models without driving or rereading', async () => {
  const calls = [];
  const projected = { ref: 'card:known', name: 'Know Thy Place', cost: 0,
    description: 'Apply 1 Weak and 1 Vulnerable. Exhaust.',
    upgrade: { ref: null, name: 'Know Thy Place+', cost: 0,
      description: 'Apply 1 Weak and 1 Vulnerable.' } };
  const game = new Sts2(new EventEmitter(), {
    async invoke(method, params) { calls.push({ method, params }); return projected; },
    async observe() { throw new Error('Inspection must not normalize or reread state'); },
  });
  const card = { ref: 'card:known', name: 'Know Thy Place' };
  for (const source of [card, 'card:known',
    { ref: 'reward-widget', cardRef: card.ref, detail: card },
    { ref: 'shop-widget', detail: { item: card } },
    { ref: 'selection-widget', detail: card },
    { ref: 'compact-choice', model: card }]) {
    assert.equal(await game.inspectCard(source, { upgrades: true }), projected);
  }
  assert.deepEqual(calls, Array.from({ length: 6 }, () => ({
    method: 'inspect', params: { kind: 'card', card: 'card:known', upgrades: true },
  })));
  assert.equal(await game.inspectCard(card), projected);
  assert.equal(calls.at(-1).params.upgrades, false);
});

test('failed card inspection stays outside the mutation queue and preserves native reference rejection', async () => {
  const game = new Sts2(new EventEmitter(), {
    async invoke() { throw new Error('Card reference is unknown'); },
    async observe() { return { value: { pending: null, choices: [] } }; },
  });
  await assert.rejects(game.inspectCard({ ref: 'old-card' }, { upgrades: true }), /Card reference is unknown/);
  await assert.rejects(game.inspectCard({ name: 'Know Thy Place' }), /card or offer.*reference/i);
  await game.resumeQueue();
});

function fixture({ finish = true, finishStatus = 'completed' } = {}) {
  const client = new EventEmitter();
  const listeners = new Set(), order = [], cards = [];
  let pending, next = 0;
  const endpoint = {
    async observe() { return { value: { screen: 'NCombatRoom', text: [], ready: pending?.status !== 'running', combat: {
      ready: pending?.status !== 'running', hand: [{ ref: 'card' + next, name: 'Strike', cost: '1', canPlay: true }],
      enemies: [{ ref: 'enemy', name: 'Dummy' }] }, pending, choices: [] } }; },
    async subscribe(name, listener) { listeners.add(listener); return async () => listeners.delete(listener); },
    async invoke(method, { kind, card }) {
      if (card) cards.push(card);
      const action = String(++next);
      order.push('start:' + kind);
      pending = { action, kind, status: 'running' };
      if (finish) setTimeout(() => {
        order.push('finish:' + kind);
        pending = { action, kind, status: finishStatus };
        for (const listener of listeners) listener();
      }, 15);
      return { action, status: 'accepted' };
    },
  };
  return { game: new Sts2(client, endpoint), order, cards, listeners, client };
}

test('queued operations wait for game completion; observations remain available', async () => {
  const { game, order, listeners } = fixture();
  const first = game.play({ ref: 'card' }, { ref: 'enemy' });
  const second = game.endTurn();
  await new Promise(resolve => setTimeout(resolve, 2));
  assert.equal((await game.state()).pending.kind, 'play');
  assert.equal((await first).status, 'completed');
  assert.equal((await second).status, 'completed');
  assert.deepEqual(order, ['start:play', 'finish:play', 'start:endTurn', 'finish:endTurn']);
  assert.equal(listeners.size, 0);
});

test('a card batch resolves names against each completed action, not an old hand snapshot', async () => {
  const { game, order, cards } = fixture();
  const result = await game.playCards([{ card: 'Strike', target: 'Dummy' }, { card: 'Strike', target: 'Dummy' }]);
  assert.equal(result.status, 'completed');
  assert.deepEqual(cards, ['card0', 'card1']);
  assert.deepEqual(order, ['start:play', 'finish:play', 'start:play', 'finish:play']);
});

test('an unplayable named card reaches native validation rather than appearing missing', async () => {
  const client = new EventEmitter();
  const endpoint = {
    async observe() { return { value: { screen: 'NCombatRoom', choices: [], combat: {
      hand: [{ name: 'Dismantle+', ref: 'card', canPlay: false, reason: 'NotEnoughEnergy' }] } } }; },
    async invoke(method, request) {
      assert.equal(request.card, 'card'); throw new Error('Card cannot be played: NotEnoughEnergy');
    },
  };
  await assert.rejects(new Sts2(client, endpoint).play('Dismantle+'), /NotEnoughEnergy/);
});

test('timeout stops queued commands without retrying or claiming cancellation', async () => {
  const { game, order, listeners } = fixture({ finish: false });
  const first = game.endTurn({ timeoutMs: 15 });
  const second = game.endTurn();
  const results = await Promise.allSettled([first, second]);
  assert.match(results[0].reason.message, /timed out.*no game action was cancelled or retried/);
  assert.match(results[1].reason.message, /queue stopped/);
  assert.deepEqual(order, ['start:endTurn']);
  assert.equal(listeners.size, 0);
  await assert.rejects(game.resumeQueue(), /still running/);
});

test('card batch stops at a new player choice and reports unsubmitted cards', async () => {
  const { game, cards } = fixture({ finishStatus: 'choice_required' });
  const result = await game.playCards([{ card: 'Strike', target: 'Dummy' }, 'Strike']);
  assert.equal(result.status, 'choice_required');
  assert.deepEqual(cards, ['card0']);
  assert.deepEqual(result.remaining, ['Strike']);
});

test('game cancellation stops subsequent queued operations', async () => {
  const { game, order } = fixture({ finishStatus: 'cancelled' });
  const results = await Promise.allSettled([game.endTurn(), game.endTurn()]);
  assert.equal(results[0].reason.outcome.status, 'cancelled');
  assert.match(results[1].reason.message, /queue stopped/);
  assert.deepEqual(order, ['start:endTurn', 'finish:endTurn']);
});

test('shop batch waits for native purchase completion and uses stable offer references', async () => {
  const client = new EventEmitter(), listeners = new Set(), order = [];
  let pending, gold = 100;
  const choices = ['Feed', 'Fire Potion'].map((label, i) => ({ ref: 'widget' + i,
    offerRef: 'offer' + i, label, enabled: true, type: i ? 'NMerchantPotion' : 'NMerchantCard' }));
  const endpoint = {
    async observe() { return { value: { screen: 'NMerchantInventory', ready: !pending || pending.status === 'completed',
      choices, player: { gold }, pending, text: [] } }; },
    async subscribe(name, listener) { listeners.add(listener); return async () => listeners.delete(listener); },
    async invoke(method, { kind, offer }) {
      assert.equal(kind, 'buy'); order.push('start:' + offer);
      pending = { action: offer, kind, status: 'running' };
      setTimeout(() => { gold -= 20; pending = { ...pending, status: 'completed' };
        order.push('finish:' + offer); for (const listener of listeners) listener(); }, 10);
      return { action: offer, status: 'accepted' };
    },
  };
  const result = await new Sts2(client, endpoint).buy([choices[0], 'Fire Potion']);
  assert.equal(result.status, 'completed'); assert.equal(result.state.player.gold, 60);
  assert.deepEqual(order, ['start:offer0', 'finish:offer0', 'start:offer1', 'finish:offer1']);
  assert.equal(listeners.size, 0);
});

test('card selection uses the declared grid signal rather than its child hitbox', async () => {
  const client = new EventEmitter();
  let selected = false;
  const choice = { ref: 'hitbox', label: 'Dismantle', enabled: true, type: 'NGridCardHolder',
    activation: { target: 'grid', signal: 'HolderPressed', argument: 'holder' } };
  client.object = id => ({ id, async call(method, signal, argument) {
    assert.equal(id, 'grid'); assert.equal(method, 'emit_signal');
    assert.equal(signal, 'HolderPressed'); assert.equal(argument.id, 'holder'); selected = true;
  } });
  const endpoint = {
    async observe() { return { value: { ready: true, screen: selected ? 'CardPreview' : 'Select', choices: selected ? [] : [choice] } }; },
    async subscribe() { return async () => {}; },
  };
  const game = new Sts2(client, endpoint);
  assert.equal((await game.pickCard('Dismantle')).status, 'observed_change');
  assert.equal(selected, true);
});

function uiFixture(initial, activate) {
  const client = new EventEmitter(), listeners = new Set(), calls = [];
  let current = initial;
  const update = state => { current = state; for (const notify of listeners) notify(); };
  client.object = id => ({ id,
    async inspect() { return { methods: [{ name: 'ForceClick' }] }; },
    async call(method) { assert.equal(method, 'ForceClick'); calls.push(id); activate(id, update); },
  });
  const endpoint = {
    async observe() { return { value: current }; },
    async subscribe(event, listener) { listeners.add(listener); return async () => listeners.delete(listener); },
  };
  return { game: new Sts2(client, endpoint), calls, update };
}

const button = (ref, type, label, detail) => ({ ref, type, label, detail, enabled: true });

test('a Proceed label on an unfinished event is still an agent decision', async () => {
  const { game, calls } = uiFixture({ screen: 'NEventRoom', ready: true, text: [],
    event: { finished: false }, choices: [button('proceed', 'NEventOptionButton', 'Proceed', { isProceed: true })] },
  () => assert.fail('Must not automatically choose an unfinished event option'));
  assert.equal((await game.decision()).kind, 'event');
  assert.deepEqual(calls, []);
});

test('a shopkeeper potion target prevents automatic inventory entry', async () => {
  const { game, calls } = uiFixture({ screen: 'NMerchantRoom', ready: true, text: [],
    shop: { canBrowse: true, control: 'merchant', open: false },
    player: { potions: [{ ref: 'foul', usable: true, targets: [{ ref: 'keeper', kind: 'shopkeeper' }] }] },
    choices: [button('keeper', 'NMerchantButton', 'Shopkeeper')] },
  () => assert.fail('Throwing at the shopkeeper is a real decision before entry'));
  assert.equal((await game.decision()).kind, 'shop_entrance');
  assert.deepEqual(calls, []);
});

test('a queued shopkeeper throw stops if the first throw starts combat', async () => {
  const client = new EventEmitter();
  const target = { ref: 'keeper', kind: 'shopkeeper' };
  const first = { ref: 'foul1', name: 'Foul Potion', usable: true, targets: [target] };
  const second = { ref: 'foul2', name: 'Foul Potion', usable: true, targets: [target] };
  let combat = false, requests = 0;
  const endpoint = {
    async observe() { return { value: { screen: combat ? 'NCombatRoom' : 'NEventRoom', ready: true,
      text: [], choices: [], combat: combat ? { enemies: [] } : null,
      player: { potions: combat ? [{ ...second, targets: [] }] : [first, second] },
      pending: combat ? { action: 'throw', kind: 'potion', status: 'completed' } : null } }; },
    async subscribe() { return async () => {}; },
    async invoke(method, request) {
      requests++; assert.equal(request.kind, 'potion'); assert.equal(request.target, 'keeper');
      combat = true; return { action: 'throw', status: 'accepted' };
    },
  };
  const game = new Sts2(client, endpoint);
  const results = await Promise.allSettled([game.throwPotionAtShopkeeper(first), game.throwPotionAtShopkeeper(second)]);
  assert.equal(results[0].value.status, 'completed');
  assert.equal(results[0].value.decision.kind, 'combat');
  assert.match(results[1].reason.message, /No shopkeeper target/);
  assert.equal(requests, 1);
});

test('an unavailable potion reward reports its blocker before sending a click', async () => {
  const { game, calls } = uiFixture({ screen: 'NRewardsScreen', ready: true, text: [],
    choices: [{ ...button('potion', 'NRewardButton', 'Potion of Binding'), enabled: false,
      blockedReason: 'Potion inventory is full; use or discard a potion first' }] },
  () => assert.fail('A full inventory cannot claim another potion'));
  await assert.rejects(game.choose('Potion of Binding'), /Potion inventory is full/);
  assert.deepEqual(calls, []);
});

test('claiming the last requested reward completes even when it opens another screen', async () => {
  const { game } = uiFixture({ screen: 'NRewardsScreen', ready: true, text: [],
    choices: [button('gold', 'NRewardButton', '17 Gold')] },
  (id, update) => update({ screen: 'NMapScreen', ready: true, text: [], choices: [] }));
  const result = await game.claimRewards(['17 Gold']);
  assert.equal(result.status, 'completed'); assert.deepEqual(result.remaining, []);
  assert.equal(result.decision.kind, 'map');
});

test('explicitly skipping the final reward leaves without a second skip decision', async () => {
  const { game, calls } = uiFixture({ screen: 'NCardRewardSelectionScreen', ready: true, text: [],
    choices: [button('card-skip', 'NCardRewardSkipButton', 'Skip')] }, (id, update) => {
    if (id === 'card-skip') update({ screen: 'NRewardsScreen', ready: true, text: [],
      rewards: { complete: false, remaining: 1, unresolved: 0 },
      choices: [{ ...button('leave', 'NProceedButton', 'Skip'), isSkip: true },
        { ...button('card', 'NRewardButton', 'Add a card'), skipped: true }] });
    else update({ screen: 'NMapScreen', ready: true, text: [], choices: [] });
  });
  const result = await game.choose('Skip');
  assert.deepEqual(calls, ['card-skip', 'leave']);
  assert.equal(result.decision.kind, 'map');
  assert.deepEqual(result.automaticSteps, ['leave_resolved_rewards']);
});

test('finishing results waits through summary animation and uses the actual menu button', async () => {
  const { game, calls } = uiFixture({ screen: 'NGameOverScreen', ready: true,
    text: ['Victory...?'], result: { win: true, score: 1234 },
    choices: [button('root', 'NGameOverScreen', 'Screen'), button('continue', 'NGameOverContinueButton', 'Continue')] },
  (id, update) => {
    if (id === 'continue') {
      update({ screen: 'NGameOverScreen', ready: true, text: ['Scoring'], choices: [button('root', 'NGameOverScreen', 'Screen')] });
      setTimeout(() => update({ screen: 'NGameOverScreen', ready: true, text: ['Score: 1234'],
        choices: [button('root', 'NGameOverScreen', 'Screen'), button('menu', 'NReturnToMainMenuButton', 'Unlock')] }), 15);
    } else { assert.equal(id, 'menu'); update({ screen: 'NTimelineScreen', ready: true, text: [], choices: [] }); }
  });
  const result = await game.finishResults();
  assert.deepEqual(calls, ['continue', 'menu']);
  assert.equal(result.runResult.win, true);
  assert.equal(result.decision.screen, 'NTimelineScreen');
  assert.ok(result.outcomeText.includes('Victory...?'));
});

for (const changed of ['enabled', 'result']) {
  test(`same-screen UI completion notices ${changed} changes without new controls`, async () => {
    const choices = [button('continue', 'NGameOverContinueButton', 'Continue'),
      { ...button('unlock', 'NReturnToMainMenuButton', 'Unlock'), enabled: false }];
    const before = { screen: 'NGameOverScreen', ready: true, choices, text: [],
      result: { win: true, phase: 'outcome', earnedEpochCount: 4 } };
    const { game, calls } = uiFixture(before, (_, update) => update({ ...before,
      choices: changed === 'enabled' ? choices.map(c => ({ ...c, enabled: !c.enabled })) : choices,
      result: changed === 'result' ? { ...before.result, phase: 'earned_epochs', earnedEpochCount: 5 } : before.result }));
    const result = await game.choose(choices[0], { timeoutMs: 20 });
    assert.equal(result.status, 'observed_change'); assert.deepEqual(calls, ['continue']);
  });
}

test('finishResults preserves the latest earned counts across a same-screen summary before explicit Timeline reveals', async () => {
  const choices = [button('continue', 'NGameOverContinueButton', 'Continue'),
    { ...button('unlock', 'NReturnToMainMenuButton', 'Unlock'), enabled: false }];
  const { game, calls } = uiFixture({ screen: 'NGameOverScreen', ready: true, text: [], choices,
    result: { win: true, score: 1396, phase: 'outcome', earnedEpochCount: 4 } }, (id, update) => {
    if (id === 'continue') update({ screen: 'NGameOverScreen', ready: true, text: [],
      choices: choices.map(c => ({ ...c, enabled: !c.enabled })),
      result: { win: true, score: 1396, phase: 'earned_epochs', earnedEpochCount: 5, pendingEpochReveals: 5, scoreUnlocksRemaining: 12 } });
    else update({ screen: 'NTimelineScreen', ready: true, text: [], choices: [
      button('epoch', 'NEpochSlot', 'An earned epoch', { state: 'Obtained' })] });
  });
  const result = await game.finishResults({ timeoutMs: 20 });
  assert.equal(result.runResult.earnedEpochCount, 5); assert.equal(result.runResult.pendingEpochReveals, 5);
  assert.equal(result.decision.kind, 'timeline'); assert.deepEqual(calls, ['continue', 'unlock']);
});

test('finishResults entered during scoring preserves counts that finish before the direct Timeline click', async () => {
  const locked = { ...button('unlock', 'NReturnToMainMenuButton', 'Unlock'), enabled: false };
  const initial = { screen: 'NGameOverScreen', ready: false, text: [], choices: [locked],
    result: { win: true, phase: 'summary_animation', earnedEpochCount: 4 } };
  const { game, calls, update } = uiFixture(initial, () => update({ screen: 'NTimelineScreen', ready: true, text: [], choices: [] }));
  const timer = setTimeout(() => update({ ...initial, ready: true, choices: [{ ...locked, enabled: true }],
    result: { win: true, phase: 'earned_epochs', earnedEpochCount: 5, pendingEpochReveals: 5 } }), 5);
  try {
    const result = await game.finishResults({ timeoutMs: 50 });
    assert.equal(result.runResult.earnedEpochCount, 5); assert.equal(result.runResult.phase, 'earned_epochs');
    assert.deepEqual(calls, ['unlock']);
  } finally { clearTimeout(timer); }
});

test('epochs remain explicit decisions, including when only one reveal is available', async () => {
  const epoch = button('epoch', 'NEpochSlot', 'Invitation', { id: 'NEOW', state: 'Obtained' });
  const { game, calls } = uiFixture({ screen: 'NTimelineScreen', ready: true, text: [],
    timeline: { busy: false }, choices: [epoch] },
  (id, update) => update({ screen: 'NEpochInspectScreen', ready: true, text: ['Revealed story'],
    epoch: { title: 'Invitation', description: 'Revealed story' }, choices: [] }));
  const decision = await game.decision();
  assert.equal(decision.kind, 'timeline'); assert.equal(decision.timeline.epochs[0].kind, 'reveal_epoch');
  assert.deepEqual(calls, []);
  const result = await game.revealEpoch(decision.timeline.epochs[0]);
  assert.deepEqual(calls, ['epoch']); assert.equal(result.decision.kind, 'epoch');
});

test('explicit menu navigation does not reveal pending epochs', async () => {
  const client = new EventEmitter(), calls = [];
  let inTimeline = true;
  client.object = id => ({ async call(method) {
    assert.equal(id, 'stack'); calls.push(method);
    if (method === 'Peek') return inTimeline ? { id: 'timeline' } : null;
    assert.equal(method, 'Pop'); inTimeline = false;
  } });
  const endpoint = {
    async observe() { return { value: { screen: inTimeline ? 'NTimelineScreen' : 'NMainMenu', ready: true,
      text: [], choices: [], timeline: inTimeline ? { busy: false } : null,
      navigation: { atMainMenu: !inTimeline, canReturnToMenu: true, menuStack: 'stack' } } }; },
    async subscribe() { return async () => {}; },
  };
  const result = await new Sts2(client, endpoint).returnToMenu();
  assert.deepEqual(calls, ['Peek', 'Pop', 'Peek']);
  assert.equal(result.decision.navigation.atMainMenu, true);
});

test('unlock acknowledgement waits for its actual confirmation to become ready', async () => {
  const confirm = button('confirm', 'NUnlockConfirmButton', 'Confirm');
  const { game, calls, update } = uiFixture({ screen: 'NUnlockCardsScreen', ready: false, text: [],
    choices: [{ ...confirm, enabled: false }] },
  (id, update) => update({ screen: 'NTimelineScreen', ready: true, text: [], choices: [] }));
  setTimeout(() => update({ screen: 'NUnlockCardsScreen', ready: true, text: [], choices: [confirm] }), 15);
  const result = await game.confirmUnlock();
  assert.deepEqual(calls, ['confirm']); assert.equal(result.decision.kind, 'timeline');
});

test('finished-event progression preserves outcome and waits for the next settled decision', async () => {
  const { game, calls } = uiFixture({ screen: 'NEventRoom', ready: true, text: [], event: { finished: false },
    choices: [button('eat', 'NEventOptionButton', 'Eat')] }, (id, update) => {
    if (id === 'eat') update({ screen: 'NEventRoom', ready: true, text: [],
      event: { finished: true, description: 'You ate the egg.' },
      choices: [button('exit', 'NEventOptionButton', 'Leave', { isProceed: true })] });
    else {
      update({ screen: 'NMapScreen', ready: false, choices: [], text: [] });
      setTimeout(() => update({ screen: 'NMapScreen', ready: true, choices: [], text: [] }), 15);
    }
  });
  const result = await game.choose('Eat');
  assert.deepEqual(calls, ['eat', 'exit']);
  assert.deepEqual(result.outcomeText, ['You ate the egg.']);
  assert.equal(result.decision.kind, 'map');
  assert.equal(result.decision.ready, true);
});

test('mandatory chest opening yields the offer without taking it', async () => {
  const { game, calls } = uiFixture({ screen: 'NTreasureRoom', ready: true, text: [],
    choices: [button('chest', 'NTreasureButton', 'Chest'), { ...button('exit', 'NProceedButton', 'Proceed'), enabled: false }] },
  (id, update) => {
    assert.equal(id, 'chest');
    const choices = [button('relic', 'NTreasureRoomRelicHolder', 'Gorget', { description: 'Gain Plating.' }),
      { ...button('exit', 'NProceedButton', 'Skip'), enabled: false }];
    update({ screen: 'NTreasureRoom', ready: true, text: [], choices });
    setTimeout(() => update({ screen: 'NTreasureRoom', ready: true, text: [],
      choices: [choices[0], { ...choices[1], enabled: true }] }), 15);
  });
  const decision = await game.decision();
  assert.deepEqual(calls, ['chest']);
  assert.equal(decision.offers[0].label, 'Gorget');
  assert.equal(decision.canDecline, true);
});

test('travel selects a real map point even when its animated widget is absent from choices', async () => {
  const client = new EventEmitter(), listeners = new Set();
  let state = { screen: 'NMapScreen', act: 1, floor: 10, ready: true, choices: [], text: [],
    mapControl: 'map', map: [{ ref: 'room', state: 'Travelable' }] };
  client.object = id => ({ id, async call(method, target) {
    assert.equal(id, 'map'); assert.equal(method, 'OnMapPointSelectedLocally'); assert.equal(target.id, 'room');
    state = { screen: 'NCombatRoom', act: 1, floor: 11, ready: true, combat: {}, choices: [], text: [] };
    for (const notify of listeners) notify();
  } });
  const endpoint = {
    async observe() { return { value: state }; },
    async subscribe(event, listener) { listeners.add(listener); return async () => listeners.delete(listener); },
  };
  const game = new Sts2(client, endpoint);
  const result = await game.travel({ ref: 'room' });
  assert.equal(result.status, 'completed');
  assert.equal(result.decision.kind, 'combat');
});

test('normalization follows game-owned advancement without repeating a vanished step', async () => {
  const client = new EventEmitter();
  let state = { screen: 'NTreasureRoom', ready: true, text: [],
    choices: [button('relic', 'NTreasureRoomRelicHolder', 'Gorget')] };
  let justCollected = false;
  const calls = [];
  client.object = id => ({ id,
    async inspect() { return { methods: [{ name: 'ForceClick' }] }; },
    async call() {
      calls.push(id); assert.equal(id, 'relic');
      state = { screen: 'NTreasureRoom', ready: true, text: [],
        choices: [{ ...button('exit', 'NProceedButton', 'Proceed'), isSkip: false }] };
      justCollected = true;
    },
  });
  const endpoint = {
    async observe() {
      const result = state;
      if (justCollected) { justCollected = false; state = { screen: 'NMapScreen', ready: true, choices: [], text: [] }; }
      return { value: result };
    },
    async subscribe() { return async () => {}; },
  };
  const result = await new Sts2(client, endpoint).takeRelic('Gorget');
  assert.deepEqual(calls, ['relic']);
  assert.equal(result.decision.kind, 'map');
});


test('startRun follows the native Standard button after the first run', async () => {
  const { game, calls } = uiFixture({ screen: 'NGame', ready: true, text: [],
    choices: [button('singleplayer', 'NMainMenuTextButton', 'Singleplayer')] }, (id, update) => {
    if (id === 'singleplayer') update({ screen: 'NSingleplayerSubmenu', ready: true, text: [],
      choices: [button('standard', 'NSubmenuButton', 'Localized standard title'),
        button('custom', 'NSubmenuButton', 'Localized custom title')] });
    else if (id === 'standard') update({ screen: 'NCharacterSelectScreen', ready: true, text: [],
      choices: [{ ...button('ironclad', 'NCharacterSelectButton', 'The Ironclad', { id: 'IRONCLAD' }), selected: true },
        button('embark', 'NConfirmButton', 'Embark')] });
    else { assert.equal(id, 'embark'); update({ screen: 'NEventRoom', ready: true, player: {},
      event: { finished: false }, text: [], choices: [button('blessing', 'NEventOptionButton', 'Blessing')] }); }
  });
  const object = game.client.object;
  game.client.object = id => ({ ...object(id), async get(property) {
    assert.equal(property, 'name'); return id === 'standard' ? 'StandardButton' : 'CustomButton';
  } });
  const result = await game.startRun({ character: 'IRONCLAD' });
  assert.deepEqual(calls, ['singleplayer', 'standard', 'embark']);
  assert.equal(result.status, 'completed');
});

test('startRun returns an intervening tutorial without acknowledging it', async () => {
  const { game, calls } = uiFixture({ screen: 'NAscensionSingleplayerFtue', ready: true, text: ['Ascension'],
    choices: [button('confirm', 'NFtueConfirmButton', 'Got it!')] },
  () => assert.fail('Tutorial acknowledgement must remain explicit'));
  const result = await game.startRun({ character: 'IRONCLAD' });
  assert.equal(result.status, 'choice_required'); assert.equal(result.decision.kind, 'tutorial');
  assert.deepEqual(calls, []);
});


test('character selection uses the native Select operation rather than a click signal', async () => {
  const client = new EventEmitter(), calls = [];
  let state = { screen: 'NCharacterSelectScreen', ready: true, text: [], choices: [
    { ...button('ironclad', 'NCharacterSelectButton', 'The Ironclad'), selected: true },
    { ...button('silent', 'NCharacterSelectButton', 'The Silent'), selected: false }] };
  client.object = id => ({ async inspect() { return { methods: [{ name: 'ForceClick' }, { name: 'Select' }] }; },
    async call(method) {
      calls.push(method);
      if (method === 'Select') state = { ...state, choices: state.choices.map(c => ({ ...c, selected: c.ref === id })) };
    } });
  const endpoint = { async observe() { return { value: state }; }, async subscribe() { return async () => {}; } };
  const result = await new Sts2(client, endpoint).choose('The Silent', { timeoutMs: 20 });
  assert.equal(result.status, 'observed_change');
  assert.deepEqual(calls, ['Select']);
  assert.equal(result.decision.choices.find(c => c.ref === 'silent').selected, true);
});


test('selection failures retain applied selections and the unsubmitted tail', async () => {
  const { game, calls } = uiFixture({ screen: 'NCardGridSelectionScreen', ready: true, text: [],
    choices: [button('valid', 'NGridCardHolder', 'Valid')] }, (id, update) => update({
      screen: 'NCardGridSelectionScreen', ready: true, text: [],
      choices: [{ ...button('valid', 'NSelectedHandCardHolder', 'Valid'), selected: true }] }));
  await assert.rejects(game.selectCards(['Valid', 'Missing']), error => {
    assert.equal(error.results.length, 1); assert.deepEqual(error.remaining, ['Missing']); return true;
  });
  assert.deepEqual(calls, ['valid']);
});

test('selection confirmation failure retains the already-applied selection', async () => {
  const { game } = uiFixture({ screen: 'NCardGridSelectionScreen', ready: true, text: [],
    choices: [button('valid', 'NGridCardHolder', 'Valid')] }, (id, update) => update({
      screen: 'NCardGridSelectionScreen', ready: true, text: [],
      choices: [button('confirm', 'NConfirmButton', 'Confirm')] }));
  const object = game.client.object;
  game.client.object = id => id === 'confirm' ? { async inspect() { return { methods: [{ name: 'ForceClick' }] }; }, async call() { throw new Error('Confirmation unavailable'); } } : object(id);
  await assert.rejects(game.selectCards('Valid'), error => {
    assert.match(error.message, /Confirmation unavailable/); assert.equal(error.results.length, 1);
    assert.deepEqual(error.remaining, []); return true;
  });
});

test('play batch retains played cards and nested selection progress on selection failure', async () => {
  const client = new EventEmitter();
  const submitted = [];
  let state = { screen: 'NCombatRoom', ready: true, text: [], choices: [], combat: {
    hand: [{ ref: 'setup', name: 'Setup', cost: 0 }, { ref: 'chooser', name: 'Choose', cost: 0 }] } };
  const endpoint = {
    async observe() { return { value: state }; }, async subscribe() { return async () => {}; },
    async invoke(method, { card }) {
      submitted.push(card);
      if (card === 'setup') state = { ...state, pending: { action: card, status: 'completed' } };
      else state = { screen: 'NCardGridSelectionScreen', ready: true, text: [],
        choices: [button('valid', 'NGridCardHolder', 'Valid')], pending: { action: card, status: 'choice_required' } };
      return { action: card, status: 'accepted' };
    },
  };
  client.object = () => ({ async inspect() { return { methods: [{ name: 'ForceClick' }] }; }, async call() {
    state = { ...state, choices: [button('other', 'NGridCardHolder', 'Other')] };
  } });
  await assert.rejects(new Sts2(client, endpoint).playCards(['Setup',
    { card: 'Choose', select: ['Valid', 'Missing'] }, 'Later']), error => {
    assert.equal(error.results.length, 2); assert.equal(error.results[0].status, 'completed');
    assert.equal(error.results[1].action, 'chooser');
    assert.equal(error.results[1].selection.results.length, 1);
    assert.deepEqual(error.results[1].selection.remaining, ['Missing']);
    assert.deepEqual(error.remaining, ['Later']); return true;
  });
  assert.deepEqual(submitted, ['setup', 'chooser']);
});

for (const [field, alternatives] of [['starCost', [1, 2]], ['replay', [1, 2]]]) {
  test(`named-card ambiguity includes different ${field} values`, async () => {
    const endpoint = { async observe() { return { value: { screen: 'NCombatRoom', choices: [], combat: {
      hand: alternatives.map((value, index) => ({ ref: String(index), name: 'Same', cost: 0,
        description: 'Same description', [field]: value })) } } }; },
      async invoke() { assert.fail('Ambiguous names must not submit a play'); } };
    await assert.rejects(new Sts2(new EventEmitter(), endpoint).play('Same'), /different variants/);
  });
}


test('end-turn failure retains the preceding successful card batch', async () => {
  const client = new EventEmitter();
  let state = { screen: 'NCombatRoom', ready: true, choices: [], combat: {
    hand: [{ name: 'Defend', ref: 'defend', cost: 1 }] } };
  const endpoint = { async observe() { return { value: state }; }, async subscribe() { return async () => {}; },
    async invoke(method, request) {
      if (request.kind === 'endTurn') throw new Error('Turn request rejected');
      state = { ...state, pending: { action: 'defend', status: 'completed' } };
      return { action: 'defend', status: 'accepted' };
    } };
  await assert.rejects(new Sts2(client, endpoint).playCards(['Defend'], { endTurn: true }), error => {
    assert.equal(error.results.length, 1); assert.equal(error.results[0].status, 'completed');
    assert.deepEqual(error.remaining, []); return true;
  });
});


for (const kind of ['play', 'buy', 'reward']) {
  test(`${kind} batch reuses its completed snapshot without a redundant fallible read`, async () => {
    const client = new EventEmitter();
    let completed = false, delivered = false;
    const initial = kind === 'play'
      ? { screen: 'NCombatRoom', choices: [], combat: { hand: [{ name: 'Defend', ref: 'card', cost: 1 }] } }
      : { screen: kind === 'buy' ? 'NMerchantInventory' : 'NRewardsScreen', choices: [
        { ...button('item', kind === 'buy' ? 'NMerchantCard' : 'NRewardButton', 'Item'), offerRef: 'offer' }] };
    const endpoint = { async observe() {
      if (delivered) throw new Error('Connection lost after completed observation');
      if (completed) delivered = true;
      return { value: { ...initial, ready: true, text: [], ...(completed ? {
        pending: { action: 'action', status: 'completed' },
        ...(kind === 'reward' ? { choices: [button('other', 'NRewardButton', 'Other')],
          rewards: { remaining: 1, unresolved: 1, complete: false } } : {}) } : {}) } };
    }, async subscribe() { return async () => {}; }, async invoke() { completed = true;
      return { action: 'action', status: 'accepted' }; } };
    client.object = () => ({ async inspect() { return { methods: [{ name: 'ForceClick' }] }; },
      async call() { completed = true; } });
    const game = new Sts2(client, endpoint);
    const result = kind === 'play' ? await game.playCards(['Defend'])
      : kind === 'buy' ? await game.buy(['Item']) : await game.claimRewards(['Item']);
    assert.equal(result.status, 'completed'); assert.equal(result.results.length, 1);
  });
}


for (const batch of [false, true]) {
  test(`${batch ? 'play batch endTurn' : 'endTurn'} returns an enemy-turn choice and retains its completion identity`, async () => {
    const client = new EventEmitter(), requests = [];
    let state = { screen: 'NCombatRoom', ready: true, text: [], choices: [], combat: {
      hand: [{ name: 'Defend', ref: 'defend', cost: 1 }], side: 'Player', round: 1 } };
    const endpoint = { async observe() { return { value: state }; }, async subscribe() { return async () => {}; },
      async invoke(method, { kind }) {
        requests.push(kind);
        if (kind === 'play') {
          state = { ...state, pending: { action: 'card', kind, status: 'completed' } };
          return { action: 'card', status: 'accepted' };
        }
        state = { screen: 'NChooseACardSelectionScreen', ready: true, text: ['Choose a curse'],
          combat: { side: 'Enemy', phase: 'None', round: 1, ready: false },
          choices: [button('curse', 'NGridCardHolder', 'Disintegration')],
          pending: { action: 'turn', kind: 'endTurn', status: 'choice_required' } };
        return { action: 'turn', status: 'accepted' };
      } };
    client.object = () => ({ async inspect() { return { methods: [{ name: 'ForceClick' }] }; },
      async call() { state = { screen: 'NCombatRoom', ready: true, text: [], choices: [],
        combat: { side: 'Player', round: 2, ready: true },
        pending: { action: 'turn', kind: 'endTurn', status: 'completed' } }; } });
    const game = new Sts2(client, endpoint);
    const turn = batch ? await game.playCards(['Defend'], { endTurn: true, timeoutMs: 20 })
      : await game.endTurn({ timeoutMs: 20 });
    assert.equal(turn.status, 'choice_required'); assert.equal(turn.action, 'turn');
    assert.equal(turn.decision.kind, 'card_selection');
    assert.equal(turn.state.pending.status, 'choice_required');
    assert.deepEqual(requests, batch ? ['play', 'endTurn'] : ['endTurn']);
    await game.pickCard('Disintegration');
    const completed = await game.waitForAction(turn.action, { timeoutMs: 20 });
    assert.equal(completed.status, 'completed'); assert.equal(completed.action, 'turn');
    assert.equal(completed.decision.combat.round, 2);
    assert.deepEqual(requests, batch ? ['play', 'endTurn'] : ['endTurn']);
  });
}


for (const manual of [false, true]) {
  test(`host-reported turn-start choice resolves two cards and retains the original endTurn (${manual ? 'manual' : 'automatic'} confirmation)`, async () => {
    const client = new EventEmitter(), calls = [], selected = new Set();
    let done = false;
    const cards = ['a', 'b'].map(ref => ({ ref, cardRef: ref, label: ref === 'a' ? 'Astral Pulse+' : 'Defend',
      type: 'NGridCardHolder', enabled: true, activation: { kind: 'select_card', card: ref } }));
    const endpoint = {
      async observe() { return { value: { screen: done ? 'NCombatRoom' : 'NCombatPileCardSelectScreen',
        ready: true, text: [], combat: { round: 3, side: 'Player', phase: done ? 'Play' : 'Start', ready: done, hand: [] },
        pending: { action: 'original-turn', kind: 'endTurn', status: done ? 'completed' : 'choice_required' },
        selection: done ? null : { prompt: 'Choose 2 cards to add into your Hand.', min: 2, max: 2,
          confirm: manual, selected: [...selected].map(ref => ({ ref })) },
        choices: done ? [] : [...cards.map(c => ({ ...c, selected: selected.has(c.ref) })),
          { ref: 'confirm', type: 'NConfirmButton', label: 'Confirm', enabled: manual && selected.size === 2 }] } }; },
      async subscribe() { return async () => {}; },
      async invoke(method, { card }) {
        assert.equal(method, 'select_card', 'No second endTurn request');
        assert.ok(!selected.has(card)); selected.add(card); calls.push(card);
        if (selected.size === 2 && !manual) done = true;
      },
    };
    client.object = ref => ({ async inspect() { return { methods: [{ name: 'ForceClick' }] }; },
      async call(method) { assert.equal(ref, 'confirm'); assert.equal(method, 'ForceClick');
        assert.equal(selected.size, 2); calls.push('confirm'); done = true; } });
    const game = new Sts2(client, endpoint);
    const choice = await game.waitForAction('original-turn', { timeoutMs: 20 });
    assert.equal(choice.status, 'choice_required'); assert.equal(choice.decision.combat.ready, false);
    assert.equal((await game.ready({ timeoutMs: 20 })).kind, 'card_selection');
    assert.equal((await game.spectate({ timeoutMs: 20 })).boundary, 'choice_required');
    assert.deepEqual(calls, [], 'Inspection does not choose cards');
    const selectedCards = await game.selectCards(['Astral Pulse+', 'Defend'], { timeoutMs: 20 });
    assert.equal(selectedCards.status, 'completed');
    const complete = await game.waitForAction(choice.action, { timeoutMs: 20 });
    assert.equal(complete.status, 'completed'); assert.equal(complete.action, 'original-turn');
    assert.deepEqual(calls, manual ? ['a', 'b', 'confirm'] : ['a', 'b']);
  });
}

test('multi-selection notices selected-only changes and submits each distinct card once', async () => {
  const cards = [button('a', 'NGridCardHolder', 'Defend'), button('b', 'NGridCardHolder', 'Defend')];
  const { game, calls } = uiFixture({ screen: 'NDeckCardSelectScreen', ready: true, text: [],
    choices: cards, selection: { min: 2, max: 2, selected: [] } }, (id, update) => {
    if (id === 'a') update({ screen: 'NDeckCardSelectScreen', ready: true, text: [], choices: cards,
      selection: { min: 2, max: 2, selected: [{ ref: 'a' }] } });
    else update({ screen: 'CardPreview', ready: true, text: [], choices: [] });
  });
  const result = await game.selectCards(cards, { confirm: false, timeoutMs: 20 });
  assert.deepEqual(calls, ['a', 'b']); assert.equal(result.status, 'completed');
});

test('a virtualized eligible card routes by stable model reference through the native selection owner', async () => {
  const client = new EventEmitter();
  const cards = Array.from({ length: 32 }, (_, i) => ({ ...button('card:' + i, 'NGridCardHolder', i === 31 ? 'Mirage' : 'Card ' + i),
    cardRef: 'card:' + i, activation: { kind: 'select_card', card: 'card:' + i } }));
  let state = { screen: 'NDeckCardSelectScreen', ready: true, text: [], choices: cards };
  client.object = () => assert.fail('Model references are not Godot widget handles');
  const endpoint = { async observe() { return { value: state }; }, async subscribe() { return async () => {}; },
    async invoke(method, args) { assert.equal(method, 'select_card'); assert.equal(args.card, 'card:31');
      state = { screen: 'CardPreview', ready: true, text: [], choices: [] }; return { dispatched: true }; } };
  const result = await new Sts2(client, endpoint).pickCard('Mirage', { timeoutMs: 20 });
  assert.equal(result.decision.kind, 'card_preview');
});

test('fake merchant entry normalizes through its shop owner without waiting for event-option Proceed', async () => {
  const client = new EventEmitter(); const calls = [];
  let state = { screen: 'NEventRoom', ready: true, text: [], event: { finished: true },
    shop: { kind: 'fake', control: 'fake', canBrowse: true, open: false },
    choices: [button('merchant', 'NMerchantButton', 'MerchantButton'), button('leave', 'NProceedButton', 'Proceed')] };
  client.object = id => ({ async call(method) { calls.push([id, method]); state = {
    screen: 'NFakeMerchantInventory', ready: true, text: [], choices: [],
    shop: { kind: 'fake', control: 'fake', open: true } }; } });
  const endpoint = { async observe() { return { value: state }; }, async subscribe() { return async () => {}; } };
  const decision = await new Sts2(client, endpoint).ready({ timeoutMs: 20 });
  assert.equal(decision.kind, 'shop'); assert.deepEqual(calls, [['fake', 'OpenInventory']]);
});

test('leaving fake inventory reaches its entrance and leaves without re-browsing', async () => {
  const { game, calls } = uiFixture({ screen: 'NFakeMerchantInventory', ready: true, text: [],
    shop: { kind: 'fake', control: 'fake', open: true }, choices: [button('back', 'NBackButton', 'Back')] },
  (id, update) => {
    if (id === 'back') update({ screen: 'NEventRoom', ready: true, text: [], event: { finished: true },
      shop: { kind: 'fake', control: 'fake', canBrowse: true, open: false },
      choices: [button('merchant', 'NMerchantButton', 'MerchantButton'), button('leave', 'NProceedButton', 'Proceed')] });
    else { assert.equal(id, 'leave'); update({ screen: 'NMapScreen', ready: true, text: [], choices: [] }); }
  });
  const result = await game.leaveShop();
  assert.deepEqual(calls, ['back', 'leave']); assert.equal(result.decision.kind, 'map');
});

test('a confirmed transform reports the observed old and new deck cards', async () => {
  const client = new EventEmitter();
  const old = { ref: 'old', name: 'Strike', description: 'Deal 6 damage.' };
  const fresh = { ref: 'new', name: 'Mirage', description: 'Native transformed card.' };
  const confirm = { ref: 'confirm', label: 'Confirm', type: 'NConfirmButton', enabled: true };
  let transformed = false;
  const endpoint = {
    async observe() { return { value: { screen: transformed ? 'NEventRoom' : 'CardPreview', ready: true,
      player: { gold: 99 }, choices: transformed ? [] : [confirm], text: [] } }; },
    async invoke(method, args) { assert.equal(method, 'inspect'); assert.equal(args.kind, 'deck');
      return { cards: transformed ? [fresh] : [old] }; },
    async subscribe() { return async () => {}; },
  };
  client.object = () => ({ async inspect() { return { methods: [{ name: 'ForceClick' }] }; },
    async call() { transformed = true; } });
  const result = await new Sts2(client, endpoint).confirmSelection();
  assert.deepEqual(result.deckChanges.replacement, { before: old, after: fresh });
});

test('concise presenter groups real copies, preserves variants and includes prices and incoming uncertainty', async () => {
  const { present } = await import('./sts2.mjs');
  const dazed = { name: 'Dazed', description: 'Unplayable. Ethereal.', cost: 0, canPlay: false };
  const raw = { kind: 'combat', choices: [{ ref: 'offer', label: 'Potion', kind: 'buy_potion', price: 51,
      detail: { price: 51, item: { description: 'Game potion description.' } }, enabled: true }],
    player: { gold: 52 }, combat: { hand: [{ ...dazed, ref: 'a' }, { ...dazed, ref: 'b' },
      { ...dazed, ref: 'c', enchantment: { name: 'Different', amount: 1 } }],
      enemies: [{ intents: [{ type: 'Attack', isAttack: true, damagePerHit: 6, hits: 2, totalDamage: 12 },
        { type: 'Unknown' }] }] } };
  const result = present({ status: 'completed', state: { huge: true }, decision: raw, results: [{ state: {} }] });
  assert.equal(result.state, undefined); assert.equal(result.decision, undefined); assert.equal(result.results, undefined);
  assert.deepEqual(result.combat.hand[0].refs, ['a', 'b']); assert.equal(result.combat.hand[0].count, 2);
  assert.equal(result.combat.hand.length, 2); assert.equal(result.player.gold, 52); assert.equal(result.choices[0].price, 51);
  assert.equal(result.combat.incoming.knownDamage, 12); assert.equal(result.combat.incoming.totalDamage, null);
  assert.equal(raw.combat.hand.length, 3);
});

test('spectator pacing holds a real preview before confirmation and defaults to no dwell', async () => {
  const client = new EventEmitter(); let done = false, clickedAt;
  const started = performance.now();
  const endpoint = {
    async observe() { return { value: { screen: done ? 'NEventRoom' : 'CardPreview', ready: true, text: [],
      choices: done ? [] : [{ ref: 'confirm', label: 'Confirm', type: 'NConfirmButton', enabled: true }] } }; },
    async subscribe() { return async () => {}; },
  };
  client.object = () => ({ async inspect() { return { methods: [{ name: 'ForceClick' }] }; },
    async call() { clickedAt = performance.now(); done = true; } });
  const game = new Sts2(client, endpoint);
  assert.throws(() => game.setSpectatorPacing(-1), RangeError);
  game.setSpectatorPacing(30);
  const action = game.confirmSelection();
  await new Promise(resolve => setTimeout(resolve, 5));
  assert.equal(done, false); assert.equal((await game.state()).screen, 'CardPreview');
  await action;
  assert.ok(clickedAt - started >= 30);
  game.setSpectatorPacing();
});


test('explicit native abandon waits for the real resulting decision', async () => {
  const client = new EventEmitter(), listeners = new Set(); let ended = false;
  const endpoint = {
    async invoke(method, { kind }) {
      assert.equal(method, 'request'); assert.equal(kind, 'abandonRun');
      setTimeout(() => { ended = true; for (const listener of listeners) listener(); }, 10);
      return { action: 'abandon', status: 'accepted' };
    },
    async observe() { return { value: { screen: ended ? 'NGameOverScreen' : 'NEventRoom',
      ready: ended, choices: [], text: [], pending: { action: 'abandon', kind: 'abandonRun', status: ended ? 'completed' : 'accepted' } } }; },
    async subscribe(_, listener) { listeners.add(listener); return async () => listeners.delete(listener); },
  };
  const result = await new Sts2(client, endpoint).abandonRun();
  assert.equal(result.decision.kind, 'game_over'); assert.equal(result.status, 'completed');
});

test('a named multi-selection plan uses an unselected copy for each repeated name', async () => {
  const client = new EventEmitter(), selected = new Set();
  const cards = ['a', 'b'].map(ref => ({ ref, cardRef: ref, label: 'Defend', type: 'NGridCardHolder', enabled: true,
    detail: { name: 'Defend', cost: 1, description: 'Gain 5 Block.' }, activation: { kind: 'select_card', card: ref } }));
  const endpoint = {
    async observe() { return { value: { screen: selected.size === 2 ? 'CardPreview' : 'NDeckCardSelectScreen', ready: true,
      choices: selected.size === 2 ? [] : cards.map(card => ({ ...card, selected: selected.has(card.ref) })), text: [] } }; },
    async invoke(_, { card }) { assert.ok(!selected.has(card)); selected.add(card); },
    async subscribe() { return async () => {}; },
  };
  await new Sts2(client, endpoint).selectCards(['Defend', 'Defend'], { confirm: false });
  assert.deepEqual([...selected], ['a', 'b']);
});

test('manual shop Back remains an entrance decision instead of automatically browsing again', async () => {
  const client = new EventEmitter(); let inside = true, opens = 0;
  const endpoint = {
    async observe() { return { value: { screen: inside ? 'NFakeMerchantInventory' : 'NEventRoom', ready: true,
      shop: { control: 'owner', open: inside, canBrowse: !inside }, event: { finished: true }, text: [],
      choices: [{ ref: 'back', label: 'Back', enabled: true, type: 'NBackButton' }] } }; },
    async subscribe() { return async () => {}; },
  };
  client.object = id => ({ async inspect() { return { methods: [{ name: 'ForceClick' }] }; },
    async call(method) { if (id === 'owner') opens++; else inside = false; } });
  const game = new Sts2(client, endpoint);
  await game.choose('Back');
  assert.equal((await game.ready()).kind, 'shop_entrance');
  assert.equal(opens, 0);
});

test('a shop removal choice returns while its underlying inventory remains open', async () => {
  const client = new EventEmitter(); let submitted = false;
  const offer = { ref: 'remove', offerRef: 'offer', type: 'NMerchantCardRemoval', enabled: true, label: 'Remove a card' };
  const endpoint = {
    async invoke() { submitted = true; return { action: 'purchase', status: 'accepted' }; },
    async observe() { return { value: { screen: submitted ? 'NDeckCardSelectScreen' : 'NMerchantInventory',
      ready: true, shop: { control: 'merchant', open: true }, choices: submitted ? [] : [offer], text: [],
      pending: submitted ? { action: 'purchase', kind: 'buy', status: 'choice_required' } : null } }; },
    async subscribe() { return async () => {}; },
  };
  const game = new Sts2(client, endpoint), wait = game.waitFor.bind(game);
  game.waitFor = (predicate, options) => wait(predicate, { timeoutMs: 20, ...options });
  const result = await game.buy(offer);
  assert.equal(result.status, 'choice_required'); assert.equal(result.decision.kind, 'card_selection');
  assert.equal(result.action, 'purchase');
});

test('a map opened over a merchant remains a map decision after reconnecting', async () => {
  const client = new EventEmitter();
  const endpoint = {
    async observe() { return { value: { screen: 'NMapScreen', ready: true, choices: [], text: [], map: [],
      shop: { control: 'underlying-merchant', open: false, canBrowse: true } } }; },
    async subscribe() { return async () => {}; },
  };
  client.object = () => assert.fail('A map decision must not open its underlying merchant');
  const result = await new Sts2(client, endpoint).ready();
  assert.equal(result.kind, 'map');
});

test('startRun waits for character controls to finish enabling before checking availability', async () => {
  const client = new EventEmitter(), listeners = new Set(), calls = [];
  let settled = false, embarked = false;
  const endpoint = {
    async observe() { return { value: { screen: embarked ? 'NEventRoom' : 'NCharacterSelectScreen',
      ready: settled || embarked, player: embarked ? {} : null, text: [], choices: embarked ? [] : [
        { ref: 'regent', label: 'The Regent', type: 'NCharacterSelectButton', selected: true,
          enabled: settled, detail: { id: 'REGENT', locked: !settled } },
        { ref: 'embark', label: 'Embark', type: 'NConfirmButton', enabled: settled }] } }; },
    async subscribe(_, listener) { listeners.add(listener); return async () => listeners.delete(listener); },
  };
  client.object = id => ({ async inspect() { return { methods: [{ name: 'ForceClick' }] }; },
    async call() { assert.ok(settled); calls.push(id); embarked = true; } });
  const timer = setTimeout(() => { settled = true; for (const listener of listeners) listener(); }, 10);
  try {
    const result = await new Sts2(client, endpoint).startRun({ character: 'REGENT', timeoutMs: 100 });
    assert.equal(result.status, 'completed'); assert.deepEqual(calls, ['embark']);
  } finally { clearTimeout(timer); }
});

test('startRun rejects a genuinely locked character after the screen has settled', async () => {
  const client = new EventEmitter();
  const endpoint = {
    async observe() { return { value: { screen: 'NCharacterSelectScreen', ready: true, text: [], choices: [
      { ref: 'ironclad', label: 'Ironclad', type: 'NCharacterSelectButton', enabled: true, detail: { id: 'IRONCLAD', locked: false } },
      { ref: 'locked', label: 'Locked', type: 'NCharacterSelectButton', enabled: false, detail: { id: 'LOCKED', locked: true } },
      { ref: 'embark', label: 'Embark', type: 'NConfirmButton', enabled: true }] } }; },
    async subscribe() { return async () => {}; },
  };
  client.object = () => assert.fail('A locked character must not be selected or embarked');
  await assert.rejects(new Sts2(client, endpoint).startRun({ character: 'LOCKED', timeoutMs: 20 }), /Character is missing, locked or ambiguous/);
});

test('Crystal Sphere coordinates cross the Godot callback as native integers and await completion', async () => {
  const client = new EventEmitter(), listeners = new Set(); let submitted = false, finished = false;
  const endpoint = {
    async invoke(method, request) {
      assert.equal(method, 'request'); assert.equal(request.kind, 'divineCell');
      assert.equal(request.x, 5n); assert.equal(request.y, 5n);
      submitted = true;
      setTimeout(() => { finished = true; for (const listener of listeners) listener(); }, 10);
      return { action: 'divination', status: 'accepted' };
    },
    async observe() { return { value: { screen: 'NCrystalSphereScreen', ready: true, choices: [], text: [],
      crystalSphere: { remaining: finished ? 2 : 3, tool: 'Big', finished: false },
      pending: submitted ? { action: 'divination', kind: 'divineCell', status: finished ? 'completed' : 'accepted' } : null } }; },
    async subscribe(_, listener) { listeners.add(listener); return async () => listeners.delete(listener); },
  };
  const result = await new Sts2(client, endpoint).divineCell({ x: 5, y: 5 });
  assert.equal(result.status, 'completed'); assert.equal(result.state.crystalSphere.remaining, 2);
  assert.equal(listeners.size, 0);
});

test('invalid Crystal Sphere coordinates stop before native submission', async () => {
  for (const value of [-1, 2.5, NaN, Infinity, '5', 2147483648]) {
    const client = new EventEmitter();
    const endpoint = { async invoke() { assert.fail('Invalid coordinates must not submit an action'); } };
    await assert.rejects(new Sts2(client, endpoint).divineCell({ x: value, y: 5 }), /Cell coordinates/);
  }
});


test('the presenter preserves native evoke-next positions without spatial reinterpretation', async () => {
  const { present } = await import('./sts2.mjs');
  const orbs = [{ name: 'Lightning', queuePosition: 1, evokeNext: true },
    { name: 'Dark', queuePosition: 2, evokeNext: false }];
  const result = present({ kind: 'combat', choices: [], combat: { orbs, orbOrder: 'evoke_next_first' } });
  assert.equal(result.combat.orbOrder, 'evoke_next_first');
  assert.deepEqual(result.combat.orbs, orbs);
  assert.equal(result.combat.orbs[0].evokeNext, true);
  assert.deepEqual(present({ kind: 'combat', choices: [], combat: { orbs: [], orbOrder: 'evoke_next_first' } }).combat.orbs, []);
});

test('last Crystal Sphere divination settles at the actual loot decision', async () => {
  const client = new EventEmitter(), listeners = new Set(); let finished = false, loot = false;
  const endpoint = {
    async invoke() { finished = true;
      setTimeout(() => { loot = true; for (const listener of listeners) listener(); }, 10);
      return { action: 'sphere-last', status: 'accepted' }; },
    async observe() { return { value: { screen: loot ? 'NRewardsScreen' : 'NCrystalSphereScreen', ready: !finished || loot, text: [],
      choices: loot ? [button('cardreward', 'NRewardButton', 'Add a card', { category: 'CardReward' })] : [],
      rewards: loot ? { remaining: 1, unresolved: 1, complete: false } : null,
      crystalSphere: loot ? null : { remaining: finished ? 0 : 1, finished, tool: 'Big' },
      pending: finished ? { action: 'sphere-last', kind: 'divineCell', status: 'completed' } : null } }; },
    async subscribe(_, listener) { listeners.add(listener); return async () => listeners.delete(listener); },
  };
  const result = await new Sts2(client, endpoint).divineCell({ x: 5, y: 5 });
  assert.equal(result.decision.kind, 'rewards'); assert.equal(listeners.size, 0);
});

test('finished sphere after resolving loot leaves through its native Proceed and settles on map', async () => {
  const { game, calls } = uiFixture({ screen: 'NCrystalSphereScreen', ready: true, text: [],
    crystalSphere: { remaining: 0, finished: true }, choices: [button('leave', 'NProceedButton', 'Proceed')] },
    (id, update) => { assert.equal(id, 'leave'); update({ screen: 'NMapScreen', ready: true, choices: [], text: [], map: [] }); });
  assert.equal((await game.ready()).kind, 'map'); assert.deepEqual(calls, ['leave']);
});

test('openCardReward opens ordinary offers and leaves special recovery explicitly selectable', async () => {
  const special = button('stolen', 'NRewardButton', 'Take your stolen card back', { category: 'SpecialCardReward' });
  const ordinary = button('ordinary', 'NRewardButton', 'Add a card', { category: 'CardReward' });
  const { game, calls } = uiFixture({ screen: 'NRewardsScreen', ready: true, text: [], choices: [special, ordinary] },
    (id, update) => update({ screen: id === 'ordinary' ? 'NCardRewardSelectionScreen' : 'NRewardsScreen',
      ready: true, text: [], choices: id === 'ordinary' ? [] : [ordinary] }));
  const result = await game.openCardReward();
  assert.deepEqual(calls, ['ordinary']); assert.equal(result.decision.kind, 'card_reward');
  const f = uiFixture({ screen: 'NRewardsScreen', ready: true, choices: [special], text: [] },
    (_, update) => update({ screen: 'NMapScreen', ready: true, choices: [], text: [] }));
  await assert.rejects(f.game.openCardReward(), /ordinary card reward/);
  await f.game.resumeQueue();
  assert.equal((await f.game.choose(special)).selectedChoice.ref, 'stolen');
});

test('collectLoot claims requested non-card rewards then opens only the ordinary card offer', async () => {
  const rewards = [button('gold', 'NRewardButton', '20 Gold', { category: 'GoldReward' }),
    button('potion', 'NRewardButton', 'Weak Potion', { category: 'PotionReward' }),
    button('relic', 'NRewardButton', 'Anchor', { category: 'RelicReward' }),
    button('special', 'NRewardButton', 'Stolen card', { category: 'SpecialCardReward' }),
    button('cards', 'NRewardButton', 'Add a card', { category: 'CardReward' })];
  let remaining = [...rewards];
  const { game, calls } = uiFixture({ screen: 'NRewardsScreen', ready: true, choices: remaining, text: [] },
    (id, update) => { remaining = remaining.filter(c => c.ref !== id); update({
      screen: id === 'cards' ? 'NCardRewardSelectionScreen' : 'NRewardsScreen',
      ready: true, choices: id === 'cards' ? [] : remaining, text: [] }); });
  const outcome = await game.collectLoot({ potions: true });
  assert.deepEqual(calls, ['gold', 'potion', 'cards']);
  assert.equal(outcome.status, 'choice_required'); assert.equal(outcome.decision.kind, 'card_reward');
  assert.deepEqual(outcome.results.map(r => r.selectedChoice.ref), calls);
  assert.ok(remaining.some(c => c.ref === 'relic')); assert.ok(remaining.some(c => c.ref === 'special'));
});

test('collectLoot returns an inventory decision with applied gold and permits explicit continuation without queue recovery', async () => {
  const gold = button('gold', 'NRewardButton', '20 Gold', { category: 'GoldReward' });
  const potion = { ...button('potion', 'NRewardButton', 'Weak Potion', { category: 'PotionReward' }),
    enabled: false, blockedBy: 'potion_inventory_full', blockedReason: 'Potion inventory full' };
  const { game, calls } = uiFixture({ screen: 'NRewardsScreen', ready: true, choices: [gold, potion], text: [] },
    (_, update) => update({ screen: 'NRewardsScreen', ready: true, choices: [potion], text: [] }));
  const outcome = await game.collectLoot({ potions: true });
  assert.equal(outcome.status, 'choice_required'); assert.equal(outcome.blocker.kind, 'potion_inventory_full');
  assert.equal(outcome.results[0].selectedChoice.ref, 'gold'); assert.equal(outcome.remaining[0].ref, 'potion');
  assert.equal(game.present(outcome).blocker.kind, 'potion_inventory_full');
  // Ordinary inventory decisions do not poison the mutation queue.
  assert.equal((await game.claimRewards([])).status, 'completed');
  assert.deepEqual(calls, ['gold']);
});

test('removeCard continues the matching purchased selection and waits for the original purchase without buying again', async () => {
  const client = new EventEmitter(), calls = [];
  let screen = 'NDeckCardSelectScreen', status = 'choice_required';
  const card = { ...button('defend', 'NGridCardHolder', 'Defend'), cardRef: 'card', activation: { kind: 'select_card', card: 'card' } };
  const endpoint = {
    async observe() { return { value: { screen, ready: true, text: [], choices: screen === 'NDeckCardSelectScreen' ? [card]
      : screen === 'CardPreview' ? [button('confirm', 'NConfirmButton', 'Confirm')] : [],
      pending: { action: 'removal-purchase', kind: 'buy', purpose: 'card_removal', status } } }; },
    async subscribe() { return async () => {}; },
    async invoke(method, request) { assert.equal(method, 'select_card', 'Existing purchase must not be submitted again');
      assert.equal(request.card, 'card'); calls.push('select'); screen = 'CardPreview'; },
  };
  client.object = () => ({ async inspect() { return { methods: [{ name: 'ForceClick' }] }; },
    async call() { calls.push('confirm'); screen = 'NMerchantInventory'; status = 'completed'; } });
  const result = await new Sts2(client, endpoint).removeCard('Defend', { timeoutMs: 20 });
  assert.equal(result.action, 'removal-purchase'); assert.equal(result.status, 'completed');
  assert.deepEqual(calls, ['select', 'confirm']);
});

test('removeCard does not adopt an unrelated active selection or submit a purchase over it', async () => {
  const { game, calls } = uiFixture({ screen: 'NDeckCardSelectScreen', ready: true, text: [], choices: [],
    pending: { action: 'other', kind: 'buy', purpose: null, status: 'choice_required' } }, () => assert.fail('No selection or purchase'));
  await assert.rejects(game.removeCard('Defend'), /selectCards\/confirmSelection/);
  assert.deepEqual(calls, []);
});

test('compact map/inventory options retain next rooms, combat counters and offer effect definitions', async () => {
  const { present } = await import('./present.mjs');
  const map = [{ ref: 'r', col: 0, row: 1, type: 'Shop', leadsTo: [], state: 'Travelable' }];
  const raw = { kind: 'map', map, nextRooms: map, choices: [],
    player: { relics: [{ name: 'Static', status: 'Normal' }, { name: 'Ornamental Fan', counter: 3 }] } };
  const small = present(raw);
  assert.equal(small.map, undefined); assert.deepEqual(small.nextRooms, map);
  assert.deepEqual(small.player.relics.map(r => r.name), ['Ornamental Fan']);
  assert.equal(present(raw, { map: true, inventory: true }).map[0].at, '0,1');
  assert.equal(present(raw, { inventory: true }).player.relics.length, 2);
  const offer = present({ kind: 'event', choices: [{ ...button('swift', 'NEventOptionButton', 'Swift'), kind: 'event_option',
    detail: { effects: [{ name: 'Swift', description: 'First play each combat draws 3 cards.' }] } }] });
  assert.equal(offer.choices[0].model.effects[0].description, 'First play each combat draws 3 cards.');
});

test('compact Architect dialogue retains current spoken text once with no advance or extra read', async () => {
  const game = new Sts2(null, { observe() { assert.fail('Pure projection only'); } });
  const raw = { kind: 'event', choices: [], event: { description: '' }, text: ['Current Architect line'],
    dialogue: { source: 'speech_bubble', line: 2, text: ['Current Architect line'], canAdvance: false } };
  const view = game.present(raw);
  assert.deepEqual(view.dialogue.text, ['Current Architect line']); assert.deepEqual(view.text, []);
  assert.equal(view.dialogue.canAdvance, false); assert.equal(raw.text[0], 'Current Architect line');
});

test('collectLoot stops at an explicitly requested relic choice and leaves special rewards reachable', async () => {
  const relic = button('relic', 'NRewardButton', 'Relic offer', { category: 'RelicReward' });
  const potion = button('potion', 'NRewardButton', 'Weak Potion', { category: 'PotionReward' });
  const { game, calls } = uiFixture({ screen: 'NRewardsScreen', ready: true, choices: [relic, potion], text: [] },
    (_, update) => update({ screen: 'NRelicSelectionScreen', ready: true,
      choices: [button('optional', 'NRelicBasicHolder', 'Optional relic')], text: [] }));
  const outcome = await game.collectLoot({ gold: false, relics: [relic] });
  assert.equal(outcome.status, 'choice_required'); assert.deepEqual(calls, ['relic']);
  assert.equal(outcome.decision.kind, 'relic_selection');
  const f = uiFixture({ screen: 'NRewardsScreen', ready: true, text: [], choices: [
    button('special', 'NRewardButton', 'Stolen card', { category: 'SpecialCardReward' })] }, () => assert.fail('Special reward is explicit'));
  assert.equal((await f.game.collectLoot()).decision.choices[0].kind, 'special_card_reward');
  assert.deepEqual(f.calls, []);
});

test('collectLoot does not open a card offer after an unconfirmed claim', async () => {
  const { game, calls } = uiFixture({ screen: 'NRewardsScreen', ready: true, text: [], choices: [
    button('gold', 'NRewardButton', '20 Gold', { category: 'GoldReward' }),
    button('cards', 'NRewardButton', 'Add a card', { category: 'CardReward' })] }, () => {});
  await assert.rejects(game.collectLoot({}, { timeoutMs: 5 }), error => {
    assert.equal(error.outcome.status, 'unconfirmed'); assert.equal(error.outcome.results.length, 1); return true;
  });
  assert.deepEqual(calls, ['gold']);
});

test('an unconfirmed collectLoot card opening stops the queue and retains applied claims', async () => {
  const gold = button('gold', 'NRewardButton', '20 Gold', { category: 'GoldReward' });
  const cards = button('cards', 'NRewardButton', 'Add a card', { category: 'CardReward' });
  const special = button('special', 'NRewardButton', 'Stolen card', { category: 'SpecialCardReward' });
  const { game, calls } = uiFixture({ screen: 'NRewardsScreen', ready: true, text: [], choices: [gold, cards, special] },
    (id, update) => {
      if (id === 'gold') update({ screen: 'NRewardsScreen', ready: true, text: [], choices: [cards, special] });
    });
  const results = await Promise.allSettled([game.collectLoot({}, { timeoutMs: 5 }), game.choose(special)]);
  assert.equal(results[0].status, 'rejected');
  assert.equal(results[0].reason.outcome.status, 'unconfirmed');
  assert.deepEqual(results[0].reason.outcome.results.map(r => r.status), ['observed_change', 'unconfirmed']);
  assert.equal(results[0].reason.outcome.results[0].selectedChoice.ref, 'gold');
  assert.match(results[1].reason.message, /queue stopped/);
  assert.deepEqual(calls, ['gold', 'cards']);
});

test('pure compact outcomes retain companions and related-card facts without duplicate hand/allied models', async () => {
  const card = { ref: 'card', name: 'Conqueror', cost: 1, mechanics: ['A native-backed rule.'], effects: [
    { name: 'Sovereign Blade', description: 'Retain. Deal 10 damage.', card: { name: 'Sovereign Blade', cost: 2, starCost: null } }] };
  const player = { ref: 'me', hp: 50, gold: 12, relics: [{ name: 'Static relic', status: 'Normal' }, { name: 'Counter', counter: 2 }] };
  const pet = { ref: 'osty', name: 'Osty', alive: false, hp: 0, maxHp: 20, statuses: [{ name: 'Doom', amount: 4 }] };
  const decision = { kind: 'card_selection', text: ['Repeated card labels'], choices: [{ ...button('hand-widget', 'NHandCardHolder', 'Conqueror'),
    kind: 'select_card', cardRef: 'card', detail: card }], player,
    combat: { ready: false, hand: [card], pets: [pet], allies: [player, pet], ongoing: true,
      noLivingEnemies: true, inactiveEnemies: [{ name: 'Test Subject', alive: false }] } };
  const game = new Sts2(null, { observe() { assert.fail('present must be pure'); } });
  const before = JSON.stringify(decision);
  const result = game.present({ status: 'choice_required', action: 'turn', decision });
  assert.deepEqual(result.combat.hand, []); assert.deepEqual(result.combat.handInChoices, ['card']);
  assert.deepEqual(result.combat.pets, [pet]); assert.deepEqual(result.combat.allies, []);
  assert.equal(result.combat.noLivingEnemies, true); assert.equal(result.combat.inactiveEnemies[0].name, 'Test Subject');
  assert.equal(result.text, undefined);
  assert.equal(result.choices[0].model.effects[0].card.cost, 2);
  assert.deepEqual(result.choices[0].model.mechanics, ['A native-backed rule.']);
  assert.deepEqual(result.player.relics.map(r => r.name), ['Counter']);
  assert.equal(JSON.stringify(decision), before);
});

test('compact Timeline summarizes history and retains every new explicit epoch decision', async () => {
  const { present } = await import('./present.mjs');
  const epochs = ['inspect_epoch', 'locked_epoch', 'reveal_epoch'].map((kind, i) => ({ ref: String(i), label: kind, kind, enabled: kind !== 'locked_epoch' }));
  const raw = { kind: 'timeline', choices: epochs, timeline: { busy: false, epochs } };
  const compact = present(raw);
  assert.deepEqual(compact.timeline.history, { completed: 1, locked: 1 });
  assert.equal(compact.timeline.epochs, undefined);
  assert.deepEqual(compact.choices.map(c => c.ref), ['2']);
  assert.equal(raw.choices.length, 3);
});

test('compact sphere shows each visible cell once, full revealed items separately from fragments', async () => {
  const { present } = await import('./sts2.mjs');
  const cells = Array.from({ length: 121 }, (_, i) => ({ ref: 'cell' + i, kind: 'divine_cell', type: 'NCrystalSphereCell',
    enabled: true, detail: { x: i % 11, y: Math.floor(i / 11), hidden: i !== 5, revealedItem: i === 5 ? 'CrystalSphereGold' : null } }));
  const d = { kind: 'crystal_sphere', choices: cells,
    crystalSphere: { remaining: 2, cells, tools: [], revealedItems: [] } };
  const result = present(d);
  assert.equal(result.choices.length, 0); assert.equal(result.crystalSphere.cells, undefined);
  assert.equal(result.crystalSphere.grid.rows.length, 11);
  assert.equal(result.crystalSphere.grid.rows[0].cells[5], 'CrystalSphereGold');
  assert.equal(result.crystalSphere.grid.rows[0].cells[4], '?');
  assert.deepEqual(result.crystalSphere.revealedItems, []);
  assert.ok(JSON.stringify(result).length < JSON.stringify(d).length / 4);
});

test('compact combat retains actionable context and material choice without repeated effect/relic prose', async () => {
  const { present } = await import('./sts2.mjs');
  const card = { ref: 'c', name: 'Dazed', description: 'Ethereal.', effects: [{ name: 'Ethereal', description: 'Long effect definition.' }] };
  const choice = { ref: 'special', kind: 'special_card_reward', label: 'Take your stolen card back',
    detail: { category: 'SpecialCardReward', card } };
  const result = present({ status: 'observed_change', selectedChoice: choice, deckChanges: { added: [card] },
    decision: { kind: 'combat', choices: [button('draw', 'NDrawPileButton', 'Draw Pile'), button('end', 'NEndTurnButton', 'End Turn')],
      player: { gold: 50, relics: [{ id: 'R', name: 'Relic', description: 'Long relic definition.', counter: 2 }] },
      combat: { hand: [card], enemies: [] } } });
  assert.deepEqual(result.choices.map(c => c.ref), ['end']);
  assert.equal(result.player.relics[0].description, undefined); assert.equal(result.player.relics[0].counter, 2);
  assert.deepEqual(result.combat.hand[0].effects, ['Ethereal']);
  assert.equal(result.selectedChoice.ref, 'special'); assert.equal(result.deckChanges.added[0].name, 'Dazed');
});

test('event combat reports actual permanent deck upgrades across action-ref reset', async () => {
  const client = new EventEmitter(), listeners = new Set(); let phase = 'event', pending;
  const before = { ref: 'before', deckRef: 'deck:1', name: 'Strike', upgraded: false, description: 'Deal6.' };
  const after = { ref: 'after', deckRef: 'deck:1', name: 'Strike+', upgraded: true, description: 'Deal9.' };
  const endpoint = {
    async observe() { return { value: { screen: phase === 'combat' ? 'NCombatRoom' : phase === 'event' ? 'NEventRoom' : 'NMapScreen',
      ready: true, player: {}, text: [], choices: phase === 'event' ? [button('train', 'NEventOptionButton', 'Train')] : [],
      combat: phase === 'combat' ? { hand: [], enemies: [] } : null, pending } }; },
    async invoke(method, args) {
      if (method === 'inspect') return { cards: phase === 'done' ? [after] : [before] };
      assert.equal(args.kind, 'endTurn'); phase = 'done'; pending = { action: 'finish', kind: 'endTurn', status: 'completed' };
      return { action: 'finish', status: 'accepted' };
    },
    async subscribe(_, listener) { listeners.add(listener); return async () => listeners.delete(listener); },
  };
  client.object = () => ({ async inspect() { return { methods: [{ name: 'ForceClick' }] }; }, async call() { phase = 'combat'; } });
  const game = new Sts2(client, endpoint);
  await game.choose('Train');
  const outcome = await game.endTurn();
  assert.equal(outcome.deckChanges.changed.length, 1);
  assert.equal(outcome.deckChanges.changed[0].before.name, 'Strike');
  assert.equal(outcome.deckChanges.changed[0].after.name, 'Strike+');
  assert.deepEqual(outcome.deckChanges.added, []); assert.deepEqual(outcome.deckChanges.removed, []);
});

test('resolving sphere loot normalizes its finished screen without claiming or choosing anything else', async () => {
  const { game, calls } = uiFixture({ screen: 'NRewardsScreen', ready: true, text: [],
    choices: [button('gold', 'NRewardButton', '10 Gold', { category: 'GoldReward', amount: 10 })] },
    (id, update) => {
      if (id === 'gold') update({ screen: 'NCrystalSphereScreen', ready: true, text: [],
        crystalSphere: { remaining: 0, finished: true }, choices: [button('leave', 'NProceedButton', 'Proceed')] });
      else { assert.equal(id, 'leave'); update({ screen: 'NMapScreen', ready: true, choices: [], text: [], map: [] }); }
    });
  const result = await game.claimGold();
  assert.equal(result.decision.kind, 'map'); assert.deepEqual(calls, ['gold', 'leave']);
  assert.deepEqual(result.automaticSteps, ['leave_finished_sphere']);
  assert.equal(game.present(result).outcomes[0].selectedChoice.model.amount, 10);
});

test('deck snapshot comparison preserves distinct duplicate copies across combat refs', async () => {
  const { deckChanges } = await import('./cards.mjs');
  const before = [{ ref: 'old1', deckRef: 'deck:1', name: 'Strike', description: 'Deal6.' },
    { ref: 'old2', deckRef: 'deck:2', name: 'Strike', description: 'Deal6.' }];
  const after = [{ ...before[1], ref: 'new2' }, { ...before[0], ref: 'new1', name: 'Strike+', description: 'Deal9.' }];
  const changes = deckChanges(before, after);
  assert.equal(changes.changed.length, 1); assert.equal(changes.changed[0].after.deckRef, 'deck:1');
  assert.deepEqual(changes.removed, []); assert.deepEqual(changes.added, []);
});

test('spectate is passive and waits past text/layout churn for a new settled human-driven position', async () => {
  const initial = { screen: 'NEventRoom', ready: true, player: { gold: 10 }, text: ['Story'],
    event: { title: 'An event', description: 'Flavour', finished: false },
    choices: [button('pick', 'NEventOptionButton', 'Take', { description: 'Gain gold' })] };
  const { game, update, calls } = uiFixture(initial, () => assert.fail('Spectating must not activate input'));
  const first = await game.spectate();
  const waiting = game.spectate({ after: first, timeoutMs: 100 });
  update({ ...initial, text: ['Typing...'], event: { ...initial.event, description: 'More flavour' },
    dialogue: { line: 2 }, choices: initial.choices.map(c => ({ ...c, ref: 'recreated-widget', rect: [2, 3, 4, 5] })) });
  let delivered = false; waiting.then(() => { delivered = true; });
  await new Promise(resolve => setTimeout(resolve, 5)); assert.equal(delivered, false);
  // No adapter-tracked pending action: ordinary human interaction is observable.
  update({ ...initial, player: { gold: 20 } });
  const second = await waiting;
  assert.equal(second.position, first.position + 1); assert.equal(second.view.player.gold, 20);
  assert.equal(second.boundary, 'settled_position'); assert.deepEqual(calls, []);
});

test('spectate exposes the host-reported enemy-turn choice on its original endTurn', async () => {
  const { game } = uiFixture({ screen: 'NChooseACardSelectionScreen', ready: true, text: [],
    pending: { action: 'end', kind: 'endTurn', status: 'choice_required' }, combat: { ready: false, side: 'Enemy' },
    choices: [button('curse', 'NGridCardHolder', 'Disintegration')] }, () => assert.fail('No choice may be made'));
  const frame = await game.spectate();
  assert.equal(frame.boundary, 'choice_required'); assert.equal(frame.view.pending.action, 'end');
  assert.equal(frame.view.kind, 'card_selection');
});

test('spectate waits through accepted/running action states and reports per-action settlement, not a batch', async () => {
  const initial = { screen: 'NCombatRoom', ready: true, player: { energy: 3 }, text: [],
    combat: { ready: true, hand: [], enemies: [] }, choices: [] };
  const { game, update } = uiFixture(initial, () => assert.fail());
  const first = await game.spectate(); let delivered = false;
  const next = game.spectate({ after: first, timeoutMs: 100 }); next.then(() => { delivered = true; });
  update({ ...initial, player: { energy: 2 }, pending: { action: 'play', kind: 'play', status: 'running' } });
  await new Promise(resolve => setTimeout(resolve, 5)); assert.equal(delivered, false);
  update({ ...initial, player: { energy: 2 }, pending: { action: 'play', kind: 'play', status: 'completed' } });
  const result = await next; assert.equal(result.view.player.energy, 2);
  assert.equal(result.boundary, 'settled_position'); assert.equal(result.batchComplete, undefined);
});

test('spectate does not normalize forced presentation and keeps one latest in-memory continuation', async () => {
  const { game, update, calls } = uiFixture({ screen: 'NTreasureRoom', ready: true, text: [], choices: [button('chest', 'NTreasureButton', 'Open')] },
    () => assert.fail('An observer must not open the mandatory chest'));
  const first = await game.spectate();
  const waiting = game.spectate({ after: first, timeoutMs: 100 });
  await assert.rejects(game.spectate({ after: first }), /already waiting/);
  update({ screen: 'NMapScreen', ready: true, choices: [], text: [], map: [] });
  const second = await waiting;
  await assert.rejects(game.spectate({ after: first }), /last frame/);
  assert.equal(second.view.kind, 'map'); assert.deepEqual(calls, []);
});

test('spectate timeout, cancellation and disconnect clean listeners without stopping the action queue', async () => {
  for (const mode of ['timeout', 'abort', 'disconnect']) {
    const { game, client, listeners } = fixture();
    await game.spectate();
    const control = new AbortController();
    const waiting = game.spectate({ timeoutMs: 15, signal: control.signal });
    await new Promise(resolve => setTimeout(resolve, 1));
    if (mode === 'abort') control.abort();
    if (mode === 'disconnect') client.emit('close');
    await assert.rejects(waiting, mode === 'timeout' ? /timed out/ : mode === 'disconnect' ? /disconnected/ : { name: 'AbortError' });
    assert.equal(listeners.size, 0); assert.equal(client.listenerCount('close'), 0);
    if (mode !== 'disconnect') assert.equal((await game.endTurn()).status, 'completed');
  }
});

test('aborting an observation with an in-flight read releases its subscription promptly', async () => {
  const client = new EventEmitter(); let unsubscribed = 0;
  const endpoint = { async subscribe() { return async () => { unsubscribed++; }; }, observe() { return new Promise(() => {}); } };
  const game = new Sts2(client, endpoint), control = new AbortController();
  const waiting = game.waitFor(() => false, { signal: control.signal, timeoutMs: 1000 });
  setTimeout(() => control.abort(), 5);
  await assert.rejects(waiting, { name: 'AbortError' });
  assert.equal(unsubscribed, 1); assert.equal(client.listenerCount('close'), 0);
});


test('aborting during subscription setup cleans a subscription that becomes ready later', async () => {
  const client = new EventEmitter(); let release, active = 0;
  const endpoint = { subscribe() { return new Promise(resolve => { release = () => {
    active++; resolve(async () => { active--; });
  }; }); }, async observe() { assert.fail('Aborted setup must not start observation'); } };
  const game = new Sts2(client, endpoint), control = new AbortController();
  const waiting = game.spectate({ signal: control.signal, timeoutMs: 100 });
  control.abort(); await assert.rejects(waiting, { name: 'AbortError' });
  release(); await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(active, 0); assert.equal(client.listenerCount('close'), 0); assert.equal(client.listenerCount('notification'), 0);
});

test('removed observation endpoints terminate the wait and clean the subscription', async () => {
  const client = new EventEmitter(); let unsubscribed = 0;
  const endpoint = { id: 'adapter', async subscribe() { return async () => { unsubscribed++; }; },
    async observe() { return { value: { screen: 'NGame', ready: false, choices: [], text: [] } }; } };
  const game = new Sts2(client, endpoint);
  const waiting = game.spectate({ timeoutMs: 100 });
  client.emit('notification', { method: 'objects.removed', params: { object: 'adapter' } });
  await assert.rejects(waiting, /endpoint was removed/);
  assert.equal(unsubscribed, 1); assert.equal(client.listenerCount('notification'), 0);
});

test('spectator timeout is not extended by a delayed unsubscribe acknowledgment', async () => {
  const client = new EventEmitter(); let release, cleanupStarted = false;
  const endpoint = { async subscribe() { return () => { cleanupStarted = true; return new Promise(resolve => { release = resolve; }); }; },
    async observe() { return { value: { screen: 'NGame', ready: false, choices: [], text: [] } }; } };
  const game = new Sts2(client, endpoint);
  const waiting = game.spectate({ timeoutMs: 5 });
  let settled = false; waiting.catch(() => { settled = true; });
  try {
    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(cleanupStarted, true); assert.equal(settled, true);
    await assert.rejects(waiting, /timed out/);
  } finally { release?.(); await waiting.catch(() => {}); }
  assert.equal(client.listenerCount('close'), 0); assert.equal(client.listenerCount('notification'), 0);
});
