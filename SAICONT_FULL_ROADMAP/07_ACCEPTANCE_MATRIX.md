# v1.2 ACCEPTANCE MATRIX

| # | Invariant | Gate |
|---|---|---|
| 1 | Current Windows baseline is genuinely reproducible | build + self-test + validate + smoke + acceptance + performance |
| 2 | Install cannot interleave lifecycle mutation | concurrent install/start/stop tests |
| 3 | WATCH authorization can be revoked mid-poll | pre-write barrier race tests |
| 4 | WATCH -> DRY-RUN produces zero post-transition writes | deterministic GUI race test |
| 5 | Rule enable/disable never mutates a policy used by an active poll | immutable/copy-on-write policy test |
| 6 | Bad config reload leaves old policy intact | malformed reload test |
| 7 | Lifecycle stop cancels in-flight poll before mutex release | GUI/TUI/terminal stop tests |
| 8 | Safety-relevant cleanup failures are observable | fault-injection diagnostics |
| 9 | Health output never contains console/prompt/command secrets | schema/content tests |
| 10 | Health writes are atomic | torn-write fault test |
| 11 | Idle health output is bounded | write-count/heartbeat test |
| 12 | Stale health is detectable | process-death/start-time test |
| 13 | Machine decision codes are stable and bounded | decision-code test table |
| 14 | `status.ps1 -Json` is read-only by default | before/after filesystem hash test |
| 15 | Human status output remains compatible | script regression test |
| 16 | Self-test extraction changes no behavior | before/after count and output semantics |
| 17 | Runtime option extraction preserves every documented launcher | table-driven launcher tests |
| 18 | No new external runtime dependency | source/package gate |
| 19 | No HTTP/db/browser/global-key/model-switch expansion | boundary guard |
| 20 | No duplicate write under churn/soak | adversarial soak |
| 21 | Memory/handles/CPU remain bounded | performance.ps1 + soak |
| 22 | Portable package contains no runtime/audit/private state | package manifest test |
| 23 | Docs match actual behavior and counts | release truth pass |
| 24 | v1.2.0 is tagged only after all previous gates | final release gate |

## v1.3 external-audit invariants (SRC-005 / T-163)

| # | Invariant | Gate |
|---|---|---|
| 25 | Corrupt/unsupported durable state has a reachable, atomic, fail-closed reconciliation | CORE-001 engine + store tests (commit before marker clear; failed commit keeps the marker) |
| 26 | OpenCode malformed/restored ambiguity is resolvable only by authoritative reconciliation | CORE-002 transport tests (reconnect alone never clears) |
| 27 | Process-discovery failure is scoped to transports that need it | CORE-003 mixed console+HTTP poll test |
| 28 | Asynchronous HTTP completion has exact rule/state ownership | CORE-004 sibling-rule consumption test |
| 29 | Every lifecycle mode publishes health bound to real PID/start/mode/config | CORE-005 health identity + semantic fingerprint tests |
| 30 | `status.ps1 -Json` joins lifecycle and health into verified classifications | CORE-006 status contract test in smoke |
| 31 | Unchanged health obeys the 45-second bound | CORE-007 write-count test at poll cadence |
| 32 | Settings preserves every target draft across selection/reload/reset | W2L1-001 draft-ownership tests |
| 33 | Every successful Settings save round-trips through the authoritative loader | W2L1-002 loader-backed Apply gate tests |
| 34 | HTTP anti-replay state is retired only by transport-correct liveness, and pruning persists | W2L1-003 pruning test at >=100 states |
| 35 | GUI/TUI cannot dispose/swap/release lifecycle ownership before workers are proven dead | W2L1-004 worker-retirement tests (timeout is a failure) |
| 36 | Forced/failed-start cleanup removes matching dead-owner artifacts or reports degraded | W2L1-005 dead-owner cleanup + `STOPPED_DEGRADED` |
| 37 | OpenCode request amplification is bounded without weakening commit safety | PERF-001 request-count budget in performance.ps1 |
| 38 | SSE session/text/frame memory is explicitly bounded | PERF-002 churn + rolling-buffer tests |
| 39 | One console is captured once per poll generation regardless of rule count | PERF-003 observation-count test; pre-send reread stays fresh |
| 40 | Failure classification avoids unconditional full-snapshot work without changing precedence | PERF-004 gate-domination + golden-corpus tests |
| 41 | The performance gate measures active production paths with valid metrics | PERF-005 mandatory scenarios; CPU from `TotalProcessorTime` |

## Absolute release blockers

Any of the following blocks v1.2.0:
- a write after WATCH authorization has been revoked;
- a duplicate write caused by policy/lifecycle race;
- install/start/stop lifecycle interleaving;
- corrupt/torn authoritative state accepted as safe;
- stale lifecycle identity accepted as current;
- self-tests crashing rather than cleanly failing;
- health/status feature becoming authoritative for send safety;
- unverified Windows-native acceptance.
