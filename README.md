# StS2 Stagehand

Remotely control Slay the Spire 2 single-player with Stagehand. Useful for
programmatic testing, bots and agents: inspect hands and enemy intents, play cards,
travel, shop, collect rewards, or watch someone else's run.

The gameplay API uses native game actions and player-visible information.
Optional **Developer Mode** adds a DevTools-style console, screenshots, object
inspection, synthetic input and property recording. It lets connected clients
**execute arbitrary code on your computer**. Enable it only when you want that access.

## Install

Download the ZIP from [Releases](https://github.com/mieko/sts2-stagehand/releases)
and extract its **RuntimeControl** folder into the game's **mods** folder.
Launch through Steam and accept the game's mod-loading prompt. No Node.js, SDK,
terminal commands or environment variables are needed to run the mod.

Stagehand starts automatically for clients on this computer. Open
**Settings → Stagehand** to see the active address and select **Copy** beside
**Connection Details**. See [installation](docs/install.md) for
platform paths, settings, updates and troubleshooting, and [security](docs/security.md)
for connection access and Developer Mode.

## Connect and play

Open the **Stagehand Address** for links to the matching client and APIs.
Download `/client.mjs` or use the `client.mjs` in the install ZIP. Connect in
Node.js 22+ using the address from Connection Details.

```js
const { connectSts2 } = await import('./client.mjs');
const game = await connectSts2({ url: 'http://localhost:27120' });
const decision = await game.decision();
console.log(game.present(decision));
```

If the active connection requires an access token, also pass `token` with the
key from Connection Details.

With a playable Strike and a living enemy:

```js
const result = await game.play('Strike', decision.combat.enemies[0]);
console.log(game.present(result));
```

Actions wait for their result or next choice. If interrupted, use `game.recover()`
to inspect applied progress; replaying a batch can play a card twice.
`state()` reads passively. `decision()` may advance forced presentation screens;
`spectate()` watches on a separate connection. `game.close()` disconnects without quitting.

- [Agent API](docs/api.md): gameplay, choices, presentation, spectators and recovery.
- [Wire operations](docs/operations.md): use the same game-owned behavior from another language.
- [Development](docs/development.md): build, package and test from source with an isolated game.
