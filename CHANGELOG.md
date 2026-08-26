# Changelog

## 1.1.0 - 2026-08-27

- **Reliability Fail-Safes**: global `AppDomain.UnhandledException` crash guard appending a last-gasp report to `run\SAICONT.crash.log`, WinForms `ThreadException` reporting instead of silent GUI death, and TUI startup/poll exception guards that keep the terminal adapter alive on poll failures.
- **Graceful Ctrl+C Lifecycle**: native `SetConsoleCtrlHandler` stop path replaces the managed hook whose finalizer raced `FreeConsole` and aborted exit with 0xE0434352; Ctrl+C now sets a stop request honored by the watcher loop, letting the existing finally-chain release pid/instance/stop artifacts cleanly.
- **SAICONT TERMINAL**: new `--terminal` mode and `SAICONT_TERMINAL.cmd` launcher opening the branded monitor/dispatcher adapter window with 220 deterministic self-tests.

## 1.0.0 - 2026-08-26

- **Production Release**: Completed full roadmap from v0.3.1 through v1.0.0 with 127 deterministic self-tests, zero warnings, and clean smoke verification.
- **Fail-Closed Transactional Safety**: Transactional pre-send re-resolution with 2-stage console membership verification and process session identity matching preventing PID reuse and race conditions.
- **Durable State Ledger**: `<saicontState version="1">` atomic XML persistence preserving active backoff, cooldown, retry attempts, and stale-trigger suppression across restarts.
- **Recovery Engine**: Explicit 11-state `RecoveryState` machine with bounded exponential backoff (`backoffMultiplier`, `maxRetryIntervalSeconds`, `maxAttemptsPerEvent`).
- **Rule Engine & Validation**: Compiled regex caching with 250ms timeout protection, `--validate-config` CLI mode, and target uniqueness validation.
- **Lifecycle & Single Instance**: Executable-level named Windows mutex lock, `<saicontInstance>` runtime record, tokenized graceful stop handshake, and automatic stale artifact recovery.
- **Performance & Bounded Memory**: Precomputed rule process name sets, bounded console buffer reads, deduplication cache pruning, and responsive sliced sleep.

## 0.9.5 - 2026-08-26

- Release Candidate audit: secured XML reader settings against DTD processing, added pre-write stop check, and verified clean tree build.

## 0.9.0 - 2026-08-26

- Added precomputed `ProcessNameSet` hash lookups, bounded console reading dimensions (max 512 cols, 2000 rows), state/dedupe cache pruning, and accelerated 1,000-cycle soak simulator.

## 0.8.0 - 2026-08-26

- Added executable-level Windows named mutex for single instance ownership, atomic `<saicontInstance>` runtime record with process start timestamps, and instance-specific tokenized stop protocol.

## 0.7.0 - 2026-08-26

- Added pre-compiled regex caching with 250ms catastrophic backtracking timeout defense, `--validate-config` mode, duplicate target name rejection, and rule fixtures.

## 0.6.0 - 2026-08-26

- Added explicit `RecoveryState` enum state machine, bounded exponential backoff, maximum attempts per event, and deterministic Timeline Simulator.

## 0.5.0 - 2026-08-26

- Added `DurableStateStore` XML persistence (`run\SAICONT.state.xml`), bounded event context deadline parsing, current-tail busy matching, and 24h automatic pruning.

## 0.4.0 - 2026-08-26

- Implemented fail-closed console membership query, 2-stage adaptive `GetConsoleProcessList`, `ProcessSessionIdentity` with process start time, and transactional pre-send re-resolution.

## 0.3.0 - 2026-08-26

- Added validated XML configuration for target rules, retry timing, and logging.
- Added Cline daily free-model limit detection and compact `8h 57m` deadline parsing.
- Added hidden start, stop, status, install, uninstall, and dry-run smoke scripts.
- Added duplicate-suppressed rotating operational logs and PID-based lifecycle control.
- Added the dark-golden TERMISAI terminal landing screen and operator guide.

## 0.2.0 - 2026-08-26

- Added Codex usage-limit reset-time parsing and guarded continuation.
- Added Cline/OpenRouter 429 detection with 60-second retry intervals.
- Added strict empty-prompt, cooldown, stale-trigger, and pre-send race guards.
- Added a hidden-console integration harness proving focus-free `cc` delivery.

## 0.1.0 - 2026-08-26

- Added classic Windows console discovery by process ancestry.
- Added bounded screen-buffer reads and focus-free console input injection.
- Added deterministic self-tests and a read-only live-session probe.
