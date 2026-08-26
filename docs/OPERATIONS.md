# SAICONT operations

SAICONT runs as a hidden, user-level Windows watcher. It reads only recent classic-console text and sends the configured command only after the matching limit message is recent, the input prompt is empty, the target is idle, and the retry deadline has arrived.

## First run

Run these commands from the project root:

```powershell
.\build.ps1
.\bin\SAICONT.exe
.\bin\SAICONT.exe --self-test
.\bin\SAICONT.exe --validate-config --config .\SAICONT.config.xml
.\bin\SAICONT.exe --probe --config .\SAICONT.config.xml
```

The no-argument command displays the TERMISAI landing screen. `--validate-config` performs safe read-only configuration verification. `--probe` is the safe live check: it reads Cline first, then Codex, and never sends input.

For the full parser, build, self-test, config validation, input injection harness, Cline-first probe, and hidden lifecycle smoke test:

```powershell
.\scripts\smoke.ps1
```

The lifecycle portion uses `--dry-run`, so it cannot inject `cc`.

## Hidden lifecycle

```powershell
.\scripts\start.ps1
.\scripts\status.ps1
.\scripts\stop.ps1
```

`start.ps1` uses `wscript.exe` with window style 0. The executable writes `run\SAICONT.pid`, watches for `run\SAICONT.stop`, and exits cleanly when `stop.ps1` creates that marker. If graceful shutdown exceeds ten seconds, `stop.ps1` verifies the executable path before terminating that exact process.

Use `start.ps1 -DryRun` to exercise the hidden lifecycle without input injection.

## Start at logon

```powershell
.\scripts\install.ps1
```

This builds the current sources, registers the `SAICONT` scheduled task for the current user, and starts the watcher. The task launches through the hidden VBS wrapper and does not require a visible console window.

Remove the task and stop the watcher with:

```powershell
.\scripts\uninstall.ps1
```

Uninstalling preserves `SAICONT.config.xml` and the `logs` directory.

## Configuration

Edit `SAICONT.config.xml` while the watcher is stopped, then start it again. Relative log paths are resolved from the configuration file directory.

Each target controls:

- process names used for process-tree discovery;
- the one-line command to submit;
- trigger, ready-prompt, and busy-state regular expressions;
- scan depth and maximum trigger distance;
- initial and repeated retry delays;
- whether a visible retry time should override the fixed delay.

The shipped configuration evaluates Cline before Codex. It recognizes OpenRouter 429 messages plus Cline free-model limit messages such as `Daily free model limit reached` and parses compact deadlines such as `Try again in 8h 57m`. The default command remains `cc`.

Malformed XML, invalid regular expressions, unsafe interval values, empty process lists, and multi-line commands fail startup instead of falling back to hidden defaults.

## Logs

Operational events are written to `logs\SAICONT.log`. Repeated identical trigger and error states are suppressed for the configured duplicate window. At the configured byte limit, files rotate to `.1`, `.2`, and so on up to `retainedFiles`.

The watcher logs startup, shutdown, limit states, failed console reads, dry-run decisions, and sent commands. Ordinary no-trigger polls produce no log line.

## Minimal terminal commands

SAICONT automates only the configured continuation command. The useful manual SAIPEN set is:

- `cc` — continue the current goal or convergence run;
- `gg <goal>` — start a new explicit goal;
- `ss` — checkpoint and stop;
- `sss` — report status.

For Cline, SAICONT relies only on the empty input prompt and normal Enter submission. `Esc` remains the operator's abort or close-menu key, `Ctrl+C` clears input or exits, and `Ctrl+L` clears the conversation. SAICONT does not change Cline provider, model, theme, plugin, auto-approve, or update settings.

## Failure recovery

- `Configuration error`: stop the watcher, repair the named XML field or regular expression, then run `--probe`.
- `SAICONT is already running`: use `scripts\status.ps1`; do not launch a duplicate watcher.
- `console unavailable`: keep the target in a classic Windows console; redirected or unsupported terminal surfaces cannot be read through the Win32 screen-buffer API.
- no Cline result during smoke: open a Cline console and rerun `--probe`; deterministic self-tests remain valid without a live session.
