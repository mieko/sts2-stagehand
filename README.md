# StS2 Stagehand

A semantic JavaScript API and native mod for Slay the Spire 2 single-player.
Inspect player-visible cards, effects, enemies, map/choices and progression; play
through the game's real operations with queued actions and completion-aware
results. It uses the separate [Stagehand](https://github.com/mieko/stagehand)
addon/client. No MCP server or provider-specific runtime is required.

## Source layout and dependencies

Keep these local repositories adjacent:

```sh
git clone git@github.com:mieko/stagehand.git
git clone git@github.com:mieko/sts2-stagehand.git
```

```text
workspace/
  stagehand/  # reusable addon, generic JS client, fixtures
  sts2-stagehand/   # this game-specific consumer
```

The npm dependency `file:../stagehand` links the live local core source.
Run `npm ci` here to install that link and the project-local `ws` dependency. The
core addon itself has no managed or Node prerequisite. This consumer's mod uses
.NET and the game's own assemblies; do not ship game DLLs with the source.

## Connect and play

`local.mjs` reads a private session produced by the isolated launcher. Select the
instance explicitly or keep an ignored development symlink:

```sh
mkdir -p .local
ln -s "$ISOLATED_INSTANCE" .local/instance
```

In a persistent JS environment, replace the absolute import path with your checkout,
import/connect once, then retain `game` for subsequent calls:

```js
var { connectLocal } = await import('/absolute/path/to/sts2-stagehand/local.mjs');
var game = await connectLocal();
```

Later, in a separate invocation:

```js
var d = await game.decision();
console.log(game.present(d));
```

```js
var result = await game.playCards([
  {card: d.combat.hand[0], target: d.combat.enemies[0]},
  'Defend',
], {endTurn: true});
console.log(game.present(result));
```

For ordinary rewards, `await game.collectLoot({potions: true})` claims gold and
requested potions, then opens the ordinary card offer. Relics require an explicit
`relics: [rewardObject]` list; unrequested/special rewards remain available. It stops
at a full inventory or new choice and never picks a card for you. Full inventory
returns `choice_required` with applied progress, allowing explicit inventory
handling without queue recovery. `game.present(result)`
is the compact, pure view of an existing outcome—no extra read or normalization.
It keeps next rooms and live relic counters; `{map: true, inventory: true}` opts
into the full graph and static inventory.

Choose actual legal cards/targets from the current observation. Exact names work
when unambiguous; pass a specific object for variants. `buy([itemA, itemB])` and
other batches execute locally in order, without another model turn per item.
Stop and inspect a new choice or failure; successful earlier actions remain applied.

To compare an offered card with its upgrade before choosing or buying it, use
`await game.inspectCard(offer, {upgrades: true})`. This passive read uses the native
detached preview and leaves the offered card unchanged; the returned `upgrade`
contains the alternative when the card can be upgraded.

For ordinary Node, use `import {connectLocal} from './local.mjs'`. An explicit
connection is also available through `connectSts2(url, {token, WebSocket})` in
`sts2.mjs`. No Codex dependency. `local.mjs` supplies `ws` when the environment lacks
a global WebSocket. The semantic module resolves its local core package before
importing it, supporting persistent JS tools that cannot resolve bare ESM names.
Changing imported JS requires a fresh module graph/kernel; closing a socket does
not reload code. `game.close()` does not quit the game.

For a separate read-only watcher, `observer.spectate()` returns the current settled
frame; subsequent `observer.spectate({after: frame, timeoutMs: 20000})` calls wait
for a new meaningful position. Print `frame.view`. It does not normalize or drive
the UI, and a real choice remains visible while its original action is pending.
Keep the frame object in the same persistent JS session. Per-action positions do
not claim another client's whole batch has ended.

See [the API](docs/api.md) for semantic state, effects/enchantments/afflictions,
queue and failure semantics, potions/shopkeepers, rewards/events and explicit
results/epoch/unlock handling. `state()`, `deck()`, `inspectCard()` and `piles()` are passive reads;
`decision()`/`ready()` may advance documented presentation-only UI.

## Build the mod

Prerequisites: Python 3, .NET SDK 10 and a Godot 4.5 executable for resource packing.
The managed host targets net9.0 and references `sts2.dll`/`GodotSharp.dll` from the
selected game version. The packer uses an empty temporary project, never replaces
the game pack/settings and does not launch the game.

```sh
python3 build.py \
  --game-data "$GAME_DATA_DIR" \
  --godot "$GODOT45"
```

For a macOS app copy, `GAME_DATA_DIR` is its
`Contents/Resources/data_sts2_macos_arm64` directory. Artifacts go to ignored
`build/` by default; `--core` and `--output` accept explicit alternate directories.
Building writes source-local ignored `obj/` and output files, but deploys nothing.
The source/load mechanism uses the native mod loader and `ModInitializerAttribute`;
no Harmony, BaseLib or engine patch is needed.

## Run an isolated copy

Keep the Steam app, normal saves and Steam Cloud separate from automation:

1. Copy the app into `$ISOLATED_INSTANCE/SlayTheSpire2.app`.
2. Inside that copy, create `Contents/MacOS/override.cfg`:

   ```ini
   [application]
   config/use_custom_user_dir=true
   config/custom_user_dir_name="stagehand-sts2-your-instance"
   ```

   Give each disposable instance a distinct name. Optional display settings may
   put the copy in a window; they are not required by the connector.
3. Build this mod, then launch with the explicit directory or `.local/instance`:

   ```sh
   python3 launch-isolated.py --directory "$ISOLATED_INSTANCE"
   ```

The launcher copies this repo's built DLL/PCK and manifest into the **copy's** mod
folder, passes `--force-steam=off`, selects a fresh private token and writes a
mode-600 `session.json` plus timestamped logs under the isolated directory. It
refuses to replace artifacts while the previously launched PID still exists.
Quit the isolated app normally before redeploying. `--artifacts` selects another
build directory. Do not point it at your Steam installation.

On the first mod-consent launch the game may save consent and exit; launch the copy
again to load the mod. If other mods are present, manage them only in the copy.
With no `STAGEHAND_TOKEN`, the initializer leaves the listener disabled.
The isolated profile is persistent development state: keep the same override
user-directory name across launches to retain runs and character/unlock progression.
Rebuilding or relaunching does not reset it. Do not delete it after evaluation.
Tokens, logs, game files, saves and generated artifacts are not repository content.

## Tests and compatibility

```sh
npm test
npm run test:host
```

The JS suite checks queues, completion, explicit choices and partial progress.
The pure C# test links the production unordered-card ordering helper and needs no game
DLLs or running game; it uses .NET 10. Build the complete host against the selected
game assemblies after managed changes. Live game checks must use a disposable
isolated instance and be coordinated with its user.

After building the host, check selection ownership against the selected game
assemblies without launching a game:

```sh
npm run test:selection -- -p:GameDataDir="$GAME_DATA_DIR"
```

This uses uninitialized managed shells of the actual game screen/grid types to
check full-list extraction and filtering. It creates no native nodes, loads no
engine, reads no saves and makes no claim to replace live selection qualification.
Game assemblies and build outputs remain ignored local prerequisites.

The binding has been exercised on Steam v0.107.1 / MegaDot 4.5.1-m.12, macOS arm64,
including complete normal three-act wins with all five main-branch characters, ordinary between-room decisions,
results, explicit epochs/unlocks and returning to a usable menu. This is concrete
coverage, not exhaustive parity. Rare bundle screens
and Fake Merchant potion combat require more live qualification. Co-op,
Daily and Custom setup are outside the current single-player API.

Some game UI fields are private managed implementation details; a game update may
require adapter changes. The generic addon remains independent. Unknown screens
can still be inspected/controlled through `game.client` primitives. No hidden RNG,
future rewards or shuffled draw order are part of the semantic API.

Mod loading references: [Alchyr mod setup](https://github.com/Alchyr/ModTemplate-StS2/wiki/Setup)
and [the native inspector initializer](https://github.com/elliotttate/sts2-modding-mcp/blob/main/explorer_mod/src/GodotExplorer.cs).

## Recovering an unresponsive instance

A game/native-runtime stall can stop all RPCs, including passive reads. A client
timeout does not cancel or undo an accepted action. Preserve the instance logs and
bounded native stack samples before recovery when practical; do not blindly retry
the batch. Quit only the isolated copy, relaunch it with the same persistent user
directory, then use the game's native Continue and inspect the actual restored
state. The save determines recovery position; already completed in-memory actions
may not have been saved. Keep saved progression intact, and coordinate control and
reloads when another user or agent is playing.

The StS2 mod ID, installed folder and artifact names remain `RuntimeControl` for
compatibility with existing modded runs. The display name is Stagehand. Keep an
existing profile's custom-user-directory name unchanged; the launcher accepts its
`runtime-control-sts2-` prefix as well as `stagehand-sts2-` for new instances.
