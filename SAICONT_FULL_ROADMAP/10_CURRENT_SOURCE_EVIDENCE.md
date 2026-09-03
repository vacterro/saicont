# CURRENT SOURCE EVIDENCE USED FOR THIS ROADMAP

This file records what was observed in `_SAICONT_31.08.26-T10-33-54.zip`. Re-check against the live tree before implementation.

## Baseline

- `VERSION`: `1.1.1`
- HEAD: `5398f05`
- tag `v1.1.1`: `005df5c`
- HEAD commit message: `docs: fix self-test count 220->265, document roadmap pack as historical (T-129 gap audit)`
- `.saipen/STATE.md`: phase DONE at the time of archive creation.
- repository evidence says 265/265 self-tests PASS on clean C# build; this roadmap authoring environment did not re-run Windows-native binaries.

## Source concentration

Approximate current line counts:
- `src/Program.cs`: 3311
- `src/TerminalUi.cs`: 1207
- `src/GuiApp.cs`: 1054
- `src/RetryPolicy.cs`: 1032
- `src/WatcherEngine.cs`: 909
- `src/StateStore.cs`: 785
- `src/NativeConsole.cs`: 777

## Lifecycle observation

`scripts/common.ps1` defines `Enter-SaiContLifecycleLock`.

Lock callers observed:
- `scripts/start.ps1`: yes
- `scripts/stop.ps1`: yes
- `scripts/status.ps1 -Repair`: yes
- `scripts/install.ps1`: no

## GUI authorization observation

`GuiApp.BackgroundPollLoop()` computes `allowInput` from `systemState` before entering `engine.PollOnce(...)`.

`GuiApp.SetState(OnDryRun)` does not call `StopBackgroundPoll()` and therefore does not increment `_pollGeneration`. A WATCH poll already in flight can keep its originally captured authorization until a different cancellation condition fires.

## GUI rule mutation observation

`GuiApp.ToggleSelectedRule()` performs:

```text
TargetRule r = currentConfig.Targets[idx];
r.Enabled = !r.Enabled;
```

The same configuration graph is owned by `WatcherEngine`. This is a mutable shared runtime policy.

## Silent control-path catches observed

Examples still present in the current source:
- GUI worker lifecycle `BeginInvoke` and `Join` paths;
- TUI worker `Join` path;
- interactive lifecycle pid/instance/stop cleanup;
- mutex release envelopes.

Not every bare catch is a defect. Cosmetic cursor/color restoration is allowed to remain best-effort. The roadmap targets only ownership, shutdown, authorization, and evidence-critical failures.

## Existing strength worth preserving

The current implementation already contains:
- process-session identity checks;
- verified console membership;
- transactional pre-write re-resolution;
- durable state with atomic XML persistence;
- conservative recovery markers;
- bounded retry/backoff;
- explicit recovery state machine;
- regex timeouts and precompiled patterns;
- single-instance resource mutexes;
- lifecycle pid/instance/stop token model;
- GUI/TUI background polling;
- performance budget script;
- clean-stage acceptance script.

The new roadmap intentionally builds on these rather than replacing them.
