# DO NOT IMPLEMENT IN THIS ROADMAP

The current classic-console core is already mature. Do not dilute this wave with unrelated expansion.

Explicitly out of scope:

- ConPTY / Windows Terminal transport rewrite;
- UIAutomation fallback;
- global keyboard/mouse automation;
- browser automation;
- automatic provider/model switching;
- arbitrary generated prompts;
- arbitrary shell execution;
- killing/restarting Cline/Codex as a recovery tactic;
- HTTP control server;
- cloud telemetry/control plane;
- database-backed history;
- giant plugin/provider framework;
- multi-machine orchestration;
- MSI/signing work unless separately requested;
- feature redesign of the Win95/Dark Golden UI.

## Why

The next value is **certainty**: when SAICONT says WATCH, DRY-RUN, STOPPED, WAITING, or SENT, that state must be transactionally true. Once that contract is solid and externally observable through a tiny local schema, future transport/provider work becomes much safer.
