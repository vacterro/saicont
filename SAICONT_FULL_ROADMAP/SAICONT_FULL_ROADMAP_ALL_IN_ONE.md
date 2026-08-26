========================================================================================
FILE: 00_READ_FIRST_MASTER_BATCH.md
========================================================================================

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


========================================================================================
FILE: 01_v0.4_TRANSACTIONAL_SEND_SAFETY.md
========================================================================================

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

# WAVE 1 — v0.4.0 TRANSACTIONAL SEND SAFETY

## Objective

Make the native transport fail closed and make every real write a transaction whose target identity is re-proven immediately before `WriteConsoleInputW`.

This wave is foundational. Do not implement later liveness intelligence until this write boundary is trustworthy.

## Primary source surfaces

Inspect and modify as necessary:

- `src/NativeConsole.cs`
- `src/ProcessDiscovery.cs`
- `src/WatcherEngine.cs`
- `src/Program.cs` tests/harness
- `scripts/smoke.ps1`
- `docs/OPERATIONS.md`
- `.saipen/*`

Avoid unrelated feature work.

## Ticket A — fail-closed console membership

Current risk: `ConsoleServesMatchedProcess()` treats null/empty membership as success.

Required invariant:

> Unknown console membership can never authorize an automatic write.

Implement explicit membership-query semantics. A successful console read and a successful ownership proof are related but not identical facts.

Recommended model:

- represent membership status separately from the list of PIDs;
- distinguish at least:
  - membership query succeeded and target is present;
  - membership query succeeded and target is absent;
  - membership query failed/unavailable;
  - target disappeared during resolution;
- allow diagnostics to report all states;
- allow writes only in the first state.

Do not encode query failure as `new List<int>()` and then guess what it means elsewhere.

### Harden `GetConsoleProcessList`

The current fixed `uint[64]` buffer is not a complete implementation of the API contract.

Implement bounded two-stage/adaptive retrieval:

1. issue a query with a reasonable initial buffer;
2. if returned count exceeds capacity, allocate exactly/boundedly enough and retry;
3. cap the maximum accepted count to a conservative sane value;
4. expose a real error when the call returns no usable data because of Win32 failure;
5. preserve target console detachment in every path.

Do not confuse `returned count > buffer size` with failure. Do not silently truncate membership evidence used for safety.

Add deterministic tests around a pure helper that classifies returned-count/buffer behavior, and native harness tests for real membership where possible.

## Ticket B — strong process session identity

PID alone is insufficient because PID reuse exists.

Introduce a small immutable identity for a matched target process and any write-critical process, conceptually:

```text
ProcessSessionIdentity
- ProcessId
- StartTimeUtc or equivalent creation-time evidence
- NormalizedProcessName where useful
```

Requirements:

- collect start time only for matched/candidate processes where needed; do not make every process snapshot expensive;
- failures to read optional metadata from unrelated processes must not kill discovery;
- a production write requires the matched target identity to be revalidated;
- if strong identity cannot be read at write time, fail closed for input and report a diagnostic reason;
- persisted-state work in the next wave must be able to reuse this identity model.

Test PID-equal/start-time-different identities as distinct sessions.

## Ticket C — explicit resolved console identity

Current fallback identity can become the resolved attach PID if window/membership information is missing. That is too weak for the write boundary.

Create an explicit `ResolvedConsoleSession`/equivalent structure containing enough proof for a safe transaction, such as:

- matched target session identity;
- resolved attach PID/session where appropriate;
- verified console process membership set;
- console window handle when available;
- stable console identity derived only from verified evidence;
- read snapshot;
- resolution timestamp/evidence reason.

Do not let arbitrary callers reconstruct safety identity ad hoc.

A console identity may use membership and handle evidence, but document how it behaves when a handle is zero or processes join/leave the console. It must be conservative enough to prevent wrong-console writes without turning normal wrapper process churn into permanent failure.

## Ticket D — transactional pre-send re-resolution

Replace the current weak sequence:

```text
old resolved attach PID -> reread -> match -> write
```

with a transaction:

```text
initial discovery/read
-> rule decision says send is eligible
-> fresh process snapshot
-> re-find the exact matched target session
-> rebuild attach candidates
-> resolve a verified console
-> compare refreshed target/console identity to decision identity
-> reread current tail
-> rerun event + ready + busy + deadline checks
-> verified write on the same attached console
```

Required abort reasons include:

- matched target disappeared;
- process session changed / PID reused;
- no verified attach candidate;
- membership unavailable;
- target absent from attached console;
- console identity changed;
- triggering event changed;
- target no longer ready;
- target now busy;
- deadline/cooldown no longer eligible;
- command invalid;
- native write failed/partial.

Every abort must be NO WRITE.

## Ticket E — verified write boundary

`NativeConsole.TryWriteLine(pid, command)` is currently too generic for production safety.

Keep a low-level primitive if useful for the controlled harness, but route production sends through a stronger operation that verifies ownership *after attaching* and immediately before input records are written.

Conceptually:

```text
TryWriteLineVerified(
    resolvedConsole,
    expectedMatchedSession,
    command,
    out failureReason)
```

Inside the write-critical lock/attachment:

- attach;
- re-query console membership;
- prove expected target membership;
- prove expected console identity according to the chosen model;
- open `CONIN$`;
- write one-line Unicode key events + Enter;
- require all events written;
- detach in `finally`.

Do not put a sleep between final verification and write.

## Ticket F — race-focused deterministic seams

Do not import a mocking framework. Introduce tiny delegates/interfaces only where necessary to deterministically simulate dangerous races:

- target disappears after first read;
- same PID now has different start time;
- first attach candidate fails, later candidate succeeds;
- membership query fails;
- attached console excludes target;
- console identity changes before send;
- safety reread sees new trigger;
- safety reread sees user input/busy state;
- write returns partial event count.

Prove zero writes on every negative path.

## Native integration harness expansion

Extend the controlled harness so it can prove:

- matched process membership is visible in its console;
- the resolver selects a usable candidate;
- verified write succeeds only for the intended harness;
- wrong expected target PID is refused;
- a deliberately invalid/mismatched identity is refused;
- exactly one command line is received;
- foreground focus is unchanged if a reliable check already exists;
- cleanup leaves no attached console state in SAICONT.

Never aim this test at real Cline/Codex.

## Observability

Expose concise reasons, for example:

```text
send_blocked=membership_unavailable
send_blocked=target_not_in_console
send_blocked=process_session_changed
send_blocked=console_changed
send_blocked=event_changed
send_blocked=prompt_not_ready
send_blocked=target_busy
send=command_written
```

Do not spam ordinary healthy no-trigger polls.

## Verification gate

Required:

- clean warnings-as-errors build;
- all old self-tests green;
- new safety/race tests green;
- controlled verified-write harness green;
- `--probe` behavior unchanged/read-only;
- multi-poll hidden dry-run green;
- no production/live-agent write during verification;
- validator PASS;
- independent review clears P0-P3.

## Version

Ship as `0.4.0` only after all invariants above pass.

## Definition of Done

- empty/unavailable membership fails closed;
- `GetConsoleProcessList` is non-truncating within a bounded safe limit;
- strong target process-session identity exists;
- every production write uses fresh process and console resolution;
- the target's membership is re-proven at the write boundary;
- process/session/console change causes zero input;
- dangerous races have deterministic negative tests;
- controlled harness receives exactly the intended command;
- previous v0.3.1 functionality remains green.
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


========================================================================================
FILE: 02_v0.5_EVENT_CORRELATION_AND_DURABLE_STATE.md
========================================================================================

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

# WAVE 2 — v0.5.0 EVENT CORRELATION AND DURABLE STATE

## Objective

Make retry decisions refer to one specific current failure event, and make the safety-critical decision state survive a SAICONT restart without duplicate continuation or cooldown reset.

Do not start this wave until v0.4 transactional write safety is verified, or prove that equivalent invariants already exist in the incoming tree.

## Primary surfaces

- `src/RetryPolicy.cs`
- `src/WatcherEngine.cs`
- `src/Configuration.cs`
- new small state-store source if justified
- `src/Program.cs` tests
- `scripts/smoke.ps1`
- `.gitignore`
- docs

## Ticket A — explicit trigger event

Current problem: the newest trigger match is selected, but retry time is parsed from the entire snapshot. This can bind an old `Try again ...` line to a new limit event.

Introduce an explicit event object, conceptually:

```text
TriggerEvent
- RuleName / pattern identity
- MatchStart / MatchEnd
- TriggerRow
- LocalContextStart / LocalContextEnd
- NormalizedTriggerText
- TriggerFingerprint
- ParsedDueUtc
```

The exact shape is flexible. The invariant is not:

> choose trigger from one location, then parse semantics from unrelated scrollback.

The selected event must own all event-local parsing.

## Ticket B — bounded event context

Determine a small event window using actual Cline/Codex output structure.

Preferred default behavior:

- selected/latest trigger line/block;
- a small bounded number of lines following it for `Try again...` details;
- a very small preceding allowance only if real output requires it;
- never scan all 180 lines for retry time once the event is selected.

Make window sizes constants or validated configuration only if operators genuinely need them. Avoid configuration surface without evidence.

Add tests with two historical limit blocks where the newer event must receive the newer deadline.

## Ticket C — current-tail busy/readiness model

Busy state is a property of the current terminal state, not the entire recent history.

Replace broad `MatchesAny(snapshot.Text, BusyPatterns)` with a bounded current-tail model.

Use available console information:

- cursor row/line;
- last N rows near cursor;
- ready prompt line;
- active output tail.

Requirements:

- an old `Working (...)` line above a current idle prompt cannot keep Codex busy;
- a current `Working (...)` state is busy;
- typed user text makes ready false;
- an empty recognized prompt is ready;
- ambiguous tail => no automatic send.

Keep current prompt safety strict.

## Ticket D — stable event fingerprint

The current token includes pattern index, absolute buffer row, and hash of matched text. Absolute rows can move as console history scrolls, while identical wording can repeat.

Define a fingerprint that supports both:

- recognizing the same unchanged event across polls/restarts;
- distinguishing a genuinely later event with identical wording.

Do not rely on only one of:

- pattern index;
- absolute row;
- matched text hash;
- PID.

Use a conservative combination of:

- strong process session identity from v0.4;
- rule identity;
- normalized event-local content;
- parsed deadline when present;
- event-relative context;
- bounded sequence/anchor evidence derived from the console tail.

Document collision/scroll behavior. If exact identity is impossible after full scroll eviction, prefer conservative re-detection/cooldown over unsafe immediate send.

## Ticket E — durable retry ledger

Add a tiny dependency-free persisted state store.

Preferred location:

```text
run\SAICONT.state.xml
```

or an equivalently documented runtime path.

This file intentionally survives normal start/stop. It is not the transient PID/stop marker.

Persist only safety-critical state. Do not persist console transcripts.

Recommended per-session/event fields:

```text
schemaVersion
ruleName
processId
processStartUtc
triggerFingerprint
lastObservedUtc
lastWriteUtc
nextAllowedAttemptUtc
awaitingOutcome
sawBusyAfterWrite
suppressedFingerprint
stateRevision / optional
```

Store the minimum required to preserve semantics.

## Ticket F — atomic state I/O and corruption safety

State writes must be interruption-safe:

- serialize to a temp file in the same directory;
- close/flush;
- replace/move into place atomically/best-effort atomically using supported .NET/Windows primitives;
- never treat a half-written file as valid state.

Add a schema version.

On corrupt/unsupported state:

- log a concise diagnostic;
- quarantine/rename or ignore safely;
- do not crash-loop;
- do not treat corruption as permission for immediate input;
- apply a conservative cooldown/read-only recovery policy until a fresh trustworthy event is established.

Document exact behavior.

## Ticket G — restart semantics

Prove these cases with a fake clock/state store or process-restart harness:

### Case 1: restart after a successful write

- SAICONT writes `cc`.
- It restarts five seconds later.
- Same old trigger remains visible.

Expected: no immediate second `cc`.

### Case 2: restart during cooldown

- next retry was 60 seconds after an attempt;
- restart occurs 20 seconds later.

Expected: about 40 seconds remain. Do not reset to 60 and do not become immediately eligible.

### Case 3: restart after stale-event suppression

Expected: same old event remains suppressed.

### Case 4: new event in the same long-lived agent session

Expected: new event is eligible according to its own deadline/cooldown.

### Case 5: new process session reuses a PID

Expected: old suppression/cooldown is not blindly inherited.

### Case 6: wall-clock anomalies

Persist UTC. Handle state timestamps absurdly far in the future/past conservatively. Do not create multi-day accidental lockout from corrupt timestamps.

## Ticket H — state pruning

Bound durable state:

- expire dead sessions after a documented retention horizon;
- retain enough history to prevent immediate stale replay after normal restarts;
- enforce a small max record count;
- prune during normal state writes/polls, not with a new maintenance thread;
- make pruning deterministic/testable.

## Probe/once/dry-run isolation

Required:

- `--probe` never writes/modifies the durable retry ledger;
- `--once` remains no-input and observational unless its documented semantics explicitly require ephemeral evaluation;
- `--dry-run` must not record a successful production write;
- smoke must use an isolated temp state path or an explicit in-memory/test mode so verification cannot contaminate the operator's real retry history.

Add command-line/internal test override for state path only if necessary. Do not expose needless complexity in normal use.

## Verification gate

Add deterministic tests for:

- newest trigger binds to its own deadline;
- old and new identical wording remain distinguishable where evidence allows;
- historical busy text + current ready prompt => not busy;
- current busy state => busy;
- same event fingerprint stable across equivalent polls;
- restart preserves cooldown;
- restart preserves stale suppression;
- new process session escapes old state;
- corrupt file fails safe;
- unsupported schema fails safe;
- atomic-write recovery behavior;
- stale records prune;
- probe/dry-run isolation.

Update controlled smoke to prove restart semantics without sending to a real agent.

## Version

Ship as `0.5.0` after full verification.

## Definition of Done

- every trigger has bounded local semantic context;
- retry time cannot cross-bind from an older event;
- busy/ready reflect the current tail rather than arbitrary history;
- event identity survives ordinary poll/restart behavior conservatively;
- cooldown and suppression persist across restart;
- PID reuse does not inherit another process session's state;
- state storage is atomic, versioned, bounded, and corruption-safe;
- read-only/test modes do not contaminate production retry history;
- all prior safety gates remain green.
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


========================================================================================
FILE: 03_v0.6_LIVENESS_AND_RECOVERY_ENGINE.md
========================================================================================

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

# WAVE 3 — v0.6.0 LIVENESS AND RECOVERY ENGINE

## Objective

Turn the current retry state machine into an explicit, bounded recovery controller that understands what happened after a continuation attempt and cannot spam indefinitely when a provider or agent remains stuck.

This wave is about behavior after an eligible `cc`, not about adding speculative new failure detectors.

## Core principle

SAICONT should distinguish these states explicitly:

```text
IDLE_NO_EVENT
EVENT_WAITING_DEADLINE
EVENT_READY_TO_ATTEMPT
COMMAND_WRITTEN_AWAITING_OUTCOME
TARGET_BECAME_BUSY_OR_PROGRESSING
RECOVERY_CONFIRMED
EVENT_STILL_PRESENT_READY
BACKOFF_WAIT
SESSION_DISAPPEARED
TARGET_UNREADABLE
AMBIGUOUS_FAIL_CLOSED
```

Names may differ, but implicit booleans should no longer be the only expression of this lifecycle.

## Ticket A — explicit recovery state machine

Refactor `RetrySessionState` into an auditable state machine with transition functions that are largely pure and fake-clock testable.

Avoid a giant generic workflow framework.

Each transition must have:

- current state;
- current observation/event;
- time;
- policy/config;
- output decision;
- next state;
- reason.

Illegal/impossible combinations should fail safe and have tests.

## Ticket B — post-send outcome classification

After a successful native write, classify later polls using observable terminal evidence.

Required categories:

- progress/busy observed after write;
- prompt changed/trigger disappeared;
- recovered and ready with old trigger only in history;
- same active limit remains and prompt is ready;
- session vanished/restarted;
- console temporarily unreadable;
- ambiguous.

Do not equate `write succeeded` with `agent recovered`.

Do not equate `trigger still visible somewhere in scrollback` with `retry failed` unless the event-correlated current state proves it.

## Ticket C — bounded retry/backoff policy

The current fixed 60-second repetition can become noisy during persistent failure.

Add a configurable but conservative retry schedule for repeated failed recovery of the same event.

Preferred behavior:

- honor provider-supplied future deadlines first;
- first fixed fallback interval remains simple;
- repeated same-event attempts increase delay with bounded exponential or stepped backoff;
- cap maximum interval;
- cap or strongly limit aggressive attempts within a rolling window;
- reset backoff on a genuinely new event/session;
- successful recovery clears active retry pressure while preserving stale-event suppression.

Avoid jitter unless multiple SAICONT instances across machines are a real use case. This is a local utility, not a distributed fleet.

Expose configuration only for values operators reasonably need, for example:

```text
retryIntervalSeconds
backoffMultiplier
maximumRetryIntervalSeconds
maximumAttemptsPerEvent (optional)
```

If adding `maximumAttemptsPerEvent`, define what happens after the cap: remain monitor-only for that event and log a one-time exhausted status until a new event occurs.

## Ticket D — no-spam invariants

Prove:

- same event cannot produce immediate repeated writes across adjacent polls;
- restart does not reset backoff;
- write failure itself does not trigger a tight loop;
- native/read errors do not become permission to retry faster;
- changing ready/busy state does not accidentally clear cooldown;
- exhausted/suppressed state logs once or at a bounded cadence;
- a new event can recover from an old exhausted state.

## Ticket E — transient unreadable behavior

Console/process reads can race or fail temporarily.

Define a small transient-failure policy:

- one failed poll does not erase durable event state;
- a bounded number/time of unreadable polls can preserve waiting state;
- no write occurs while unreadable;
- persistent unreadability is surfaced distinctly and does not spam logs;
- when readability returns, re-establish process/session/console/event identity before resuming decisions.

Do not retry Win32 attachment in a hot inner loop. Poll cadence is already a retry boundary.

## Ticket F — liveness telemetry in local logs

Keep logs human-usable. Emit transition-oriented events rather than every poll.

Useful examples:

```text
EVENT_DETECTED
WAITING deadline=...
ATTEMPT_WRITTEN attempt=1
PROGRESS_OBSERVED
BACKOFF attempt=2 next=...
RECOVERY_CONFIRMED
EVENT_SUPPRESSED_STALE
RECOVERY_EXHAUSTED
SESSION_CHANGED
TARGET_UNREADABLE
```

Do not log console transcript contents or secrets.

## Ticket G — deterministic timeline simulator

Build a small in-process test harness with fake time and scripted observations.

It should execute sequences such as:

```text
limit -> wait -> ready -> write -> busy -> ready/no-current-event -> recovered
```

and:

```text
limit -> write -> no progress -> same event -> backoff -> write -> persistent limit -> exhausted
```

and:

```text
limit -> write -> SAICONT restart -> restore -> busy -> recovered
```

This simulator should make state-machine behavior testable without sleeping real minutes.

## Ticket H — policy boundaries

SAICONT must remain a continuation watcher, not an autonomous prompt generator.

Do not add:

- free-form LLM reasoning;
- arbitrary command synthesis;
- automatic provider/model switching;
- clearing conversations;
- Esc/Ctrl+C recovery automation;
- killing/restarting the user's agent process;
- changing Cline/Codex configuration.

Only the configured one-line continuation command remains eligible for injection.

## Verification

Required tests include:

- success lifecycle;
- provider deadline lifecycle;
- same-event repeated failure/backoff;
- restart mid-backoff;
- native write failure;
- temporary unreadable session;
- session restart after attempt;
- recovery followed by stale scrollback;
- genuinely new later event;
- retry-exhausted behavior;
- attempt counter reset boundaries;
- no duplicate send in adjacent polls.

Run all earlier v0.4/v0.5 negative safety cases too.

## Version

Ship as `0.6.0`.

## Definition of Done

- recovery lifecycle is explicit rather than implicit boolean soup;
- successful write, progress, and recovery are separate facts;
- repeated same-event failures back off in a bounded way;
- no transient error can create a tight retry loop;
- restart preserves attempt/backoff semantics;
- exhaustive timeline tests cover critical transitions;
- logs describe state transitions without poll spam;
- no new autonomous command scope is introduced.
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


========================================================================================
FILE: 04_v0.7_RULE_ENGINE_AND_CONFIGURATION_HARDENING.md
========================================================================================

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

# WAVE 4 — v0.7.0 RULE ENGINE AND CONFIGURATION HARDENING

## Objective

Make rules cheap, bounded, diagnosable, and safe to extend through XML without duplicating product configuration in source or relying on broad multi-line regex behavior.

This is not a plugin framework wave.

## Ticket A — canonical runtime rule model

`WatcherConfiguration.CreateDefaults()` currently duplicates product rules and has already drifted from `SAICONT.config.xml` (`cline-openrouter-429` vs `cline-limits`).

Remove production/test configuration duplication.

Preferred design:

- production source of truth remains `SAICONT.config.xml`;
- unit tests construct purpose-specific rules via tiny test builders/factories;
- no hidden fallback to stale default production rules after XML failure;
- malformed/missing required config fails explicitly as it does today.

If `CreateDefaults()` has a legitimate UI/demo purpose, reduce it to non-production test/sample data and name it accordingly. Do not silently run it in production.

## Ticket B — compile regex once

Current config validation constructs regexes, while polling later invokes static `Regex.Matches/IsMatch` from pattern strings again.

Compile validated regex objects once at configuration load or rule construction.

Requirements:

- culture-invariant semantics preserved;
- use explicit options intentionally;
- avoid `RegexOptions.Compiled` blindly if startup cost/memory outweigh benefit on .NET Framework; benchmark both or use reusable `Regex` instances without compiled IL;
- no regex compilation per poll;
- invalid regex identifies target + field + pattern index.

## Ticket C — regex timeout / pathological-pattern defense

Because rules are editable, a pathological regex can block the watcher.

Use the .NET regex timeout constructor where available in the target framework, with a conservative validated timeout.

On timeout:

- do not crash the watcher;
- mark that rule evaluation failed;
- fail closed for input;
- log a deduplicated diagnostic with target/pattern identity, not sensitive console text.

Add deterministic catastrophic-pattern timeout coverage if reliable on the runtime. Avoid flaky timing tests; test the exception-handling path with a seam if necessary.

## Ticket D — bounded event grammar

Review shipped Cline/Codex trigger regexes containing broad `(?is).*?` spans.

Prefer matching within event-local bounded text blocks established in v0.5.

Goals:

- avoid a trigger spanning unrelated console history;
- keep patterns understandable/editable;
- preserve known real output variants;
- add fixtures for each shipped rule variant;
- test near-miss false positives.

Do not create one giant parser if small regex + structural event windows are sufficient.

## Ticket E — rule validation invariants

At config load, validate at least:

- unique target names, case-insensitive;
- enabled target has process names;
- process names normalize to safe basename-like names;
- command is non-empty, one line, within a sane max length;
- poll interval bounds;
- scan line bounds;
- trigger-distance <= scan lines or otherwise logically valid;
- delay/backoff fields within sane bounds;
- trigger patterns non-empty;
- ready patterns non-empty for rules allowed to send;
- pattern count/length bounded;
- log path and state path rules are deterministic/documented;
- unsupported/unknown XML fields are either rejected or explicitly ignored according to one documented policy.

Fail startup with actionable errors.

## Ticket F — configuration diagnostics command

Add a read-only validation/diagnostic mode if one does not already exist, for example:

```powershell
.\bin\SAICONT.exe --validate-config --config .\SAICONT.config.xml
```

Expected behavior:

- parse/validate only;
- no process discovery required;
- no state mutation;
- no console input;
- exit 0 valid, nonzero invalid;
- print concise target/rule summary without dumping secrets.

This becomes part of smoke/install preflight.

## Ticket G — rule fixture tests

Create small in-source or test-data fixtures representing known output:

Cline:

- OpenRouter 429 forms already supported;
- daily free model limit wording;
- compact `8h 57m` retry deadline;
- unrelated 429 text that should not trigger;
- old trigger + new trigger ordering.

Codex:

- usage limit + `try again at`;
- prompt ready;
- current working/busy tail;
- historical busy + current ready;
- unrelated text containing `usage limit` that should not trigger if structurally different.

Keep fixture text synthetic/minimal, not copied large proprietary transcripts.

## Ticket H — diagnostics without transcript leakage

Probe/result output should expose useful structural fields:

- rule;
- target process identity;
- resolved console proof summary;
- event yes/no;
- ready/busy;
- due/next attempt;
- decision reason;
- state mode.

Do not print large recent console text by default. If a debug mode is added, make it explicit and redact/minimize output.

## Ticket I — cleanup

Remove objectively dead residue after v0.3.1-v0.6 changes, including candidates such as:

- `FindWindowBearingAncestor()` if truly unreferenced;
- unused `snapshotProcesses` parameter;
- old rule/token helpers superseded by event objects;
- duplicate parsing/config helpers.

No broad aesthetic rewrite.

## Verification

Add tests for:

- duplicate target names;
- invalid bounds;
- multi-line/oversized commands;
- malformed regex;
- regex evaluation failure/timeout handling;
- fixture positives and near-miss negatives;
- config validation command is read-only;
- production XML and copied `bin` XML stay identical after build;
- no fallback to stale hard-coded rules.

Run performance microchecks on rule evaluation before/after to prove no regression.

## Version

Ship as `0.7.0`.

## Definition of Done

- production rule definitions have one source of truth;
- polling does not compile regex strings repeatedly;
- pathological editable regex cannot hang automatic sending indefinitely;
- shipped rules operate on bounded event context;
- config errors are actionable and fail startup;
- a read-only config validation path exists;
- false-positive/near-miss fixtures exist;
- dead configuration/resolver residue is removed;
- all previous safety/restart/liveness tests remain green.
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


========================================================================================
FILE: 05_v0.8_LIFECYCLE_OPERATIONS_AND_CRASH_RECOVERY.md
========================================================================================

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

# WAVE 5 — v0.8.0 LIFECYCLE, OPERATIONS, AND CRASH RECOVERY

## Objective

Make SAICONT boring to operate: one real instance, trustworthy status, clean stale-marker recovery, instance-specific stop behavior, safe scheduled-task installation, and predictable crash recovery.

## Ticket A — executable-level single-instance ownership

PID files are useful operator metadata, not the strongest lock.

Use a Windows named mutex or equivalent in-box primitive inside `SAICONT.exe` for continuous modes.

Requirements:

- `--watch` and `--dry-run` continuous instances cannot overlap for the same installation/state domain unless explicitly designed;
- read-only `--probe`, `--self-test`, config validation, and harness modes are not unnecessarily blocked;
- abandoned mutex behavior is handled safely;
- duplicate start returns a clear stable exit/status rather than relying only on script timing.

Define mutex naming using a stable installation identity/path hash if needed so unrelated copies of SAICONT can coexist intentionally without sharing state.

## Ticket B — runtime instance record

Replace bare PID-only semantics with a small atomic instance record, for example:

```text
run\SAICONT.instance.xml
- pid
- processStartUtc
- mode
- executablePathHash or normalized path
- startedUtc
- instanceToken
```

The scripts may preserve a simple PID field for compatibility, but lifecycle decisions must validate process start time and executable path, not PID alone.

Protect against PID reuse.

## Ticket C — instance-specific graceful stop

A generic `run\SAICONT.stop` marker can be stale.

Strengthen stop semantics:

- stop request includes/targets the current instance token;
- a stale stop marker from a previous process cannot immediately terminate a new process;
- startup atomically clears/rotates stale request state only after obtaining instance ownership;
- stop script validates current instance record before requesting graceful shutdown;
- forced termination remains last resort and validates identity again immediately before `Stop-Process`.

No broad IPC framework is needed. A tiny tokenized file protocol is enough.

## Ticket D — stale runtime artifact recovery

Cover:

- PID/instance file exists but process absent;
- PID reused by another executable;
- PID reused by SAICONT from a different start time;
- stop marker exists before start;
- state temp file remains after interrupted atomic write;
- log rotation interrupted;
- scheduled task exists but executable moved/missing.

Scripts/status should report and safely repair what is safe to repair.

Do not delete the durable retry ledger merely because process lifecycle artifacts are stale.

## Ticket E — status/diagnostics contract

Make `status.ps1` useful beyond `RUNNING`/`STOPPED` while preserving scriptability.

Suggested concise fields:

```text
RUNNING pid=... start=... mode=... state=... config=...
STOPPED
STALE_RUNTIME_ARTIFACT ...
```

Optionally add a diagnostic switch for:

- task installed/not installed;
- current config valid;
- state ledger readable/schema;
- last log event/time;
- current continuous instance identity.

Keep default output compact.

Define stable exit codes in docs.

## Ticket F — install/update/uninstall transaction

Harden `install.ps1`:

1. validate PowerShell syntax/environment;
2. build;
3. validate config;
4. run deterministic self-tests;
5. optionally stop old valid instance;
6. register/update task;
7. start;
8. verify status;
9. if registration/start fails, leave actionable output and avoid an ambiguous half-installed state.

Do not require admin when current user limited task is sufficient.

Uninstall:

- stops only the verified SAICONT instance;
- removes only the intended task;
- preserves config, logs, and durable retry state by default;
- documents an explicit optional clean-data action rather than silently deleting operational history.

## Ticket G — launcher/path robustness

Test paths containing:

- spaces;
- parentheses;
- Unicode characters where Windows PowerShell/.NET Framework support permits;
- trailing separators;
- current directory unrelated to project root.

Do not rely on caller CWD.

VBS/PowerShell argument quoting must be deterministic.

## Ticket H — scheduled task recovery behavior

Current task uses `MultipleInstances IgnoreNew` and no execution time limit, which is sensible. Evaluate and explicitly set only useful recovery options supported by target Windows 10, such as restart-on-failure if it does not create a crash loop.

If adding restart-on-failure:

- bounded restart count/interval;
- config corruption must not create endless task relaunch spam;
- executable should return distinct permanent configuration error vs transient runtime failure where useful.

Do not overengineer Task Scheduler policy.

## Ticket I — lifecycle smoke matrix

Automate safe scenarios:

- start -> status -> stop;
- duplicate start;
- stale PID/instance record;
- stale stop token;
- wrong PID same executable-name negative control where feasible;
- dry-run lifecycle;
- crash/force-kill -> restart with durable state preserved;
- install -> task presence -> start -> uninstall;
- install `-WhatIf` semantics if retained;
- path with spaces test copy/harness if practical.

Never run real `--watch` against a real target during automated smoke unless input is positively disabled or targets point only to the controlled harness.

## Version

Ship as `0.8.0`.

## Definition of Done

- continuous mode has executable-level single-instance protection;
- lifecycle identity includes process start/session evidence, not PID alone;
- stale stop artifacts cannot kill a newly started instance;
- status detects stale/invalid lifecycle state correctly;
- install/uninstall are transactional enough to avoid ambiguous common failure states;
- forced stop validates identity immediately before kill;
- paths/quoting are robust;
- crash/restart preserves durable retry semantics;
- lifecycle smoke matrix is green.
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


========================================================================================
FILE: 06_v0.9_PERFORMANCE_STABILITY_AND_SOAK.md
========================================================================================

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

# WAVE 6 — v0.9.0 PERFORMANCE, STABILITY, AND SOAK

## Objective

Prove that the now-correct watcher stays cheap and bounded over long runs. Optimize measured hot paths only, then subject state/process/rule/lifecycle logic to accelerated soak and failure injection.

## Establish budgets first

Record baseline on the target workstation/environment where possible. Use relative and practical budgets rather than fake universal numbers.

Track at least:

- idle watcher CPU over multiple polls;
- private working set / managed memory trend;
- handle count trend;
- poll duration median / p95 / max in synthetic scenarios;
- process snapshot cost;
- console attach/read cost for live compatible targets;
- rule evaluation cost;
- state-write frequency/bytes;
- log-write frequency/bytes.

Do not optimize by deleting safety checks.

## Ticket A — polling allocation review

Inspect repeated allocations in:

- process snapshot/dictionaries;
- normalized process-name sets per target;
- regex matches;
- string joins/console text;
- state keys/identity formatting;
- result/log formatting.

Safe optimizations may include:

- precomputed normalized process-name sets in immutable runtime rules;
- prebuilt regex objects from v0.7;
- bounded reusable helper structures where simple;
- avoiding duplicate full process snapshots inside one poll except when write-time revalidation is required;
- avoiding duplicate reads of the same verified console for multiple rules only if safety and rule isolation remain obvious.

Do not introduce unsafe shared mutable caching across polls for process/console ownership.

## Ticket B — console read bounds

Review screen-buffer reading:

- exact number of rows needed;
- very wide console buffer behavior;
- large scanLines validation bounds;
- cursor near top/bottom;
- partial/failed row reads;
- Unicode behavior;
- line trimming semantics.

Cap width * rows to a sane maximum so malformed/extreme console dimensions cannot allocate absurd buffers.

If reading rows one at a time is measurable overhead, evaluate a bounded block read API only if it remains simpler and well-tested. Do not rewrite native I/O for theoretical speed.

## Ticket C — bounded in-memory caches

Prune:

- watcher session state map;
- operational-log dedupe map;
- any new process/rule/state caches added in earlier waves.

Requirements:

- max entry bounds and/or last-seen expiration;
- cleanup piggybacks on poll/log cadence;
- no dedicated maintenance thread;
- deterministic pruning tests;
- safety state is not pruned too early to permit stale duplicate sends.

## Ticket D — logging resilience

Test:

- log directory missing;
- log file locked/read-only;
- rotation at boundary;
- retained file cap;
- repeated errors deduplicate;
- later changed errors are not hidden;
- logging failure does not authorize input or crash a safe watcher loop unnecessarily.

Decide whether critical logging failure should disable writes. For unattended safety, strongly consider fail-closed or a one-time stderr/event route only if the operator otherwise loses all auditability. Document the policy.

## Ticket E — accelerated deterministic soak

Create a no-sleep simulation capable of tens/hundreds of thousands of logical polls with fake clock/process/console observations.

Include mixes of:

- no target;
- target idle/no event;
- repeated limit events;
- recovery cycles;
- process restarts;
- PID reuse;
- console read errors;
- state reloads;
- log dedupe keys;
- config rules;
- stale state pruning.

Assert:

- memory/state collections remain bounded by design;
- send count matches expected exactly;
- no adjacent duplicate sends;
- no stale-event resurrection;
- no state-machine illegal transition;
- expected writes to durable state are bounded and not every poll.

## Ticket F — native stress harness

Without touching live agents, run repeated controlled console attach/read/write/detach cycles against temporary harness processes.

Check:

- no leaked console attachment;
- no escalating process handle count;
- no input duplication;
- process exit during attach handled;
- multiple temporary harnesses deduplicate/resolve correctly;
- cleanup after forced harness termination.

Keep runtime reasonable. This is an accelerated stress, not a 24-hour human-wait requirement.

## Ticket G — fault injection

Inject deterministic failures at important boundaries:

- process snapshot throws/returns race;
- process start time unavailable;
- attach fails;
- membership fails;
- screen buffer read fails midway;
- regex evaluation fails/times out;
- state load corrupt;
- state write fails;
- log write fails;
- input write fails/partial;
- stop requested during poll;
- stop requested just before eligible send.

For every fault, define whether watcher:

- skips write and continues;
- enters conservative state;
- exits with explicit permanent error;
- logs/deduplicates.

No fault should accidentally convert uncertainty into permission to write.

## Ticket H — stop responsiveness

Current loop uses poll sleeps. Ensure graceful stop latency is bounded and reasonable even at configured maximum poll intervals.

Prefer an interruptible wait primitive/event if it simplifies responsiveness without adding a complex concurrency model.

Test stop while:

- idle waiting;
- after process snapshot;
- before a would-send decision;
- immediately before native write.

A stop request that arrives before the write-critical transaction should abort the send.

## Ticket I — measured optimization report

For every performance change, record:

- baseline;
- change;
- resulting measurement;
- safety impact assessment.

Reject optimizations that improve tiny CPU percentages while obscuring the correctness model.

## Version

Ship as `0.9.0`.

## Definition of Done

- polling/runtime collections are bounded;
- regex/config work is not rebuilt per poll;
- state writes occur only on meaningful state changes;
- console read memory is bounded for extreme dimensions;
- accelerated soak proves exact send counts and bounded state;
- native stress shows no obvious handle/attachment leak;
- fault injection always fails closed for writes;
- graceful stop is responsive and can abort pending sends;
- measured resource profile remains appropriately tiny for a 2-second watcher.
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


========================================================================================
FILE: 07_v0.9.5_RELEASE_CANDIDATE_AUDIT.md
========================================================================================

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

# WAVE 7 — v0.9.5 RELEASE CANDIDATE AUDIT

## Objective

Freeze feature development and audit the whole product as an integrated unattended utility. This wave is allowed to fix defects, simplify dangerous complexity, and improve tests/docs. It is not allowed to casually add new features.

## RC policy

Target `0.9.5` as a release-candidate stabilization marker. If the project convention prefers `0.9.1` or `1.0.0-rc`, keep the existing simple version-file style and choose one consistent documented form. Do not import a versioning framework.

## Audit 1 — correctness map

Trace every path from:

```text
process discovery
-> console resolution
-> snapshot read
-> event detection
-> deadline/current state
-> durable state
-> send eligibility
-> write transaction
-> post-send outcome
-> persistence/logging
```

For each stage document:

- input evidence;
- failure modes;
- fail-open/fail-closed behavior;
- state mutation;
- test coverage.

Look specifically for:

- stale object reuse;
- old timestamps;
- mixed process sessions;
- state key mismatch;
- trigger fingerprint instability;
- retry time cross-binding;
- ready/busy ambiguity;
- exception paths that skip `RecordAttempt`/state save incorrectly;
- writes after stop request;
- write success recorded when partial/unknown.

## Audit 2 — native Win32 boundary

Review all P/Invoke declarations and lifetime behavior:

- signatures/types/SetLastError;
- `FreeConsole()` paths;
- safe handles;
- attach/read/write serialization lock;
- console process membership retrieval;
- screen dimensions and integer conversions;
- Unicode key records;
- partial writes;
- errors after process exit;
- thread/process console state assumptions.

Add negative controls for every material finding.

## Audit 3 — persistent state and upgrade behavior

Treat `0.3.1 -> current RC` as an upgrade scenario.

Test:

- no state file from old version;
- current state file;
- corrupt state;
- future schema version;
- old supported schema if migration was introduced;
- state file present while target process absent;
- state from a moved/copied installation;
- state from same PID but different process start time.

Define whether durable state is installation-scoped. Ensure copies do not accidentally share one state path/mutex unless configured intentionally.

## Audit 4 — configuration/security boundary

SAICONT is a local automation tool, but config controls a command that will be typed into an agent.

Review:

- command validation;
- XML external entity behavior / DTD processing as applicable to the chosen XML parser;
- path resolution;
- relative path traversal implications;
- config replacement while running;
- regex denial-of-service mitigation;
- accidental logging of command/console content;
- local credential patterns in release hygiene.

If using `XmlDocument`/`XmlReader`, explicitly disable external entity resolution/DTD unless genuinely required. Add regression coverage.

Do not pretend local user-controlled XML is an internet security boundary, but do remove avoidable parser hazards.

## Audit 5 — lifecycle/operations

Run clean scenarios:

- fresh extracted source tree with no `bin/logs/run`;
- build;
- config validation;
- self-test;
- probe with no target -> honest SKIP;
- controlled native harness;
- dry-run start/status/stop;
- duplicate start;
- stale instance artifacts;
- install/status/uninstall if environment permits;
- rebuild/update with existing durable state;
- interrupted/crash recovery.

No release gate should depend on a user's real Cline/Codex being rate-limited at that moment.

## Audit 6 — test integrity

Audit the tests themselves for false positives.

Every critical negative test must prove its setup actually entered the dangerous condition before asserting no write.

Examples:

- wrong membership test must prove attach succeeded but membership excluded the target;
- changed-event test must prove initial state would otherwise send;
- cooldown restore test must prove persisted remaining time is nonzero;
- regex-timeout test must prove timeout/error path executed;
- state corruption test must prove corrupt bytes were actually loaded;
- harness exact-one-write must fail on both zero and two writes.

Avoid tests that merely assert exit code 0.

## Audit 7 — performance/stability gate

Re-run v0.9 measured baseline and soak on final RC bytes.

Reject:

- unbounded collection growth;
- per-poll durable file rewrites when nothing changed;
- repeating native/log errors every two seconds;
- CPU spikes from pathological regex;
- leaked handles across stress cycles.

## Audit 8 — documentation truthfulness

Update README/OPERATIONS/CHANGELOG to accurately state:

- supported OS/runtime architecture;
- classic console limitation;
- what Cline/Codex forms are supported;
- what SAICONT never does;
- exact probe exit codes;
- state file purpose/location/schema/corruption behavior;
- retry/backoff behavior;
- logs and rotation;
- lifecycle commands and status codes;
- install/uninstall preservation rules;
- troubleshooting for unreadable/unsupported console surfaces;
- safe verification procedure.

Remove stale claims from older versions.

## P0-P3 review gate

Classify findings:

- P0: can cause destructive/unbounded wrong input or severe system impact;
- P1: can send continuation to wrong session/event or materially violate retry safety;
- P2: can lose durable retry semantics, crash-loop, or create serious operational unreliability;
- P3: significant diagnostics/performance/config/lifecycle defect that undermines unattended use.

RC cannot complete with unresolved P0-P3.

P4 polish can be deferred if documented and harmless.

## Version/ship

After all RC fixes and reruns, set the chosen RC version (`0.9.5` preferred by this roadmap) and return to HUNT/ready state according to SAIPEN conventions. Do not label `1.0.0` yet.

## Definition of Done

- whole send path audited end-to-end;
- native declarations/lifetimes reviewed;
- state upgrade/corruption/install-scope behavior tested;
- XML/regex/path safety reviewed;
- clean-tree lifecycle rehearsal passes;
- tests audited against false-positive patterns;
- final performance/soak rerun passes;
- docs match actual behavior;
- no unresolved P0-P3 findings remain.
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


========================================================================================
FILE: 08_v1.0_RELEASE_AND_FINAL_ACCEPTANCE.md
========================================================================================

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

# WAVE 8 — v1.0.0 RELEASE AND FINAL ACCEPTANCE

## Objective

Ship the first logically complete SAICONT release only after proving the full unattended-safety contract. This wave should be mostly verification, release hygiene, and documentation. Any major architecture change discovered here means return to the appropriate earlier wave, not patch around it blindly.

## Feature freeze

No new providers, UI features, commands, config knobs, or architecture layers during v1.0 release unless required to fix a release-blocking defect.

## Gate 1 — clean source tree build

From a release-style tree with generated artifacts removed:

- `bin/` absent;
- `run/` absent;
- `logs/` absent or empty according to packaging policy;
- no temp files;
- no local credentials;
- no test-generated state.

Run canonical build.

Verify:

- warnings-as-errors clean;
- `bin\SAICONT.exe` produced;
- canonical XML copied correctly;
- binary launches TERMISAI with no args;
- read-only config validation succeeds.

## Gate 2 — deterministic full test suite

Run all self-tests on final bytes.

Report exact count.

The suite must cover at minimum:

- process lineage candidate ordering/deduplication;
- fail-closed membership;
- membership buffer behavior;
- strong process identity/PID reuse;
- transactional pre-send race negatives;
- event-local retry deadlines;
- current busy/ready semantics;
- event fingerprint behavior;
- durable state restart/corruption/pruning;
- liveness/backoff state machine;
- regex/config validation and timeout failure;
- lifecycle instance/token behavior;
- state/cache pruning;
- fault injection;
- accelerated soak exact-send assertions.

Zero failures.

## Gate 3 — controlled native integration

Using only temporary harness consoles:

- read target console;
- verify membership;
- verified write exactly once;
- wrong membership/identity refuses;
- process disappears race refuses;
- repeated attach/read/detach stress passes;
- no focus stealing;
- no leaked runtime markers after cleanup.

Zero input to real agents.

## Gate 4 — live read-only probe

Run against any current compatible Cline/Codex sessions.

Accepted outcomes:

- PASS when all discovered compatible targets read successfully;
- SKIP when no matching target is available;
- FAIL_ALL/FAIL_MIXED blocks release until explained/resolved if discovered targets should be supported.

Do not convert unsupported terminal architecture into PASS. If a target is conclusively ConPTY/unsupported by the documented v1 architecture, classify and document it explicitly rather than pretending it was read.

## Gate 5 — hidden dry-run lifecycle

Run multiple polls, restart, status, stop, and cleanup.

Verify:

- single-instance protection;
- no production state contamination from smoke;
- stop responsiveness;
- clean instance/stop markers;
- no recurring structural errors;
- durable production state remains intact when expected.

## Gate 6 — install/uninstall rehearsal

Where environment permits:

- install current-user scheduled task;
- verify action/path/arguments/principal/settings;
- verify watcher starts;
- verify status;
- stop/restart;
- uninstall;
- verify task removed;
- verify config/log/state preservation policy.

If Task Scheduler access is unavailable, mark this gate externally unavailable with exact manual verification commands. Do not fabricate PASS.

## Gate 7 — release artifact audit

Review release tree:

- `VERSION` exactly `1.0.0` only after all gates pass;
- README badge/version matches;
- CHANGELOG contains 1.0.0 summary and upgrade notes;
- OPERATIONS reflects final state/lifecycle semantics;
- `.gitignore` covers generated/runtime/temp/local-secret artifacts;
- no accidental binaries/logs/state in source release unless packaging explicitly wants them;
- no obsolete roadmap/test scratch files inside product tree;
- no credentials/tokens/user-specific absolute paths in shipped docs/config/source.

Search for obvious local path remnants and secrets before ship.

## Gate 8 — final code review

Review specifically for:

- any write path bypassing verified transaction;
- any fail-open unknown state;
- any read-only mode mutating production state;
- any state transition allowing cooldown bypass;
- any stale PID/session issue;
- any unbounded collection/file growth;
- any swallowed exception that could silently enable writes;
- any test-only bypass reachable from normal production args;
- any debug mode that can inject without safeguards.

No P0-P3 unresolved.

## Gate 9 — SAIPEN integrity

- all release tickets done through actual SAIPEN/SAIOPS lifecycle;
- board has no forgotten DOING/BLOCKED item relevant to v1;
- state/log/recovery receipts are consistent;
- validator PASS on final bytes;
- final state transitions according to protocol to shipped/completed then HUNT/idle as appropriate;
- no manual fake DONE markers.

## v1.0 product contract

At release, document SAICONT as:

> A small Windows classic-console watcher that safely detects configured Cline/Codex limit events and submits a fixed continuation command only after verified target ownership, current prompt readiness, deadline/cooldown eligibility, event identity, and durable retry safeguards are satisfied. It runs hidden without foreground input automation and remains fail-closed when console/session evidence is uncertain.

Do not claim support beyond tested console surfaces.

## No-publish behavior

Current SAIPEN mode is `no-publish` and the inspected project has no repository publication evidence.

Therefore:

- complete local ship/release state;
- do not invent Git tags/remote pushes/releases;
- if the incoming environment later contains a legitimate repository and project policy explicitly permits publishing, follow that actual state instead.

## Final handoff

Return one standalone v1.0 handoff with:

- final architecture summary;
- complete safety invariants;
- version history from 0.3.1 through 1.0.0;
- tickets/waves completed;
- files changed in final release wave;
- exact test count;
- exact build/self-test/smoke commands and outcomes;
- controlled native integration outcome;
- live probe outcome;
- install/uninstall outcome;
- accelerated soak/resource summary;
- audit P0-P4 summary;
- known limitations;
- final `VERSION`;
- final SAIPEN state/validator result;
- explicit `STATUS: SAICONT_V1: COMPLETE` only if every mandatory gate is actually satisfied.

If any mandatory gate fails, do not set 1.0.0 and do not emit COMPLETE.

## Definition of Done

`1.0.0` exists only when the project is safe, restart-correct, bounded, tested, auditable, operable, and honestly documented. A version number is not evidence. Humanity has tried that strategy with software for decades; the results are available in every bug tracker on Earth.
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


========================================================================================
FILE: 10_FINAL_ACCEPTANCE_MATRIX.md
========================================================================================

# SAICONT v1.0 FINAL ACCEPTANCE MATRIX

Use this matrix during RC and v1 ship. Every mandatory row needs evidence.

| Area | Mandatory invariant | Evidence required | Release blocker |
|---|---|---|---|
| Console ownership | Unknown membership never authorizes write | deterministic negative + native harness | Yes |
| Membership API | no safety-relevant truncation; failures explicit | unit/native test | Yes |
| Process identity | PID reuse/session change detected | deterministic race test | Yes |
| Console identity | write uses freshly resolved same intended console | transactional race tests | Yes |
| Write boundary | membership/identity checked immediately before input | code review + harness | Yes |
| Ready state | only recognized empty current prompt can send | fixtures/tests | Yes |
| Busy state | historical busy output cannot block/authorize current state incorrectly | fixtures/tests | Yes |
| Event scope | newest trigger owns its own retry context | two-event regression test | Yes |
| Retry time | old deadline cannot cross-bind to new event | deterministic test | Yes |
| Event identity | same event stable; new event distinguishable conservatively | deterministic tests | Yes |
| Durable cooldown | restart preserves remaining cooldown | state/restart test | Yes |
| Durable suppression | restart cannot resurrect stale trigger | state/restart test | Yes |
| PID reuse + state | new process session does not inherit unsafe stale state | deterministic test | Yes |
| State file | atomic, versioned, corruption-safe, bounded | corruption + atomic/prune tests | Yes |
| Probe | read-only; PASS/SKIP/FAIL classifications honest | CLI tests/smoke | Yes |
| Dry-run | zero input and no production retry contamination | smoke isolation test | Yes |
| Recovery | successful write != automatic recovery | timeline tests | Yes |
| Backoff | repeated same-event failure is bounded/no-spam | timeline tests | Yes |
| Errors | unreadable/unknown paths fail closed for input | fault injection | Yes |
| Stop | stop before write-critical transaction can prevent input | deterministic/native test | Yes |
| Single instance | duplicate continuous watcher prevented in executable | lifecycle smoke | Yes |
| PID artifacts | stale/reused PID cannot control wrong process | lifecycle negative tests | Yes |
| Stop artifact | stale stop request cannot kill new instance | lifecycle test | Yes |
| Install | current-user task install/update/start verified | rehearsal or honest unavailable | Environment-dependent |
| Uninstall | removes intended task, preserves documented data | rehearsal | Environment-dependent |
| Regex | validated once/reused; pathological rules bounded | config/timeout tests | Yes |
| Config | one production source of truth; actionable failures | validation tests | Yes |
| XML parser | external entity/DTD behavior safe/documented | code review/test | Yes |
| Memory | session/log/cache collections bounded | accelerated soak | Yes |
| State writes | no per-poll rewrite without meaningful change | instrumentation/soak | Yes |
| Handles | no attach/read/write leak trend | native stress | Yes |
| CPU/poll | watcher remains low-overhead | measured baseline/report | Yes if regression severe |
| Logging | rotation/dedupe bounded; critical errors visible | tests | Yes |
| Test integrity | negative tests prove dangerous setup really occurred | RC audit | Yes |
| Clean build | warnings-as-errors clean from generated-artifact-free tree | release command output | Yes |
| Docs | behavior/limits/exit codes/state/lifecycle truthful | RC review | Yes |
| SAIPEN | state/board/log/receipts consistent; validator PASS | validator | Yes |
| Review | no unresolved P0-P3 | final audit | Yes |
| Version | `1.0.0` set only after all mandatory gates | release review | Yes |

## Required final command/evidence bundle

At minimum capture the actual equivalent of:

```powershell
.\build.ps1
.\bin\SAICONT.exe --validate-config --config .\SAICONT.config.xml
.\bin\SAICONT.exe --self-test
.\bin\SAICONT.exe --probe --config .\SAICONT.config.xml
.\scripts\smoke.ps1
```

Plus whatever deterministic soak/fault/lifecycle commands are introduced by the roadmap.

Do not treat a live-target SKIP as a deterministic-test failure. Do not treat a discovered unreadable target as a SKIP merely to release.
