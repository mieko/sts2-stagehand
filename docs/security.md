# Security

**Anyone who can authenticate to the running mod can execute arbitrary GDScript
in the Slay the Spire 2 process.** Connecting is like attaching DevTools to a
browser tab. It gives a trusted developer an interactive console and access to
live objects. The wire protocol is Stagehand's JSON-RPC over WebSocket.

## Gameplay and debugger access

The `Sts2` endpoint reports player-visible information and submits native game
actions. For example, its draw pile reports the cards still in the pile with
their order unknown. This view is useful for testing and playing under the
same information constraints as a human.

The mod also enables `Script`, `Object` and `PropertyRecorder` by default.
`Script` evaluates supplied GDScript, `Object` can change properties and call
methods on game objects, and recorder getters can execute game code. A client
with the session token can call these directly regardless of which JavaScript
wrapper it uses. A spectator uses the same credentials and can switch to the
debugger at any time. A script can inspect hidden state, change a run, read or write files
and launch programs wherever the game process has permission. The gameplay
view offers no anti-cheat protection against such a client.

## Connection and profile

The mod starts its listener only when `STAGEHAND_TOKEN` is nonempty. The launcher
generates a new token, writes it to the instance's private `session.json` and starts
the mod on an available loopback port. Keep that file private. Loopback controls
which machines can reach the listener, and the token controls who can connect.
Neither restricts what an authenticated script can do.

The separate app, Steam-off launch and custom profile keep ordinary saves and
progress separate from Steam play. Scripts can still access other files permitted
by the OS. Plain `ws://` carries the token and traffic unencrypted. If a trusted
client connects from another machine, use an encrypted tunnel to the loopback
listener.

## Timeouts and changes

Scripts run on the game thread. A blocking script can freeze the game, including
the listener. Client timeouts cannot interrupt it or roll back changes. Accepted
game operations can also continue after a timeout or disconnect. Inspect the
current state and the last operation receipt before issuing more input. Never
automatically replay an uncertain action. See [operation recovery](api.md#queue-and-completion-contract).
