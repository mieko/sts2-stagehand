# Service operations

The `Sts2` endpoint executes ordered plans, selections, purchases and rewards in
the game. Use the WebSocket at `/rpc` on the Stagehand base address to submit
plans and read their results.
The [JavaScript API](api.md) adds a local queue, completion waits, presentation
and deck comparisons. The connection also provides the
[developer endpoints](security.md) when Developer Mode is enabled.

Discover the registered `Sts2` object through Stagehand. `operate` submits work;
`operation` reads its receipt. Both use ordinary `object.call`, alongside native
`request`, `select_card`, `inspect` and observation.

## Submit and observe

Submit using current card/target references:

```json
{
  "jsonrpc": "2.0",
  "id": "plan-1",
  "method": "object.call",
  "params": {
    "object": "THE_STS2_ENDPOINT_ID",
    "method": "operate",
    "params": {
      "kind": "playCards",
      "plays": [{ "card": "Strike", "target": { "ref": "THE_ENEMY_REF" } }, "Defend"],
      "endTurn": true
    }
  }
}
```

The RPC result acknowledges acceptance, not completion:

```json
{
  "operation": "THE_OPERATION_ID",
  "kind": "playCards",
  "status": "accepted",
  "completionTimeoutMs": 150000
}
```

Subscribe to `changed` **before** reading the observation to avoid missing a fast
completion. `state.operation` has `operation`, `kind`, `status`,
`completionTimeoutMs` and `atChoiceStop`. The last flag says whether the current
position still matches the operation's choice stop; later human input does not
rewrite its historical receipt.

At a stop, fetch the receipt:

```json
{
  "jsonrpc": "2.0",
  "id": "result-1",
  "method": "object.call",
  "params": {
    "object": "THE_STS2_ENDPOINT_ID",
    "method": "operation",
    "params": { "operation": "THE_OPERATION_ID" }
  }
}
```

Receipts contain `operation`, `kind`, `status` and, after settlement, `outcome`.
`accepted`/`running` are in flight. Stops are `completed`, `choice_required`,
`observed_change`, `ready`, `failed`, `cancelled` and `unconfirmed`. A UI change or
choice stop is not necessarily native-action completion.

`outcome` retains final `state`, historical `results[i].state` (including nested
selections), unsubmitted `remaining`, native action IDs, choices, errors and
`automaticSteps`. Step history becomes available at the stop, not as a live stream.
Travel/shop/menu activations retain an `attempted` record, including the chosen
room/shop/menu identity. Menu return and run setup retain ordered `results`, including
partial progress on failure or an uncertain final activation. Their internal
navigation steps have no automatically resumable tail; inspect the current position
before making a new explicit request. Travel waits for actual ready floor/act arrival;
its completion allowance is a caller wait, not an eight-second UI cancellation timer.

Only the latest receipt is retained: fetch it before submitting another operation.
Omit the request's `operation` to read that shared latest receipt, which may belong
to another caller. An expired/unknown ID does not mean its actions failed.

## Operations

| `kind`         | Parameters and defaults                                                                                                                                                                                            |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `playCards`    | `plays`: names/current card objects or `{card, target?, select?}`. `select` is an explicit choice/list. Optional `endTurn: false`.                                                                                 |
| `selectCards`  | `cards`: name/current choice or list. Select unselected copies; optional `confirm: true` confirms when enabled.                                                                                                    |
| `buy`          | `items`: offer name/current object or list; resolve each against the shop after the preceding purchase.                                                                                                            |
| `claimRewards` | `items`: reward name/current object or list. No implicit card/relic choice.                                                                                                                                        |
| `collectLoot`  | Optional `gold: true`, `potions: false` (boolean/list), `relics: []` (explicit list only), `openCardReward: true`. Claim requested loot, then open ordinary cards without picking; leave special rewards explicit. |
| `choose`       | `choice`: current object, exact label or reference. Activate its native handler and wait for the changed ready position.                                                                                           |
| `travel`       | `room`: a currently travelable map-room object/reference object. Revalidates current map membership and native legality after pacing; waits for ready arrival on a different floor/act.                            |
| `openShop`     | Enter the current ordinary/Fake Merchant inventory through its native handler. Explicit entry may pass a shopkeeper-potion decision; automatic settlement still stops there. Already open returns `ready`.         |
| `returnToMenu` | Pop the current native menu stack only while no run/tutorial/unlock operation blocks it. Revalidates each pop and waits for ready menu transitions; preserves pending epochs.                                      |
| `startRun`     | Optional `character`: exact visible name or native character ID. Follows native Singleplayer/Standard identity, selects an available character and confirms. Tutorial/FTUE/unlock boundaries remain explicit.      |
| `settle`       | Perform [forced presentation](api.md#forced-presentation-versus-decisions). Optional `outcome` preserves prior progress, not authority to act on stale state.                                                      |

Common options:

- `autoSkipDialogue: false`: `true` advances nonbranching Ancient speech to actual
  offers. Resets per request; does not affect other callers or other forced settlement.
- `humanDelayMs: 0`: integer 0–5000, wall-time dwell before each native activation.

Names follow the [API identity rules](api.md#observe-and-identify). The host
validates identity, eligibility, targets, readiness and payment. Unsupported native
controls fail explicitly; use generic Stagehand input/inspection if needed.

## Stops, choices and partial progress

Plans are sequential, not atomic. Failure/cancellation leaves earlier steps
applied and stops the plan.

| Stop                          | Retained progress / next step                                                                                                                                                                                                                                                                                                            |
| ----------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `choice_required`             | The native action may still be waiting; its ID/status remain in `state.pending` and the step receipt. Resolve the selection, then observe that same action. Inspect the new position before submitting the unsubmitted tail; it never auto-resumes.                                                                                      |
| `unconfirmed`                 | Uncertain activation or post-acceptance observation failure. Retains the attempted action/choice and earlier results; the attempted step is **excluded** from `remaining`. Its post-action `state` may be null and the action may still be running. Do not replay it; inspect when observation returns.                                  |
| Failed/unconfirmed settlement | Outer state is last observed (`stateScope: "last_observed"`); unavailable `automaticOutcome.state` stays null. Applied results, outcome text and completed `automaticSteps` survive. `executionStatus` preserves the preceding execution result; `automaticOutcome` describes presentation failure and outer `status` the composed stop. |

Explicit selection plans wait for each selection's change, confirmation, and the
same parent action before continuing. Standalone `selectCards` with `confirm: false`
completes selections only, not the suspended parent. Combat ending stops unused
plays and suppresses end-turn. Full potion inventory is a `choice_required` stop
for rewards/shop buys, retaining earlier progress without failing the JS FIFO.
When a selection closes and its same native parent resumes, UI acknowledgment is
complete; waiting for that parent's turn/animation completion uses the gameplay
wait rather than the eight-second UI acknowledgment bound.
A confirmation that immediately reaches another selection returns `choice_required`
with the same parent action, even without an observable intermediate running frame.

The native result screen entering `summary_animation` acknowledges Continue before
its score unlocks and discoveries finish. The service then waits for the actual
next ready decision, including when settlement first observes an ongoing summary;
the eight-second UI acknowledgment bound does not limit that native animation.
Historical completed combat actions do not acknowledge a new result input.

Forced settlement follows native completion/enabled controls, never a button label
alone. It does not choose optional cards/relics, acknowledge tutorials or reveal
epochs. Settlement failure cannot undo an action or make it safe to replay.

## Lifetime and client responsibilities

Accepted work belongs to the running mod. Socket closure, lost responses, aborted
waits and client deadlines do not cancel it. It runs to completion, choice or
failure. There is no automatic reconnect/replay; process exit loses receipts.
Native save recovery is [separate](install.md#recovering-an-unresponsive-instance).

Only one service operation runs at a time. Concurrent `operate` and external
low-level native requests/card selections are refused while it runs; passive reads
remain available. Human/generic input can still change the game, so each native
step validates current state.

JS callers use [recovery and queue resumption](api.md#queue-and-completion-contract).
Service operation IDs differ from native action IDs used by `waitForAction()`.

| Wait                              | Default / meaning                                                                                                                                                                                                                                                         |
| --------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Connection/handshake and each RPC | 10 seconds; independent of gameplay.                                                                                                                                                                                                                                      |
| `completionTimeoutMs`             | 60-second settlement margin + (30 seconds + pacing) per planned activation, including selections, confirmation and end-turn. `collectLoot` budgets visible rewards plus opening cards; `startRun` allows four activations and `returnToMenu` uses the current menu depth. |
| Single low-level native action    | 60 seconds.                                                                                                                                                                                                                                                               |
| Passive `waitFor` / spectator     | 30 seconds.                                                                                                                                                                                                                                                               |
| UI confirmation                   | Eight-second bound.                                                                                                                                                                                                                                                       |

The JS facade uses `completionTimeoutMs`; other clients can read it from acceptance.
Explicit `timeoutMs` overrides only the local completion wait. These are wait
allowances, not animation estimates or cancellation timers; tracked actions finish
according to native status.

Adopt matching DLL, PCK and client together. This facade requires the service
methods; it does not fall back to client-side execution against an older mod.
