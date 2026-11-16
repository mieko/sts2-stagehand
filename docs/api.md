# Semantic API

## REPL quick start

See [setup and connection](../README.md#connect-and-play). Import/connect once in
a persistent JS session; retain `game` between invocations:

```js
var { connectLocal } = await import('/absolute/path/to/sts2-stagehand/client/local.mjs');
var game = await connectLocal();
var d = await game.decision();
console.log(game.present(d));
```

Choose legal cards/targets from that observation. For example, with these cards
in hand:

```js
var result = await game.playCards([
  {card: 'Strike', target: d.combat.enemies[0]},
  'Defend',
], {endTurn: true});
console.log(game.present(result));
```

Every successful mutation returns its resulting `decision`; inspect it before
continuing. Batches stop at new choices or failures and retain applied progress.
**Do not replay a whole batch after interruption.** See [recovery](#queue-and-completion-contract).

## Observe and identify

| Method | Result / behavior |
| --- | --- |
| `decision({wait: false})` | Decision kind, player resources, inventory, hand, enemies/intents/statuses, choices, prompt and `nextRooms`. May advance [forced presentation](#forced-presentation-versus-decisions). |
| `ready()` / `decision({wait: true})` | Wait for a normalized decision. |
| `brief()` | A compact decision, with the same normalization as `decision()`. |
| `present(result, options)` | Pure compact projection of an existing decision/outcome; no game read or mutation. Also exported as `present`. |
| `state()` | Passive rich state: visible map graph when open, raw UI types/bounds and tracked action. |
| `deck({upgrades: false})` | Passive permanent-deck cards, including upgrades/modifiers. `true` adds each eligible card's next `upgrade`. |
| `inspectCard(cardOrOffer, {upgrades: false})` | Passive rich card projection, including unowned reward/shop offers and selection cards. `true` adds the native next `upgrade` when eligible. |
| `piles()` | Passive discard/exhaust and sorted public draw multiset, with `draw.order: "unknown"`. |
| `waitFor(predicate, {timeoutMs, signal})` | Passive raw-state subscription wait; reads fresh state after subscribing. Default 30 seconds. |
| `waitForAction(action, {timeoutMs})` | Passively await that native action's completion or next choice; returns state/decision without normalizing. |
| `screenshot()` | Capture metadata (`width`, `height`, `mime`, etc.) plus `image: Buffer` containing PNG bytes. Save/display `shot.image`. |
| `info()` | Passive native release version, commit, branch, release date and build-time assembly hash; works without a run. Missing metadata is unavailable. |

Use passive reads when watching someone else's run. Descriptions/effects come
from current game models and localized hover tips, not inferred hidden state.
No shuffled draw order, future rewards, hidden room contents or RNG are exposed.

Identity rules:

- Exact names/labels work when unambiguous. Identical card copies are interchangeable;
  different text, costs, modifiers, native values or playability require a specific
  object. Same-named enemies require a specific enemy object.
- Refresh snapshot objects/references after combat boundaries, reconnect or reload.
  `deckRef` identifies the same permanent-deck model across combat transitions for
  comparison only: it is not an action reference or draw position.
- `inspectCard` accepts an observed card, raw/compact choice or opaque card reference,
  **not** a name/catalog ID. Upgrade alternatives and related-card hover tips have
  no actionable reference or playability claim. Inspection uses detached upgrade
  previews without changing/acquiring/selecting the source card.
- Compact identical-card groups have `count` and individual `refs`;
  `{ref: group.refs[1]}` addresses another copy. Offers keep individual identities.

```js
// At an observed card offer; inspection does not choose it.
var offer = d.choices.find(choice => choice.label === 'Know Thy Place');
var card = await game.inspectCard(offer, {upgrades: true});
console.log({current: card.description, upgraded: card.upgrade?.description});
```

## Play and choose

| Method | Behavior |
| --- | --- |
| `play(cardOrExactName, enemyOrExactName?)` | Play through the native action path. |
| `playCards(plays, {endTurn: false})` | Ordered plan. Entries are `{card, target?, select?}`, a name or an untargeted card object. Names resolve against the fresh hand at execution. `select` supplies an explicit selection card/list; otherwise new choices stop with `remaining`. `endTurn: true` waits for the next playable round, but is suppressed if combat ends. |
| `endTurn()` | Wait for the next playable round or resulting decision. An enemy/start-of-turn choice returns `choice_required`: resolve it, then `waitForAction(result.action)`, not another end-turn. |
| `usePotion(potion, enemy?)` / `discardPotion(potion)` | Native potion operations and observed outcomes. |
| `throwPotionAtShopkeeper(potion)` | Use a currently offered `potion.targets` entry of kind `shopkeeper`, **before entering** inventory. Native Foul Potion behavior owns gold/dialogue/combat. Fake Merchant combat is not yet live-qualified. |
| `choose(choiceOrExactLabel)` | Visible event, reward, rest, relic, menu, dialogue or other supported native choice. Ambiguous labels require an object. No implicit mouse fallback; unsupported controls require explicit `game.client` input. |
| `pickCard(cardChoiceOrName)` | Choose from a reward/selection grid. |
| `selectCards(cardOrList, {confirm: true})` | Explicit selection plan; `false` selects without completing the suspended parent action. |
| `confirmSelection()` / `cancelSelection()` | Resolve actual selection previews. |
| `upgradeCard(card)` / `removeCard(card)` | Explicit Smith/removal operation through selection and confirmation. |
| `travel(room)` | Take a currently travelable room object. |
| `proceed()` | Choose one clear Proceed/Confirm/Continue/Embark/Leave control; never silently skip a reward. |

Selection choices enumerate the complete native eligible card list, including
virtualized grids/combat-pile pickers. A choice can be `ready: true` while
`combat.ready` is false and its parent action is still pending.

### Loot and shops

| Method | Behavior |
| --- | --- |
| `openShop()` / `leaveShop()` | Enter/leave native shop UI. Automatic entry pauses when a usable potion offers a shopkeeper target. |
| `buy(itemOrList)` | Visible shop objects or exact names, sequentially; each native purchase must finish before the next. Restocked offers get new references. Card removal opens a decision and stops the list. |
| `claimRewards(list)` | Explicit visible rewards; stops at a new choice. |
| `claimGold()` | Claim ordinary gold. |
| `openCardReward()` | Open an ordinary `CardReward`, without choosing a card. Special/stolen-card rewards remain `special_card_reward` choices for `choose(offer)` or `claimRewards([offer])`. |
| `takeRelic(offerOrName)` / `declineRelic()` | Explicitly resolve a relic offer. |
| `pickBundle(bundleChoice, {confirm: true})` | Choose a visible bundle and confirm. Rare variants remain a compatibility limitation; inspect offered controls. |
| `setDivinationTool('big'\|'small')` / `divineCell({x,y})` | Native Crystal Sphere controls. `crystalSphere` exposes remaining divinations, tools and visible cells only. Final divination waits for native loot/next decision; rewards remain explicit. |

`removeCard(card)` waits for confirmation **and** the parent purchase. If a prior
`buy([...,'Card Removal Service'])` opened the removal grid, it continues that
`purpose: "card_removal"` purchase, not an unrelated selection or a second purchase.
An already opened preview remains explicit: use `confirmSelection()` and
`waitForAction(originalPurchaseAction)`.

```js
await game.collectLoot({potions: true}); // Gold + potions, then open ordinary cards.
```

`collectLoot({gold: true, potions: false, relics: [], openCardReward: true}, options)`
shows the defaults. Claims run gold → potions → explicitly listed relics, then
open an enabled ordinary card offer without picking it. `potions` accepts `true`
for all, or a list of reward objects/exact labels. Unrequested/special rewards
remain; `openCardReward: false` leaves the card offer closed.

Full inventory returns `choice_required`, `blocker.kind: "potion_inventory_full"`,
applied `results` and unsubmitted `remaining`. It does not discard/replace potions
or stop the local FIFO. Explicitly use/discard one, then claim the still-offered
reward; do not repeat applied claims. `claimRewards` and shop buys share this
boundary. Blocked potion choices retain `blockedReason`.

### Runs, results and unlocks

| Method | Behavior |
| --- | --- |
| `startRun({character: "IRONCLAD"})` / `continueRun()` | Native single-player setup/continue. Start follows Standard after the first run; locked characters remain unavailable. |
| `finishResults()` | Wait for and advance actual result controls; return latest `runResult`, outcome text and next decision. Epoch reveals/acknowledgements remain explicit. |
| `revealEpoch(epochOrTitleOrId)` | Reveal an obtained epoch. |
| `inspectEpoch(epochOrTitleOrId)` | Inspect an already completed epoch. |
| `closeEpoch()` / `confirmUnlock()` | Explicit story/confirmation boundaries. Inspect the next decision: acknowledgements may be queued. |
| `returnToMenu()` | Navigate when no run, tutorial or unlock operation blocks it; preserve pending epochs. |
| `abandonRun()` | **Destructive:** abandon an in-progress single-player run through the native operation, then wait for game-over. Use `finishResults()` and handle unlocks explicitly; no unlock is guaranteed. Not for saved runs at the menu. |

Tutorial/FTUE remains a `tutorial` decision: acknowledge its choice, then call
`startRun()` again. Obtained, unrevealed epochs disable Singleplayer when no run
save exists; `navigation.pendingEpochReveals` and `blockedReason` explain why.
Timeline decisions retain native requirements; unlock results include card/relic/
potion descriptions or character identity. Daily/custom setup and co-op are outside
this API. Random character selection reports unknown starting resources/relics,
not placeholder stats; availability waits for native enabling/unlock animation.

## Queue and completion contract

Mutations share one FIFO per facade; reads remain available. Helpers wait for a
game outcome/next decision, not RPC acceptance. Batches execute in the mod,
sequentially, **not atomically**. Results retain final `state`/`decision`, historical
`results[i].state`, and nested `selection.results`/`selection.remaining` where relevant.

- `choice_required` is a stop, not necessarily native-action completion. Resolve
  the visible choice and observe its original action before submitting more.
  The unsubmitted `remaining` tail does not automatically resume.
- Failures, cancellation, timeouts and unconfirmed UI clicks stop queued mutations.
  Inspect `error.outcome`, `error.results`, `error.remaining` and current state.
  Native cancellation stays cancellation; UI clicks report `observed_change`, not
  completion of a game task they do not own.
- `await game.recover()` passively awaits/reads the last submitted operation after
  interruption, including applied results, remainder and next decision. On the
  same facade it also recovers individual native actions; a fresh connection uses
  the latest retained shared operation, possibly another caller's. It neither
  replays nor normalizes. Inspect it before explicitly `resumeQueue()`; resumption
  refuses while a native action or service operation is accepted/running.
- Failed forced settlement does not undo the preceding action. `error.outcome`
  retains applied results, last known state and completed `automaticSteps`;
  `automaticOutcome` identifies the failed/unconfirmed presentation step.
- Gameplay waits scale with plan length/pacing. Explicit `timeoutMs` overrides the
  local gameplay wait, not independent connection/RPC deadlines (10 seconds by
  default). Disconnects/deadlines do not cancel accepted work or authorize replay.

See [service operations](operations.md) for receipts, uncertain attempted steps and
[completion allowances/lifetime](operations.md#lifetime-and-client-responsibilities).
Only the latest service receipt is retained; process exit loses it. Diagnostic
`operation(id)` reads that receipt; its service ID is not a native action ID for
`waitForAction`. For a stalled instance, use the
[recovery procedure](../README.md#recovering-an-unresponsive-instance).

## Forced presentation versus decisions

`decision()`, `ready()` and command settlement may perform these steps, recorded
in `automaticSteps`:

- Open mandatory chests, wait for enabled relic choices, then leave **after** resolution.
- Open merchant inventory, except at a usable shopkeeper-potion decision.
- Leave rewards after all are claimed/skipped; leave rest sites when no option
  remains and Proceed is enabled.
- Leave events only when native `IsFinished` and `IsProceed` establish completion,
  never from a button label alone.
- After final Crystal Sphere use, wait for native rewards and enabled Proceed.
- Advance Ancient speech **only** with `setDialogueAutoskip(true)`. Default is off,
  per connection; `false` disables it. Normal output is speech plus one
  `advance_dialogue` choice. Autoskip stops at actual offers.

Purchases, card/relic choices, paid branches, skips, tutorials and epoch/unlock
confirmations remain explicit. Event/dialogue `outcomeText` survives on both outcome
and decision; UI outcomes retain `selectedChoice` with descriptions/effects.
Architect's current visible speech bubble appears as `dialogue.text`,
`source: "speech_bubble"`; it is not auto-advanced or extended with future lines.

## Compact output and public state

Prefer `present(result)` to printing rich snapshots. Options `{map: true,
inventory: true}` add the full graph/static relic inventory; both default false.
Ordinary output keeps `nextRooms`, live relic counters/stacks/status/used-up facts,
card descriptions/modifiers/costs/legality, native values, copy counts, references,
selected choices and `deckChanges`. Zero resources stay zero; unknown stays unknown.

Routine absent metadata, inspection controls and raw labels are omitted. Combat
hover tips keep names/related-card facts/mechanics without repeating full definitions;
event/Ancient offers retain effect definitions. `{diagnostics: true}` restores card
metadata, full value/context/effect fields, inspection controls and service tracking.
Rich states and historical results are unchanged; this is a stateless projection.
Completed, error-free last actions are omitted from `pending` unless diagnostics
are enabled; active parents, failures and cancellations remain visible.

| State | Interpretation |
| --- | --- |
| Selection | Each selectable card appears once. Hand copies already in choices become `combat.handInChoices` references; other cards stay in `combat.hand`. Meaningful disabled choices remain visible. |
| Companions | `combat.pets` retains real state, including dead Osty; `combat.allies` omits duplicates of player/pets. No character-based filtering hides stars, pets or orbs. |
| Combat ending | `ongoing: true` with `noLivingEnemies: true` is **not victory**. `inactiveEnemies` has only retained dead creatures/public statuses, not future forms or revival predictions. Native hand/end-turn legality still applies. |
| Orbs | `combat.orbOrder: "evoke_next_first"`; 1-based `queuePosition` and `evokeNext` preserve native queue order, not screen position/age. Effects can reorder it; Dualcast evokes the front orb twice before removing it. |
| Intents | `damagePerHit`, `hits`, `totalDamage`, `isAttack`, `damageScope`; non-attack numbers are null. `combat.incoming` sums known local-player previews; unknown intents make the total unknown. These are not predicted HP loss. |
| Surrounded | Native `player.facing` is Left/Right; enemy `relativePosition` has `side`/`behind` from Back Attack powers. Ambiguous side gives `behind: null`; outside Surrounded both position/facing are null. Not a facing command or damage prediction. |
| Map | Compact `at`/`to` strings; raw `decision().map` keeps coordinate objects/edges. Boss identity comes from the visible icon, not hidden ordinary encounters. |
| Crystal Sphere | One coordinate matrix: `?` covered, `.` uncovered empty, item type for a visible fragment. `revealedItems` contains native-revealed whole items only; fragments do not establish earned rewards. Raw controls remain individually addressable. |
| Timeline/results | Compact Timeline summarizes locked/completed history but retains new reveals. `runResult` has native `win`, `score`, `stats`, animation phase and counts; HP zero during ending is not a loss signal. |

Deck-changing UI helpers (`choose`, `pickCard`, `selectCards`, `confirmSelection`,
`upgradeCard`, `removeCard`) compare pre/post out-of-combat decks: `deckChanges`
reports added/removed/changed cards, with `replacement: {before, after}` only for
a single observed replacement. Opening a preview alone is not a completed change.
This adds two deck RPCs, not per-card reads. An event entering combat carries its
pre-event baseline until combat settles; reconnect cannot reconstruct that baseline.
A failed final observation retains the applied result in `error.outcome`, not permission to retry.

## Passive spectator frames

On a separate observer connection:

```js
var frame = await observer.spectate({timeoutMs: 20000});
if (frame) console.log(frame.view);
// Later; retain the previous frame on idle timeout.
var next = await observer.spectate({after: frame ?? undefined, timeoutMs: 20000});
if (next) { frame = next; console.log(frame.view); }
```

`spectate({after, timeoutMs, signal})` passively returns `{position, boundary, view}`:
a local delivery ordinal, `settled_position` or `choice_required`, and compact view.
First call returns the current settled position; later calls wait for a new one
even without `after`. `after` must be the latest frame object from this observer,
not a serialized/reconnectable cursor. One wait per observer; default 30 seconds.
Idle returns null without advancing the cursor. `signal` is an AbortSignal; abort,
disconnect, endpoint removal and RPC failure reject without stopping game actions
or poisoning the FIFO. Keep the tool's timeout longer than this wait.

Frames use changed subscriptions, not polling or UI normalization. They ignore
label/typewriter/layout/request-status churn and expose ready choices even while
the parent action is pending. Running service work is not a settled frame;
`state.operation.atChoiceStop` distinguishes its current choice from an old receipt.
A frame is a position, not proof another client's whole queue or every animation
has ended. Coalescing can miss fast positions; no-op/indistinguishable changes need
not yield frames. No replay/reconnect is automatic. A later passive read is not
an atomic capture of an earlier frame; an already-sent read can finish after abort.

`setSpectatorPacing(400)` adds 400ms before each activation, including confirmation.
Default/reset `setSpectatorPacing()` is zero; range 0–5000ms. This connection-local
`pacingMs` preference does not pause the game or change completion; reads stay available.

## Required capabilities

Ordinary semantic gameplay uses only the registered `Sts2` endpoint; it does not
require Stagehand's `Object`, `SceneTree`, `Input` or `Script` builtin roots.
Travel, shop entry, menu return and Standard run setup execute in the mod's shared
[service operations](operations.md), with server-side validation and recovery receipts.
`screenshot()` separately requires the optional `Screen` capability. The current
mod startup still explicitly exposes all builtin roots for generic inspection,
input and DevTools testing.

## Advanced inspection and connection details

Card costs are numeric or `"X"`; absent star costs are null. `[Energy]` preserves
native icons. Owned cards use `costScope: "current_card"`, including current native
energy/star modifiers. `effects[].card` contains public related-card name/text,
cost/type/target/upgrade facts with `costScope: "printed_related_card"`, not handles
or payment promises. Enchantments/afflictions include names, amounts, descriptions
and extra text. `mechanics` clarifies native rules; it is not a rules engine.

| Preview field | Meaning |
| --- | --- |
| `values` | Native numeric variable keys with `nativeType`, `base`, `enchanted`, `preview`, units (unknown = null), and native-backed `meaning` where available. Display parameters, not a full effect list; repetitions are not necessarily hits. Boolean/string variables are excluded. |
| `valueContext.source` | `"cached_native_model"`: read existing values without recalculating the source. |
| `descriptionPile` / `modelPile` | Formatter context / actual pile (null for unplaced offers/previews). |
| `upgradePreview` | Native Deck/Combat/None upgrade context. Detached upgrade previews refresh native dynamic/enchantment values without changing the source. |
| `globalHooksEligible` | Eligibility for combat-hand/play or combat-upgrade modifiers, not evidence that hooks ran. Selection and hand previews may differ. |
| `target` / `prospectiveDamage` | `"not_recorded"` / `false`: no target simulation or promised per-enemy damage/HP loss. Preview values and payment costs are separate contexts, not future acquisition/turn guarantees. |

Result phases follow native readiness: `outcome`, `summary_animation`,
`earned_epochs`, `summary` (no earned epochs), `leaderboard`, `transition`.
`earnedEpochCount` counts this run's discovered epochs; `pendingEpochReveals`
counts obtained unrevealed epochs; `scoreUnlocksRemaining` is a separate score pool.
`finishResults().runResult` preserves the latest summary/counts before leaving.

`connectSts2(url, {token, WebSocket})` discovers the registered `Sts2` endpoint.
For generic Stagehand clients, pass exported `sts2Wrappers` to `connect()` and
use `await client.findObject('Sts2')`. `connectSts2(url, {client})` installs the
factory on an existing client without replacing other wrappers; discovery failure
closes only a connection it created. One facade per connection/endpoint shares
FIFO, recovery, pacing and spectator state; separate connections are independent.
Snapshots are plain data. Raw game metadata uses `inspect({kind: "game"})`, sourced
from native `ReleaseInfoManager`, not engine/session metadata.

`game.client` exposes generic inspection/input. `close()` closes the connection,
not the game or accepted work. Adopt matching DLL/PCK/client together; old generic
`game` registration is not an StS2 wrapper. Changed imports require a fresh module
graph/kernel, not just a new socket. See [README](../README.md) for deployment,
isolation and compatibility limits.
