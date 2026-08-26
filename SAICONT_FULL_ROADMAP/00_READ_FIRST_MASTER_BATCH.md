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

# MASTER BATCH ROADMAP: v0.3.1 -> v1.0

Execute this roadmap in order. The goal is not to maximize version numbers. The goal is to make SAICONT trustworthy enough to run unattended for long periods without sending continuation input to the wrong console, repeating stale work after restart, hiding operational failures, or silently degrading over time.

## Wave order

1. `01_v0.4_TRANSACTIONAL_SEND_SAFETY.md`
   - fail-closed console ownership;
   - strong process-session identity;
   - transactional pre-send re-resolution;
   - verified write boundary.

2. `02_v0.5_EVENT_CORRELATION_AND_DURABLE_STATE.md`
   - trigger-local retry deadlines;
   - current-tail busy/ready state;
   - durable cooldown/suppression ledger;
   - restart-correct behavior.

3. `03_v0.6_LIVENESS_AND_RECOVERY_ENGINE.md`
   - explicit post-send outcome model;
   - bounded retry/backoff/recovery behavior;
   - stalled/unknown state classification;
   - no-spam and no-infinite-loop guarantees.

4. `04_v0.7_RULE_ENGINE_AND_CONFIGURATION_HARDENING.md`
   - compiled rule set;
   - bounded event matching;
   - canonical config model;
   - diagnostics/validation suitable for safe extension without code forks.

5. `05_v0.8_LIFECYCLE_OPERATIONS_AND_CRASH_RECOVERY.md`
   - stronger single-instance ownership;
   - stale PID/stop recovery;
   - instance-specific lifecycle semantics;
   - scheduled-task/install/uninstall hardening;
   - atomic runtime artifacts.

6. `06_v0.9_PERFORMANCE_STABILITY_AND_SOAK.md`
   - allocation/regex/process-poll efficiency;
   - bounded caches/state/logging;
   - deterministic accelerated soak;
   - failure injection;
   - resource budgets.

7. `07_v0.9.5_RELEASE_CANDIDATE_AUDIT.md`
   - whole-product correctness/security/operations audit;
   - upgrade/migration checks;
   - clean-machine-style release rehearsal;
   - no P0-P3 known defects gate.

8. `08_v1.0_RELEASE_AND_FINAL_ACCEPTANCE.md`
   - freeze behavior;
   - complete docs/release notes;
   - final reproducible verification;
   - v1.0 ship in current no-publish mode unless repository capabilities legitimately change.

9. `09_POST_V1_BACKLOG_DO_NOT_IMPLEMENT_NOW.md`
   - explicitly deferred ideas so the implementation agent does not contaminate the v1 path with speculative scope.

10. `10_FINAL_ACCEPTANCE_MATRIX.md`
   - one matrix covering the complete v1 contract and evidence required.

## Batch behavior

If all roadmap files are supplied to the agent in one batch, process them sequentially.

At the beginning of each wave:

- inspect current bytes;
- compare actual implementation against that wave's Definition of Done;
- if the wave is already complete, run the minimum trustworthy verification proving that fact and proceed;
- if incomplete, implement it fully before proceeding.

Do not ask the human to re-authorize the next roadmap wave merely because one wave completed. Continue automatically while context/runtime permits.

If an execution/context limit interrupts the batch:

- finish the current atomic operation;
- write a machine-resumable SAIPEN checkpoint;
- report the exact current wave, ticket, phase, completed evidence, remaining gate, and next command/action;
- do not restart completed earlier waves on continuation.

## Global v1.0 non-negotiable invariants

By v1.0 the project must prove all of the following:

- A console is never considered owned merely because membership could not be queried.
- A write is never performed against an attach PID selected by stale discovery evidence.
- Process identity protects against PID reuse during write-critical and persisted-state decisions.
- The exact current trigger event owns its retry/deadline context.
- Historical busy text cannot represent current busy state.
- Restarting SAICONT cannot erase cooldown or resurrect a stale trigger as a fresh continuation opportunity.
- A successful write has an explicit post-send lifecycle and cannot generate unbounded retry spam.
- Persistent runtime state is atomic, versioned, bounded, non-secret, and corruption-safe.
- Single-instance/lifecycle ownership is robust against stale PID files and process replacement.
- Probe/dry-run/smoke cannot mutate production continuation history.
- Deterministic and native harness tests cover both success and dangerous negative paths.
- Long-running state/log/cache growth is bounded.
- Performance remains appropriate for a 2-second watcher polling loop.
- A clean build/test/install/start/status/stop/uninstall rehearsal passes.
- Documentation describes actual behavior, limitations, state files, exit codes, recovery, and safety model.
- No unresolved P0-P3 correctness/safety issue remains at v1.0 ship.

## Scope rule

Do not add new terminal-agent providers just to make the roadmap look feature-rich. First make the existing Cline/Codex path correct, durable, observable, and operationally boring. A watcher that reliably does two things is more valuable than one that theoretically supports twelve agents and occasionally sends Enter into the wrong universe.
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
