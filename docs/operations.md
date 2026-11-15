# Service operations

The StS mod owns ordered card plans, card selections, purchases, explicit reward
collection, native choice activation and forced presentation settlement. JavaScript,
Python, Ruby and direct JSON-RPC callers invoke the same operations. The JS facade
adds its familiar method names, a local FIFO, observation waits and presentation;
it does not implement another batch executor.

Use Stagehand discovery to find the registered `Sts2` object. Its `operate` and
`operation` methods are ordinary registered object operations, alongside `request`,
`select_card`, `inspect` and observation. No change to Stagehand's protocol is needed.

## Submit and observe

For example, after observing current card and target references:

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
      "plays": [
        {"card": "Strike", "target": {"ref": "THE_ENEMY_REF"}},
        "Defend"
      ],
      "endTurn": true
    }
  }
}
```

The ordinary RPC result is acceptance:

```json
{"operation": "THE_OPERATION_ID", "kind": "playCards", "status": "accepted", "completionTimeoutMs": 150000}
```

Subscribe to the endpoint's `changed` event, then read its observation. Install
that subscription before reading so a fast completion cannot fall between the
read and subscription. `state.operation` contains `operation`, `kind`, `status` and
`completionTimeoutMs`. Its `atChoiceStop` flag identifies whether the current
observation still matches that operation’s choice stop. Historical receipt status
stays unchanged when a human or another input path progresses the game.
While it is `accepted` or `running`, the service still owns submitted work. At its
stop, fetch the receipt with another ordinary object call:

```json
{
  "jsonrpc": "2.0",
  "id": "result-1",
  "method": "object.call",
  "params": {
    "object": "THE_STS2_ENDPOINT_ID",
    "method": "operation",
    "params": {"operation": "THE_OPERATION_ID"}
  }
}
```

The receipt has `operation`, `kind`, `status` and, after settlement, `outcome`.
`accepted` and `running` are in flight. Terminal stops include `completed`,
`choice_required`, `observed_change`, `ready`, `failed`, `cancelled` and
`unconfirmed`; a ready UI change is distinct from native-action completion.
The outcome retains the final `state`, each step’s historical `results[i].state`,
unsubmitted `remaining`, native action IDs where applicable, choices, errors and
automatic presentation steps. Explicit selection results also retain their own
historical snapshots. Later game changes do not rewrite that evidence.
Only the latest service receipt is retained. Fetch it before submitting another
operation; an unknown/expired ID is not evidence that its actions failed. Omitting
`operation` from the passive status request reads the latest retained operation.
That shared latest result may belong to another caller; inspect it before acting.

## Operations

| `kind` | Parameters |
| --- | --- |
| `playCards` | `plays: [...]`, optional `endTurn: false`. A play is a card name/current card object, or `{card, target?, select?}`. `select` is an explicit card choice or list for that play. |
| `selectCards` | `cards`: card name/current choice object or list; optional `confirm: true`. Chooses unselected copies, then confirms if requested and enabled. |
| `buy` | `items`: offer name/current offer object or list. Each purchase resolves against the current shop after the preceding purchase. |
| `claimRewards` | `items`: reward name/current choice object or list. No implicit card/relic choice. |
| `collectLoot` | Optional `gold: true`, `potions: false` (boolean or explicit list), `relics: []` (explicit list only), `openCardReward: true`. Opens an ordinary card offer after the requested loot, without picking it. Special rewards remain explicit. |
| `choose` | `choice`: current choice object, exact label or reference. Activates its real native handler and waits for the changed ready position. |
| `settle` | Advance only the documented forced presentation steps from current native state. Optional `outcome` retains the caller's already obtained result/progress; it does not authorize actions from its old snapshot. |

All operations accept optional `autoSkipDialogue: false`. Explicit true advances
nonbranching Ancient conversation until actual offers; false leaves the current
speech/advance decision intact. This operation preference is reset for each
request, so client languages can keep it as a connection/session setting without
affecting other callers. Chest and other forced settlement remain independent.

All operations accept optional `pacingMs: 0` in 0–5000, an integral wall-time dwell
before each native activation. UI confirmation has an eight-second bound. Native
tracked actions wait for their actual status rather than being declared complete
because a timer expired. Unknown controls without a supported native activation
fail explicitly; use Stagehand's generic input/inspection primitives for that case.

Names must be unambiguous in the current observation. Identical card copies are
interchangeable; different descriptions, modifiers, costs or native values require
a specific reference. The native host still owns identity, eligibility, target,
readiness and payment validation. The service calls those existing paths.

## Stops, choices and partial progress

A plan executes sequentially, not atomically. An illegal later step leaves earlier
completed steps applied and reports the actual unsubmitted tail. A native failure
or cancellation stops the plan. An uncertain UI activation returns `unconfirmed`;
it is not retried, and its attempted step is recorded separately from the tail.
An observation failure after native acceptance/activation likewise returns
`unconfirmed`, retaining the action or selected choice and all earlier results.
The attempted step is excluded from `remaining`; its post-action `state` may be
null when unavailable. Failed forced settlement retains the last observed
position in the outer outcome with `stateScope: "last_observed"`; its attempted
`automaticOutcome.state` stays null when the new position is unavailable.
The native action may still be running. Inspect current
state when observation becomes available; uncertainty does not authorize replay.

An unexpected native choice stops with `choice_required`. The native action may
still be waiting: its ID/status remain in `state.pending` and the corresponding
step receipt. Explicit `selectCards` can resolve the current selection; observe the
same native action afterward rather than playing the card or ending the turn again.
The stopped work-order's remaining tail is not automatically resumed. Submit a new
intent only after inspecting the new position.

An explicit selection plan can choose first and second cards and confirm as part
of the original play. Each selection waits for its own observed change. Completing
the selection then waits for the same native parent before proceeding. A standalone `selectCards` with
`confirm: false` completes only those selection requests, not the suspended
parent action. Combat
ending stops unused plays and suppresses a requested end-turn. Full potion
inventory is an ordinary `choice_required` boundary for rewards and shop buys,
with prior claims/purchases retained, without failing the local FIFO.

Forced settlement uses native event completion, reward resolution, shop ownership
and actual enabled controls. It never infers a completed event from its button
label, chooses an optional relic/card, acknowledges a tutorial or reveals an epoch.
Outcome text and completed `automaticSteps` survive a later settlement failure.
In that case `executionStatus` retains the preceding execution result, while
`automaticOutcome` describes the failed/unconfirmed presentation step and `status`
reports the composed operation's stop. This does not undo an applied action.
The full list remains in [the semantic API](api.md#forced-presentation-versus-decisions).

## Lifetime and client responsibilities

An accepted plan is a work-order owned by the running mod. Closing a socket,
losing a response, aborting a JS wait or reaching a client deadline does not cancel
it. It continues to completion, a real choice or failure while the mod is running.
No automatic reconnect, replay or gameplay cancellation is implied. A native game
cancellation remains a cancellation. A process exit loses in-memory operation
receipts; native save recovery is a separate matter.

One service operation runs at a time. Concurrent `operate` calls are refused;
external low-level native requests/card-selection calls are also refused while
that operation is running. Passive observations remain available. This is not
multiplayer arbitration: a human or explicit generic input can still change the
game, and the next native step must pass current-state validation.

Ordinary JS methods wait and return their outcomes; callers need not track operation
IDs. On an interrupted wait, `await game.recover()` passively waits for the last
accepted operation’s stop and returns the same rich results/remaining/decision.
On that same facade it also recovers an interrupted individual native action. A
fresh connection recovers the latest retained service operation. It does not
normalize UI, replay anything or resume the local FIFO; inspect the outcome and
explicitly acknowledge the stopped queue with `resumeQueue()` when appropriate.
Recovery while work is running awaits its stop; completed step history is retained
by that execution and becomes available at its stop, not streamed as live history.

Connection/handshake and individual RPC deadlines stay short (ten seconds by
default). They are independent of gameplay completion. The service supplies
`completionTimeoutMs`: a sixty-second settlement margin plus thirty seconds and
requested pacing per planned activation, including explicit selections, confirmation
and end-turn. `collectLoot` uses the visible reward count plus opening the card
offer as its bound. This is a conservative wait allowance, not a measured animation
duration or a timer that cancels gameplay. Native tracked actions still finish
according to native status. The JS facade uses that allowance automatically;
other-language callers can use the same acceptance field. An explicit `timeoutMs`
overrides only the local completion wait. Single low-level native-action waits
default to sixty seconds; passive `waitFor`/spectator waits default to thirty.

Diagnostic `operation(id)` and an interrupted error’s `operation` remain available.
A service ID is distinct from a native `action` ID: `waitForAction()` still waits for
native actions. The local FIFO stops on failure/uncertainty; `resumeQueue()` refuses
while either the native action or service operation is still running. Outcomes
include rich state and a pure JS decision. `present(outcome)` summarizes that
result without printing historical snapshots or service tracking; `{diagnostics:true}`
includes service tracking details. Other languages consume the same underlying facts.

Some conveniences still compose calls in JS: menu/run setup, explicit ending/
unlock navigation, specialized screen helpers and client-session deck-change
comparisons. Pure presentation, subscriptions, local wait deadlines and FIFO also
remain external. Those conveniences are separate from the service's shared execution operations.

Adopt the matching DLL, PCK and client together. The service methods are additive
wire capabilities, but this JS facade requires them; it does not silently fall
back to a second client-owned workflow implementation against an older mod.
