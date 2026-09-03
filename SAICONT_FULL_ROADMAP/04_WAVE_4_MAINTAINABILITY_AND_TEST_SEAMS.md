# WAVE 4 — MAINTAINABILITY WITHOUT BEHAVIORAL REWRITE

Goal: reduce the cost/risk of the next audits. Current source is healthy but concentrated: `Program.cs` is ~3311 lines and the project keeps deterministic tests inside the production entry-point file.

Do this only after Waves 1-3 are green.

## T-143 [P2] Extract self-test harness from Program.cs

Move deterministic self-test code and fixtures into dedicated source files, for example:

```text
src/SelfTests.cs
src/SelfTestFixtures.cs
```

Requirements:
- `--self-test` CLI remains byte-for-byte compatible in meaning;
- same or higher test count;
- same exit semantics;
- no new test framework/dependency;
- production paths do not depend on test-only mutable globals.

This is a mechanical extraction, not a redesign.

---

## T-144 [P2] Extract runtime options/path finalization

Move CLI parsing and canonical runtime path derivation out of the giant entry point into a focused unit such as `RuntimeOptions.cs`.

Preserve:
- all existing aliases;
- explicit `--config` precedence;
- repo-root vs standalone config rules;
- lifecycle-owning mode path finalization;
- read-only modes staying non-mutating.

Add table-driven tests for every documented launcher/mode combination.

---

## T-145 [P2] Add deterministic fault/race injection seams

Generalize only the seams needed by the new tests:
- lifecycle file delete/read/write;
- worker join/wakeup;
- health writer;
- pre-write barrier;
- policy generation cancellation.

No DI container. No mocking framework. Use delegates/interfaces in the same lightweight style already used by `WatcherEngine`.

---

## T-146 [P2] Source-size and dependency guard

Add a simple release check that confirms:
- no external runtime package/dependency has appeared;
- no HTTP listener/server;
- no database files/providers;
- no UI automation/global key path;
- no provider/model switching code;
- no arbitrary shell execution beyond the configured continuation line.

This is a product-boundary regression gate, not security theater.

Wave 4 gate: code is easier to audit, but product behavior remains the same except for the intentional 1.2 control/health features.
