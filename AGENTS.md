# Agent directions

Read README.md and docs/api.md before editing. This repo owns the StS2 managed
binding and semantic client; keep game policy out of the separate generic
addon/client and do not duplicate its transport.

Use game descriptions, visible state and native actions. Never expose hidden draw
order or future RNG/rewards, or fake legal operations with direct state edits.
Preserve FIFO mutations, partial progress and explicit choice/epoch/tutorial
boundaries. Acceptance is not completion.

Run `npm test` and `npm run test:host`. After managed changes, build against the
selected game's assemblies. Coordinate live-instance/client reloads with its user;
never restart an evaluation run merely to install a source fix. Use an isolated
app copy/profile with Steam off; protect normal saves and the Steam app.

Keep plans/checklists/review diaries, secrets, decompiled game sources, game assets,
saves and build products out of Git. Commit or publish only on request.
