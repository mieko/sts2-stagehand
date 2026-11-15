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

function groupChoices(choices = [], diagnostics = false) {
  const groups = new Map();
  for (const { rect, detail, activation, ...choice } of choices) {
    // Only true card copies are interchangeable. Keep individual shop offers,
    // map coordinates and arbitrary UI choices separately addressable.
    const cardChoice = ['select_card', 'deselect_card'].includes(choice.kind);
    const groupKey = cardChoice
      ? JSON.stringify([choice.kind, choice.enabled, choice.selected, cardVariant({ detail })])
      : choice.ref;
    if (!groups.has(groupKey)) {
      const model = detail?.item ?? detail?.relic ?? detail?.potion ?? detail;
      groups.set(groupKey, {
        ref: choice.ref,
        label: choice.label,
        kind: choice.kind,
        enabled: choice.enabled,
        selected: diagnostics || cardChoice ? choice.selected : (choice.selected ?? undefined),
        blockedReason: diagnostics
          ? choice.blockedReason
          : (choice.blockedReason ?? (choice.enabled === false ? null : undefined)),
        blockedBy: diagnostics ? choice.blockedBy : (choice.blockedBy ?? undefined),
        price: diagnostics ? choice.price : (choice.price ?? undefined),
        offerRef: diagnostics ? choice.offerRef : (choice.offerRef ?? undefined),
        cardRef: diagnostics ? choice.cardRef : (choice.cardRef ?? undefined),
        model,
        ...(cardChoice ? { count: 0, refs: [] } : {}),
      });
    }
    if (cardChoice) {
      const group = groups.get(groupKey);
      group.count++;
      group.refs.push(choice.ref);
    }
  }
  return [...groups.values()];
}

export function incoming(combat) {
  const intents = (combat?.enemies ?? []).flatMap((enemy) => enemy.intents ?? []);
  const attacks = intents.filter((intent) => intent.isAttack || intent.totalDamage != null);
  const unknownAttacks = attacks.filter((intent) => intent.totalDamage == null).length;
  const hiddenIntents = intents.filter((intent) => /hidden|unknown/i.test(intent.type)).length;
  const knownDamage = attacks.reduce((sum, intent) => sum + (intent.totalDamage ?? 0), 0);
  return {
    knownDamage,
    totalDamage: unknownAttacks || hiddenIntents ? null : knownDamage,
    unknownAttacks,
    hiddenIntents,
    scope: 'native_local_player_intent_preview',
    note: 'Intent preview, before block; conditional effects, target changes and later actions can change the outcome.',
  };
}

const combatInspectionButtons = new Set([
  'NDrawPileButton',
  'NDiscardPileButton',
  'NExhaustPileButton',
  'NTopBarDeckButton',
  'NTopBarMapButton',
  'NSettingsButton',
  'NPeekButton',
]);

function cardView(card) {
  const { costScope, valueContext, canUpgrade, canRemove, ...view } = card;
  // Native nulls here mean absent/not assessed, unlike an unknown cost or value.
  for (const key of [
    'starCost',
    'deckRef',
    'canPlay',
    'legalTargets',
    'enchantment',
    'affliction',
    'upgrade',
  ])
    if (view[key] == null) delete view[key];
  if (card.canPlay !== false && card.reason == null) delete view.reason;
  if (card.replay === 0) delete view.replay;
  if (canRemove === false) view.canRemove = false;
  if (canUpgrade === false && card.upgraded !== true) view.canUpgrade = false;
  for (const key of ['mechanics', 'effects'])
    if (Array.isArray(view[key]) && view[key].length === 0) delete view[key];
  if (view.values && Object.keys(view.values).length === 0) delete view.values;
  return view;
}

function sphereView(sphere) {
  if (!sphere) return undefined;

  const { cells = [], tools, ...state } = sphere;
  const xs = [...new Set(cells.map((c) => c.detail.x))].sort((a, b) => a - b);
  const ys = [...new Set(cells.map((c) => c.detail.y))].sort((a, b) => a - b);
  const lookup = new Map(cells.map((c) => [`${c.detail.x},${c.detail.y}`, c.detail]));
  return {
    ...state,
    grid: {
      columns: xs,
      rows: ys.map((y) => ({
        y,
        cells: xs.map((x) => {
          const cell = lookup.get(`${x},${y}`);
          return !cell ? null : cell.hidden ? '?' : (cell.revealedItem ?? '.');
        }),
      })),
      legend: {
        '?': 'covered',
        '.': 'uncovered empty',
        itemNames: 'visible fragments, not necessarily a whole revealed item',
      },
    },
  };
}

// Retain game descriptions while moving repeated hover-tip definitions to the
// full decision/inspection API. Undefined projection fields need no output space.
function compact(value, explainEffects = false, diagnostics = false) {
  if (Array.isArray(value)) return value.map((item) => compact(item, explainEffects, diagnostics));

  if (!value || typeof value !== 'object') return value;

  if (!diagnostics && typeof value.name === 'string' && Object.hasOwn(value, 'cost'))
    value = cardView(value);
  explainEffects ||= diagnostics || value.kind === 'event_option';
  return Object.fromEntries(
    Object.entries(value)
      .filter(([, item]) => item !== undefined)
      .map(([key, item]) => {
        if (!diagnostics && key === 'values' && item && typeof item === 'object') {
          return [
            key,
            Object.fromEntries(
              Object.entries(item).map(([name, value]) => [
                name,
                compact({
                  value: value.preview,
                  base: value.base !== value.preview ? value.base : undefined,
                  enchanted:
                    value.enchanted !== value.preview && value.enchanted !== value.base
                      ? value.enchanted
                      : undefined,
                  unit: value.unit ?? undefined,
                  power: value.power ?? undefined,
                  meaning: value.meaning ?? undefined,
                  nativeType: value.unit == null ? value.nativeType : undefined,
                }),
              ]),
            ),
          ];
        }

        if (key === 'effects' && Array.isArray(item)) {
          return [
            key,
            item.map((effect) =>
              explainEffects
                ? compact(effect, true, diagnostics)
                : typeof effect === 'string'
                  ? effect
                  : effect.card || effect.mechanics?.length
                    ? compact({ name: effect.name, card: effect.card, mechanics: effect.mechanics })
                    : effect.name,
            ),
          ];
        }

        return [key, compact(item, explainEffects, diagnostics)];
      }),
  );
}

// Pure projection: printing an outcome never causes game input or another read.
export function present(value, { map = false, inventory = false, diagnostics = false } = {}) {
  const d = value.decision ?? value;
  const p = d.player;
  const c = d.combat;
  const choiceCards = new Set(
    (d.choices ?? [])
      .filter((choice) => ['select_card', 'deselect_card'].includes(choice.kind))
      .map((choice) => choice.cardRef ?? choice.ref),
  );
  const pets = c?.pets ?? [];
  const petRefs = new Set(pets.map((pet) => pet.ref));
  const epochHistory = new Set(['inspect_epoch', 'locked_epoch']);
  const choices = groupChoices(
    (d.choices ?? []).filter(
      (choice) =>
        diagnostics ||
        (choice.type !== 'NCrystalSphereCell' &&
          choice.kind !== 'divine_cell' &&
          choice.type !== 'NPingButton' &&
          !(
            choice.type === 'NEndTurnButton' &&
            choice.enabled === false &&
            !choice.blockedReason &&
            !choice.blockedBy
          ) &&
          !((c || d.selection) && combatInspectionButtons.has(choice.type)) &&
          !(d.kind === 'timeline' && epochHistory.has(choice.kind))),
    ),
    diagnostics,
  );
  return compact(
    {
      ...(value.decision
        ? {
            status: value.status,
            stateScope: value.stateScope,
            executionStatus: value.executionStatus,
            action: value.action,
            remaining: value.remaining,
            blocker: value.blocker,
            deckChanges: value.deckChanges,
            selectedChoice: value.selectedChoice
              ? groupChoices([value.selectedChoice], diagnostics)[0]
              : undefined,
            outcomes: value.results
              ?.filter(
                (result) =>
                  result.status || result.action || result.selectedChoice || result.deckChanges,
              )
              .map((result) => ({
                status: result.status,
                action: result.action,
                selectedChoice: result.selectedChoice
                  ? groupChoices([result.selectedChoice], diagnostics)[0]
                  : undefined,
                deckChanges: result.deckChanges,
              })),
          }
        : {}),
      kind: d.kind,
      at: { act: d.act, floor: d.floor, totalFloor: d.totalFloor, room: d.room },
      player: p && {
        name: p.name,
        hp: p.hp,
        maxHp: p.maxHp,
        block: p.block,
        gold: p.gold,
        facing: p.facing,
        energy: p.energy,
        maxEnergy: p.maxEnergy,
        stars: p.stars,
        statuses: p.statuses,
        potions: p.potions,
        relics: p.relics
          ?.filter(
            (relic) =>
              inventory ||
              relic.counter != null ||
              relic.usedUp === true ||
              relic.stacks > 1 ||
              (relic.status && relic.status !== 'Normal'),
          )
          .map((relic) => ({
            id: relic.id,
            name: relic.name,
            counter: relic.counter,
            stacks: relic.stacks,
            status: relic.status,
            usedUp: relic.usedUp,
          })),
      },
      combat: c && {
        round: c.round,
        side: c.side,
        phase: c.phase,
        ready: c.ready,
        hand: groupCards(c.hand?.filter((card) => !choiceCards.has(card.ref))),
        handInChoices: c.hand?.filter((card) => choiceCards.has(card.ref)).map((card) => card.ref),
        enemies: c.enemies,
        allies: c.allies?.filter((ally) => ally.ref !== p?.ref && !petRefs.has(ally.ref)),
        orbs: c.orbs,
        orbSlots: c.orbSlots,
        orbOrder: c.orbOrder,
        pets: c.pets,
        incoming: incoming(c),
        ongoing: c.ongoing,
        noLivingEnemies: c.noLivingEnemies,
        inactiveEnemies: c.inactiveEnemies,
        drawCount: c.drawCount,
        discardCount: c.discardCount,
        exhaustCount: c.exhaustCount,
      },
      selection: d.selection && {
        ...d.selection,
        selected: d.selection.selected?.map((card) => ({ ref: card.ref, name: card.name })),
      },
      preview: d.preview,
      event: d.event,
      dialogue: d.dialogue,
      shop: d.shop,
      choices,
      nextRooms: d.nextRooms,
      map: map
        ? d.map?.map((point) => ({
            ref: point.ref,
            at: `${point.col},${point.row}`,
            type: point.type,
            state: point.state,
            boss: point.boss,
            to: point.leadsTo.map((next) => `${next.col},${next.row}`),
          }))
        : undefined,
      result: d.result,
      navigation: d.navigation,
      epoch: d.epoch,
      unlock: d.unlock,
      timeline: d.timeline && {
        ...d.timeline,
        epochs: undefined,
        history: {
          completed: (d.timeline.epochs ?? []).filter((e) => e.kind === 'inspect_epoch').length,
          locked: (d.timeline.epochs ?? []).filter((e) => e.kind === 'locked_epoch').length,
        },
      },
      crystalSphere: sphereView(d.crystalSphere),
      outcomeText: d.outcomeText ?? value.outcomeText,
      automaticSteps: d.automaticSteps ?? value.automaticSteps,
      text: c
        ? undefined
        : d.text?.filter(
            (line) =>
              line !== d.event?.description &&
              !d.dialogue?.text?.includes(line) &&
              !choices.some((choice) => choice.label === line),
          ),
      pending: d.pending,
      operation: diagnostics ? d.operation : undefined,
    },
    false,
    diagnostics,
  );
}
