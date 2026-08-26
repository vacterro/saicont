# POST-v1 BACKLOG — DO NOT IMPLEMENT DURING THE v1 ROADMAP

This file exists to prevent scope creep. These ideas may be useful later, but none is required to make SAICONT v1.0 trustworthy.

## Candidate v1.x/v2 ideas

### Explicit terminal transport abstraction

Only after v1 classic-console behavior is stable, isolate transport behind a very small interface so alternative terminal surfaces can be evaluated without contaminating rule/retry logic.

### ConPTY / Windows Terminal support

Do this only if evidence proves important target sessions are inaccessible through classic Win32 console attachment and users actually need those surfaces.

Before implementation:

- characterize target terminal architecture;
- determine whether owning process exposes a pseudoconsole handle at all;
- evaluate whether an external wrapper/launcher is required;
- preserve zero-focus and verified-target guarantees;
- build a controlled harness first.

Do not fake ConPTY support by falling back to UIAutomation/global keys.

### More terminal agents/providers

Support additional agents primarily through configuration when their console/prompt/error behavior fits the existing rule model.

Only add code when a genuinely different protocol requires it.

### Configuration hot reload

Potentially useful, but requires atomic snapshot semantics so rules cannot change mid-send transaction. Restart-to-apply is safer and perfectly acceptable for v1.

### Rich status/health export

A local JSON/text health snapshot could help other automation, but avoid HTTP servers or telemetry unless there is a concrete consumer.

### Packaging/signing

Potential future MSI/portable packaging, Authenticode, release automation, GitHub workflows, etc., only after a real repository/release channel exists.

### Advanced policy

Per-provider adaptive retry behavior, richer event parsers, or agent-specific outcome classifiers may be useful after real field evidence. Keep any future automation fixed-command and fail-closed by default.

## Explicitly rejected unless product goals change

- database-backed event history;
- cloud control plane;
- browser automation;
- global keyboard/mouse automation;
- automatic model/provider switching;
- arbitrary prompt generation;
- autonomous shell command execution beyond the configured continuation line;
- killing/restarting user agents as a recovery tactic;
- giant plugin framework.
