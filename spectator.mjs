const activeAction = (pending) =>
  ['accepted', 'running', 'choice_required'].includes(pending?.status);

export function isSettledObservation(state) {
  if (!state.ready) return false;
  if (['accepted', 'running'].includes(state.operation?.status)) return false;
  // Native choice overlays may be ready while their owning action is suspended.
  // An accepted/running combat action is not itself a completed position.
  if (state.screen === 'NCombatRoom') return state.combat?.ready && !activeAction(state.pending);

  return true;
}

// Compare gameplay facts, not widget handles, rectangles, typewriter text,
// dialogue ticks or adapter request IDs. Model/card/creature refs remain useful
// identities; no hidden state or animation-stillness claim is introduced.
export function observationKey(state) {
  const choices = state.choices
    .map(({ type, label, enabled, selected, detail, cardRef, offerRef, isSkip, skipped }) =>
      JSON.stringify({
        type,
        label,
        enabled,
        selected,
        detail,
        cardRef,
        offerRef,
        isSkip,
        skipped,
      }),
    )
    .sort();
  return JSON.stringify({
    screen: state.screen,
    room: state.room,
    act: state.act,
    floor: state.floor,
    totalFloor: state.totalFloor,
    ascension: state.ascension,
    player: state.player,
    combat: state.combat,
    choices,
    selection: state.selection,
    preview: state.preview,
    event: state.event && { title: state.event.title, finished: state.event.finished },
    shop: state.shop && {
      kind: state.shop.kind,
      open: state.shop.open,
      canBrowse: state.shop.canBrowse,
    },
    map: state.map?.map(({ col, row, type, state, boss, leadsTo }) => ({
      col,
      row,
      type,
      state,
      boss,
      leadsTo,
    })),
    rewards: state.rewards,
    crystalSphere: state.crystalSphere,
    result: state.result,
    navigation: state.navigation && {
      atMainMenu: state.navigation.atMainMenu,
      canReturnToMenu: state.navigation.canReturnToMenu,
      pendingEpochReveals: state.navigation.pendingEpochReveals,
    },
    epoch: state.epoch && { title: state.epoch.title, unlocks: state.epoch.unlocks },
    unlock: state.unlock && {
      cards: state.unlock.cards,
      relics: state.unlock.relics,
      potions: state.unlock.potions,
      character: state.unlock.character,
    },
    timeline: state.timeline,
  });
}

export function observationBoundary(state) {
  return activeAction(state.pending) || state.operation?.atChoiceStop
    ? 'choice_required'
    : 'settled_position';
}
