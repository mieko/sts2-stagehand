# Semantic API

- `decision()` gives the current kind of decision, player HP/block/gold/energy,
  relics/potions, current hand, visible enemies/intents/statuses, readable choices,
  prompt text and travelable next rooms. Disabled choices remain visible.
  Full-inventory potion rewards/shop offers include `blockedReason`; discard/use
  first rather than clicking and waiting for a replacement picker the game lacks.
- `state()` includes the complete visible map graph when open, raw UI types/bounds,
  and the latest tracked action. `state()`, `deck()`, `inspectCard()`, `piles()` and `waitFor()` are
  passive reads. `decision()` and command outcomes normalize the known forced UI
  steps below; a semantic decision read may therefore open a mandatory chest or
  advance a completed event.
- Card descriptions include the game's own effect explanations. Enchantments and
  afflictions carry names, amounts, descriptions and extra card text. These fields
  do not infer hidden draw order, future room contents or RNG results.
  Related-card hover tips include `effects[].card` with the actual public model's
  name, description, energy/star costs, type, target and upgrade state. These have
  `costScope: "printed_related_card"`, no action reference and no playability claim.
  Owned cards use `costScope: "current_card"`; combat star costs include the same
  global discounts as native payment, alongside the existing energy modifiers.
  `"X"` remains an X cost rather than a promised fixed payment.
  Small `mechanics` notes explain native rules left implicit by prose, including
  Void Form's budgets/stacking/counting, powered hits, multi-hit Vigor, star gains,
  Panache, potion X costs, player Plating, Death March, self-Doom, Reaper Form and
  Helmet's payment threshold. They are explanations, not strategy or
  a replacement rules engine; the binding targets the game version in the README.
- `play(cardOrExactName, enemyOrExactName?)` accepts stable snapshot objects. Names
  work when unambiguous; identical card copies are interchangeable. Cards with
  different costs/text/modifiers require a specific object. Enemies with identical
  names require a specific enemy object.
- `playCards([{card, target, select?}, ...])` accepts a whole planned sequence. A string
  or direct card-object entry means an untargeted play. Names resolve against the fresh
  hand at execution time. `{endTurn: true}` also waits for the next turn when
  combat continues; a final kill proceeds to rewards without trying to end a
  nonexistent turn. New choices stop the list and report `remaining`, unless an
  explicit `select` card/list was supplied for that play's selection.
- `deck()` returns the current deck, including upgrades/modifiers.
  `deck({upgrades: true})` includes the actual next-upgrade alternative for each
  eligible card. Real deck/selection cards have opaque references too; these reset
  across combat boundaries and reconnection/reload requires a fresh observation.
  Deck inspection also includes `deckRef`, a comparison-only identity for the actual
  permanent-deck card model that survives combat transitions; it is not an action
  reference and does not identify shuffled draw position.
  Upgrade alternatives have no actionable reference. Upgrade-selection choices include them too; previews use detached
  native clones, refresh the same dynamic/enchantment preview values as the native
  upgrade UI, and leave the source card, enchantment and deck unchanged. `piles()`
  returns discard/exhaust and the public draw-pile contents sorted with
  `draw.order: "unknown"`; it never supplies shuffled draw order.
- `inspectCard(cardOrOffer, {upgrades: true})` passively inspects an already observed
  card, including unowned reward/shop offers and selection cards. Pass the card,
  raw/compact choice object or opaque card reference; exact card names and catalog
  IDs are not references. It returns the same rich card projection used by `deck()`.
  `upgrade` contains its native next-upgrade alternative when eligible; omit
  `upgrades` to inspect just the current model. The alternative is a detached
  preview, with no actionable reference or playability claim. Costs reflect the
  model's current modifiers and native deck/combat preview context, not a promise
  about payment after acquisition or a later turn. Inspection does not acquire,
  upgrade, select or click the card and does not enter the mutation queue. References
  reset at combat boundaries/reload; use a fresh observation if a reference expires.
  Related-card hover tips without a reference remain printed facts, not catalog
  lookup handles.
- `endTurn()`, `usePotion(potion, enemy?)`, `discardPotion(potion)` use real game
  operations, returning their observed outcome and resulting state. End-turn can
  return `choice_required` for a real enemy-turn or start-of-turn card choice; the turn remains in
  progress. Resolve that choice explicitly, then `waitForAction(result.action)`
  observes the original action without submitting another end-turn request.
- `throwPotionAtShopkeeper(potion)` uses a currently offered `potion.targets`
  entry of kind `shopkeeper`. This is **before entering** the inventory. The
  native Foul Potion target lookup covers both ordinary and fake merchants; its
  native use action owns gold, dialogue or starting the fake merchant encounter.
  No outcome is reimplemented. When such a target is available, automatic shop
  entry stops so the caller retains that decision. The Fake Merchant branch uses
  the native path but has not yet been live-qualified.
- `choose(choiceOrExactLabel)` handles event options, rewards, rest options,
  chests/relics, menu/dialogue and other visible UI choices. Ambiguous names require
  the choice object. `pickCard(cardChoiceOrName)` handles reward/selection grids.
- `travel(room)` takes a currently travelable room object. `proceed()` chooses one
  clear Proceed/Confirm/Continue/Embark/Leave control; it never silently skips a
  reward. `upgradeCard(card)` and `removeCard(card)` carry out an explicitly chosen
  Smith/removal operation through its selection/confirmation UI.
  `selectCards(cardOrList, {confirm: true})` chooses an explicit selection plan.
  `confirmSelection()` / `cancelSelection()` handle actual selection
  previews. `startRun({character: "IRONCLAD"})`, `continueRun()`, `openShop()` and
  `leaveShop()` provide common entry/exit operations. Locked characters remain
  unavailable through the normal game path. Character selection waits for native
  control enabling/unlock animation before judging availability. The Random
  selection pseudo-character exposes the native selection description and unknown
  starting HP/gold/energy/relics, rather than its internal placeholder values.
- `buy(itemOrList)` takes visible shop choice objects or exact item names. Each
  purchase uses the native merchant operation and waits for its `Task<bool>`
  result before the next; acceptance or a changed widget is insufficient. Offer
  references change when a restocking relic replaces the underlying item. Card removal
  opens a further decision and stops the list there. `claimRewards(list)` similarly
  claims an explicit list of currently visible rewards and stops at any new choice.
  `removeCard(card)` waits for both selection confirmation and the parent purchase
  task. If `buy([...,'Card Removal Service'])` has already opened its removal grid,
  `removeCard(card)` continues that same purchase instead of buying again. The host
  marks that pending purchase `purpose: "card_removal"`; unrelated selections are
  never adopted. A preview already opened by another call remains explicit through
  `confirmSelection()` and `waitForAction(originalPurchaseAction)`.
- `takeRelic(offerOrName)` / `declineRelic()` resolve a relic offer. Chest opening
  and leaving a resolved treasure room are handled as forced presentation.
- `pickBundle(bundleChoice, {confirm: true})` resolves a visible bundle and its
  confirmation. Crystal Sphere decisions expose `crystalSphere` with remaining
  divinations, tool state and visible cells/tools. `setDivinationTool('big'|'small')`
  and `divineCell({x,y})` use the native controls/async cell handler. Hidden cell
  contents are never serialized. The final divination waits for the native loot
  decision, or the next decision when no loot remains. Resolving loot then leaves
  the finished sphere through its enabled native Proceed. Rewards are never
  automatically claimed. Big divination and loot were live-qualified; the refined
  settlement is regression-qualified. Bundle choices still need live coverage.
- `finishResults()` explicitly advances the actual result controls after they become
  ready, returning `runResult`, preserved outcome text and the next decision. Native
  result data includes `win`, `score`, `stats` and animation phase; HP zero during
  the ending is not treated as a loss. Structured result fields follow native
  summary calculations and have been exercised through complete wins.
  Host phases are `outcome`, `summary_animation`, `earned_epochs` (the enabled
  Timeline/Unlock step), `summary` (no earned epochs), `leaderboard`, and
  `transition`. They follow native control/animation state, not a button label or
  container's visibility. `earnedEpochCount` counts this run's discovered epochs;
  `pendingEpochReveals` is the currently obtained unrevealed count.
  `scoreUnlocksRemaining` is the separate score-based unlock pool, not the number
  of new epochs to reveal. `finishResults().runResult` retains the latest summary
  and counts before leaving; all Timeline reveals and queued acknowledgements stay
  explicit. Ordinary click settlement observes enabled-state and result changes
  even when the screen, control identities and labels remain unchanged.
- Timeline decisions expose epoch state and native unlock requirements.
  `revealEpoch(epochOrTitleOrId)` requires an obtained epoch;
  `inspectEpoch(...)` requires an already completed epoch. `closeEpoch()` and
  `confirmUnlock()` retain the explicit story/confirmation boundaries. Unlock
  results include native card/relic/potion descriptions or character identity as
  appropriate. Confirming may reveal another queued acknowledgement, so inspect
  the returned decision. Neither `decision()` nor `ready()` reveals epochs.
- `returnToMenu()` navigates the native menu stack when no run, tutorial or unlock
  operation blocks it. Pending epochs are preserved. On this game version,
  **any obtained, unrevealed epoch disables Singleplayer** when no run save exists;
  `navigation.pendingEpochReveals` and disabled-choice `blockedReason` explain it.
  Returning to the menu does not bypass this native progression rule.
  `startRun()` follows Standard mode after the first run. Tutorial/FTUE screens
  remain explicit `tutorial` decisions; acknowledge their visible choice and call
  `startRun()` again. Daily/custom setup and co-op are outside the current API.
- `claimGold()` claims ordinary gold. `openCardReward()` opens an ordinary
  `CardReward` offer only; it does not recover a stolen card or consume another
  `SpecialCardReward`. Special offers remain explicit `special_card_reward` choices
  reachable through `choose(offer)` or `claimRewards([offer])`.
- `collectLoot({gold: true, potions: false, relics: [], openCardReward: true}, options)`
  names the common post-fight intent: collect the requested non-card loot, then
  open an ordinary card offer without choosing a card. The shown values are the
  defaults. `potions: true` requests all offered potions; a list requests specific
  reward objects or exact labels. Relics always require an explicit list. Claims
  run gold, potions, then relics in order, using the existing native reward path.
  Unrequested loot and special rewards remain available. Full potion inventory
  returns `choice_required` with `blocker.kind: "potion_inventory_full"`, applied
  `results` and unsubmitted `remaining`; it never discards or replaces a potion
  automatically. This ordinary inventory decision does not stop the local queue:
  explicitly use/discard a potion, then claim the still-offered remaining reward.
  Do not repeat already applied claims. `claimRewards()` uses this same boundary.
  A new native choice stops the helper. Card offers are only opened when enabled;
  it never skips a reward, picks a card, chooses an optional relic, or retries an
  unconfirmed claim. `openCardReward: false` leaves the ordinary offer unopened.
- `ready()` waits for a normalized decision; `decision({wait: true})` is equivalent.
  `waitFor(predicate, {timeoutMs})` observes raw state via subscription, with a
  fresh state read after subscription setup. `waitForAction(action, {timeoutMs})`
  passively waits for that tracked action's completion or next explicit choice
  and returns its state/decision; it does not execute or normalize UI. `screenshot()` returns a PNG Buffer
  in `result.image`. `game.client` remains available for generic inspection/input.
- `close()` closes this connection. It does not quit the game or cancel an action
  that the game already accepted.

Examples, each usable as a later independent tool invocation:

```js
// At an already observed card reward; does not choose the offer.
var offer = d.choices.find(choice => choice.label === 'Know Thy Place');
var card = await game.inspectCard(offer, {upgrades: true});
console.log({current: card.description, upgraded: card.upgrade?.description});
```

```js
var d = await game.decision();
var enemy = d.combat.enemies[0];
var result = await game.playCards([
  {card: 'Setup Strike', target: enemy},
  {card: 'Strike', target: enemy},
  'Defend',
], {endTurn: true});
// Inspect result before ending the turn if there was another choice or a failure.
```

```js
await game.buy(['Strength Potion', 'Shrug It Off']);
```

```js
var d = await game.decision();
await game.travel(d.nextRooms.find(room => room.type === 'RestSite'));
// Inspect actual options before choosing a rest action.
```

## Forced presentation versus decisions

Navigation and command results finish at the next settled decision. The adapter
performs only these identified presentation steps, reporting `automaticSteps`:

- Open a mandatory chest, wait until taking/declining is enabled, expose its actual
  relic offer, and leave after that offer is resolved. No relic is automatically taken.
- Open a merchant's inventory so the next decision presents items/prices, except
  when a usable potion exposes a shopkeeper target before entry.
- Leave a rewards screen after every reward has been claimed or explicitly skipped.
- Leave a rest site after no rest option remains and its Proceed control is enabled.
- Leave an event only when the actual EventModel.IsFinished and option IsProceed
  flags establish completion. A Continue/Proceed label alone never suffices.
- Advance enabled ancient dialogue until actual event options appear.
- After the last Crystal Sphere use, wait for the native reward flow. Leave the
  finished sphere only after its native reward task enables Proceed.

Event/dialogue outcome text is retained in `outcomeText` on both the command result
and its `decision`. UI results also retain `selectedChoice`, including its game
description/effects. Purchases, card/relic choices, paid event branches and skips
remain explicit. `state()` and the generic `game.client` give the underlying UI
when debugging presentation itself. Card-grid choices enumerate the native complete eligible model list, independently
of virtualized holders. The grid-owned filtered list also covers combat-pile
pickers such as Hologram; the screen base list can deliberately be empty. Selection invokes the same native `OnCardClicked` handler
connected to HolderPressed; hand choices use their Pressed signal, and ordinary controls their registered
ForceClick handler; character selection uses its native Select operation (the game
selects those controls on focus rather than a click signal). Generic mouse input
is the fallback for controls without an exposed operation.

Descriptions and effect meanings come from the current game models/localized hover
tips. `[Energy]` represents the game's energy icon; a number of icons is preserved
as icons rather than guessing grammatical quantities. Structured costs are numeric
or `"X"`; absent star costs are null. UI role names are the adapter's vocabulary.

## Queue and completion contract

Mutating helpers share one FIFO per `game`. Callers may submit several without
awaiting between them; reads remain available while they execute. Waiting on a
helper waits for its game outcome/next decision, not just JSON-RPC acceptance.
Batches execute locally without another model turn per item. They are sequential
game actions, not an atomic transaction; earlier successful actions remain applied.

The host owns interaction readiness and tracked-action status. A card-choice
overlay can be `ready: true` while ordinary `combat.ready` remains false, including
choices awaited before normal drawing in the player's Start phase. Such a choice
reports the original pending action as `choice_required`, then returns to running
or completed as the native operation continues. Clients consume those facts rather
than infer action status from screen names or combat phases. Character selection
and finished Crystal Sphere reward transitions likewise expose readiness only when
their actual next interaction is available.

Card actions track the game's action state and CompletionTask. Cancellation stays
cancellation even if that task resolves normally. End-turn waits for the next
playable round or combat's resulting decision. Potion actions track the matching
UsePotionAction. Choice-required is an intermediate decision, not action completion.
Batches stop there and return `remaining`; resolve the visible choice before
submitting more. UI clicks return `observed_change` rather than claiming a game
queue task they do not own.

Timeouts, cancellations, failures and unconfirmed UI clicks stop further queued
mutations. They never retry or pretend to cancel an accepted game action. Inspect
`state()` and any error's `outcome`/batch `results`, then explicitly call
`resumeQueue()` when appropriate. It refuses while the tracked game action is
still accepted/running. Use `waitFor` to observe completion without sending another
action. Every successful mutating helper includes a semantic `decision` from its
finished snapshot; callers do not need another poll to learn the next decision.
If a batch fails after earlier items, do not replay the whole list.


## Inspecting versus normalizing

Use `state()`, `deck()` and `piles()` for passive observation. `decision()` and
`ready()` can advance the documented presentation-only steps. Epoch reveals,
new unlock confirmations and tutorials stay explicit, even with one available
option. A caller inspecting another user's run should use passive methods.

Draw-pile contents are an unordered public multiset. Opaque references do not
indicate draw position. Refresh objects after combat boundaries or reconnects.
On batch failure, inspect `error.results` and `error.remaining`; a play awaiting
explicit selection may include nested `selection.results`/`selection.remaining`.
Completed batch outcomes reuse the operation's completion snapshot rather than
requiring a redundant final observation. Do not replay already-applied items.

Combat orbs retain native queue order. `combat.orbOrder` is
`"evoke_next_first"`; each orb has a 1-based `queuePosition` and `evokeNext` flag.
The first current orb is next for the native EvokeNext operation. This is not a
screen-space left/right cue or an assumption about channeling age; game effects
can reorder the queue. Native Dualcast evokes that same front orb twice before
removing it. The presenter preserves these cues and order.

## Compact agent view and observations

`await game.brief()` returns one concise semantic decision. It has the same
normalization behavior as `decision()`. `game.present(result)` (or the exported
pure `present(result)`) formats an already obtained decision/command result;
it never reads or changes the game. Print that instead of printing the full raw
result, which retains rich snapshots for debugging.

`game.present(result, {map: true, inventory: true})` optionally includes the full
map graph and static relic inventory. Both default to false, so ordinary outcomes
and spectator views keep `nextRooms` and meaningful relic counters/stacks/status
without repeating that bulk. These are pure output options, not a cache of prior
positions. Full map/inventory inspection remains available in rich state.

The presenter groups genuinely identical cards with `count` and individual
`refs`; pass `{ref: group.refs[1]}` to address another copy. Enchantments,
afflictions, text/cost and playability differences remain distinct. Offers keep
individual references, prices and descriptions; player gold is retained.
Compact combat keeps card descriptions/modifiers, legality, statuses and intent
semantics. Relic output retains counters, stacks, non-normal status and
used-up facts, including zero counters. Hover-tip effects show
names, related-card facts and any mechanics clarification without repeating other
full definitions during combat. Event/Ancient offer choices retain native effect
definitions such as Swift and Glam, so their consequences can be read before
choosing. Pile/map/settings inspection buttons and raw combat label dumps
are omitted, including during selection. Full descriptions, effects and
controls remain in `decision()`/`state()`/`piles()` for explicit inspection. The
presenter is a projection, not a replacement game state.

Architect dialogue uses combat-layout speech bubbles. The host projects only the
current visible bubble into `dialogue.text` with `source: "speech_bubble"`, never
future lines. Its native event choices remain explicit; the projection does not
auto-advance that speech. Compact output prints the spoken text once.

Selection choices contain each selectable card model once. Hand cards also present
among those choices appear as references in `combat.handInChoices`, with other hand
models retained in `combat.hand`. `combat.pets` keeps real companion state, including
Osty when not alive; `combat.allies` omits copies already represented as the local
player or pets. This is stateless deduplication, not a cache of earlier observations.
The Timeline view summarizes completed/locked history counts while retaining all
new reveal decisions. Raw Timeline choices remain available for explicit inspection.

An active `combat` includes `ongoing: true` and `noLivingEnemies`. When true, the
encounter is still active with no living enemy targets; it is not a victory signal.
`inactiveEnemies` contains only current retained dead creatures and public statuses,
without future form stats, moves or predicted revival. The usual hand/end-turn
legality still comes from the game.

`selectedChoice`, concise batch choice outcomes and actual `deckChanges` remain
visible in presented results. Crystal Sphere has one coordinate-indexed matrix:
`?` is covered, `.` is uncovered empty, and an item type names a visible fragment.
`revealedItems` lists only whole items the native game has already revealed; a
fragment does not imply a reward was earned. Cells/tools are not printed again as
a second grid. Raw decisions retain individually addressed controls.

A compact map graph uses `at`/`to` coordinate strings. Raw `decision().map` retains
coordinate objects and all edges. Boss nodes include the encounter identity used
by the current map's visible boss icon; ordinary hidden encounters are not exposed.

Native attack intents include `damagePerHit`, `hits`, `totalDamage`, `isAttack`
and `damageScope`. Non-attack numbers are null. `combat.incoming` summarizes known
local-player intent previews; hidden/unknown intents make the total unknown.
It is not predicted HP loss: block, conditional effects, later actions and target
changes can alter the outcome. Descriptions retain targeting/special-effect context.

Deck-changing UI helpers (`choose`, `pickCard`, `selectCards`,
`confirmSelection`, `upgradeCard`, `removeCard`) snapshot the out-of-combat deck
before submission and after the settled result. `deckChanges` reports added,
removed and changed cards. A single observed replacement includes
`replacement: {before, after}`; multiple changes are not paired speculatively.
These observations add two deck RPCs outside combat. When an observed event
choice enters combat, the client carries that pre-event snapshot through the
encounter and reports the actual permanent-deck changes when it settles back out
of combat. No per-card deck RPCs are added. `deckRef` correlates the same deck model
across action-reference resets. This observation baseline belongs to that client
session; reconnecting mid-event cannot reconstruct an earlier snapshot. A failed final observation
preserves the applied outcome in `error.outcome`; it is not an instruction to retry.
An action that only opens a preview reports no completed deck change yet.

## Passive spectator frames

`await observer.spectate({after: previousFrame, timeoutMs: 20000, signal})`
waits for a different native-ready gameplay position using the existing changed
subscription. It never enters the mutation queue, calls `decision()`/`ready()`,
normalizes UI, clicks, claims a reward or selects anything.

The first call returns the current settled position. Subsequent calls wait for a
new position even when `after` is omitted. Each result is
`{position, boundary, view}`: a local delivery ordinal, `settled_position` or
`choice_required`, and the compact semantic view. Keep the returned frame itself
as the continuation; `after` must be the latest frame from this same observer
object. It is an in-memory cursor, not a serialized/reconnectable event history.
One `spectate` wait may be active per observer. Separate clients can observe the
same human/API-driven game independently.

```js
var frame = await observer.spectate({timeoutMs: 20000});
nodeRepl.write(frame.view);
// Later, while a human or another client drives:
frame = await observer.spectate({after: frame, timeoutMs: 20000});
nodeRepl.write(frame.view);
```

An ordinary Node script can use the same loop, replacing `nodeRepl.write` with
its own output. Keep a tool invocation's execution timeout longer than `timeoutMs`.
Optional `signal` is an AbortSignal. Timeout, abort, disconnect and endpoint
removal release the wait's listeners/subscription; they do not stop game actions
or poison the local action queue. A read already sent to the host may still
complete after cancellation. Rich passive inspection remains `state()`, `deck()`
and `piles()`; the extra read is not an atomic capture of an earlier frame.

Positions compare structured gameplay facts, ignoring raw label/typewriter text,
dialogue ticks, widget rectangles/handles and adapter request-status-only churn.
A native-ready choice is visible while its owning action remains pending, including
an enemy-turn card choice. A running combat action is not reported as completed.
This works without an adapter action ID for human-driven changes.

The boundary describes an observed game position, **not a global client-batch
completion**. Another client may immediately start its next queued card. Existing
host notifications are sampled/coalesced; fast intervening positions can be missed,
and no-op inputs or a return to an indistinguishable position need not yield a
frame. This is neither a log of every input nor proof that all animation has stopped.
There is no observer timer polling, replay or automatic reconnection.

## Spectating and ending a run

`game.setSpectatorPacing(400)` adds a 400ms wall-time dwell before each local game
activation, including preview confirmations inside a queued plan. Default is zero;
`game.setSpectatorPacing()` restores full speed. Range is 0–5000ms. This does not
pause the game, require extra decisions or change completion conditions. Passive
observations remain available during a dwell. It is a local client preference,
not a game setting.

`await game.abandonRun()` explicitly abandons an active single-player run through
`RunManager.Abandon`, the same native operation used by the in-run confirmation
popup. It waits for the real game-over decision; then use `finishResults()` and
handle any native unlocks explicitly. It does not fabricate progress or force an
unlock. This is destructive to that run: use only when actually choosing to abandon.
Saved runs at the main menu can still be managed through their native UI; this
helper requires an in-progress run. The native abandon/results/earned-unlock flow
has been exercised with Regent and Necrobinder; this does not qualify their combat
systems or imply that abandoning always earns an unlock.
