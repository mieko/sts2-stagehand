# Security

Normal mode exposes **Registry** and **Sts2**: discovery, player-visible game facts
and native gameplay actions. A connected client can play cards, spend resources,
change progress, start runs and abandon them. This is control of your game, not a
read-only connection. Spectators use the same credentials and can submit actions too.

## Developer Mode

**Developer Mode lets connected clients execute arbitrary code on this computer.**
It adds Script, Object, PropertyRecorder, SceneTree, Runtime, Screen and Input.
Treat it like an attached DevTools console. Scripts and method calls can inspect
hidden game state, change a run, read or write files and launch programs with the
game process's permissions. The gameplay API's information limits do not restrict
a debugger client. Enable Developer Mode only for clients you trust with that access.

## Connections

The listener rejects WebSocket upgrades with an `Origin` header, including
ordinary browser connections. Discovery, documentation and the JavaScript
download are available without a token. RPC uses the WebSocket at `/rpc`.
By default, any local tool can use the exposed API on the `localhost` listener.
Enable **Require Access Token** to limit the API to clients with the key.

**Copy** beside **Connection Details** and `RuntimeControl/connection.json`
include the active token when required. A key entered in **Access Token** is
stored in `RuntimeControl/settings.json`.
Share credentials only with intended clients. The generated token changes each launch.

Allow Remote Connections permits exposing the listener beyond loopback and
requires authentication. Plain `http://` discovery and `ws://` RPC traffic,
including the token, are unencrypted. Use a trusted network or encrypted
tunnel; this mod does not provide TLS. A separate save profile protects ordinary
play from accidental game actions, not from arbitrary code's filesystem access.

## Completion and timeouts

Scripts execute on the main thread. A blocking script can freeze the game and
listener; timeouts cannot interrupt it or roll back changes. Accepted gameplay
work may continue after a timeout or disconnect. Inspect `recover()` and fresh
state before submitting more input. Never automatically replay an uncertain action.
