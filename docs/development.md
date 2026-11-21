# Development

Players install the release ZIP; these tools are for building and testing it.
You need Node.js 22+, .NET SDK 10, the installed game and a Godot 4.5 executable.
The build links to the game's managed assemblies without redistributing them.

```sh
git clone --recurse-submodules https://github.com/mieko/sts2-stagehand.git
cd sts2-stagehand
npm ci
npm run build -- --game-data /path/to/game/assemblies --godot /path/to/Godot
npm run package
```

Find the directory containing `sts2.dll` and `GodotSharp.dll` in the installed
game. On macOS it is under the app's `Contents/Resources/data_sts2_macos_arm64`.
On other platforms inspect the game's `data_sts2_*` directory. Build paths may
contain spaces. The commands produce `build/` artifacts and a versioned ZIP in
`dist/`; nothing is deployed. `package.json` supplies the manifest, assembly and
ZIP version. The build bundles the StS2 and Stagehand clients into one `client.mjs`.
It mounts the resource-only PCK in an empty Godot project, registers the StS2
adapter, and checks that discovery serves the three matching build assets at
`/client.mjs`, `/api.md` and `/game.md`. `--core` selects the Stagehand checkout
for both the client bundle and the packed server.
`--output` selects a build directory and `package --artifacts` reads it.

The ZIP uses the native loader's persistent **RuntimeControl** identity. It
includes the matching JS client, API docs and licenses, but no game assemblies,
assets, saves, session credentials or build intermediates. Packaging requires the
Stagehand checkout's license. Build it locally against
a purchased game; do not put commercial game files in CI. No release is published
by these commands.

## Isolated macOS launcher

The optional launcher targets a separate Apple Silicon app copy, not the normal
Steam installation. Create `.local/instance/SlayTheSpire2.app` with Finder or
`ditto`, then add `Contents/MacOS/override.cfg` inside the **copy**:

```ini
[application]
config/use_custom_user_dir=true
config/custom_user_dir_name="stagehand-sts2-local"
```

Keep the profile name across launches. Use a distinct `stagehand-sts2-` name per
instance; existing `runtime-control-sts2-` names are also accepted. Quit before
relaunching:

```sh
node tools/launch-isolated.mjs
```

This deploys the built artifacts only to that copy, starts Steam-off with developer
tools enabled, and writes a private launch token/session file plus timestamped logs.
Use `--directory` and `--artifacts` for alternate locations. In a persistent JS REPL:

```js
const { connectLocal } = await import('./client/local.mjs');
const game = await connectLocal();
console.log(game.present(await game.decision()));
```

The launcher remains macOS-specific; normal installation and the build/package
commands do not require it. Coordinate before replacing a running instance's files
or reimporting a client's module graph.

## Checks

| Command                                                        | Scope                                                                      |
| -------------------------------------------------------------- | -------------------------------------------------------------------------- |
| `npm test`                                                     | Settings, managed workflows, JS client, presentation and launcher fixtures |
| `npm run test:host`                                            | Public unordered card lists, without game assemblies                       |
| `npm run test:presentation`                                    | Pure presenter                                                             |
| `npm run test:registration -- --godot /path/to/Godot`          | GDScript endpoint in an isolated fixture                                   |
| `npm run test:selection -- -p:GameDataDir=/path/to/assemblies` | Native models, selection, costs, upgrades and ending phases                |
| `npm run format:check`                                         | Maintained JS/Markdown formatting                                          |

Build before the native selection check. Add `-p:HostBuildDir=/path/to/artifacts`
for a custom output directory. Its Sentry extension-not-loaded notice is expected;
it loads assemblies without native game nodes or saves.
