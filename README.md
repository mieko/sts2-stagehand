# StS2 Stagehand

A semantic JavaScript API and native mod for Slay the Spire 2 single-player.
Read player-visible state and play through native game actions, with ordered
batches and completion-aware results. Uses [Stagehand](https://github.com/mieko/stagehand);
no MCP server, Codex or provider-specific runtime required.

- [API reference](docs/api.md): gameplay, inspection, choices, recovery and spectators.
- [Service operations](docs/operations.md): language-neutral wire contract.

## Source layout and dependencies

| Directory | Contents |
| --- | --- |
| `mod/` | Native C# binding, GDScript registration and mod manifest. |
| `client/` | Semantic JavaScript API, presentation and local connection helper. |
| `tools/` | Build, resource packing, isolated launch and development proof scripts. |
| `test/` | Offline service, client, native model and registration fixtures. |
| `stagehand/` | Pinned generic addon/client dependency. |

Clone with the pinned Stagehand submodule:

```sh
git clone --recurse-submodules git@github.com:mieko/sts2-stagehand.git
cd sts2-stagehand
npm ci
```

For an existing clone, run `git submodule update --init --recursive` before
`npm ci`. Both the npm dependency `file:./stagehand` and the default mod build use
that checkout. Game DLLs are local prerequisites, not distributable source.

## Build the mod

Requires Python 3, .NET SDK 10 and Godot 4.5 for resource packing. The net9.0 host
references `sts2.dll` and `GodotSharp.dll` from the selected game:

```sh
python3 tools/build.py \
  --game-data "$GAME_DATA_DIR" \
  --godot "$GODOT45"
```

For a macOS app copy, `GAME_DATA_DIR` is `Contents/Resources/data_sts2_macos_arm64`.
Output defaults to ignored `build/`; `--core` selects another Stagehand checkout
and `--output` another output directory. The build writes ignored `obj/`, packs
resources in a temporary project, and neither launches, modifies nor deploys to
the game. Loading uses the native mod loader and `ModInitializerAttribute`, without
Harmony, BaseLib or engine patches.

## Run an isolated copy

Protect the Steam app, normal saves and Steam Cloud. Use a separate app and profile:

1. Copy the app into `$ISOLATED_INSTANCE/SlayTheSpire2.app`.
2. Create `Contents/MacOS/override.cfg` inside the copy:

   ```ini
   [application]
   config/use_custom_user_dir=true
   config/custom_user_dir_name="stagehand-sts2-your-instance"
   ```

   Give each instance a distinct name, then keep it unchanged across launches.
3. Build the mod and launch:

   ```sh
   python3 tools/launch-isolated.py --directory "$ISOLATED_INSTANCE"
   ```

The launcher installs the built DLL/PCK/manifest into the copy, uses
`--force-steam=off`, and writes a private token, mode-600 `session.json` and timestamped
logs. `--artifacts` selects another build directory. Never point it at Steam's app.
Quit the copy normally before redeploying: the launcher refuses while its previous
PID exists. Coordinate control/reloads with anyone using the instance; do not
restart an evaluation run just to install a source fix.

On first mod consent, the game may save consent and exit; launch the copy again.
Manage other mods only in the copy. Without `STAGEHAND_TOKEN`, the listener stays
disabled. Adopt matching DLL, PCK and client together.

The isolated profile persists runs and unlocks across rebuilds/relaunches; do not
delete it after evaluation. Existing `runtime-control-sts2-` profile names remain
supported alongside `stagehand-sts2-`. The mod ID, folder and artifacts remain
`RuntimeControl` for saved-run compatibility; its display name is Stagehand.
Keep tokens, logs, game files, saves and build products out of Git.

## Connect and play

`client/local.mjs` reads the launcher's private session. In a persistent JS REPL, import
and connect once using your checkout and instance paths:

```js
var { connectLocal } = await import('/absolute/path/to/sts2-stagehand/client/local.mjs');
var game = await connectLocal({directory: '/absolute/path/to/isolated-instance'});
var d = await game.decision();
console.log(game.present(d));
```

Alternatively, set an ignored default for both connector and launcher:

```sh
mkdir -p .local
ln -s "$ISOLATED_INSTANCE" .local/instance
```

Then use `connectLocal()` and omit the launcher's `--directory`. Ordinary Node can
use `import {connectLocal} from './client/local.mjs'`; `client/local.mjs` supplies `ws` if needed.
Explicit connections use `connectSts2(url, {token, WebSocket})` from `sts2-stagehand`.
Changed imports need a fresh module graph/kernel; closing a socket does not reload
code. `game.close()` disconnects without quitting the game.

Continue with the [REPL examples and method reference](docs/api.md). Use observed
legal cards/choices; inspect each returned decision. Batches retain successful
steps when interrupted: use `game.recover()`, not blind replay.
`decision()`/`ready()` can advance documented presentation UI; use passive
`state()`/`deck()`/`inspectCard()`/`piles()` or [spectator frames](docs/api.md#passive-spectator-frames)
when watching another player's run.

## Tests and compatibility

```sh
npm test
npm run test:host
```

| Check | Scope / prerequisites |
| --- | --- |
| `npm test` | JS API, managed workflow service/fixture and offline Python launcher tests with fake native ports/apps. Requires .NET SDK 10; no game or engine. |
| `npm run test:host` | Production unordered-card ordering helper in pure C#; .NET 10, no game DLLs. |
| `npm run test:presentation` | Presenter-only JS checks; no game or managed build. |
| `npm run test:registration -- --godot "$GODOT45"` | Actual GDScript registration in a temporary game-free project, including passive status-read isolation. |

After managed changes, build against the selected game's assemblies. Then check
native models and selection ownership without launching the game:

```sh
npm run test:selection -- -p:GameDataDir="$GAME_DATA_DIR"
```

For another host build, add `-p:HostBuildDir=/absolute/path/to/output`. This checks
eligible cards, detached previews/model preservation, numeric context, costs,
facing and ending phases using game-type shells: no native nodes, engine or saves.
It does not replace live qualification. Coordinate live tests in an isolated
app/profile; keep game assemblies and outputs ignored.

Exercised on Steam v0.107.1 / MegaDot 4.5.1-m.12, macOS arm64, including normal
three-act wins with all five main-branch characters, between-room decisions,
results, explicit epochs/unlocks and return to menu. Rare bundle screens and Fake
Merchant potion combat need more live qualification. Co-op, Daily and Custom setup
are outside this API. Private game UI fields may require adapter updates after a
game release. Unsupported screens have explicit `game.client` input/inspection;
no hidden RNG, future rewards or shuffled draw order are exposed.

## How the integration fits together

The mod registers one `Sts2` object on the generic Stagehand bridge. Native game
knowledge stays here; Stagehand supplies discovery, transport, subscriptions,
screenshots and input. This mod also explicitly enables Stagehand's `Script` root
for runtime GDScript evaluation and conditional input through `game.client`.

| File | Responsibility |
| --- | --- |
| [Entry.cs](mod/Entry.cs) | Bridge installation and cleanup. |
| [sts2.gd](mod/sts2.gd) | Object, methods, observation and `changed` registration. |
| [Sts2Host.cs](mod/Sts2Host.cs) | Game objects/UI, native actions and completion. |
| [WorkflowService.cs](mod/WorkflowService.cs) | Ordered plans, choices and forced settlement. |
| [ModelProjection.cs](mod/ModelProjection.cs) | Public model facts without object ownership. |
| [sts2.mjs](client/sts2.mjs) | Semantic facade and local FIFO. |
| [decision.mjs](client/decision.mjs), [present.mjs](client/present.mjs), [spectator.mjs](client/spectator.mjs) | Decision/presentation transforms and settled-position comparison. |

## Recovering an unresponsive instance

A native stall can block even passive RPCs. A timeout does not cancel an accepted
action. Preserve logs and bounded native stack samples when practical, then quit
only the isolated copy and relaunch with the **same user directory**. Use native
Continue and inspect the restored state: completed in-memory actions may not have
reached the save. Preserve progression and coordinate recovery with other players.
