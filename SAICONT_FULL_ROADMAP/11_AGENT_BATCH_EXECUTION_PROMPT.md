# SAICONT ROADMAP BASELINE

Repository baseline inspected for this roadmap:

- Product: SAICONT
- Version: 0.3.1
- SAIPEN phase: HUNT
- SAIPEN last_event: 113
- Mode: no-publish
- Existing completed tickets: T-001, T-002, T-003, and the SAIOPS-created `T-4`
- Canonical runtime configuration: `SAICONT.config.xml`
- Canonical build: `build.ps1`
- Deterministic verification: `bin\SAICONT.exe --self-test`
- Read-only live verification: `bin\SAICONT.exe --probe --config .\SAICONT.config.xml`
- Full current smoke: `scripts\smoke.ps1`

Current architecture is intentionally small: Windows-only, dependency-free where practical, .NET Framework compiled with the in-box x64 `csc.exe`, classic Win32 console APIs, XML configuration, rotating text logs, PowerShell/VBS lifecycle scripts, and no database/service/browser/UI automation stack.

## Already implemented and not to be redone

The current tree already includes:

- process-tree discovery for Cline/Codex;
- bounded console-buffer reads;
- focus-free `WriteConsoleInputW` input injection;
- Codex usage-limit recognition;
- Cline/OpenRouter 429 recognition;
- Cline daily free-model-limit recognition;
- retry/deadline parsing;
- empty-prompt and pre-send safety checks;
- stale-trigger suppression in memory;
- XML configuration validation;
- hidden start/stop/status/install/uninstall scripts;
- rotating duplicate-suppressed logs;
- TERMISAI landing UI;
- controlled native input harness;
- lineage-based attachment candidates;
- console membership checks;
- honest probe classification: PASS=0, SKIP=1, FAIL_ALL=2, FAIL_MIXED=3;
- 45 deterministic self-tests at v0.3.1;
- Cline-first smoke and hidden multi-poll dry-run.

The previous `AttachConsole ... The handle is invalid (6)` Cline failure was investigated in v0.3.1. Do not restart that investigation unless current bytes demonstrate a regression.

## Verified high-value remaining risks in v0.3.1

The roadmap is based on concrete implementation weaknesses found in the current source:

1. `ProcessDiscovery.ConsoleServesMatchedProcess()` accepts missing/empty console membership as success. Automatic input must fail closed instead.
2. `NativeConsole.TryRead()` uses a fixed 64-element `GetConsoleProcessList()` buffer and does not expose membership-query failure distinctly.
3. The send path re-reads the old resolved attach PID before `TryWriteLine()`, but does not transactionally re-prove process identity, target membership, or same-console identity at write time.
4. PID alone is used as process identity; Windows PID reuse is therefore not fully guarded.
5. `RuleMatcher` chooses the most recent trigger, while `RetryTimeParser` parses retry time from the entire screen snapshot and can bind an old deadline to a new trigger.
6. busy matching runs against the entire snapshot, so historical `Working` output can represent current state incorrectly.
7. `RetrySessionState` is in-memory only, so a SAICONT restart forgets cooldown, prior send, awaiting-outcome, and stale-trigger suppression.
8. regexes are validated at config load but repeatedly compiled through static `Regex.Matches/IsMatch` calls on polling paths.
9. `WatcherEngine._states` and `OperationalLog._lastWrites` have no lifetime pruning.
10. `WatcherConfiguration.CreateDefaults()` duplicates production configuration and already drifts from `SAICONT.config.xml` target naming.
11. old helpers such as `FindWindowBearingAncestor()` and an unused `snapshotProcesses` parameter remain after the v0.3.1 resolver change.
12. lifecycle ownership is primarily PID-file based. The executable also refuses duplicates, but runtime identity, stale-marker recovery, and instance-specific stop semantics can be made stronger.

Everything after v0.3.1 should reduce those risks before feature expansion.

# SINGLE MASTER COMMAND FOR AN AGENT RECEIVING THIS WHOLE PACK

Execute the complete SAICONT roadmap contained in this roadmap pack from the current repository state to the logically complete v1.0 target.

Read first:

1. `00_READ_FIRST_MASTER_BATCH.md`
2. `01_v0.4_TRANSACTIONAL_SEND_SAFETY.md`
3. `02_v0.5_EVENT_CORRELATION_AND_DURABLE_STATE.md`
4. `03_v0.6_LIVENESS_AND_RECOVERY_ENGINE.md`
5. `04_v0.7_RULE_ENGINE_AND_CONFIGURATION_HARDENING.md`
6. `05_v0.8_LIFECYCLE_OPERATIONS_AND_CRASH_RECOVERY.md`
7. `06_v0.9_PERFORMANCE_STABILITY_AND_SOAK.md`
8. `07_v0.9.5_RELEASE_CANDIDATE_AUDIT.md`
9. `08_v1.0_RELEASE_AND_FINAL_ACCEPTANCE.md`
10. `10_FINAL_ACCEPTANCE_MATRIX.md`

`09_POST_V1_BACKLOG_DO_NOT_IMPLEMENT_NOW.md` is explicitly out of scope for this run.

## Execution instruction

Treat the pack as one ordered goal with sequential independently verified waves.

Do not stop after producing a plan. Implement the roadmap.

Do not restart completed work. At every wave inspect the repository's current version/state and advance from the first incomplete invariant.

Do not ask the human to manually approve each next wave. Continue through the batch while the environment/context allows.

Use SAIPEN/SAIOPS to represent work honestly. The roadmap proposes logical work decomposition, not hard-coded board IDs. Let the actual repository tooling own ticket IDs and transitions.

After each wave:

- build;
- run deterministic tests;
- run the safe native harness where relevant;
- run read-only live probe where relevant;
- run lifecycle/dry-run smoke where relevant;
- review final bytes;
- validate SAIPEN;
- update version only if that wave's Definition of Done passed;
- continue immediately to the next wave.

If a later wave exposes an earlier invariant as incomplete, repair it and rerun affected gates. Do not preserve a version milestone merely for appearance.

Never inject test input into real Cline/Codex sessions. All destructive/input verification uses a controlled temporary harness.

Final status may be `SAICONT_V1: COMPLETE` only when the final acceptance matrix is satisfied and `VERSION` is legitimately `1.0.0`.

If interrupted by a hard execution/context limit, write a machine-resumable checkpoint identifying the exact roadmap file/wave/ticket/phase and remaining acceptance gate. On continuation, resume from that point rather than rereading/reimplementing all completed waves.
# COMMON EXECUTION CONTRACT FOR EVERY WAVE

These rules apply to every roadmap wave.

## Continuation semantics

- Work from the repository bytes you actually receive, not from assumptions in this roadmap.
- Inspect `VERSION`, `.saipen/STATE.md`, `.saipen/BOARD.md`, `.saipen/LOG.md`, relevant source, config, scripts, and docs before changing anything.
- If earlier roadmap work is already present, verify it and continue from the first genuinely incomplete gate. Never implement a completed wave a second time merely because its instruction file is being supplied again.
- Preserve all still-valid earlier fixes.
- Do not reset SAIPEN state or replace `.saipen/`.
- Use the repository's installed/current SAIPEN and SAIOPS conventions. Do not invent ticket syntax or manually fake state transitions.
- The current board demonstrates that SAIOPS may choose an ID form such as `T-4`; therefore treat ticket IDs as tool-owned rather than forcing a manually formatted ID.
- Run the SAIPEN validator at meaningful state transitions and before declaring a wave shipped.
- Keep `.saipen/STATE.md`, `.saipen/BOARD.md`, `.saipen/LOG.md`, recovery receipts, and actual implementation evidence consistent.

## Quality policy

Quality is more important than speed.

For each root cause:

1. reproduce or characterize it with evidence;
2. implement the smallest robust architectural correction;
3. add deterministic regression coverage;
4. add native/integration coverage where the issue crosses Win32/process/lifecycle boundaries;
5. run negative controls where practical;
6. independently review final bytes for P0-P3 correctness/safety regressions;
7. update documentation only after behavior is known;
8. do cleanup only after correctness is established.

Do not declare PASS because a command ran. PASS requires its assertions to prove the intended invariant.

## Safety invariants

Production input injection is the highest-risk operation in this project. Preserve these invariants throughout all waves:

- no global keyboard automation;
- no foreground-window activation;
- no clipboard dependency;
- no mouse automation;
- no arbitrary window-title targeting;
- no input when target ownership is unknown;
- no input when the current prompt is not proven empty/ready;
- no input when current target state is busy;
- no input when the triggering event is stale, changed, or ambiguous;
- no input before the configured/parsed deadline;
- no test input into the user's real Cline/Codex session;
- controlled input tests must target only an intentionally created harness console;
- probe/diagnostic commands remain observational and do not mutate retry state;
- dry-run never records a real successful write.

When evidence is incomplete, fail closed for writes and remain useful for read-only diagnostics.

## Architectural constraints

Unless a later wave explicitly proves the current architecture inadequate, do not add:

- database or SQLite;
- cloud backend;
- HTTP server;
- browser automation;
- Electron;
- Node/Python runtime dependency;
- service-host framework;
- large dependency-injection container;
- NuGet package pile;
- UIAutomation stack;
- plugin marketplace/framework;
- ConPTY rewrite merely because it is newer;
- telemetry or remote analytics.

Prefer the current dependency-free .NET Framework + Win32 + XML + PowerShell model.

## Verification commands

The exact paths may evolve, but preserve equivalent gates for:

```powershell
.\build.ps1
.\bin\SAICONT.exe --self-test
.\bin\SAICONT.exe --probe --config .\SAICONT.config.xml
.\scripts\smoke.ps1
```

A live probe may honestly SKIP when no compatible target exists. A discovered unreadable target is not a SKIP.

## Final handoff for every wave

Return a standalone handoff containing:

- wave/version;
- root causes confirmed;
- tickets completed;
- architecture/behavior changes;
- files changed;
- exact safety invariants added or strengthened;
- exact verification commands executed;
- deterministic test count/result;
- native harness result;
- live probe result;
- hidden lifecycle/dry-run result where applicable;
- SAIPEN validator result;
- review result;
- remaining limitations/risks;
- resulting `VERSION`;
- final SAIPEN phase/state.

Do not stop after analysis if implementation is possible. If a truly external live condition is unavailable, complete every deterministic part and leave a precise machine-resumable checkpoint instead of fabricating evidence.
