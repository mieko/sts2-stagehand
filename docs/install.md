# Installation

The launcher expects the macOS arm64 Steam version of Slay the Spire 2. Use Steam's
**Manage → Browse local files** to locate the installed app. You also need
Node.js 22+, Python 3, .NET SDK 10 and a Godot 4.5 editor executable for resource
packing. The C# mod links against the locally installed game's assemblies.

Clone with the Stagehand submodule and install the JavaScript dependencies:

```sh
git clone --recurse-submodules https://github.com/mieko/sts2-stagehand.git
cd sts2-stagehand
npm ci
```

For an existing clone, run `git submodule update --init --recursive` before
`npm ci`.

## Prepare the isolated copy

Start with an empty `.local/instance/` in the checkout. Replace the first path
below with the actual app you located in Steam. The launcher expects the copy
to be named `SlayTheSpire2.app`.

```sh
GAME_APP="/path/to/installed/SlayTheSpire2.app"
INSTANCE="$PWD/.local/instance"
mkdir -p "$INSTANCE"
ditto "$GAME_APP" "$INSTANCE/SlayTheSpire2.app"
```

Create `.local/instance/SlayTheSpire2.app/Contents/MacOS/override.cfg` in the
**copy** with these settings:

```ini
[application]
config/use_custom_user_dir=true
config/custom_user_dir_name="stagehand-sts2-local"
```

Give each instance a distinct profile name beginning with `stagehand-sts2-`.
Keep that name across relaunches to retain runs and unlocks. Existing
`runtime-control-sts2-` profiles are also accepted.

## Build and launch

Set `GODOT45` to the Godot 4.5 executable on your machine:

```sh
GODOT45="/path/to/Godot.app/Contents/MacOS/Godot"
GAME_DATA_DIR="$INSTANCE/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
python3 tools/build.py --game-data "$GAME_DATA_DIR" --godot "$GODOT45"
python3 tools/launch-isolated.py
```

The build reads `sts2.dll` and `GodotSharp.dll`, compiles the managed mod
(targeting .NET 9) and packs the GDScript resources in `build/`. The launcher
copies the DLL, PCK and manifest into the isolated app, turns off Steam
integration and generates a token for this launch. It writes a private
`session.json` and timestamped logs under the instance directory.
On first launch the game may save mod consent and exit. If it does, run the
launcher again. Connect using the [JavaScript example](../README.md#connect-and-play).

Quit the isolated copy before rebuilding and relaunching. Keep the DLL, PCK and
client from the same revision. The native mod ID and filenames are
`RuntimeControl` for compatibility with existing saves.

To use another instance directory, pass `--directory /path/to/instance` to the
launcher and `{directory: '/path/to/instance'}` to `connectLocal()`. The build
accepts `--output` and the launcher accepts the corresponding `--artifacts`.
Use the build's `--core` option to select a different Stagehand checkout.

## Compatibility

On macOS arm64, Standard runs have been tested through Act 3 on v0.107.1.
On v0.111.1 (Steam build 26655341, MegaDot 4.5.1-m.14), live gameplay checks
extend through first combat, ordinary rewards and returning to the map. Later
combat, results and unlocks on that build are unverified. Rare bundle screens
and Fake Merchant potion combat have limited coverage. The gameplay API covers
single-player Standard runs. Daily, Custom and co-op setup are outside its scope.
Updates to the game's private UI fields can require a mod update.

### Recovering an unresponsive instance

Known failures include native GC/finalizer stalls and an invalid-free crash.
The cause of the crash is unknown.
A stalled game can stop answering even passive reads. If the instance cannot
recover, keep the logs, quit the isolated copy and relaunch with the same profile.
Use the game's Continue command and inspect the restored position. Actions
completed in memory may be newer than the last native save.

## Tests

Run from the checkout after `npm ci`:

| Command                                                     | What it checks                                                                                                  |
| ----------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------- |
| `npm test`                                                  | JS client, managed workflow service and Python launcher. Uses .NET SDK 10 and game-free fixtures.               |
| `npm run test:host`                                         | Unordered card lists in C# without game assemblies.                                                             |
| `npm run test:presentation`                                 | JavaScript presentation only.                                                                                   |
| `npm run test:registration -- --godot "$GODOT45"`           | GDScript endpoint registration and passive status reads in a temporary Godot project.                           |
| `npm run test:selection -- -p:GameDataDir="$GAME_DATA_DIR"` | Native card models, selection, costs, upgrades and ending phases. Build the mod against those assemblies first. |

The selection test loads `Sentry.Godot.dll` and `Sentry.dll` from the selected
game and runs without native nodes or saves. An engine-extension-not-loaded
notice is expected in this test process. For a custom build directory add
`-p:HostBuildDir=/absolute/path/to/output`.
