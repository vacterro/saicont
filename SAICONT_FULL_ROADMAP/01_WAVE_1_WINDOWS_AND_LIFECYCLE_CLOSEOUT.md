# WAVE 1 — WINDOWS PROOF + LIFECYCLE SERIALIZATION

Goal: close the remaining release-trust holes before adding new capability.

## T-131 [P0] Establish a real Windows 1.1.1+ baseline

### Problem
The repository records `265/265 PASS`, but the SAIPEN board still carries operator-gated Windows acceptance items (`T-120`, `T-117`, `T-30`). A new feature wave must not stack on top of an unclosed native-runtime proof gap.

### Implement
No product change unless the gate finds a failure.

Run from the current Windows 10 x64 project root:

```powershell
.\build.ps1
.\bin\SAICONT.exe --self-test
.\bin\SAICONT.exe --validate-config --config .\SAICONT.config.xml
.\scripts\smoke.ps1
.\scripts\acceptance.ps1
.\scripts\performance.ps1
```

Also execute the existing T-30 launcher/config-root manual matrix for `--app`, `--gui`, and `--terminal` through direct EXE, CMD, and PowerShell launch paths.

### Evidence
Record exact exit codes, self-test count, performance `MEASURE:` lines, and smoke/acceptance final status. Do not paraphrase a PASS that was not actually produced.

### Done when
- clean build;
- 265/265 or higher self-test count, all PASS;
- config validation exit 0;
- smoke PASS;
- acceptance PASS;
- performance budget PASS;
- documented launch paths all consume the canonical root config and create/clean correct lifecycle records.

---

## T-132 [P0] Serialize install as one lifecycle transaction

### Evidence in current tree
`scripts/common.ps1` defines `Enter-SaiContLifecycleLock`. `start.ps1` and `stop.ps1` use it. `scripts/install.ps1` does not acquire it around its read/stop/register/start/status/rollback sequence.

### Risk
Concurrent `install`, `start`, `stop`, or repair can interleave the install read-modify-write transaction. Task state, runtime state, and rollback assumptions can diverge.

### Implement
Make installation one serialized lifecycle transaction.

Requirements:
- one ownership boundary covers: previous-state capture -> stop old runtime -> task replace -> start new runtime -> final verified status -> rollback if needed;
- avoid accidental deadlock from nested `start.ps1`/`stop.ps1` lock acquisition;
- do not weaken standalone start/stop locking;
- preserve rollback semantics from the current implementation;
- lock release must occur in `finally` even on parser/build/task/start/status failure.

Preferred shape: move lock-aware lifecycle primitives into `common.ps1`, with wrapper scripts acquiring the lock once. An internal "already-held" path is acceptable only if it cannot silently bypass locking for ordinary callers.

### Tests
- two simultaneous `install.ps1` calls serialize;
- `install.ps1` racing `stop.ps1` serializes;
- `install.ps1` racing `start.ps1` serializes;
- injected failure at task registration restores previous task/runtime state;
- no lock leak after failure;
- timeout produces explicit nonzero failure.

### Done when
No lifecycle mutation can observe or create a half-installed state because another lifecycle operation interleaved.

---

## T-133 [P1] Stop swallowing safety-relevant teardown failures

### Evidence in current tree
Production bare catches remain around control/lifecycle paths, including:
- `GuiApp.cs`: worker/BeginInvoke shutdown paths;
- `TerminalUi.cs`: worker join;
- `Program.cs`: interactive pid/instance/stop cleanup and mutex release envelope.

Cosmetic console restoration catches are not the target.

### Implement
Create one minimal diagnostic path for teardown/control failures:
- write a bounded WARN/ERROR to the canonical crash/operational diagnostic sink when available;
- fall back to `Console.Error` only when normal logging is unavailable;
- never throw a second exception that hides the primary failure;
- distinguish cosmetic cleanup from ownership/safety cleanup.

### Tests
Fault-inject:
- worker join failure/timeout;
- stop-file read/delete failure;
- instance/pid cleanup failure;
- UI BeginInvoke failure during lifecycle stop.

Verify each safety-relevant failure is observable and does not become a silent success.

---

## T-134 [P1] Close stale blocked-ticket accounting after proof

After T-131..T-133:
- update only the SAIPEN tickets whose gates were genuinely reproduced;
- close duplicate/umbrella historical tickets by explicit disposition rather than pretending old wording still describes current code;
- keep historical evidence intact;
- do not rewrite old logs to make validation prettier.

Wave 1 gate: native Windows baseline trusted + install transaction serialized + no silent control-path failures.
