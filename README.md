# SAICONT

![version](https://img.shields.io/badge/version-1.2.2-darkgoldenrod)

SAICONT is a compact, dependency-free Windows watcher for safely resuming terminal AI agents without stealing window focus. Codex and Cline use the verified native console transport. OpenCode/SAIPAL uses the OpenCode HTTP/SSE transport and never relies on a readable ConPTY screen buffer.

## Safety & Architecture Properties

- **No Window Activation**: Zero global keystrokes, clipboard access, mouse automation, or foreground window activation.
- **Fail-Closed Transactional Send**: Pre-send re-resolution with 2-stage console membership verification and process start time matching to prevent PID reuse and race conditions.
- **Durable State Ledger**: XML state persistence (`run\SAICONT.state.xml`) preserving active cooldowns, exponential backoff, attempt counts, and stale-trigger suppression across restarts.
- **Explicit Recovery Engine**: 11-state `RecoveryState` machine with bounded exponential backoff and configurable maximum retry intervals.
- **Failure Intelligence**: 21 normalized provider-failure classes select deterministic retry, reset-wait, or stop actions; volatile request IDs and timestamps cannot create fresh retry events, and provider/model selection remains outside SAICONT.
- **Regex Hardening**: Compiled regex caching with a strict 250ms timeout protection against catastrophic backtracking.
- **Single-Instance Enforcement**: Executable-level named Windows mutex lock and atomic `<saicontInstance>` runtime records with tokenized graceful stop.
- **Clean Configuration**: Validated XML configuration with `--validate-config` preflight mode.
- **Local Health Contract**: Atomic `run\SAICONT.health.json` snapshots and read-only `scripts\status.ps1 -Json` output expose bounded machine state without console or prompt contents.
- **Transactional Lifecycle**: Start, stop, install, task replacement, and rollback share one lifecycle lock; an enabled current-user logon task starts the hidden watcher when the interactive session begins.
- **OpenCode Safety Boundary**: `transport="opencode-http"` requires an explicit `endpoint` and `projectDir`, binds to the exact session observed on the project-scoped SSE stream, reconciles `/session/status` and `/session/:id/message`, and fails closed on ambiguity or disagreement.
- **No Blind TUI Injection**: OpenCode continuation uses `POST /session/:id/message` with the observed agent/model context in a cancellable background operation. `/tui/append-prompt`, `/tui/submit-prompt`, clipboard, focus, and keyboard injection are not used.

## Build

Run `./build.ps1` from Windows PowerShell or PowerShell 7. The script uses the 64-bit .NET Framework C# compiler included with Windows, writes `bin/SAICONT.exe`, and copies the editable XML configuration beside it.

## Verify & Interactive GUI Modes

- `.\bin\SAICONT.exe --app` (or `.\SAICONT_WIN.cmd` / `.\scripts\gui_win.ps1`) — Launch full-fledged Win95 Dark Golden Desktop GUI window (session table, live log stream, toolbar, deep inspector, system tray).
- `.\bin\SAICONT.exe --gui` (or `.\SAICONT_GUI.cmd` / `.\scripts\gui.ps1`) — Launch interactive Dark Golden Win95 Terminal TUI dashboard.
- `.\bin\SAICONT.exe --terminal` (or `.\SAICONT_TERMINAL.cmd`) — SAICONT TERMINAL: the monitor + dispatcher adapter console (same dark-golden TUI, branded window title).
- `.\bin\SAICONT.exe --self-test` — Deterministic self-test suite (640 checks) including the Timeline Simulator, accelerated soak harness, and the external-audit regressions.
- `.\bin\SAICONT.exe --validate-config --config .\SAICONT.config.xml` — Read-only preflight configuration validation.
- `.\bin\SAICONT.exe --probe --config .\SAICONT.config.xml` — Read-only live console attachment and rule probe without input injection.
- `.\scripts\smoke.ps1` — Complete automated smoke test suite (PowerShell parser checks, build, self-test, config validation, input harness test, live probe, dry-run multi-poll lifecycle).
- `.\scripts\package.ps1 -Rebuild` — Create the allowlisted portable ZIP and its internal SHA-256 manifest; runtime, logs, audit state, and private repository data are excluded.

See [docs/OPERATIONS.md](docs/OPERATIONS.md) for configuration, hidden start/stop/install commands, log behavior, and recovery steps. See [CHANGELOG.md](CHANGELOG.md) for release history.

## OpenCode / SAIPAL target

Pin the exact OpenCode project. A target without `projectDir` is rejected when
enabled; old targets without `transport` continue to mean `transport="console"`.

```xml
<target name="saipal" enabled="true" transport="opencode-http"
        endpoint="http://127.0.0.1:41961"
        projectDir="V:\___VAC\__K\__CODE\_AI_STUFF_AGENTIC\_SAIPAL"
        command="cc" initialDelaySeconds="60" retryIntervalSeconds="60"
        parseRetryTime="false">
  <triggerPatterns>
    <pattern>(?i)"code"\s*:\s*"internal_error"</pattern>
  </triggerPatterns>
</target>
```

Start the real TUI with `opencode --hostname 127.0.0.1 --port 41961 .` from
the pinned project directory. Proof payloads for the installed OpenCode
version are in [docs/proof](docs/proof).
