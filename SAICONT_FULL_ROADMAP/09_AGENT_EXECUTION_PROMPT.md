# IMPLEMENTATION AGENT START PROMPT

You are the implementation agent for SAICONT.

Target repository is the current SAICONT tree corresponding to archive `_SAICONT_31.08.26-T10-33-54.zip`, observed at VERSION 1.1.1 / HEAD 5398f05 when this roadmap was authored.

Read first:
1. `00_READ_FIRST.md`
2. the current wave file
3. `06_TICKET_GRAPH.md`
4. `07_ACCEPTANCE_MATRIX.md`
5. `08_DO_NOT_IMPLEMENT.md`

Then inspect the live repository because the operator may have advanced it since this roadmap was created.

## Execution rules

- Work one ticket at a time in dependency order.
- Reproduce the ticket's current-source premise before editing. If already fixed, record evidence and skip implementation rather than duplicating it.
- Preserve SAICONT's fixed-command, zero-focus, fail-closed safety model.
- No dependency/framework rewrite.
- No mass refactor while repairing a safety race.
- Add a deterministic regression/fault/race test with every behavioral fix.
- A test that cannot run in the current environment is not PASS. Mark it MANUAL-VERIFY/BLOCKED with the exact command required on Windows.
- Do not falsify SAIPEN closure evidence.
- Do not rewrite historical `.saipen/LOG.md` entries to appease validators.
- Do not publish/push/tag remotely unless the operator explicitly instructs it.
- Keep documentation claims synchronized with measured reality.

## First action

Start with the first incomplete ticket in the current batch. For the first roadmap batch, that is T-131 unless the repository already contains later verified evidence.

## Completion output per ticket

Return:
- ticket id;
- files changed;
- concise root cause;
- implemented behavior;
- tests added/changed;
- exact commands run;
- exact PASS/FAIL/MANUAL-VERIFY result;
- remaining blocker, if any;
- next ticket id.
