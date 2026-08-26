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
