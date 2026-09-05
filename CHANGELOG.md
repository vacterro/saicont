# Changelog

## 1.2.1 - 2026-09-05

- **External audit SRC-005 (T-163)**: implemented every actionable clause of the `audit/1.md` handoff. Recoverability: corrupt or future-schema durable state now has a reachable atomic reconciliation (commit the fresh ledger first, clear the recovery marker only after that commit, keep the quarantined document), and the OpenCode transport's malformed/restored-ambiguity latch became a resolvable reconciliation generation that clears only when a newer live SSE stream and authoritative REST evidence agree on one idle bound session. Isolation: a Win32 process-snapshot failure now fails console targets closed without disabling independent `opencode-http` targets, and an asynchronous HTTP send is owned by (rule, durable state key, session, operation generation) so two rules sharing an endpoint cannot cross-consume a completion. Observability: one immutable lifecycle-health context (real PID, process start, mode, non-secret instance hash, semantic config fingerprint) is injected into every engine including GUI/TUI replacements, `WriteHealth` is bounded by a semantic fingerprint instead of "a result exists", and `status.ps1 -Json` joins verified lifecycle identity with the health snapshot to emit `RUNNING_VERIFIED` / `STOPPED_VERIFIED` / `POSITIVELY_STALE` / `UNVERIFIABLE`.
- **Settings and lifecycle correctness**: the Settings dialog captures the outgoing target before loading another and rebases its reset baseline on reload, and Apply now validates the whole configuration by parsing the exact bytes it is about to commit through `WatcherConfiguration.Load`. State retirement became transport-aware — an `opencode-http` record's synthetic pseudo-PID can no longer age anti-replay authority out during an endpoint outage — and pruning persists as a first-class durable mutation. One worker-retirement primitive now governs TUI reload, TUI shutdown and GUI shutdown: cancellation first, then a proven join, and a timed-out join is reported as a lifecycle failure instead of disposing the engine anyway. Forced and failed-start cleanup verify dead-owner artifacts against the captured PID/start/token and report `STOPPED_DEGRADED` instead of an unconditional `STOPPED`.
- **Performance**: one bounded authoritative REST snapshot per poll generation (a bound idle poll is three requests; message history is refetched only when SSE evidence advanced), the duplicate pre-send validation is gone while the independent commit-window revalidation stays, HTTP keep-alive is enabled, SSE runtime sessions are capped at 64 with the bound/pending/failed ones protected, session text uses a rolling bounded buffer, the SSE input line is bounded while reading, one console is captured once per poll generation regardless of rule count (pre-send rereads stay fresh), and failure classification runs behind a single linear prefilter. `one_core_cpu_pct` is now derived from `Process.TotalProcessorTime`, and `performance.ps1` fails when any active-path scenario is missing. 640 deterministic self-tests PASS.

## 1.2.0 - 2026-09-01

- **v1.2 control and operations**: atomic local health JSON, bounded decision codes, read-only JSON status, generation-safe GUI/TUI mode transitions, transactional logon-task installation, deterministic allowlisted packaging, and explicit classic-console support boundaries.
- **Failure Intelligence**: classify 21 provider/transport failure families from HTTP status, structured code/type, message signatures, and transport exceptions. Automatic continuation is limited to transient failures or explicit reset deadlines; permanent failures stop, volatile IDs normalize into one durable event, and existing exponential backoff plus per-event attempt caps prevent loops.

## 1.1.1 - 2026-08-31

- **Audit ALL_3 wave 3**: ship the static-complete implementation of the remaining findings from the AUDIT_ALL_3 audit handoff. W2-001 TerminalUi worker join, W2-005 nonzero on handler failure, W2-006 stopfile cleanup in finally, PERF-001 ManualResetEventSlim interruptible wait, PERF-004 rule-by-name dict + per-state dirty flag + bounded checkpoint, PERF-005 perf script one-core CPU + median/p95/max + active scenario, plus the prior wave's audit-defect fixes (T-26..T-35, T-59..T-70, T-97..T-114). 265/265 self-tests PASS on a clean csc build. Live Windows-handoff verification (smoke.ps1, acceptance.ps1, W2-005 handler-failure integration) remains an operator task.
- **Resource identity**: per-resource SHA-256 mutex lease over the canonical physical runtime paths (volume+file-index for existing files, canonical final path otherwise) with sorted acquisition to avoid cross-config deadlock and pairwise collision rejection before any delete/write/quarantine/rotation.

## 1.1.0 - 2026-08-27

- **Reliability Fail-Safes**: global `AppDomain.UnhandledException` crash guard appending a last-gasp report to `run\SAICONT.crash.log`, WinForms `ThreadException` reporting instead of silent GUI death, and TUI startup/poll exception guards that keep the terminal adapter alive on poll failures.
- **Graceful Ctrl+C Lifecycle**: native `SetConsoleCtrlHandler` stop path replaces the managed hook whose finalizer raced `FreeConsole` and aborted exit with 0xE0434352; Ctrl+C now sets a stop request honored by the watcher loop, letting the existing finally-chain release pid/instance/stop artifacts cleanly.
- **SAICONT TERMINAL**: new `--terminal` mode and `SAICONT_TERMINAL.cmd` launcher opening the branded monitor/dispatcher adapter window with 265 deterministic self-tests.

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
