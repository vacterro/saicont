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
