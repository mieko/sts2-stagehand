import { cardVariant } from './cards.mjs';

function groupCards(cards = []) {
  const groups = new Map();
  for (const card of cards) {
    const key = cardVariant(card);
    if (!groups.has(key)) groups.set(key, { ...card, count: 0, refs: [] });
    const group = groups.get(key);
    group.count++;
    group.refs.push(card.ref);
  }
  return [...groups.values()];
}

function groupChoices(choices = []) {
  const groups = new Map();
  for (const { rect, detail, activation, ...choice } of choices) {
    // Only true card copies are interchangeable. Keep individual shop offers,
    // map coordinates and arbitrary UI choices separately addressable.
    const cardChoice = ['select_card', 'deselect_card'].includes(choice.kind);
    const groupKey = cardChoice ? JSON.stringify([choice.kind, choice.enabled, choice.selected, cardVariant({ detail })]) : choice.ref;
    if (!groups.has(groupKey)) {
      const model = detail?.item ?? detail?.relic ?? detail?.potion ?? detail;
      groups.set(groupKey, { ref: choice.ref, label: choice.label, kind: choice.kind,
        enabled: choice.enabled, selected: choice.selected, blockedReason: choice.blockedReason, blockedBy: choice.blockedBy,
        price: choice.price, offerRef: choice.offerRef, cardRef: choice.cardRef,
        model, ...(cardChoice ? { count: 0, refs: [] } : {}) });
    }
    if (cardChoice) { const group = groups.get(groupKey); group.count++; group.refs.push(choice.ref); }
  }
  return [...groups.values()];
}

export function incoming(combat) {
  const intents = (combat?.enemies ?? []).flatMap(enemy => enemy.intents ?? []);
  const attacks = intents.filter(intent => intent.isAttack || intent.totalDamage != null);
  const unknownAttacks = attacks.filter(intent => intent.totalDamage == null).length;
  const hiddenIntents = intents.filter(intent => /hidden|unknown/i.test(intent.type)).length;
  const knownDamage = attacks.reduce((sum, intent) => sum + (intent.totalDamage ?? 0), 0);
  return { knownDamage, totalDamage: unknownAttacks || hiddenIntents ? null : knownDamage,
    unknownAttacks, hiddenIntents, scope: 'native_local_player_intent_preview',
    note: 'Intent preview, before block; conditional effects, target changes and later actions can change the outcome.' };
}

const combatInspectionButtons = new Set(['NDrawPileButton', 'NDiscardPileButton', 'NExhaustPileButton',
  'NTopBarDeckButton', 'NTopBarMapButton', 'NSettingsButton']);

function sphereView(sphere) {
  if (!sphere) return undefined;
  const { cells = [], tools, ...state } = sphere;
  const xs = [...new Set(cells.map(c => c.detail.x))].sort((a, b) => a - b);
  const ys = [...new Set(cells.map(c => c.detail.y))].sort((a, b) => a - b);
  const lookup = new Map(cells.map(c => [`${c.detail.x},${c.detail.y}`, c.detail]));
  return { ...state, grid: { columns: xs, rows: ys.map(y => ({ y, cells: xs.map(x => {
    const cell = lookup.get(`${x},${y}`);
    return !cell ? null : cell.hidden ? '?' : cell.revealedItem ?? '.';
  }) })), legend: { '?': 'covered', '.': 'uncovered empty', itemNames: 'visible fragments, not necessarily a whole revealed item' } } };
}

// Retain game descriptions while moving repeated hover-tip definitions to the
// full decision/inspection API. Undefined projection fields need no output space.
function compact(value, explainEffects = false) {
  if (Array.isArray(value)) return value.map(item => compact(item, explainEffects));
  if (!value || typeof value !== 'object') return value;
  explainEffects ||= value.kind === 'event_option';
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined)
    .map(([key, item]) => [key, key === 'effects' && Array.isArray(item)
      ? item.map(effect => explainEffects ? compact(effect, true) : typeof effect === 'string' ? effect : effect.card || effect.mechanics?.length
        ? compact({ name: effect.name, card: effect.card, mechanics: effect.mechanics }) : effect.name) : compact(item, explainEffects)]));
}

// Pure projection: printing an outcome never causes game input or another read.
export function present(value, { map = false, inventory = false } = {}) {
  const d = value.decision ?? value;
  const p = d.player;
  const c = d.combat;
  const choiceCards = new Set((d.choices ?? []).filter(choice => ['select_card', 'deselect_card'].includes(choice.kind))
    .map(choice => choice.cardRef ?? choice.ref));
  const pets = c?.pets ?? [];
  const petRefs = new Set(pets.map(pet => pet.ref));
  const epochHistory = new Set(['inspect_epoch', 'locked_epoch']);
  const choices = groupChoices((d.choices ?? []).filter(choice => choice.type !== 'NCrystalSphereCell'
    && choice.kind !== 'divine_cell' && !(c && combatInspectionButtons.has(choice.type))
    && !(d.kind === 'timeline' && epochHistory.has(choice.kind))));
  return compact({ ...(value.decision ? { status: value.status, action: value.action, remaining: value.remaining,
      blocker: value.blocker,
      deckChanges: value.deckChanges,
      selectedChoice: value.selectedChoice ? groupChoices([value.selectedChoice])[0] : undefined,
      outcomes: value.results?.filter(result => result.status || result.action || result.selectedChoice || result.deckChanges)
        .map(result => ({ status: result.status, action: result.action,
          selectedChoice: result.selectedChoice ? groupChoices([result.selectedChoice])[0] : undefined,
          deckChanges: result.deckChanges })) } : {}),
    kind: d.kind, at: { act: d.act, floor: d.floor, totalFloor: d.totalFloor, room: d.room },
    player: p && { name: p.name, hp: p.hp, maxHp: p.maxHp, block: p.block, gold: p.gold,
      energy: p.energy, maxEnergy: p.maxEnergy, stars: p.stars, statuses: p.statuses,
      potions: p.potions, relics: p.relics?.filter(relic => inventory || relic.counter != null || relic.usedUp === true
        || relic.stacks > 1 || relic.status && relic.status !== 'Normal').map(relic => ({ id: relic.id, name: relic.name,
        counter: relic.counter, stacks: relic.stacks, status: relic.status, usedUp: relic.usedUp })) },
    combat: c && { round: c.round, side: c.side, phase: c.phase, ready: c.ready,
      hand: groupCards(c.hand?.filter(card => !choiceCards.has(card.ref))),
      handInChoices: c.hand?.filter(card => choiceCards.has(card.ref)).map(card => card.ref),
      enemies: c.enemies, allies: c.allies?.filter(ally => ally.ref !== p?.ref && !petRefs.has(ally.ref)), orbs: c.orbs,
      orbSlots: c.orbSlots, orbOrder: c.orbOrder, pets: c.pets, incoming: incoming(c),
      ongoing: c.ongoing, noLivingEnemies: c.noLivingEnemies, inactiveEnemies: c.inactiveEnemies,
      drawCount: c.drawCount, discardCount: c.discardCount, exhaustCount: c.exhaustCount },
    selection: d.selection && { ...d.selection, selected: d.selection.selected?.map(card => ({ ref: card.ref, name: card.name })) },
    preview: d.preview,
    event: d.event, dialogue: d.dialogue, shop: d.shop,
    choices, nextRooms: d.nextRooms,
    map: map ? d.map?.map(point => ({ ref: point.ref, at: `${point.col},${point.row}`, type: point.type,
      state: point.state, boss: point.boss, to: point.leadsTo.map(next => `${next.col},${next.row}`) })) : undefined,
    result: d.result, navigation: d.navigation, epoch: d.epoch, unlock: d.unlock,
    timeline: d.timeline && { ...d.timeline, epochs: undefined,
      history: { completed: (d.timeline.epochs ?? []).filter(e => e.kind === 'inspect_epoch').length,
        locked: (d.timeline.epochs ?? []).filter(e => e.kind === 'locked_epoch').length } },
    crystalSphere: sphereView(d.crystalSphere),
    outcomeText: d.outcomeText ?? value.outcomeText, automaticSteps: d.automaticSteps ?? value.automaticSteps,
    text: c ? undefined : d.text?.filter(line => line !== d.event?.description
      && !d.dialogue?.text?.includes(line) && !choices.some(choice => choice.label === line)), pending: d.pending });
}
