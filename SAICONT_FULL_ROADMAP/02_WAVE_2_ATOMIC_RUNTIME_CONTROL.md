# WAVE 2 — ATOMIC RUNTIME CONTROL

Goal: make every change to write authorization and rule policy transactional relative to an in-flight poll.

## T-135 [P0] Make WATCH -> non-WATCH revoke input authorization immediately

### Evidence in current WinForms code
`BackgroundPollLoop()` captures:

```csharp
bool allowInput = (systemState == SystemOperationalState.OnWatch);
```

and then calls `engine.PollOnce(allowInput, ...)`.

`SetState(OnDryRun)` updates `systemState` and calls `StartBackgroundPoll()` but does not invalidate `_pollGeneration` or stop/join the already-running WATCH poll. Therefore a poll that captured `allowInput=true` can outlive the operator's switch to DRY-RUN.

### Implement
Define a single authorization-generation rule:
- entering WATCH may authorize future polls only after explicit confirmation;
- every transition **away from WATCH** increments/cancels the active authorization generation before the UI reports DRY-RUN/PAUSED/DISABLED;
- an in-flight poll must re-check the generation at every pre-write stop boundary and refuse input if revoked;
- mode display changes only after revocation is issued.

Do not solve this with arbitrary sleeps.

### Tests
Use a deterministic writer barrier:
1. start WATCH poll and pause it immediately before verified write;
2. switch to DRY-RUN;
3. release barrier;
4. assert zero writes and explicit `send_blocked=authorization_revoked` (or equivalent stable code).

Repeat WATCH -> PAUSED and WATCH -> DISABLED.

---

## T-136 [P0] Remove live mutation of TargetRule objects

### Evidence
`GuiApp.cs::ToggleSelectedRule()` directly changes `currentConfig.Targets[idx].Enabled` while the background worker may be evaluating the same configuration object through `WatcherEngine`.

### Implement
No mutable policy object may be shared between UI control mutation and an executing poll.

Choose one bounded design and keep it small:
- immutable/copy-on-write `WatcherConfiguration` snapshot per engine generation; or
- disable runtime rule toggle while a poll owns the active snapshot, then atomically replace the engine with a new snapshot after cancellation/join.

Requirements:
- never mutate a `TargetRule` instance already visible to an engine;
- rule toggles must be clearly labeled as runtime-only overrides unless persisted intentionally;
- config reload and runtime toggle share one policy-swap path;
- state migration is fingerprint-aware and preserves only compatible durable state.

### Tests
- toggle enabled -> disabled at pre-write barrier: zero write;
- toggle unrelated rule: active rule state retained;
- toggle then reload: deterministic documented result;
- malformed reload leaves old policy fully active, not half-replaced.

---

## T-137 [P1] Introduce one Runtime Control generation contract

Create a small internal controller/contract that owns:
- current mode;
- current policy/config snapshot;
- engine instance;
- policy/authorization generation;
- poll cancellation token/generation;
- atomic swap/reload boundary.

Do not build a framework. The point is to remove duplicated informal synchronization from GUI and TUI.

Minimum operations:
- `RequestMode(...)`;
- `ReloadPolicy(...)`;
- `SetRuleOverride(...)` or equivalent;
- `BeginPollSnapshot(...)` returning immutable mode/policy/generation;
- `IsStillAuthorized(generation)`.

### Invariant
`allowInput=true` is a capability tied to one generation, not a bool that remains trustworthy forever.

---

## T-138 [P1] GUI/TUI parity race suite

Add deterministic tests for both control surfaces:
- WATCH -> DRY-RUN during poll;
- WATCH -> PAUSE during poll;
- WATCH -> config reload during poll;
- WATCH -> disable active rule during poll;
- repeated rapid WATCH/DRY-RUN/PAUSE transitions;
- lifecycle stop while config reload is pending;
- UI/TUI exit while poll is blocked;
- at most one poll worker owns one engine generation;
- no native write after authorization revocation.

Wave 2 gate: write authority is generation-bound and all policy/mode transitions are atomic relative to pre-write execution.
