# WAVE 5 — v1.2 RELEASE GATE

## T-147 [P1] Adversarial control-plane soak

Run a Windows soak that repeatedly exercises:
- normal idle polling;
- active trigger/deadline/retry paths;
- WATCH <-> DRY-RUN <-> PAUSE transitions;
- config reloads;
- runtime rule overrides;
- lifecycle stop/start;
- health snapshot reads;
- log rotation;
- state checkpointing.

Suggested minimum: 2 hours automated churn + one overnight idle run when practical.

Pass criteria:
- zero duplicate writes;
- zero writes after authorization revocation;
- zero orphan workers;
- zero lifecycle lock leaks;
- bounded handles/memory;
- health snapshot stays parseable and freshness-correct;
- no unexplained crash log entries.

---

## T-148 [P1] Portable package script

Add a deterministic source/release packaging script that creates a portable SAICONT ZIP from a clean build.

Include only intentional runtime deliverables:
- executable;
- canonical config;
- launch CMD/PowerShell/VBS scripts needed by users;
- README/OPERATIONS/CHANGELOG/VERSION.

Exclude:
- `.git`;
- `.saipen`;
- `run`;
- `logs`;
- audit roadmaps;
- crash/state files;
- local manifests;
- temporary outputs.

Generate SHA-256 manifest for packaged files.

---

## T-149 [P1] Documentation truth pass

Update documentation to state exactly:
- mode transition semantics;
- runtime-only rule override semantics;
- safe config reload behavior;
- health/status JSON contract;
- stale-health interpretation;
- install transaction locking;
- supported classic-console boundary;
- still no ConPTY/Windows Terminal support unless separately proven/approved.

Remove stale counts/claims discovered during release verification.

---

## T-150 [P0] Version/tag only after all gates

Only after the acceptance matrix is green:
- set `VERSION` to `1.2.0`;
- update README badge;
- write precise CHANGELOG entry;
- clean build;
- all deterministic tests PASS;
- smoke PASS;
- acceptance PASS;
- performance PASS;
- package reproducibility PASS;
- git working tree clean;
- tag `v1.2.0` locally according to the repository's existing release practice.

Do not publish remotely unless explicitly instructed by the operator.
