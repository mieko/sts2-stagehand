# Agent directions

Read README.md and docs/api.md before editing. This repository owns the StS2
managed binding and semantic external client. The generic addon/client is a
separate dependency; do not copy StS2 policy into it or duplicate its transport.

Use real game descriptions, visible state and native action paths. Never expose
hidden draw order, future RNG/rewards or use direct state edits to fake legal
operations. Acknowledged actions are not necessarily finished. Preserve FIFO
mutation, partial progress and explicit choice/epoch/tutorial boundaries.

Run `npm test` and `npm run test:host`; build against the selected game's assemblies
when managed code changes. Coordinate live-instance/client reloads with its user.
Do not restart an evaluation run merely to install a source fix. Use an isolated
app copy/user directory with Steam off; normal saves and the Steam app are protected.

Keep plans/checklists/review diaries, secrets, decompiled game sources, game assets,
saves and local build products outside Git. Commit or publish only on request.
