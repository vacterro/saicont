# SAICONT

![version](https://img.shields.io/badge/version-1.0.0-darkgoldenrod)

SAICONT is a compact, dependency-free Windows console watcher for safely resuming terminal AI agents without stealing window focus. It finds Cline and Codex through their process trees, reads recent console text, and injects `cc` only when a configured failure is recent, the target input prompt is proven empty and ready, and all retry/backoff deadlines are satisfied.

## Safety & Architecture Properties

- **No Window Activation**: Zero global keystrokes, clipboard access, mouse automation, or foreground window activation.
- **Fail-Closed Transactional Send**: Pre-send re-resolution with 2-stage console membership verification and process start time matching to prevent PID reuse and race conditions.
- **Durable State Ledger**: XML state persistence (`run\SAICONT.state.xml`) preserving active cooldowns, exponential backoff, attempt counts, and stale-trigger suppression across restarts.
- **Explicit Recovery Engine**: 11-state `RecoveryState` machine with bounded exponential backoff and configurable maximum retry intervals.
- **Regex Hardening**: Compiled regex caching with a strict 250ms timeout protection against catastrophic backtracking.
- **Single-Instance Enforcement**: Executable-level named Windows mutex lock and atomic `<saicontInstance>` runtime records with tokenized graceful stop.
- **Clean Configuration**: Validated XML configuration with `--validate-config` preflight mode.

## Build

Run `./build.ps1` from Windows PowerShell or PowerShell 7. The script uses the 64-bit .NET Framework C# compiler included with Windows, writes `bin/SAICONT.exe`, and copies the editable XML configuration beside it.

## Verify & Interactive GUI Modes

- `.\bin\SAICONT.exe --app` (or `.\SAICONT_WIN.cmd` / `.\scripts\gui_win.ps1`) — Launch full-fledged Win95 Dark Golden Desktop GUI window (session table, live log stream, toolbar, deep inspector, system tray).
- `.\bin\SAICONT.exe --gui` (or `.\SAICONT_GUI.cmd` / `.\scripts\gui.ps1`) — Launch interactive Dark Golden Win95 Terminal TUI dashboard.
- `.\bin\SAICONT.exe --self-test` — 212 deterministic self-tests including Timeline Simulator and accelerated soak harness.
- `.\bin\SAICONT.exe --validate-config --config .\SAICONT.config.xml` — Read-only preflight configuration validation.
- `.\bin\SAICONT.exe --probe --config .\SAICONT.config.xml` — Read-only live console attachment and rule probe without input injection.
- `.\scripts\smoke.ps1` — Complete automated smoke test suite (PowerShell parser checks, build, self-test, config validation, input harness test, live probe, dry-run multi-poll lifecycle).

See [docs/OPERATIONS.md](docs/OPERATIONS.md) for configuration, hidden start/stop/install commands, log behavior, and recovery steps. See [CHANGELOG.md](CHANGELOG.md) for release history.
