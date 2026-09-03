# SAICONT v1.2 — NEXT IMPLEMENTATION ROADMAP

Target baseline: `_SAICONT_31.08.26-T10-33-54.zip`
Observed version: `1.1.1`
Observed HEAD: `5398f05`
Release tag behind HEAD: `v1.1.1` at `005df5c`
Roadmap intent: **Field Reliability + Control Contract**, not feature sprawl.

## Why this roadmap exists

The historical `SAICONT_FULL_ROADMAP` from v0.3.1 through v1.0 is already functionally superseded. The present tree has the safety/recovery engine, durable state, GUI/TUI, lifecycle ownership, 265 deterministic self-tests recorded by repository evidence, and post-audit hardening. The next useful step is not another rewrite.

The current source still exposes several concrete post-1.1 seams:

1. `scripts/install.ps1` does not acquire `Enter-SaiContLifecycleLock` while `start.ps1`, `stop.ps1`, and repairing `status.ps1` do. Install is therefore still not one serialized lifecycle transaction.
2. `src/GuiApp.cs` changes `systemState` from WATCH to DRY-RUN without invalidating an already in-flight poll. That poll captured `allowInput=true` before the mode change and can continue until its existing generation is cancelled.
3. `src/GuiApp.cs::ToggleSelectedRule()` mutates `TargetRule.Enabled` on the live configuration object while the background poll engine may be reading the same rule graph.
4. Safety-relevant teardown/control paths still contain silent bare catches, including GUI worker shutdown, TUI worker join, and interactive stop-artifact cleanup.
5. Windows-native acceptance is still operator-gated in the SAIPEN board (`T-120`, `T-117`, `T-30`) even though static/self-test evidence is strong.
6. There is no small, stable, machine-readable local health/status contract for another local orchestrator such as TERMISAI to consume without scraping UI text or logs.

## Product direction

Build SAICONT 1.2 as a **trustworthy local continuity component**:

- fixed-command only;
- zero focus stealing;
- no arbitrary prompt generation;
- fail closed for write authorization;
- deterministic lifecycle ownership;
- atomic runtime-policy changes;
- observable locally through a bounded file/status contract;
- no HTTP server, database, telemetry service, browser automation, or plugin framework.

## Execution order

Do the waves in order. Do not jump to health/status export before Wave 1 and Wave 2 are green.

1. `01_WAVE_1_WINDOWS_AND_LIFECYCLE_CLOSEOUT.md`
2. `02_WAVE_2_ATOMIC_RUNTIME_CONTROL.md`
3. `03_WAVE_3_LOCAL_HEALTH_CONTRACT.md`
4. `04_WAVE_4_MAINTAINABILITY_AND_TEST_SEAMS.md`
5. `05_WAVE_5_V1_2_RELEASE.md`

Read `07_ACCEPTANCE_MATRIX.md` before implementation and use `09_AGENT_EXECUTION_PROMPT.md` as the implementation-agent handoff.

## Hard rule

A ticket is not DONE because the code looks plausible. It is DONE only when its verification gate is actually reproduced. If the environment cannot run a Windows-native gate, record `MANUAL-VERIFY`/`BLOCKED` honestly and continue only with work that does not depend on fabricated evidence.
