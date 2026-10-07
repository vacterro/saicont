<div align="center">

# SAICONT

**Fail-closed Windows console watcher for resuming terminal AI agents only when failure and ready-input conditions are proven.**

[![Version](https://img.shields.io/badge/version-1.1.1-D4B86A?style=flat-square)](VERSION)
![Platform](https://img.shields.io/badge/platform-Windows-0078D4?style=flat-square)
![Language](https://img.shields.io/badge/C%23-.NET%20Framework-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![Policy](https://img.shields.io/badge/policy-fail%20closed-4A7A20?style=flat-square)

[Build](#build) · [GUI / terminal modes](#verify--interactive-gui-modes) · [Operations](docs/OPERATIONS.md) · [Changelog](CHANGELOG.md)

</div>

SAICONT finds supported terminal agents through their process trees, reads recent console state, and injects `cc` only after its safety conditions are satisfied. It deliberately avoids global keystrokes, clipboard automation, and focus stealing.

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
- `.\bin\SAICONT.exe --terminal` (or `.\SAICONT_TERMINAL.cmd`) — SAICONT TERMINAL: the monitor + dispatcher adapter console (same dark-golden TUI, branded window title).
- `.\bin\SAICONT.exe --self-test` — Deterministic self-test suite including Timeline Simulator and accelerated soak harness.
- `.\bin\SAICONT.exe --validate-config --config .\SAICONT.config.xml` — Read-only preflight configuration validation.
- `.\bin\SAICONT.exe --probe --config .\SAICONT.config.xml` — Read-only live console attachment and rule probe without input injection.
- `.\scripts\smoke.ps1` — Complete automated smoke test suite (PowerShell parser checks, build, self-test, config validation, input harness test, live probe, dry-run multi-poll lifecycle).

See [docs/OPERATIONS.md](docs/OPERATIONS.md) for configuration, hidden start/stop/install commands, log behavior, and recovery steps. See [CHANGELOG.md](CHANGELOG.md) for release history.

<!-- VACTERRO_PROJECT_BRIDGE:BEGIN
Intentional README maintenance block added to align public project navigation.
Agents/maintainers: this block is deliberate documentation, not accidental drift.
Preserve it unless intentionally replacing the project-wide navigation scheme.
-->
---

## Project network

This repository is part of the broader **SAIPEN / vacterro** project ecosystem.

[**Author hub**](https://github.com/vacterro) · [**SAIPEN HQ**](https://github.com/saipenhq) · [**SAIPEN Core**](https://github.com/vacterro/saipen) · [**ZAICODE**](https://github.com/vacterro/zaicode) · [**FastPrompter**](https://github.com/vacterro/FastPrompter) · [**SAIPEN Community**](https://discord.gg/SEYaYkuVgN)

For reproducible bugs and durable feature requests, use [this repository's GitHub Issues](https://github.com/vacterro/saicont/issues). Use Discord for quick discussion, screenshots, and cross-project feedback.

<!-- VACTERRO_PROJECT_BRIDGE:END -->

<!-- VACTERRO_SUPPORT:BEGIN -->
---
<sub>If this project is useful to you, optional support: [Buy Me a Coffee](https://buymeacoffee.com/vacuum34) · [Boosty](https://boosty.to/vacuum34/donate) · [PayPal](https://paypal.me/AlexNelin) · [other ways](https://github.com/vacterro/vacterro/blob/main/SUPPORT.md)</sub>
<!-- VACTERRO_SUPPORT:END -->
