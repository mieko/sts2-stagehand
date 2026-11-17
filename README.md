# StS2 Stagehand

Control Slay the Spire 2 single-player over a JSON WebSocket. A test, bot or
agent can inspect the hand and enemy intents, play cards in order, and wait for
the next decision. The mod uses the game's native actions for combat, map
travel, shops, rewards and unlocks. You can also watch a run without driving it,
take screenshots or inspect a live Godot scene from a REPL.

**The default developer endpoints let any authenticated client run arbitrary code
in the game process.** The gameplay API shows player-visible information.
The debugger can inspect and change game internals. See [security](docs/security.md)
before connecting a client.

## Install and launch

The supplied launcher runs a separate Steam-off copy on Apple Silicon macOS.
You'll need the installed Steam game, Node.js 22+, Python 3, .NET SDK 10 and a
Godot 4.5 executable to pack the mod's resources.

```sh
git clone --recurse-submodules https://github.com/mieko/sts2-stagehand.git
cd sts2-stagehand
npm ci
```

Follow the [installation guide](docs/install.md) to copy the game, build the mod
against its assemblies and launch it with a separate save profile. Once set up,
run `python3 tools/launch-isolated.py` for subsequent launches.

## Connect and play

Start `node` in the checkout with the isolated game running:

```js
const { connectLocal } = await import('./client/local.mjs');
const game = await connectLocal();
const decision = await game.decision();
console.log(game.present(decision));
```

At a combat decision with a playable Strike and a living enemy, for example:

```js
const result = await game.play('Strike', decision.combat.enemies[0]);
console.log(game.present(result));
```

Actions wait for a result or a choice, such as a card-selection prompt. Inspect
the result before continuing. If a call times out, use `game.recover()` to see
which steps took effect. Replaying the original batch can play a card twice.

`decision()` may advance forced presentation screens. Use `state()` for a passive
read, or `spectate()` on a second connection to watch for settled positions.
`game.close()` disconnects the client while the game keeps running.

- [Gameplay API](docs/api.md) covers cards, choices, run setup, spectators and recovery.
- [Service operations](docs/operations.md) describes submitting plans and reading results from any WebSocket client.
- [Installation, compatibility and tests](docs/install.md) lists tested game versions, known limits and local checks.
