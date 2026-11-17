# Agent directions

Read README.md and docs/api.md before editing. This repo owns the StS2 managed
binding and semantic client; keep game policy out of the separate generic
addon/client and do not duplicate its transport.

Use game descriptions, visible state and native actions in the semantic API.
Keep hidden draw order and future RNG/rewards out of its observations. The
separate developer endpoints allow unrestricted inspection and scripting.
Preserve FIFO mutations, partial progress and explicit choice/epoch/tutorial
boundaries. Acceptance is not completion.

Run `npm test` and `npm run test:host`. After managed changes, build against the
selected game's assemblies. Coordinate live reloads with the person using the
instance. Use an isolated app and profile with Steam off to protect normal saves
and the Steam installation.

Keep plans/checklists/review diaries, secrets, decompiled game sources, game assets,
saves and build products out of Git. Commit or publish only on request.
