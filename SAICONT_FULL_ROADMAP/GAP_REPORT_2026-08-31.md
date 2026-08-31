# SAICONT roadmap gap report (2026-08-31)

Repo state at gap-audit: HEAD = 005df5c (v1.1.1), self-test 265/265 PASS on
clean csc build, --validate-config exit 0, --probe SKIP (no live target).

The roadmap pack in `SAICONT_FULL_ROADMAP/` describes the path from a
v0.3.1 baseline to a v1.0 release; the current tree is at v1.1.1 with
T-25..T-35, T-59..T-70, T-97..T-114, T-119 audit defects implemented and
self-tested. The full-ROADMAP wave files (01..08) are therefore
historically superseded by the implementation tickets; only residual
gaps remain.

## Roadmap invariant status (matrix from 10_FINAL_ACCEPTANCE_MATRIX.md)

| # | Area | Status | Evidence |
|---|------|--------|----------|
| 1 | Unknown membership never authorizes write | PASS | 265/265 self-test includes empty/null console list fails-closed |
| 2 | Membership API no safety-relevant truncation | PASS | self-test "membership API refuses safety-relevant truncation" |
| 3 | Process identity detects PID reuse/session change | PASS | "PID reuse prevented send", "process session identity matching" |
| 4 | Write uses freshly resolved same console | PASS | "console change prevented send", "changed event at safety reread" |
| 5 | Membership/identity checked before input | PASS | "verified write rejects ..." suite |
| 6 | Only recognized empty current prompt can send | PASS | "current Cline empty prompt", "typed prompt blocks injection" |
| 7 | Historical busy cannot represent current busy | PASS | "historical busy outside tail ignored" |
| 8 | Newest trigger owns its retry context | PASS | "multi-event picks latest trigger" + CORE-005 (T-26) |
| 9 | Old deadline cannot cross-bind to new event | PASS | T-26 stable event identity |
| 10 | Event identity stable; new occurrence distinguishable | PASS | T-26 byte-identical later occurrence + fresh lifecycle test |
| 11 | Restart preserves cooldown | PASS | "restored state preserves active cooldown on restart" |
| 12 | Restart cannot resurrect stale trigger | PASS | "restart preserves stale-event suppression" |
| 13 | New process session does not inherit unsafe state | PASS | W2-005 (T-101) schema-fingerprint binding |
| 14 | State file atomic, versioned, corruption-safe, bounded | PASS | "atomic state save leaves no temp artifact" + CORE-007 (T-32) |
| 15 | Probe honest PASS/SKIP/FAIL | PASS | "probe with zero matches is SKIP", "FAIL_ALL", "FAIL_MIXED" |
| 16 | Dry-run no production retry contamination | PASS | hidden dry-run lifecycle tested |
| 17 | Successful write != automatic recovery | PASS | "successful write persisted awaiting-outcome" + CORE-004 (T-29) |
| 18 | Repeated failure bounded/no-spam | PASS | "repeated native write failures are bounded" |
| 19 | Unreadable/unknown paths fail closed | PASS | "unwritable durable state prevents input" |
| 20 | Stop before write-critical transaction can prevent input | PASS | "pre-send stop request aborted write execution" |
| 21 | Duplicate continuous watcher prevented | PASS | "first mutex acquisition succeeds as new", "second detects existing" |
| 22 | Stale/reused PID cannot control wrong process | PASS | T-100 (W2-004) + T-112 (CORE-001) per-resource identity |
| 23 | Stale stop request cannot kill new instance | PASS | T-100 + T-126 (install lifecycle lock) |
| 24 | Current-user task install/update/start verified | DEFERRED | operator handoff required; smoke.ps1 + acceptance.ps1 exist |
| 25 | Uninstall removes intended task, preserves data | DEFERRED | operator handoff required |
| 26 | Regex validated once/reused; pathological bounded | PASS | "pathological regex fails closed (not triggered/marked busy/proves timeout path executed)" |
| 27 | Config one source of truth; actionable failures | PASS | --validate-config exits 0 on valid; rejects unknown attribute / unknown element / multiline / oversized / duplicate / malformed regex / invalid distance |
| 28 | XML parser DTD safe | PASS | "security: XML DTD processing prohibited" |
| 29 | Session/log/cache bounded | PASS | "durable state record count is hard bounded", "rotating log keeps active file + backup", "deduplication map hard entry bound" |
| 30 | No per-poll rewrite without meaningful change | PASS | "unchanged durable state does not rewrite file every poll" |
| 31 | No handle leak | PARTIAL | "locked log failure is explicit and non-throwing"; full native stress deferred |
| 32 | Watcher low overhead | PASS | "PERF-010: average idle poll < 50ms (actual=0,00ms)" |
| 33 | Rotation/dedupe bounded; critical errors visible | PASS | rotating log + dedup pruning tests |
| 34 | Negative tests prove dangerous setup | PASS | multiple "negative entered write-eligible transaction" |
| 35 | Clean build warnings-as-errors | PASS | build.ps1 /warnaserror+ green; 0 errors 0 warnings |
| 36 | Docs truthful | PARTIAL | README/OPERATIONS updated through T-83..T-96; CHANGELOG head still says "220" (T-89 tracks) |
| 37 | SAIPEN state/board/log consistent | PARTIAL | 12 remaining closure-evidence FAILs + 1 mechanical provenance FAIL all on historical entries; new entries protocol-clean |
| 38 | No unresolved P0-P3 | PARTIAL | T-30/T-120 BLOCKED with operator handoff gate |
| 39 | Version 1.0.0 | SUPERSEDED | v1.1.1 is the live release; the roadmap's "1.0.0" baseline is past |

## Concrete remaining gaps

1. CHANGELOG.md head still says "220 deterministic self-tests" (T-89 tracks).
   Actual: 265. CHANGELOG already updated in 1.1.1 entry but the 1.1.0 entry
   line "SAICONT TERMINAL ... 220 deterministic self-tests" is now stale.
   Resolution: edit CHANGELOG.md 1.1.0 line to "265 deterministic self-tests".

2. SAICONT_FULL_ROADMAP/ is in the v1.1.1 branch but its 09_POST_V1_BACKLOG
   file declares scope that was explicitly deferred. Per T-90: either delete
   the directory or add a one-line pointer in docs/OPERATIONS.md AND sign
   SHA256SUMS.txt. The current state keeps the directory without disclaimer.
   Resolution: add a 1-line "Roadmap pack: historical, superseded by
   v1.1.0/v1.1.1; see CHANGELOG.md" header note in 00_READ_FIRST_MASTER_BATCH.md
   and add a pointer in docs/OPERATIONS.md.

3. smoke.ps1 / acceptance.ps1 require Windows handoff (T-120 BLOCKED gate).

4. 12 SAIPEN validator FAILs are all HISTORICAL closure-evidence
   (T-31..T-35, T-93..T-96, T-115, T-119) recorded under the pre-strict
   grammar; the validator explicitly classifies them as legacy debt. New
   T-127/T-128 entries are protocol-clean (conf: high, MANUAL-VERIFY, op_id,
   transition chain).

## Conclusion

Roadmap v0.3.1->v1.0 is functionally complete on the current tree
(265/265 self-test, --validate-config, --probe SKIP honest). Items
1-2 above are documentation/hygiene cleanups (2-3 line edits each).
Item 3 is operator-gated and not closeable in this Windows host session
without pwsh smoke/acceptance reproduction. Item 4 is pre-boundary
historical debt and is recorded as such by the validator itself; not
fabricating new evidence is the protocol-correct response.
