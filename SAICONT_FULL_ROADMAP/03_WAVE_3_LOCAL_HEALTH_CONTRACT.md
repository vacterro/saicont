# WAVE 3 — LOCAL MACHINE-READABLE HEALTH CONTRACT

Goal: let TERMISAI or another local tool observe SAICONT without scraping GUI strings, console frames, or log prose.

This is deliberately a file/CLI contract. No HTTP server. No telemetry daemon. No database.

## T-139 [P1] Define `SAICONT.health.json` schema v1

Create a tiny versioned schema. Suggested fields:

```text
schema_version
product_version
process_id
process_start_utc
instance_token_hash_or_short_id
mode
policy_revision
config_fingerprint
last_poll_utc
last_successful_poll_utc
last_error_utc
last_error_code
state_store_disposition
state_store_healthy
active_sessions
triggered_sessions
write_eligible_sessions
next_due_utc
last_write_utc
last_write_target
health_updated_utc
```

Privacy/safety rules:
- no console text;
- no prompt contents;
- no command contents;
- no access tokens/credentials;
- no full user environment dump;
- no arbitrary exception stack unless already in the crash log.

`instance_token` should not be copied verbatim into health output; use a short non-authoritative identifier/hash if correlation is needed.

---

## T-140 [P1] Atomic bounded health writer

Write `run\SAICONT.health.json` atomically.

Requirements:
- update on meaningful state transition;
- bounded heartbeat, e.g. no more than once per 30-60 seconds when idle;
- health write failure is **non-authoritative**: report it but do not change safety/retry truth;
- do not rewrite every poll when nothing changed;
- remove/mark stale health on verified clean shutdown;
- on crash, stale file must be detectable by PID/start-time/age.

Use the existing atomic-file primitive rather than ad-hoc temp writes.

### Tests
- unchanged state does not rewrite every poll;
- meaningful transition rewrites immediately;
- torn write fault leaves previous valid snapshot;
- stale snapshot detected after process death;
- Unicode/space install path works.

---

## T-141 [P1] Add stable machine decision codes

`PollResult.Reason` is useful to humans but should not become an external API by accident.

Add a stable bounded `DecisionCode`/string contract, e.g.:

```text
NO_TARGET
READ_FAILED
NO_TRIGGER
NOT_READY
BUSY
DEADLINE_WAIT
BACKOFF_WAIT
ATTEMPT_LIMIT
STATE_STORE_BLOCKED
AUTHORIZATION_REVOKED
PREWRITE_IDENTITY_CHANGED
WRITE_FAILED
WRITE_SENT
RECOVERY_AWAITING_OUTCOME
RECOVERY_CONFIRMED
```

Keep the human reason text separately.

Requirements:
- one code per final decision;
- unknown/new codes fail harmlessly for consumers;
- logs, health snapshot, GUI inspector and TUI can render the same code;
- do not expose internal exception messages as protocol fields.

---

## T-142 [P1] `status.ps1 -Json` and read-only health inspection

Extend status tooling with a machine-readable mode that:
- verifies lifecycle identity using existing logic;
- reads health snapshot if present;
- reports `RUNNING_VERIFIED`, `STOPPED_VERIFIED`, `POSITIVELY_STALE`, or `UNVERIFIABLE` plus health freshness;
- never repairs/mutates unless explicit `-Repair` is supplied;
- returns nonzero on unverifiable/corrupt machine state;
- preserves current human status output by default.

This becomes the supported integration seam for TERMISAI.

Wave 3 gate: another local process can answer "is SAICONT alive, in what mode, healthy, and when is the next due action?" without parsing visual UI or free-form log lines.
