using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace SaiCont
{
    internal static partial class Program
    {
        private static int _selfTestCount;
        private static string _crashReportPathOverride;
        private static volatile bool cancelRequested;

        /// <summary>True once the operator pressed Ctrl+C; loops treat this as a graceful stop request.</summary>
        internal static bool CancelRequested
        {
            get { return cancelRequested; }
            set { cancelRequested = value; }
        }

        internal static void ResetInterruptForTests()
        {
            cancelRequested = false;
        }
        private static int Main(string[] args)
        {
            InstallCrashGuard();
            if (args.Length == 0 || (args.Length == 1 && (args[0] == "--help" || args[0] == "-h")))
            {
                TerminalUi.PrintLandingPage();
                return 0;
            }

            if (args.Length == 2 && args[0] == "--input-harness")
            {
                try
                {
                    while (Console.KeyAvailable) { Console.ReadKey(true); }
                }
                catch { }
                File.WriteAllText(args[1] + ".ready", "READY");
                string line = Console.ReadLine();
                System.Threading.Thread.Sleep(250);
                bool duplicateInput = false;
                try { duplicateInput = Console.KeyAvailable; } catch { }
                File.WriteAllText(args[1], duplicateInput ? "DUPLICATE_INPUT" : (line ?? String.Empty));
                return duplicateInput ? 1 : 0;
            }

            if (args.Length == 6 && args[0] == "--verified-harness-inject")
            {
                return RunVerifiedHarnessInjection(args);
            }

            if (args.Length == 1 && args[0] == "--self-test")
            {
                return RunSelfTests();
            }

            RuntimeOptions options;
            string optionError;
            if (!TryParseOptions(args, out options, out optionError))
            {
                Console.Error.WriteLine(optionError);
                Console.Error.WriteLine("Use --gui, --watch, --dry-run, --once, --probe, --validate-config, or --self-test; optional: --config PATH --pid-file PATH --stop-file PATH --state-file PATH --instance-file PATH.");
                return 2;
            }

            if (options.Mode == "--validate-state")
            {
                return RunValidateState(options.StateFilePath);
            }

            WatcherConfiguration configuration;
            try
            {
                configuration = WatcherConfiguration.Load(options.ConfigurationPath);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Configuration error: " + exception.Message);
                return 2;
            }

            if (IsLifecycleMode(options.Mode))
            {
                try
                {
                    options.ConfigurationPath = CanonicalizePhysicalPath(options.ConfigurationPath);
                    configuration = WatcherConfiguration.Load(options.ConfigurationPath);
                    PrepareRuntimeResources(options, configuration);
                    string runtimeDirectory = Path.GetDirectoryName(options.StateFilePath);
                    _crashReportPathOverride = Path.Combine(runtimeDirectory, "SAICONT.crash.log");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("Runtime path error: " + exception.Message);
                    return 2;
                }
            }

            if (options.Mode == "--app" || options.Mode == "--win-gui")
            {
                return RunInteractiveWithLifecycle(configuration, options, (stateStore, shouldStop, health) =>
                    SaiContGuiForm.RunDesktopGui(configuration, options.ConfigurationPath, options.Mode, stateStore, shouldStop, health));
            }

            if (options.Mode == "--terminal")
            {
                // SAICONT TERMINAL: one console window that monitors the discovered
                // agent sessions and dispatches guarded continuation on demand.
                return RunInteractiveWithLifecycle(configuration, options, (stateStore, shouldStop, health) =>
                    TerminalUi.RunInteractiveTui(configuration, options.ConfigurationPath, options.Mode, stateStore, shouldStop, health));
            }

            if (options.Mode == "--gui")
            {
                return RunInteractiveWithLifecycle(configuration, options, (stateStore, shouldStop, health) =>
                    TerminalUi.RunInteractiveTui(configuration, options.ConfigurationPath, options.Mode, stateStore, shouldStop, health));
            }

            if (options.Mode == "--validate-config")
            {
                return RunValidateConfig(configuration, options.ConfigurationPath);
            }

            if (options.Mode == "--probe")
            {
                return RunProbe(configuration);
            }

            if (options.Mode == "--once")
            {
                return PrintPollResults(new WatcherEngine(configuration).PollOnce(false));
            }

            return RunContinuous(configuration, options, options.Mode == "--watch");
        }

        private static InstanceMutexLease AcquireInstanceMutex(string[] resourcePaths, out bool createdNew)
        {
            if (resourcePaths == null || resourcePaths.Length == 0)
            {
                throw new ArgumentException("Runtime resource paths are required.", "resourcePaths");
            }

            var mutexes = new List<System.Threading.Mutex>();
            try
            {
                string[] orderedPaths = resourcePaths
                    .Where(path => !String.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (orderedPaths.Length != resourcePaths.Length)
                {
                    throw new InvalidOperationException("Runtime resource paths must be canonical and pairwise disjoint.");
                }

                foreach (string resourcePath in orderedPaths)
                {
                    string digest;
                    using (SHA256 sha256 = SHA256.Create())
                    {
                        digest = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(resourcePath))).Replace("-", String.Empty);
                    }

                    bool resourceCreated;
                    System.Threading.Mutex mutex;
                    mutex = new System.Threading.Mutex(true, @"Global\SAICONT_" + digest, out resourceCreated);

                    mutexes.Add(mutex);
                    if (!resourceCreated)
                    {
                        createdNew = false;
                        for (int index = mutexes.Count - 1; index >= 0; index--)
                        {
                            try { mutexes[index].ReleaseMutex(); } catch { }
                            mutexes[index].Close();
                        }
                        return new InstanceMutexLease(new List<System.Threading.Mutex>());
                    }
                }

                createdNew = true;
                return new InstanceMutexLease(mutexes);
            }
            catch
            {
                for (int index = mutexes.Count - 1; index >= 0; index--)
                {
                    try { mutexes[index].ReleaseMutex(); } catch { }
                    mutexes[index].Close();
                }
                throw;
            }
        }

        private static InstanceMutexLease AcquireInstanceMutex(string configPath, out bool createdNew)
        {
            return AcquireInstanceMutex(new[] { CanonicalizePhysicalPath(configPath) }, out createdNew);
        }

        private static void InstallCrashGuard()
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs eventArgs)
            {
                Exception exception = eventArgs.ExceptionObject as Exception;
                string details = exception != null
                    ? exception.ToString()
                    : Convert.ToString(eventArgs.ExceptionObject, System.Globalization.CultureInfo.InvariantCulture);
                TryWriteCrashReport("Unhandled appdomain exception", details, eventArgs.IsTerminating);
            };
        }

        internal static void TryReportTeardownFailure(string context, Exception exception)
        {
            // T-133: safety-relevant teardown/control failures must never become
            // silent successes. Write a bounded WARN line to the same canonical
            // crash/operational diagnostic sink used for fatal reports, falling
            // back to Console.Error when the sink is unavailable. Never throw a
            // second exception that hides the primary failure.
            if (exception == null) { return; }
            TryWriteCrashReport(
                "TEARDOWN WARN: " + (context ?? "unknown") + ": " + exception.GetType().Name + ": " + exception.Message,
                exception.ToString(),
                false);
        }

        internal static void TryWriteCrashReport(string headline, string details, bool terminating)
        {
            try { Console.Error.WriteLine("SAICONT FATAL: " + headline); } catch { }
            try
            {
                if (!String.IsNullOrEmpty(details))
                {
                    Console.Error.WriteLine(details);
                }
            }
            catch { }
            try
            {
                string path = _crashReportPathOverride;
                if (String.IsNullOrEmpty(path))
                {
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "run", "SAICONT.crash.log");
                }
                string directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                string stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
                File.AppendAllText(
                    path,
                    stamp + " terminating=" + (terminating ? "true" : "false") + " " + headline + Environment.NewLine +
                    (details ?? String.Empty) + Environment.NewLine);
            }
            catch
            {
                // Last-gasp reporting must never throw: the failing path is already fatal.
            }
        }

        private static bool TryWriteInstanceFile(string path, int pid, DateTime startUtc, string mode, string exePath, string instanceToken, out string error)
        {
            error = null;
            if (String.IsNullOrEmpty(path))
            {
                error = "instance path is empty";
                return false;
            }

            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(
                    "saicontInstance",
                    new XAttribute("version", "1"),
                    new XElement("pid", pid),
                    new XElement("processStartUtc", startUtc.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                    new XElement("mode", mode ?? String.Empty),
                    new XElement("executablePath", exePath ?? String.Empty),
                    new XElement("startedUtc", DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                    new XElement("instanceToken", instanceToken ?? String.Empty)));
            return AtomicFile.TryWrite(
                path,
                delegate(Stream stream)
                {
                    var settings = new XmlWriterSettings
                    {
                        Encoding = new UTF8Encoding(false),
                        Indent = true,
                        CloseOutput = false
                    };
                    using (XmlWriter writer = XmlWriter.Create(stream, settings))
                    {
                        document.Save(writer);
                    }
                },
                out error);
        }

        // CORE-003: shared lifecycle for interactive modes. Acquires the same
        // installation mutex as RunContinuous so two interactive watchers on the
        // same configuration cannot both be live, performs the same PID/instance
        // publication, and cleans up on exit. The UI runner is supplied as a
        // delegate so --app / --terminal / --gui all share this envelope.
        // Note: durable state sharing with the hidden --watch is a larger
        // refactor; the mutex + lifecycle half is the safety-critical part.
        private static void TryReleaseInstanceMutex(InstanceMutexLease instanceMutex)
        {
            if (instanceMutex == null) return;
            try { instanceMutex.Dispose(); } catch (Exception exception) { try { Console.Error.WriteLine("Instance mutex release warning: " + exception.Message); } catch { } }
        }

        // CORE-005: one place that builds the lifecycle health identity. The raw
        // instance token never leaves this method; only a short non-secret hash
        // of it is published.
        internal static LifecycleHealthContext BuildLifecycleHealthContext(
            string healthFilePath,
            int processId,
            DateTime processStartUtc,
            string mode,
            string instanceToken,
            bool stateStoreApplicable)
        {
            return new LifecycleHealthContext
            {
                HealthFilePath = healthFilePath,
                ProcessId = processId,
                ProcessStartUtc = processStartUtc,
                Mode = mode,
                InstanceShortId = LifecycleHealthContext.DeriveInstanceShortId(instanceToken),
                StateStoreApplicable = stateStoreApplicable
            };
        }

        /// <summary>
        /// CORE-006: publish the first health snapshot BEFORE the PID file becomes
        /// observable. scripts\status.ps1 treats a verified live instance without a
        /// readable health snapshot as UNVERIFIABLE, and every lifecycle consumer
        /// discovers the instance through the PID file, so writing health first
        /// makes that classification deterministic instead of a startup race.
        /// Failure is non-authoritative: health never gates safety.
        /// </summary>
        internal static void TryPublishInitialHealth(LifecycleHealthContext lifecycle, WatcherConfiguration configuration)
        {
            if (lifecycle == null || String.IsNullOrEmpty(lifecycle.HealthFilePath)) return;
            try
            {
                var report = new HealthReport
                {
                    ProductVersion = "1.2",
                    ProcessId = lifecycle.ProcessId,
                    ProcessStartUtc = lifecycle.ProcessStartUtc == DateTime.MinValue ? (DateTime?)null : lifecycle.ProcessStartUtc,
                    InstanceShortId = lifecycle.InstanceShortId,
                    Mode = lifecycle.Mode,
                    PolicyRevision = 0,
                    ConfigFingerprint = "starting",
                    StateStoreDisposition = lifecycle.StateStoreApplicable ? "unknown" : "not_applicable",
                    StateStoreHealthy = false,
                    HealthUpdatedUtc = DateTime.UtcNow
                };
                string error;
                report.TryWrite(lifecycle.HealthFilePath, out error);
            }
            catch
            {
            }
        }

        private static int RunInteractiveWithLifecycle(WatcherConfiguration configuration, RuntimeOptions options, Func<DurableStateStore, Func<bool>, int> uiRunner)
        {
            return RunInteractiveWithLifecycle(configuration, options, delegate(DurableStateStore stateStore, Func<bool> shouldStop, LifecycleHealthContext health) { return uiRunner(stateStore, shouldStop); });
        }

        private static int RunInteractiveWithLifecycle(
            WatcherConfiguration configuration,
            RuntimeOptions options,
            Func<DurableStateStore, Func<bool>, LifecycleHealthContext, int> uiRunner)
        {
            bool isNewMutex;
            InstanceMutexLease instanceMutex = null;
            try
            {
                instanceMutex = AcquireInstanceMutex(options.RuntimeResourceMutexKeys, out isNewMutex);
                if (!isNewMutex)
                {
                    Console.Error.WriteLine("SAICONT is already running for this installation (mutex held).");
                    return 3;
                }
            }
            catch (Exception mutexEx)
            {
                Console.Error.WriteLine("Could not acquire instance mutex: " + mutexEx.Message);
                return 3;
            }

            string instanceToken = Guid.NewGuid().ToString("N");
            DurableStateStore sharedStateStore = null;
            LifecycleHealthContext lifecycleHealth = null;
            try
            {
                Process currentProcess = Process.GetCurrentProcess();
                DateTime procStartUtc;
                try { procStartUtc = currentProcess.StartTime.ToUniversalTime(); } catch { procStartUtc = DateTime.UtcNow; }
                string exePath = Assembly.GetExecutingAssembly().Location;
                TryDelete(options.StopFilePath);
                // CORE-005 / CORE-006: build the identity and publish health BEFORE
                // the PID file exists, so a consumer that discovers this instance
                // always finds a joinable snapshot.
                lifecycleHealth = BuildLifecycleHealthContext(
                    Path.Combine(Path.GetDirectoryName(options.StateFilePath), "SAICONT.health.json"),
                    currentProcess.Id,
                    procStartUtc,
                    options.Mode,
                    instanceToken,
                    true);
                TryPublishInitialHealth(lifecycleHealth, configuration);
                string instanceError;
                if (!TryWriteInstanceFile(options.InstanceFilePath, currentProcess.Id, procStartUtc, options.Mode, exePath, instanceToken, out instanceError))
                {
                    TryDelete(options.StopFilePath);
                    TryReleaseInstanceMutex(instanceMutex);
                    Console.Error.WriteLine("Could not create atomic instance record: " + instanceError);
                    return 1;
                }
                string pidError;
                if (!TryAcquirePidFile(options.PidFilePath, out pidError))
                {
                    TryDelete(options.InstanceFilePath);
                    TryReleaseInstanceMutex(instanceMutex);
                    Console.Error.WriteLine(pidError);
                    return 1;
                }
                sharedStateStore = new DurableStateStore(options.StateFilePath);
                string stateError;
                if (!sharedStateStore.TryPreflight(out stateError))
                {
                    TryDelete(options.PidFilePath);
                    TryDelete(options.InstanceFilePath);
                    TryReleaseInstanceMutex(instanceMutex);
                    Console.Error.WriteLine("Interactive lifecycle state preflight failed: " + stateError);
                    return 1;
                }
            }
            catch (Exception setupEx)
            {
                TryDelete(options.PidFilePath);
                TryDelete(options.InstanceFilePath);
                TryReleaseInstanceMutex(instanceMutex);
                Console.Error.WriteLine("Interactive lifecycle setup failed: " + setupEx.Message);
                return 1;
            }

            Func<bool> shouldStop = delegate
            {
                if (cancelRequested)
                {
                    return true;
                }
                try
                {
                    return File.Exists(options.StopFilePath) && String.Equals(File.ReadAllText(options.StopFilePath).Trim(), instanceToken, StringComparison.Ordinal);
                }
                catch
                {
                    return false;
                }
            };

            try
            {
                return uiRunner(sharedStateStore, shouldStop, lifecycleHealth);
            }
            finally
            {
                try { FaultInjectionSeams.DeleteIfPresent(options.PidFilePath); } catch (Exception ex) { Program.TryReportTeardownFailure("interactive PID cleanup", ex); }
                try { FaultInjectionSeams.DeleteIfPresent(options.InstanceFilePath); } catch (Exception ex) { Program.TryReportTeardownFailure("interactive instance cleanup", ex); }
                // W2-006: interactive lifecycle must clean its own stop
                // artifact before relinquishing the installation mutex, so a
                // token-correct stop request does not leave a stale
                // SAICONT.stop behind for the next start.
                try
                {
                    if (File.Exists(options.StopFilePath))
                    {
                        string current = File.ReadAllText(options.StopFilePath).Trim();
                        if (String.Equals(current, instanceToken, StringComparison.Ordinal))
                        {
                            FaultInjectionSeams.DeleteIfPresent(options.StopFilePath);
                        }
                    }
                }
                catch (Exception ex) { Program.TryReportTeardownFailure("interactive stop-file cleanup", ex); }
                try
                {
                    if (instanceMutex != null)
                    {
                        TryReleaseInstanceMutex(instanceMutex);
                    }
                }
                catch (Exception ex) { Program.TryReportTeardownFailure("interactive instance-mutex release", ex); }
            }
        }

        private static int RunContinuous(WatcherConfiguration configuration, RuntimeOptions options, bool allowInput)
        {
            bool isNewMutex;
            InstanceMutexLease instanceMutex = null;
            try
            {
                instanceMutex = AcquireInstanceMutex(options.RuntimeResourceMutexKeys, out isNewMutex);
                if (!isNewMutex)
                {
                    Console.Error.WriteLine("SAICONT is already running for this installation (mutex held).");
                    return 3;
                }
            }
            catch (Exception mutexEx)
            {
                Console.Error.WriteLine("Could not acquire instance mutex: " + mutexEx.Message);
                return 3;
            }

            OperationalLog log;
            try
            {
                log = new OperationalLog(
                    configuration.LogFilePath,
                    configuration.LogMaximumBytes,
                    configuration.LogRetainedFiles,
                    configuration.LogDuplicateWindowSeconds);
            }
            catch (Exception exception)
            {
                if (instanceMutex != null) { TryReleaseInstanceMutex(instanceMutex); }
                Console.Error.WriteLine("Log initialization failed: " + exception.Message);
                return 1;
            }

            Process currentProcess = Process.GetCurrentProcess();
            DateTime procStartUtc;
            try { procStartUtc = currentProcess.StartTime.ToUniversalTime(); } catch { procStartUtc = DateTime.UtcNow; }
            string instanceToken = Guid.NewGuid().ToString("N");
            string exePath = Assembly.GetExecutingAssembly().Location;
            // Ownership is already held; remove only a request that predates this new token.
            TryDelete(options.StopFilePath);
            // CORE-005 / CORE-006: the health identity is authoritative from here
            // on, and the first snapshot is published before the PID file becomes
            // observable so status.ps1 can always join them.
            string continuousHealthPath = Path.Combine(Path.GetDirectoryName(options.StateFilePath), "SAICONT.health.json");
            LifecycleHealthContext continuousHealth = BuildLifecycleHealthContext(
                continuousHealthPath,
                currentProcess.Id,
                procStartUtc,
                options.Mode,
                instanceToken,
                allowInput && !String.IsNullOrEmpty(options.StateFilePath));
            TryPublishInitialHealth(continuousHealth, configuration);
            string instanceError;
            if (!TryWriteInstanceFile(options.InstanceFilePath, currentProcess.Id, procStartUtc, options.Mode, exePath, instanceToken, out instanceError))
            {
                if (instanceMutex != null) { TryReleaseInstanceMutex(instanceMutex); }
                Console.Error.WriteLine("Could not create atomic instance record: " + instanceError);
                return 1;
            }

            string pidError;
            if (!TryAcquirePidFile(options.PidFilePath, out pidError))
            {
                TryDelete(options.InstanceFilePath);
                if (instanceMutex != null) { TryReleaseInstanceMutex(instanceMutex); }
                log.TryWrite("ERROR", pidError);
                Console.Error.WriteLine(pidError);
                return 1;
            }

            if (!log.TryWrite("INFO", "started mode=" + options.Mode + " pid=" + currentProcess.Id + " token=" + instanceToken + " config=" + options.ConfigurationPath))
            {
                ReleasePidFile(options.PidFilePath);
                TryDelete(options.InstanceFilePath);
                if (instanceMutex != null) { TryReleaseInstanceMutex(instanceMutex); }
                Console.Error.WriteLine("Log initialization failed: could not write " + configuration.LogFilePath);
                return 1;
            }

            NativeConsole.ConsoleCtrlHandler interruptHandler = delegate(int controlType)
            {
                cancelRequested = true;
                log.TryWrite("INFO", "console interrupt received; beginning graceful stop");
                return true;
            };
            if (!NativeConsole.TrySetCtrlHandler(interruptHandler))
            {
                log.TryWrite("ERROR", "console interrupt handler registration failed");
                Console.Error.WriteLine("Console interrupt handler registration failed.");
                ReleasePidFile(options.PidFilePath);
                TryDelete(options.InstanceFilePath);
                if (instanceMutex != null) { TryReleaseInstanceMutex(instanceMutex); }
                return 1;
            }

            try
            {
                DurableStateStore stateStore = (!String.IsNullOrEmpty(options.StateFilePath) && allowInput) ? new DurableStateStore(options.StateFilePath) : null;
                var engine = new WatcherEngine(configuration, stateStore);
                // T-139 / CORE-005: publish health bound to the SAME lifecycle
                // identity that was written to run/SAICONT.instance.xml, so a
                // consumer can reject a dead or reused process.
                engine.LifecycleHealth = continuousHealth;
                bool loggingHealthy = true;
                engine.Run(
                    allowInput,
                    delegate(PollResult result)
                    {
                        if (!LogPollResult(log, result))
                        {
                            loggingHealthy = false;
                        }
                    },
                    delegate
                    {
                        if (cancelRequested)
                        {
                            return true;
                        }
                        if (!loggingHealthy)
                        {
                            return true;
                        }
                        if (!File.Exists(options.StopFilePath))
                        {
                            return false;
                        }
                        try
                        {
                            string stopContent = File.ReadAllText(options.StopFilePath).Trim();
                            if (String.Equals(stopContent, instanceToken, StringComparison.Ordinal))
                            {
                                return true;
                            }
                            return false;
                        }
                        catch
                        {
                            return false;
                        }
                    });
                if (!loggingHealthy)
                {
                    Console.Error.WriteLine("Critical logging failure; watcher stopped fail-closed.");
                    return 1;
                }
                if (cancelRequested)
                {
                    log.TryWrite("INFO", "stopped by console interrupt");
                    return 0;
                }
                log.TryWrite("INFO", "stopped by lifecycle request");
                return 0;
            }
            finally
            {
                // The native control handler is unhooked before FreeConsole to keep
                // teardown deterministic; see NativeConsole.TrySetCtrlHandler notes.
                NativeConsole.UnsetCtrlHandler();
                ReleasePidFile(options.PidFilePath);
                TryDelete(options.InstanceFilePath);
                TryDelete(options.StopFilePath);
                NativeConsole.Detach();
                if (instanceMutex != null)
                {
                    instanceMutex.Dispose();
                }
            }
        }

        private static int RunValidateState(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine("STATE: MISSING");
                return 0;
            }
            var store = new DurableStateStore(path);
            List<StateRecord> records = store.ValidateReadOnly(DateTime.UtcNow);
            switch (store.LastLoadDisposition)
            {
                case StateLoadDisposition.Missing:
                    Console.WriteLine("STATE: MISSING");
                    return 0;
                case StateLoadDisposition.Valid:
                    Console.WriteLine("STATE: VALID_V1 records=" + records.Count);
                    return 0;
                case StateLoadDisposition.UnsupportedSchema:
                    Console.WriteLine("STATE: UNSUPPORTED error=" + store.LastError);
                    return 1;
                case StateLoadDisposition.Unavailable:
                    Console.WriteLine("STATE: I/O_UNAVAILABLE error=" + store.LastError);
                    return 1;
                default:
                    Console.WriteLine("STATE: CORRUPT error=" + store.LastError);
                    return 1;
            }
        }

        private static int RunValidateConfig(WatcherConfiguration config, string path)
        {
            Console.WriteLine("VALID: configuration path=\"" + path + "\" targets=" + config.Targets.Count + " poll_interval=" + config.PollIntervalMilliseconds + "ms log=\"" + config.LogFilePath + "\"");
            foreach (TargetRule target in config.Targets)
            {
                Console.WriteLine("  TARGET name=\"" + target.Name + "\" enabled=" + target.Enabled + " command_length=" + target.Command.Length + " processes=[" + String.Join(",", target.ProcessNames) + "] triggers=" + target.TriggerPatterns.Length + " ready=" + target.ReadyPatterns.Length + " busy=" + target.BusyPatterns.Length + " delay=" + target.InitialDelaySeconds + "s retry=" + target.RetryIntervalSeconds + "s backoff=" + target.BackoffMultiplier + "x max_retry=" + target.MaximumRetryIntervalSeconds + "s max_attempts=" + target.MaximumAttemptsPerEvent);
            }
            return 0;
        }

        private static bool LogPollResult(OperationalLog log, PollResult result)
        {
            if (result.Sent)
            {
                return log.TryWrite("INFO", FormatPollResult(result));
            }

            if (!String.IsNullOrEmpty(result.Error))
            {
                string line = FormatPollResult(result);
                string logMessage = line.StartsWith("ERROR ", StringComparison.Ordinal) ? line.Substring(6) : line;
                return log.TryWriteDeduplicated("error:" + result.Target + ":" + result.ProcessId + ":" + result.Error, "ERROR", logMessage);
            }

            if (result.Triggered)
            {
                return log.TryWriteDeduplicated("trigger:" + result.Target + ":" + result.ProcessId + ":" + result.TriggerToken + ":" + result.Reason, "INFO", FormatPollResult(result));
            }
            if (!String.IsNullOrEmpty(result.FailureClass))
            {
                return log.TryWriteDeduplicated(
                    "failure:" + result.Target + ":" + result.ProcessId + ":" + result.FailureClass + ":" + result.FailureAction,
                    "WARN",
                    FormatPollResult(result));
            }
            return true;
        }

        private static bool TryAcquirePidFile(string path, out string error)
        {
            error = null;
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (File.Exists(path))
                {
                    // The executable-level named mutex is already held by this process.
                    // A pre-existing PID file is therefore stale metadata, never ownership.
                    TryDelete(path);
                }

                try
                {
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(Process.GetCurrentProcess().Id);
                    }
                    return true;
                }
                catch (IOException)
                {
                    if (attempt == 1)
                    {
                        error = "Could not acquire PID file: " + path;
                        return false;
                    }
                }
            }

            error = "Could not acquire PID file: " + path;
            return false;
        }

        private static void ReleasePidFile(string path)
        {
            try
            {
                int recordedProcessId;
                if (File.Exists(path) && Int32.TryParse(File.ReadAllText(path).Trim(), out recordedProcessId) && recordedProcessId == Process.GetCurrentProcess().Id)
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static int RunProbe(WatcherConfiguration configuration)
        {
            TextWriter originalOutput = Console.Out;
            IList<PollResult> results = new WatcherEngine(configuration).PollOnce(false);
            int readable;
            int unreadable;
            string verdict = ClassifyProbe(results, out readable, out unreadable);
            foreach (PollResult result in results)
            {
                originalOutput.WriteLine(FormatProbeResult(result));
                originalOutput.WriteLine(FormatPollResult(result));
            }
            originalOutput.WriteLine("TOTAL " + results.Count + " readable=" + readable + " unreadable=" + unreadable + " result=" + verdict);
            originalOutput.Flush();

            if (String.Equals(verdict, "SKIP", StringComparison.Ordinal))
            {
                originalOutput.WriteLine("RESULT SKIP: no matching target process/session found for any enabled rule.");
                return 1;
            }

            if (String.Equals(verdict, "PASS", StringComparison.Ordinal))
            {
                originalOutput.WriteLine("RESULT PASS: every discovered target console was read successfully (readable=" + readable + ").");
                return 0;
            }

            if (String.Equals(verdict, "FAIL_ALL", StringComparison.Ordinal))
            {
                originalOutput.WriteLine("RESULT FAIL: all discovered target candidates failed to read/attach (unreadable=" + unreadable + ").");
                return 2;
            }

            originalOutput.WriteLine("RESULT FAIL: mixed probe result (readable=" + readable + " unreadable=" + unreadable + "); at least one discovered target console could not be read.");
            return 3;
        }

        private static int RunVerifiedHarnessInjection(string[] args)
        {
            int processId;
            long expectedStartTicks;
            int stressReads;
            if (!Int32.TryParse(args[1], out processId) ||
                !Int64.TryParse(args[2], out expectedStartTicks) ||
                !Int32.TryParse(args[4], out stressReads) ||
                stressReads < 1 || stressReads > 200)
            {
                Console.Error.WriteLine("Invalid verified harness arguments.");
                return 2;
            }

            string command = args[3];
            string scenario = args[5];
            if (scenario != "normal" && scenario != "wrong-start" && scenario != "wrong-membership")
            {
                Console.Error.WriteLine("Invalid verified harness scenario.");
                return 2;
            }

            ProcessSessionIdentity actualIdentity;
            try
            {
                using (Process target = Process.GetProcessById(processId))
                {
                    string expectedExecutable = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                    string actualExecutable = Path.GetFullPath(target.MainModule.FileName);
                    if (!String.Equals(expectedExecutable, actualExecutable, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("REFUSED: harness target is not this SAICONT executable.");
                        return 2;
                    }
                    actualIdentity = ProcessDiscovery.ResolveSessionIdentity(processId, target.ProcessName);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("REFUSED: harness target unavailable: " + exception.Message);
                return 1;
            }

            if (!actualIdentity.IsStrong || actualIdentity.StartTimeUtc.Ticks != expectedStartTicks)
            {
                Console.Error.WriteLine("REFUSED: harness process identity does not match expected start time.");
                return 1;
            }

            ConsoleSnapshot snapshot = null;
            string error = null;
            int handlesBefore = Process.GetCurrentProcess().HandleCount;
            Stopwatch harnessTimer = Stopwatch.StartNew();
            for (int index = 0; index < stressReads; index++)
            {
                if (!NativeConsole.TryRead(processId, 20, out snapshot, out error))
                {
                    Console.Error.WriteLine("FAIL: harness console read " + index + ": " + error);
                    return 1;
                }
                if (!ProcessDiscovery.ConsoleServesMatchedProcess(snapshot.ConsoleProcessIds, processId))
                {
                    Console.Error.WriteLine("FAIL: harness membership proof missing target.");
                    return 1;
                }
            }

            var session = new ResolvedConsoleSession
            {
                MatchedTargetSession = actualIdentity,
                ResolvedAttachProcessId = processId,
                ConsoleProcessIds = snapshot.ConsoleProcessIds,
                WindowHandle = snapshot.WindowHandle,
                StableConsoleId = ProcessDiscovery.ComputeStableConsoleId(snapshot, processId),
                Snapshot = snapshot,
                ResolvedUtc = DateTime.UtcNow
            };

            ProcessSessionIdentity expectedIdentity = actualIdentity;
            if (scenario == "wrong-start")
            {
                expectedIdentity = new ProcessSessionIdentity
                {
                    ProcessId = actualIdentity.ProcessId,
                    ProcessName = actualIdentity.ProcessName,
                    StartTimeUtc = actualIdentity.StartTimeUtc.AddTicks(1)
                };
            }
            else if (scenario == "wrong-membership")
            {
                expectedIdentity = null;
                foreach (Process process in Process.GetProcesses())
                {
                    try
                    {
                        if (snapshot.ConsoleProcessIds.Contains(process.Id))
                        {
                            continue;
                        }
                        ProcessSessionIdentity candidateIdentity = ProcessDiscovery.ResolveSessionIdentity(process.Id, process.ProcessName);
                        if (candidateIdentity.IsStrong)
                        {
                            expectedIdentity = candidateIdentity;
                            break;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
                if (expectedIdentity == null)
                {
                    Console.Error.WriteLine("FAIL: no live process outside the harness console was available for the membership negative control.");
                    return 1;
                }
            }

            if (scenario != "normal")
            {
                // Keep the resolved-session contract internally consistent so each
                // negative reaches the intended write-boundary proof.
                session.MatchedTargetSession = expectedIdentity;
            }

            NativeWriteOutcome writeOutcome = NativeConsole.TryWriteLineVerified(session, expectedIdentity, command, out error);
            NativeConsole.Detach();
            harnessTimer.Stop();
            int handlesAfter = Process.GetCurrentProcess().HandleCount;
            bool expectedWrite = scenario == "normal";
            if ((writeOutcome == NativeWriteOutcome.CompleteInputCommitted) != expectedWrite)
            {
                Console.Error.WriteLine("FAIL: scenario=" + scenario + " written=" + (writeOutcome == NativeWriteOutcome.CompleteInputCommitted) + " error=" + error);
                return 1;
            }
            if (handlesAfter > handlesBefore + 3)
            {
                Console.Error.WriteLine("FAIL: possible handle leak before=" + handlesBefore + " after=" + handlesAfter);
                return 1;
            }

            Console.WriteLine(
                "PASS: verified harness scenario=" + scenario +
                " reads=" + stressReads +
                " written=" + (writeOutcome == NativeWriteOutcome.CompleteInputCommitted) +
                " elapsed_ms=" + harnessTimer.ElapsedMilliseconds +
                " handles_before=" + handlesBefore +
                " handles_after=" + handlesAfter +
                (String.IsNullOrEmpty(error) ? String.Empty : " refusal=" + error));
            return 0;
        }

        internal static string ClassifyProbe(IList<PollResult> results, out int readable, out int unreadable)
        {
            readable = 0;
            unreadable = 0;
            if (results != null)
            {
                foreach (PollResult result in results)
                {
                    if (result.Read && String.IsNullOrEmpty(result.Error))
                    {
                        readable++;
                    }
                    else
                    {
                        unreadable++;
                    }
                }
            }

            if (results == null || results.Count == 0)
            {
                return "SKIP";
            }

            if (unreadable == 0)
            {
                return "PASS";
            }

            if (readable == 0)
            {
                return "FAIL_ALL";
            }

            return "FAIL_MIXED";
        }

        private static string FormatProbeResult(PollResult result)
        {
            if (!String.IsNullOrEmpty(result.Error))
            {
                return String.Format(
                    "PROBE target={0} name={1} pid={2} parent={3} attach_chain=[{4}] attach={5} window=0x{6:X} status=ERROR error={7}",
                    result.Target,
                    Quote(result.ProcessName),
                    result.ProcessId,
                    result.ParentProcessId,
                    result.AttachChain,
                    result.AttachProcessId,
                    result.ConsoleWindow.ToInt64(),
                    Quote(result.Error));
            }

            return String.Format(
                "PROBE target={0} name={1} pid={2} parent={3} attach_chain=[{4}] attach={5} window=0x{6:X} title={7} console=[{8}] status=READ",
                result.Target,
                Quote(result.ProcessName),
                result.ProcessId,
                result.ParentProcessId,
                result.AttachChain,
                result.AttachProcessId,
                     result.ConsoleWindow.ToInt64(),
                     Quote(result.Title),
                     result.ConsolePids);
        }

        private static int AssertEqual<T>(T expected, T actual, string name)
        {
            _selfTestCount++;
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Console.WriteLine("PASS: " + name);
                return 0;
            }

            Console.Error.WriteLine("FAIL: " + name + " expected=" + expected + " actual=" + actual);
            return 1;
        }

        private static string JoinChain(IList<int> values)
        {
            var parts = new string[values.Count];
            for (int index = 0; index < values.Count; index++)
            {
                parts[index] = values[index].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return String.Join(",", parts);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\"", "'") + "\"";
        }

        private static int PrintPollResults(IList<PollResult> results)
        {
            TextWriter originalOutput = Console.Out;
            foreach (PollResult result in results)
            {
                originalOutput.WriteLine(FormatPollResult(result));
            }
            originalOutput.Flush();
            return 0;
        }

        private static string FormatPollResult(PollResult result)
        {
            if (!String.IsNullOrEmpty(result.Error) && !result.Read)
            {
                return String.Format("ERROR target={0} pid={1} attach={2} error={3} code={4}", result.Target, result.ProcessId, result.AttachProcessId, Quote(result.Error), result.DecisionCode);
            }

            return String.Format(
                "MATCH target={0} pid={1} attach={2} title={3} trigger={4} ready={5} busy={6} would_send={7} sent={8} next={9} reason={10} failure={11} action={12} retryable={13} severity={14} matched_by={15} code={16}",
                result.Target,
                result.ProcessId,
                result.AttachProcessId,
                Quote(result.Title),
                result.Triggered,
                result.Ready,
                result.Busy,
                result.WouldSend,
                result.Sent,
                result.NextAttemptUtc == DateTime.MinValue ? "-" : result.NextAttemptUtc.ToString("o"),
                Quote(result.Reason),
                result.FailureClass ?? "-",
                result.FailureAction ?? "-",
                result.FailureRetryable,
                result.FailureSeverity ?? "-",
                Quote(result.FailureMatchedBy),
                result.DecisionCode);
        }
    }
}
