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

# CURRENT v0.3.1 AUDIT NOTES USED TO ORDER THE ROADMAP

These are implementation observations from the inspected v0.3.1 archive. They are not claims about future modified trees; re-check them before acting.

## Watcher state

`WatcherEngine` keeps:

```csharp
private readonly Dictionary<string, RetrySessionState> _states = ...;
```

There is no durable retry state in v0.3.1.

Current keying is effectively rule + console identity. Restart recreates the dictionary.

## Pre-send path

The current send path:

- resolves a console during normal poll;
- makes a retry decision;
- re-reads the previously resolved attach PID;
- re-runs trigger/ready/busy;
- calls `NativeConsole.TryWriteLine(resolvedAttach, ...)`.

It does not re-snapshot/re-resolve the matched process identity immediately before the write.

## Membership fail-open

`ConsoleServesMatchedProcess` currently returns true when membership list is null/empty. This is the first priority safety correction.

## Membership buffer

`NativeConsole.TryRead()` currently allocates:

```csharp
uint[] processIds = new uint[64];
```

and copies up to that capacity. Safety-relevant membership should not be silently truncated/unknown.

## Event/deadline mismatch

`RuleMatcher` selects the last/newest trigger match by text index.

Then it calls retry-time parsing using the whole snapshot text. `RetryTimeParser` uses first regex matches for compact duration/duration/clock. Multiple historical limit events can therefore be semantically cross-bound.

## Busy scope

Busy patterns are evaluated against `snapshot.Text`, the full bounded scan window, while ready is evaluated against `snapshot.CursorLine`. Current busy state should be tail-scoped like the current prompt.

## Regex cost

Configuration validation instantiates regex objects only to validate patterns, but runtime matching uses static `Regex.Matches/IsMatch` with strings. A canonical prevalidated reusable rule representation should remove repeated parse/cache pressure and enable explicit timeouts.

## Lifecycle

PowerShell helpers read `run\SAICONT.pid`, then verify that PID currently belongs to the expected executable path. This is already better than blind PID killing, but process start-time/instance-token evidence would close PID-reuse/stale-stop gaps.

## Runtime log evidence

After the v0.3.1 repair, hidden dry-run logs show Codex matches with a new 60-second cooldown after each watcher restart, e.g. restarts around 15:28, 15:28:43, and 15:30:12. That is expected from RAM-only state and is direct motivation for durable retry semantics.

## Deliberately deferred

There is no evidence requiring a ConPTY rewrite for v1.0. Classic-console architecture should be made correct first. If later live evidence proves an important target is unsupported, report that honestly and move alternative transports to the post-v1 backlog unless it becomes a hard product requirement.
