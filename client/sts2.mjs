import { createRequire } from 'node:module';
import { cardVariant, deckChanges } from './cards.mjs';
import { present } from './present.mjs';
import { decisionKind, choiceView, decisionView } from './decision.mjs';
import { isSettledObservation, observationKey, observationBoundary } from './spectator.mjs';
export { present } from './present.mjs';

const { connect } = await import(createRequire(import.meta.url).resolve('godot-stagehand'));

const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
class DecisionTimeoutError extends Error {}
function cardByName(cards, name) {
  const matches = cards.filter((c) => (c.name ?? c.label).toLowerCase() === name.toLowerCase());
  const variants = new Set(matches.map(cardVariant));
  if (!matches.length || variants.size > 1)
    throw new Error('Card name is missing or has different variants; pass a specific card object');

  return matches[0];
}

function creatureTarget(state, target) {
  if (typeof target !== 'string') {
    if (target && !target.ref) throw new TypeError('Pass a creature object or exact creature name');

    return target;
  }

  const creatures = [...(state.combat?.enemies ?? []), ...(state.combat?.allies ?? [])].filter(
    (e) => e.ref === target || e.name.toLowerCase() === target.toLowerCase(),
  );
  if (creatures.length !== 1)
    throw new Error('Target is missing or ambiguous; use a creature object');

  return creatures[0];
}

function serviceOutcome(outcome) {
  const result = { ...outcome };
  if (result.selectedChoice) result.selectedChoice = choiceView(result.selectedChoice);
  if (result.blocker?.choice)
    result.blocker = { ...result.blocker, choice: choiceView(result.blocker.choice) };
  if (result.results) result.results = result.results.map(serviceOutcome);
  if (result.selection) result.selection = serviceOutcome(result.selection);
  if (result.automaticOutcome) result.automaticOutcome = serviceOutcome(result.automaticOutcome);
  return result;
}

// Rediscovery and decoded references must share the same FIFO and spectator state.
const facades = new WeakMap();
export const sts2Wrappers = {
  Sts2(remote) {
    let objects = facades.get(remote.client);
    if (!objects) {
      objects = new Map();
      facades.set(remote.client, objects);
    }
    if (!objects.has(remote.id)) objects.set(remote.id, new Sts2(remote.client, remote));

    return objects.get(remote.id);
  },
};

export async function connectSts2(url, options = {}) {
  const client =
    options.client ??
    (await connect(url, {
      ...options,
      wrappers: { ...sts2Wrappers, ...options.wrappers },
    }));
  client.wrappers.Sts2 ??= sts2Wrappers.Sts2;
  try {
    const game = await client.findObject('Sts2');
    if (!game) throw new Error('The adapter-local Sts2 host is not loaded');

    if (!(game instanceof Sts2)) {
      throw new Error('The host must register the Sts2 type; reload the matching mod/client');
    }

    return game;
  } catch (error) {
    if (!options.client) client.close();
    throw error;
  }
}

export class Sts2 {
  #tail = Promise.resolve();
  #queueError;
  #spectatorDelayMs = 0;
  #autoSkipDialogue = false;
  #deckBeforeCombat;
  #spectatorLast;
  #spectatorWaiting = false;
  #spectatorPosition = 0;
  #lastOperation;
  #lastAction;
  constructor(client, endpoint) {
    this.client = client;
    this.endpoint = endpoint;
  }
  async state() {
    return (await this.endpoint.observe()).value;
  }
  async info() {
    return this.endpoint.invoke('inspect', { kind: 'game' });
  }
  async deck({ upgrades = false } = {}) {
    return (await this.endpoint.invoke('inspect', { kind: 'deck', upgrades })).cards;
  }
  async inspectCard(card, { upgrades = false } = {}) {
    const model = card?.detail?.item ?? card?.detail ?? card?.model ?? card;
    const ref = typeof card === 'string' ? card : (card?.cardRef ?? model?.ref);
    if (typeof ref !== 'string' || !ref)
      throw new TypeError('Pass an observed card or offer with a card reference');

    return this.endpoint.invoke('inspect', { kind: 'card', card: ref, upgrades });
  }
  async piles() {
    return this.endpoint.invoke('inspect', { kind: 'piles' });
  }
  async operation(operation) {
    const receipt = await this.endpoint.invoke('operation', { operation });
    return {
      ...receipt,
      ...(receipt.outcome && { outcome: serviceOutcome(receipt.outcome) }),
    };
  }

  // Passive recovery: await the submitted work, never resubmit its remaining tail.
  async recover(options) {
    options?.signal?.throwIfAborted();
    await this.#tail;
    let outcome;
    if (this.#lastAction) outcome = await this.#waitAction(this.#lastAction, options);
    else {
      const receipt = await this.operation(this.#lastOperation);
      this.#lastOperation = receipt.operation;
      outcome = await this.#waitOperation(receipt, options);
    }
    return {
      ...outcome,
      decision: outcome.state ? decisionView(outcome.state) : undefined,
    };
  }
  async decision({ wait = false, ...options } = {}) {
    const state = wait ? await this.waitFor((s) => s.ready, options) : await this.state();
    if (!this.#queueError && state.ready)
      return (await this.#enqueue(async () => ({ status: 'ready', state: await this.state() })))
        .decision;

    return decisionView(state);
  }

  present(value, options) {
    return present(value, options);
  }
  async brief(options) {
    return present(await this.decision(options));
  }
  setSpectatorPacing(milliseconds = 0) {
    if (!Number.isFinite(milliseconds) || milliseconds < 0 || milliseconds > 5000)
      throw new RangeError('Spectator pacing must be in 0..5000 milliseconds');

    this.#spectatorDelayMs = milliseconds;
  }
  setDialogueAutoskip(enabled = false) {
    if (typeof enabled !== 'boolean') throw new TypeError('Dialogue autoskip must be boolean');

    this.#autoSkipDialogue = enabled;
  }
  async #pace() {
    if (this.#spectatorDelayMs) await pause(this.#spectatorDelayMs);
  }

  #enqueue(work, { trackDeck = false } = {}) {
    const result = this.#tail.then(async () => {
      if (this.#queueError)
        throw new Error('Action queue stopped: ' + this.#queueError.message, {
          cause: this.#queueError,
        });

      try {
        const beforeState = trackDeck ? await this.state() : null;
        const beforeDeck = beforeState?.player && !beforeState.combat ? await this.deck() : null;
        const outcome = await this.#settle(await work());
        if (beforeDeck && outcome.state?.combat) this.#deckBeforeCombat ??= beforeDeck;
        const baseline = this.#deckBeforeCombat ?? beforeDeck;
        if (baseline && outcome.state?.player && !outcome.state.combat) {
          try {
            outcome.deckChanges = deckChanges(baseline, await this.deck());
            this.#deckBeforeCombat = undefined;
          } catch (error) {
            error.outcome = outcome;
            throw error;
          }
        }
        if (outcome.state && !outcome.state.player) this.#deckBeforeCombat = undefined;
        if (['unconfirmed', 'failed', 'cancelled'].includes(outcome.status)) {
          const error = new Error(
            'Action did not finish successfully: ' +
              (outcome.warning ?? outcome.error ?? outcome.status),
          );
          error.outcome = outcome;
          error.results = outcome.results;
          error.remaining = outcome.remaining;
          throw error;
        }

        return {
          ...outcome,
          decision: outcome.state
            ? {
                ...decisionView(outcome.state),
                automaticSteps: outcome.automaticSteps,
                outcomeText: outcome.outcomeText,
              }
            : undefined,
        };
      } catch (error) {
        this.#queueError = error;
        throw error;
      }
    });
    this.#tail = result.catch(() => {});
    return result;
  }

  async resumeQueue() {
    await this.#tail;
    const state = await this.state();
    if (
      ['accepted', 'running'].includes(state.pending?.status) ||
      ['accepted', 'running'].includes(state.operation?.status)
    )
      throw new Error('The game action is still running; observe or wait for its outcome');

    this.#queueError = undefined;
    return state;
  }

  travel(point, options) {
    return this.#enqueue(() => this.#operate('travel', { room: point }, options));
  }

  async #operate(kind, params, options) {
    options?.signal?.throwIfAborted();
    this.#lastOperation = undefined;
    this.#lastAction = undefined;
    const accepted = await this.endpoint.invoke('operate', {
      kind,
      ...params,
      pacingMs: this.#spectatorDelayMs,
      autoSkipDialogue: this.#autoSkipDialogue,
    });
    this.#lastOperation = accepted.operation;
    return this.#waitOperation(accepted, options);
  }

  async #waitOperation(accepted, options) {
    try {
      let receipt = accepted;
      if (['accepted', 'running'].includes(accepted.status)) {
        await this.waitFor(
          (state) =>
            state.operation?.operation === accepted.operation &&
            !['accepted', 'running'].includes(state.operation.status),
          { ...options, timeoutMs: options?.timeoutMs ?? accepted.completionTimeoutMs ?? 60_000 },
        );
        receipt = await this.operation(accepted.operation);
      }
      return receipt.outcome;
    } catch (error) {
      error.operation = accepted.operation;
      throw error;
    }
  }

  async #settle(outcome) {
    if (
      !outcome.state ||
      outcome.operation ||
      ['unconfirmed', 'failed', 'cancelled'].includes(outcome.status)
    )
      return outcome;

    return this.#operate('settle', { outcome });
  }

  takeRelic(relic, options) {
    return this.#enqueue(async () => {
      const state = await this.state();
      const matches = state.choices.filter(
        (c) =>
          c.enabled &&
          (['NRelicBasicHolder', 'NTreasureRoomRelicHolder'].includes(c.type) ||
            (c.type === 'NRewardButton' && c.detail?.category === 'RelicReward')) &&
          (typeof relic === 'string'
            ? c.label.toLowerCase() === relic.toLowerCase()
            : c.ref === relic?.ref),
      );
      if (matches.length !== 1)
        throw new Error('Relic offer is missing or ambiguous; use an offer object');

      return this.#choose(matches[0], options);
    });
  }

  declineRelic(options) {
    return this.#enqueue(async () => {
      const state = await this.state();
      if (state.screen === 'NTreasureRoom') return this.#chooseType('NProceedButton', options);

      if (decisionKind(state) !== 'relic_selection') throw new Error('No relic offer is active');

      const skip = state.choices.find((c) => c.enabled && c.label === 'Skip');
      if (!skip) throw new Error('The game does not offer a decline action here');

      return this.#choose(skip, options);
    });
  }

  pickCard(card, options) {
    return this.#enqueue(() => this.#pickCard(card, options), { trackDeck: true });
  }

  pickBundle(bundle, { confirm = true, ...options } = {}) {
    return this.#enqueue(async () => {
      const state = await this.state();
      if (decisionKind(state) !== 'bundle_selection')
        throw new Error('No bundle selection is active');

      const matches = state.choices
        .filter((c) => c.type === 'NCardBundle' && c.enabled)
        .filter((c) => (typeof bundle === 'string' ? c.label === bundle : c.ref === bundle?.ref));
      if (matches.length !== 1)
        throw new Error('Bundle is missing or ambiguous; pass its choice object');

      let result = await this.#choose(matches[0], options);
      if (confirm && result.state.choices.some((c) => c.type === 'NConfirmButton' && c.enabled))
        result = await this.#chooseType('NConfirmButton', options);
      return result;
    });
  }

  setDivinationTool(tool, options) {
    return this.#enqueue(async () => {
      const state = await this.state();
      if (!state.crystalSphere) throw new Error('No Crystal Sphere decision is active');

      const button = state.choices.find(
        (c) => c.type === 'NDivinationButton' && c.enabled && c.detail?.tool === tool,
      );
      if (!button) throw new Error('That divination tool is unavailable');

      if (button.detail.active) return { status: 'completed', state };

      return this.#choose(button, options);
    });
  }

  divineCell({ x, y }, options) {
    return this.#enqueue(() => {
      if (![x, y].every((value) => Number.isInteger(value) && value >= 0 && value <= 2147483647))
        throw new TypeError('Cell coordinates must be nonnegative 32-bit integers');
      // BigInt uses the protocol's native-int encoding. Ordinary JSON numbers
      // become floats in Godot and fail the managed callback's GetInt32 parse.
      return this.#request('divineCell', { x: BigInt(x), y: BigInt(y) }, options);
    });
  }

  async #pickCard(card, options, { unselected = false } = {}) {
    const state = await this.state();
    const candidates = state.choices.filter(
      (c) =>
        c.enabled &&
        (!unselected || c.selected !== true) &&
        c.type.includes('CardHolder') &&
        !c.type.includes('Preview'),
    );
    const matches =
      typeof card === 'string'
        ? [cardByName(candidates, card)]
        : candidates.filter((c) => c.ref === card?.ref || c.cardRef === card?.ref);
    if (matches.length !== 1)
      throw new Error('Card choice is missing or ambiguous; pass its choice object');

    return this.#choose(matches[0], options);
  }

  selectCards(cards, options) {
    return this.#enqueue(() => this.#selectCards(cards, options), { trackDeck: true });
  }

  async #selectCards(cards, { confirm = true, ...options } = {}) {
    return this.#operate('selectCards', { cards, confirm }, options);
  }

  upgradeCard(card, options) {
    return this.#enqueue(
      async () => {
        let state = await this.state();
        if (state.screen === 'NRestSiteRoom') {
          const smith = state.choices.find(
            (c) => c.type === 'NRestSiteButton' && c.label === 'Smith' && c.enabled,
          );
          if (!smith) throw new Error('Smith is unavailable at this rest site');

          state = (await this.#choose(smith, options)).state;
        }
        if (state.screen !== 'NDeckUpgradeSelectScreen')
          throw new Error('No deck upgrade selection is active');

        let result = await this.#pickCard(card, options);
        if (decisionKind(result.state) === 'card_preview')
          result = await this.#chooseType('NConfirmButton', options);
        return result;
      },
      { trackDeck: true },
    );
  }

  removeCard(card, options) {
    return this.#enqueue(
      async () => {
        let state = await this.state();
        let purchase;
        if (
          state.screen === 'NDeckCardSelectScreen' &&
          state.pending?.kind === 'buy' &&
          state.pending.purpose === 'card_removal' &&
          state.pending.status === 'choice_required'
        ) {
          purchase = { action: state.pending.action, state };
        } else {
          if (decisionKind(state) !== 'shop')
            throw new Error(
              'removeCard needs a shop or its purchased removal grid; use selectCards/confirmSelection for the current choice',
            );

          const removal = state.choices.find((c) => c.type === 'NMerchantCardRemoval' && c.enabled);
          if (!removal) throw new Error('Card removal is unavailable or unaffordable');

          purchase = await this.#buy(removal, options);
          state = purchase.state;
          if (purchase.status !== 'choice_required' || decisionKind(state) !== 'card_selection')
            return purchase;
        }
        let result = await this.#pickCard(card, options);
        if (result.status !== 'observed_change') return result;

        if (decisionKind(result.state) === 'card_preview')
          result = await this.#chooseType('NConfirmButton', options);
        if (result.status !== 'observed_change') return result;

        return this.#waitAction(purchase.action, options);
      },
      { trackDeck: true },
    );
  }

  proceed(options) {
    return this.#enqueue(async () => {
      const state = await this.state();
      let matches = state.choices.filter(
        (c) =>
          c.enabled &&
          /^(proceed|continue|confirm|embark|leave|confirmbutton|previewconfirm)$/i.test(c.label),
      );
      if (!matches.length)
        matches = state.choices.filter((c) => c.enabled && c.type === 'NConfirmButton');
      if (matches.length !== 1)
        throw new Error('No single clear proceed/confirm choice; inspect decision().choices');

      return this.#choose(matches[0], options);
    });
  }

  confirmSelection(options) {
    return this.#enqueue(() => this.#chooseType('NConfirmButton', options), { trackDeck: true });
  }
  cancelSelection(options) {
    return this.#enqueue(() => this.#chooseType('NBackButton', options));
  }

  async #chooseType(type, options) {
    const state = await this.state();
    const matches = state.choices.filter((c) => c.enabled && c.type === type);
    if (matches.length !== 1)
      throw new Error(`No single enabled ${type} choice in the current decision`);

    return this.#choose(matches[0], options);
  }

  continueRun(options = {}) {
    return this.#enqueue(async () => {
      options = { timeoutMs: 30_000, ...options };
      await this.waitFor((state) => state.ready, options);
      return this.#chooseType('NMainMenuContinueButton', options);
    });
  }

  revealEpoch(epoch, options) {
    return this.#enqueue(() => this.#epoch(epoch, 'Obtained', options));
  }
  inspectEpoch(epoch, options) {
    return this.#enqueue(() => this.#epoch(epoch, 'Complete', options));
  }
  closeEpoch(options) {
    return this.#enqueue(async () => {
      if (decisionKind(await this.state()) !== 'epoch')
        throw new Error('No epoch detail screen is open');

      return this.#chooseType('NCloseButton', { timeoutMs: 30_000, ...options });
    });
  }
  confirmUnlock(options) {
    return this.#enqueue(async () => {
      if (decisionKind(await this.state()) !== 'unlock')
        throw new Error('No unlock confirmation screen is open');

      await this.waitFor(
        (s) => s.ready && s.choices.some((c) => c.type === 'NUnlockConfirmButton' && c.enabled),
        options,
      );
      return this.#chooseType('NUnlockConfirmButton', { timeoutMs: 30_000, ...options });
    });
  }

  async #epoch(epoch, requiredState, options) {
    const state = await this.state();
    const matches = state.choices
      .filter((c) => c.type === 'NEpochSlot' && c.enabled && c.detail?.state === requiredState)
      .filter((c) =>
        typeof epoch === 'string'
          ? [c.label, c.detail.id].some((v) => v?.toLowerCase() === epoch.toLowerCase())
          : c.ref === epoch?.ref,
      );
    if (matches.length !== 1)
      throw new Error(
        'Epoch is missing, ambiguous or not in the required state; inspect timeline.epochs',
      );

    return this.#choose(matches[0], { timeoutMs: 30_000, ...options });
  }

  returnToMenu(options) {
    return this.#enqueue(() => this.#operate('returnToMenu', {}, options));
  }

  finishResults(options) {
    return this.#enqueue(async () => {
      let state = await this.state();
      if (decisionKind(state) !== 'game_over') throw new Error('No run result screen is open');

      let runResult = state.result;
      const outcomeText = new Set(state.text),
        results = [];
      for (let step = 0; step < 2; step++) {
        state = await this.waitFor(
          (s) =>
            s.screen !== 'NGameOverScreen' ||
            s.choices.some(
              (c) =>
                c.enabled &&
                ['NGameOverContinueButton', 'NReturnToMainMenuButton'].includes(c.type),
            ),
          options,
        );
        if (state.screen !== 'NGameOverScreen') break;
        runResult = state.result ?? runResult;
        const button =
          state.choices.find((c) => c.enabled && c.type === 'NReturnToMainMenuButton') ??
          state.choices.find((c) => c.enabled && c.type === 'NGameOverContinueButton');
        for (const line of state.text ?? []) outcomeText.add(line);
        const result = await this.#choose(button, { timeoutMs: 30_000, ...options });
        results.push(result);
        state = result.state;
        runResult = state.result ?? runResult;
        if (result.status !== 'observed_change')
          return { ...result, results, runResult, outcomeText: [...outcomeText] };

        if (state.screen !== 'NGameOverScreen') break;
      }
      return {
        status: state.screen === 'NGameOverScreen' ? 'choice_required' : 'completed',
        state,
        results,
        runResult,
        outcomeText: [...outcomeText],
      };
    });
  }

  startRun({ character, ...options } = {}) {
    return this.#enqueue(() => this.#operate('startRun', { character }, options));
  }

  claimGold(options) {
    return this.#enqueue(async () => {
      let state = await this.state();
      const rewards = state.choices.filter(
        (c) => c.enabled && c.type === 'NRewardButton' && c.detail?.category === 'GoldReward',
      );
      const results = [];
      for (const reward of rewards) {
        const result = await this.#choose(reward, options);
        results.push(result);
        state = result.state;
        if (result.status !== 'observed_change') return { ...result, results };
      }
      return { status: 'completed', results, state };
    });
  }

  openCardReward(options) {
    return this.#enqueue(() => this.#openCardReward(options));
  }

  async #openCardReward(options) {
    const state = await this.state();
    if (decisionKind(state) === 'card_reward') return { status: 'ready', state };

    const reward = state.choices.find(
      (c) => c.enabled && c.type === 'NRewardButton' && c.detail?.category === 'CardReward',
    );
    if (!reward)
      throw new Error(
        'No ordinary card reward to open; choose a visible special_card_reward explicitly',
      );

    return this.#choose(reward, options);
  }

  openShop(options) {
    return this.#enqueue(() => this.#operate('openShop', {}, options));
  }

  leaveShop(options) {
    return this.#enqueue(async () => {
      let state = await this.state();
      if (decisionKind(state) === 'shop') {
        state = (await this.#chooseType('NBackButton', options)).state;
      }
      if (decisionKind(state) !== 'shop_entrance') throw new Error('Not at the shop entrance');

      return this.#chooseType('NProceedButton', options);
    });
  }
  close() {
    this.client.close();
  }
  async screenshot() {
    return (await this.client.builtin('Screen')).capture();
  }

  async waitFor(predicate, { timeoutMs = 30_000, signal } = {}) {
    if (!Number.isFinite(timeoutMs) || timeoutMs < 0 || timeoutMs > 2147483647)
      throw new RangeError('timeoutMs must be a finite nonnegative 32-bit duration');

    signal?.throwIfAborted();
    let wake,
      unsubscribe,
      finished = false,
      rejectStop;
    const stopped = new Promise((_, reject) => {
      rejectStop = reject;
    });
    const timer = setTimeout(
      () =>
        rejectStop(
          new DecisionTimeoutError(
            'Decision wait timed out; no game action was cancelled or retried',
          ),
        ),
      timeoutMs,
    );
    const closed = () => rejectStop(new Error('Stagehand disconnected while waiting'));
    const aborted = () => rejectStop(signal.reason);
    const removed = (item) => {
      if (item.method === 'objects.removed' && item.params.object === this.endpoint.id)
        rejectStop(new Error('Observation endpoint was removed'));
    };
    this.client.on('close', closed);
    this.client.on('notification', removed);
    signal?.addEventListener('abort', aborted, { once: true });
    const setup = this.endpoint
      .subscribe('changed', () => wake?.())
      .then((cancel) => {
        // Setup can complete after a timeout/abort. That subscription still belongs
        // to this wait and must not survive it.
        if (finished) cancel().catch(() => {});
        else unsubscribe = cancel;
      });
    try {
      await Promise.race([setup, stopped]);
      while (true) {
        let notify;
        const changed = new Promise((resolve) => {
          notify = resolve;
        });
        wake = notify;
        const state = await Promise.race([this.state(), stopped]);
        if (predicate(state)) return state;

        await Promise.race([changed, stopped]);
      }
    } finally {
      finished = true;
      clearTimeout(timer);
      this.client.off('close', closed);
      this.client.off('notification', removed);
      signal?.removeEventListener('abort', aborted);
      // The client detaches local routing synchronously; remote acknowledgment
      // must not extend this wait's deadline or cancellation.
      unsubscribe?.().catch(() => {});
    }
  }

  async spectate({ after, ...options } = {}) {
    if (this.#spectatorWaiting)
      throw new Error('This observer is already waiting; await or abort that spectate call');

    if (after !== undefined && after !== this.#spectatorLast?.frame)
      throw new Error(
        'Pass the last frame returned by this observer; a reconnect starts a new continuation',
      );

    this.#spectatorWaiting = true;
    try {
      const prior = this.#spectatorLast?.key;
      const state = await this.waitFor(
        (state) => isSettledObservation(state) && observationKey(state) !== prior,
        options,
      );
      const frame = {
        position: ++this.#spectatorPosition,
        boundary: observationBoundary(state),
        view: present(decisionView(state)),
      };
      this.#spectatorLast = { frame, key: observationKey(state) };
      return frame;
    } catch (error) {
      if (error instanceof DecisionTimeoutError) return null;

      throw error;
    } finally {
      this.#spectatorWaiting = false;
    }
  }

  ready(options) {
    return this.decision({ ...options, wait: true });
  }

  async waitForAction(action, options) {
    const outcome = await this.#waitAction(action, options);
    return { ...outcome, decision: decisionView(outcome.state) };
  }

  async #request(kind, params, options) {
    options?.signal?.throwIfAborted();
    await this.#pace();
    options?.signal?.throwIfAborted();
    this.#lastAction = undefined;
    this.#lastOperation = undefined;
    const accepted = await this.endpoint.invoke('request', { kind, ...params });
    this.#lastAction = accepted.action;
    try {
      return await this.#waitAction(accepted.action, options);
    } catch (error) {
      error.action = accepted.action;
      throw error;
    }
  }

  async #waitAction(action, options) {
    const state = await this.waitFor(
      (state) =>
        state.pending?.action === action &&
        !['accepted', 'running'].includes(state.pending.status) &&
        (state.ready || ['failed', 'cancelled'].includes(state.pending.status)),
      { ...options, timeoutMs: options?.timeoutMs ?? 60_000 },
    );
    return { action, status: state.pending.status, error: state.pending.error, state };
  }

  play(card, target, options) {
    return this.#enqueue(() => this.#play(card, target, options));
  }

  async #play(card, target, options) {
    if (typeof card !== 'string' && !card?.ref)
      throw new TypeError('Pass a hand card object or exact card name');

    const state = await this.state();
    if (typeof card === 'string') card = cardByName(state.combat?.hand ?? [], card);
    target = creatureTarget(state, target);
    return this.#request('play', { card: card.ref, target: target?.ref }, options);
  }

  abandonRun(options) {
    return this.#enqueue(() => this.#request('abandonRun', {}, options));
  }

  endTurn(options) {
    return this.#enqueue(() => this.#request('endTurn', {}, options));
  }

  playCards(plays, { endTurn = false, ...options } = {}) {
    return this.#enqueue(() => this.#operate('playCards', { plays, endTurn }, options));
  }

  async #buy(item, options) {
    return this.#operate('buy', { items: [item] }, options);
  }

  buy(items, options) {
    return this.#enqueue(() =>
      this.#operate(
        'buy',
        {
          items: Array.isArray(items) ? items : [items],
        },
        options,
      ),
    );
  }

  claimRewards(items, options) {
    return this.#enqueue(() =>
      this.#operate(
        'claimRewards',
        {
          items: Array.isArray(items) ? items : [items],
        },
        options,
      ),
    );
  }

  collectLoot(params = {}, options) {
    return this.#enqueue(() => this.#operate('collectLoot', params, options));
  }

  usePotion(potion, target, options) {
    return this.#enqueue(() => this.#potion('potion', potion, target, options));
  }

  throwPotionAtShopkeeper(potion, options) {
    return this.#enqueue(async () => {
      const state = await this.state();
      const matches =
        state.player?.potions.filter((p) =>
          typeof potion === 'string'
            ? p.name.toLowerCase() === potion.toLowerCase()
            : p.ref === potion?.ref,
        ) ?? [];
      if (matches.length !== 1)
        throw new Error('Potion is missing or ambiguous; pass its current potion object');

      const target = matches[0].targets?.find((t) => t.kind === 'shopkeeper');
      if (!matches[0].usable || !target)
        throw new Error(
          'No shopkeeper target is available; this happens before entering the shop inventory',
        );

      return this.#potion('potion', matches[0], target, options);
    });
  }

  discardPotion(potion, options) {
    return this.#enqueue(() => this.#potion('discardPotion', potion, undefined, options));
  }

  async #potion(kind, potion, target, options) {
    const state = await this.state();
    if (typeof potion === 'string') {
      const matches =
        state.player?.potions.filter((p) => p.name.toLowerCase() === potion.toLowerCase()) ?? [];
      if (matches.length !== 1)
        throw new Error('Potion is missing or ambiguous; use its current potion object');

      potion = matches[0];
    }
    if (!potion?.ref) throw new TypeError('Pass a current potion object or exact potion name');

    target = creatureTarget(state, target);
    return this.#request(kind, { potion: potion.ref, target: target?.ref }, options);
  }

  choose(choice, options) {
    return this.#enqueue(() => this.#choose(choice, options), { trackDeck: true });
  }

  async #choose(choice, options) {
    return this.#operate('choose', { choice }, options);
  }
}
