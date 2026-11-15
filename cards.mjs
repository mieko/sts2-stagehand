// Content identity is shared by exact-name resolution, grouping and deck changes.
export function cardVariant(card) {
  const c = card.detail ?? card;
  return JSON.stringify([c.id, c.name ?? card.label, c.cost, c.starCost, c.type, c.description,
    c.target, c.upgraded, c.enchantment, c.affliction, c.replay, c.upgrade, c.canPlay, c.reason, c.legalTargets]);
}

export function deckChanges(before, after) {
  const previous = new Map(before.map(card => [card.deckRef ?? card.ref, card]));
  const current = new Map(after.map(card => [card.deckRef ?? card.ref, card]));
  const removed = before.filter(card => !current.has(card.deckRef ?? card.ref));
  const added = after.filter(card => !previous.has(card.deckRef ?? card.ref));
  const changed = after.filter(card => previous.has(card.deckRef ?? card.ref) && cardVariant(previous.get(card.deckRef ?? card.ref)) !== cardVariant(card))
    .map(card => ({ before: previous.get(card.deckRef ?? card.ref), after: card }));
  // A one-for-one replacement is observable; multiple replacements cannot be
  // paired honestly from snapshots alone.
  return { removed, added, changed,
    replacement: removed.length === 1 && added.length === 1 ? { before: removed[0], after: added[0] } : undefined };
}
