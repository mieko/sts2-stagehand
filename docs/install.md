# Install

## Download and enable

1. Download `sts2-stagehand-VERSION.zip` from
   [Releases](https://github.com/mieko/sts2-stagehand/releases).
2. In Steam, select **Slay the Spire 2 → Manage → Browse local files**.
3. Create a `mods` folder if needed, then extract the ZIP there. The resulting
   layout must be `mods/RuntimeControl/RuntimeControl.json`, alongside
   `RuntimeControl.dll` and `RuntimeControl.pck`.
4. Launch the game and accept its mod-loading prompt. If the game exits after
   saving your consent, launch again.

On macOS, put `mods` inside **SlayTheSpire2.app → Contents → MacOS** (right-click
and choose **Show Package Contents**). On Windows and Linux, use the game directory
opened by Steam. Keep the DLL, PCK, manifest, client and docs together when updating.
Quit the game before replacing the folder.

The game manages modded saves and any first-launch save-copy prompt. This mod does
not copy, reset or edit profiles. Remove `RuntimeControl` to uninstall; the game's
modded saves remain.

## Settings and connection details

After launch, the gameplay API is available at **http://localhost:27120**
without an access token. Open **Settings → Stagehand** to see the active
address or change how clients connect.

- **Enable Stagehand** starts or stops the service.
- **Allow Remote Connections** permits a non-loopback listener and turns on
  **Require Access Token**. It uses all network interfaces unless you set an
  explicit listen address.
- **Require Access Token** is off by default for the localhost listener.
- **Access Token** sets a shared key that persists across launches. When
  authentication is required, leave it blank to use a launch-generated key.
- **Developer Mode** exposes arbitrary code execution and debugger tools. It defaults off.

Select **Copy** beside **Connection Details** to copy the active address. If
authentication is enabled, it also copies the token and links to the matching
client and APIs. These details are also in `RuntimeControl/connection.json` in
the game's user-data directory.

Select **Apply** to save and activate changes to the switches or token field.
Finish any active gameplay action first. Changes to the running connection
disconnect clients and clear the previous session's references and operation
receipt.

The listener uses port **27120**, shared across available IPv4 and IPv6 loopback
interfaces. Use `http://localhost:27120` for discovery and JavaScript clients.
The RPC WebSocket is at `ws://localhost:27120/rpc`. For another machine,
substitute the game's LAN hostname or IP. See [security](security.md) for
remote access and encryption.

## Advanced configuration

Create `RuntimeControl/settings.json` inside the game's user-data directory
(the directory containing its logs). The settings tab saves its switches here,
and you can edit advanced options while the game is closed. For example:

```json
{
  "port": 27120,
  "listenAddress": "localhost",
  "developerMode": false
}
```

`listenAddress` accepts `localhost` (both available loopbacks), `*` (all interfaces)
or an IP address without an interface scope. Non-loopback addresses require
`allowRemoteConnections: true`. Tokenless access requires remote connections off
and the default loopback address, `localhost`. A saved `accessToken`, enabled
Developer Mode, enabled remote connections, or a custom address other than
`localhost` makes authentication the default when `requireAccessToken` is
omitted. `port: 0` selects an available port. The `enabled` key controls whether
Stagehand starts.
Invalid settings stop startup and report the cause in the game log.

For launcher integration, `STAGEHAND_TOKEN`, `STAGEHAND_PORT`, `STAGEHAND_LISTEN`
and `STAGEHAND_DEVELOPER` override their corresponding settings. They are not
required for normal installation. An explicitly empty `STAGEHAND_TOKEN` disables
authentication when the effective listen address is `localhost` and remote
connections are off. Only the key set in the **Access Token** field or `settings.json`
persists. Generated and environment-supplied keys are session settings.

## Compatibility and recovery

The gameplay API supports single-player Standard runs. Daily, Custom and co-op
setup are outside its scope. Game updates can change private UI fields and require
a mod update. This build targets v0.111.1 or later. Live gameplay coverage on
v0.111.1 (Steam build 26655341, MegaDot 4.5.1-m.14) extends through first combat,
ordinary rewards and returning to the map on macOS arm64. Windows/Linux
runtime and later encounters on v0.111.1 still need qualification.
Rare bundle screens and Fake Merchant potion combat have limited coverage.

### Recovering an unresponsive instance

Known native failures include GC/finalizer stalls and an invalid-free crash;
the causes remain unresolved. If even passive reads stop responding, retain the
logs, quit and relaunch using the same profile. Use native Continue and inspect
the actual restoration. In-memory actions may be newer than the last native save;
never replay them just to reconstruct the previous position.
