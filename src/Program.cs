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
    internal static class Program
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
        private sealed class RuntimeOptions
        {
            public string Mode;
            public string ConfigurationPath;
            public string PidFilePath;
            public string StopFilePath;
            public string StateFilePath;
            public string InstanceFilePath;
            public string[] RuntimeResourcePaths;
            public string[] RuntimeResourceIdentities;
        }

        private sealed class InstanceMutexLease : IDisposable
        {
            private readonly List<System.Threading.Mutex> _mutexes;

            public InstanceMutexLease(List<System.Threading.Mutex> mutexes)
            {
                _mutexes = mutexes;
            }

            public void Dispose()
            {
                for (int index = _mutexes.Count - 1; index >= 0; index--)
                {
                    try { _mutexes[index].ReleaseMutex(); } catch { }
                    _mutexes[index].Close();
                }
                _mutexes.Clear();
            }
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
                return RunInteractiveWithLifecycle(configuration, options, (stateStore, shouldStop) =>
                    SaiContGuiForm.RunDesktopGui(configuration, options.ConfigurationPath, options.Mode, stateStore, shouldStop));
            }

            if (options.Mode == "--terminal")
            {
                // SAICONT TERMINAL: one console window that monitors the discovered
                // agent sessions and dispatches guarded continuation on demand.
                return RunInteractiveWithLifecycle(configuration, options, (stateStore, shouldStop) =>
                    TerminalUi.RunInteractiveTui(configuration, options.ConfigurationPath, options.Mode, stateStore, shouldStop));
            }

            if (options.Mode == "--gui")
            {
                return RunInteractiveWithLifecycle(configuration, options, (stateStore, shouldStop) =>
                    TerminalUi.RunInteractiveTui(configuration, options.ConfigurationPath, options.Mode, stateStore, shouldStop));
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

        private static bool TryParseOptions(string[] args, out RuntimeOptions options, out string error)
        {
            options = new RuntimeOptions
            {
                ConfigurationPath = ResolveDefaultConfigurationPath()
            };
            error = null;

            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                if (argument == "--watch" || argument == "--dry-run" || argument == "--once" || argument == "--probe" || argument == "--validate-config" || argument == "--validate-state" || argument == "--gui" || argument == "--tui" || argument == "-g" || argument == "--terminal" || argument == "--app" || argument == "--win-gui" || argument == "--window" || argument == "--desktop")
                {
                    if (options.Mode != null)
                    {
                        error = "Choose exactly one operating mode.";
                        return false;
                    }
                    if (argument == "--app" || argument == "--win-gui" || argument == "--window" || argument == "--desktop")
                    {
                        options.Mode = "--app";
                    }
                    else
                    {
                        options.Mode = (argument == "--tui" || argument == "-g") ? "--gui" : argument;
                    }
                    continue;
                }

                if (argument == "--config" || argument == "--pid-file" || argument == "--stop-file" || argument == "--state-file" || argument == "--instance-file")
                {
                    if (index + 1 >= args.Length)
                    {
                        error = argument + " requires a path.";
                        return false;
                    }

                    string rawValue = args[++index];
                    string value;
                    if (!TryNormalizePath(rawValue, argument, out value))
                    {
                        error = "Invalid path for " + argument + ": " + rawValue;
                        return false;
                    }
                    if (argument == "--config")
                    {
                        options.ConfigurationPath = value;
                    }
                    else if (argument == "--pid-file")
                    {
                        options.PidFilePath = value;
                    }
                    else if (argument == "--stop-file")
                    {
                        options.StopFilePath = value;
                    }
                    else if (argument == "--state-file")
                    {
                        options.StateFilePath = value;
                    }
                    else
                    {
                        options.InstanceFilePath = value;
                    }
                    continue;
                }

                error = "Unknown argument: " + argument;
                return false;
            }

            if (options.Mode == null)
            {
                error = "An operating mode is required.";
                return false;
            }

            if (options.Mode == "--watch" || options.Mode == "--dry-run"
                 || options.Mode == "--app" || options.Mode == "--win-gui"
                 || options.Mode == "--gui" || options.Mode == "--terminal"
                 || options.Mode == "--validate-state")
            {
                string configDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ConfigurationPath));
                string runDirectory = Path.Combine(configDirectory, "run");
                if (options.PidFilePath == null)
                {
                    options.PidFilePath = Path.Combine(runDirectory, "SAICONT.pid");
                }
                if (options.StopFilePath == null)
                {
                    options.StopFilePath = Path.Combine(runDirectory, "SAICONT.stop");
                }
                if (options.StateFilePath == null)
                {
                    options.StateFilePath = Path.Combine(runDirectory, "SAICONT.state.xml");
                }
                if (options.InstanceFilePath == null)
                {
                    options.InstanceFilePath = Path.Combine(runDirectory, "SAICONT.instance.xml");
                }
            }

            return true;
        }

        private static string ResolveDefaultConfigurationPath()
        {
            string executableDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string colocated = Path.Combine(executableDirectory, "SAICONT.config.xml");
            string parentDirectory = Directory.GetParent(executableDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) == null
                ? null
                : Directory.GetParent(executableDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).FullName;
            string repositoryConfig = parentDirectory == null ? null : Path.Combine(parentDirectory, "SAICONT.config.xml");
            return String.Equals(Path.GetFileName(executableDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "bin", StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrEmpty(repositoryConfig) && File.Exists(repositoryConfig)
                ? repositoryConfig
                : colocated;
        }

        private static bool TryNormalizePath(string value, string option, out string normalized)
        {
            normalized = null;
            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            try
            {
                normalized = Path.GetFullPath(value);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (PathTooLongException)
            {
                return false;
            }
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

        private static bool IsLifecycleMode(string mode)
        {
            return mode == "--watch" || mode == "--dry-run" || mode == "--app" ||
                mode == "--gui" || mode == "--terminal" || mode == "--win-gui";
        }

        private static void PrepareRuntimeResources(RuntimeOptions options, WatcherConfiguration configuration)
        {
            options.ConfigurationPath = CanonicalizePhysicalPath(options.ConfigurationPath);
            configuration.LogFilePath = CanonicalizePhysicalPath(configuration.LogFilePath);
            options.PidFilePath = CanonicalizePhysicalPath(options.PidFilePath);
            options.StopFilePath = CanonicalizePhysicalPath(options.StopFilePath);
            options.StateFilePath = CanonicalizePhysicalPath(options.StateFilePath);
            options.InstanceFilePath = CanonicalizePhysicalPath(options.InstanceFilePath);
            options.RuntimeResourcePaths = new[]
            {
                options.ConfigurationPath,
                configuration.LogFilePath,
                options.PidFilePath,
                options.StopFilePath,
                options.StateFilePath,
                options.InstanceFilePath
            };

            var identities = new string[options.RuntimeResourcePaths.Length];
            for (int index = 0; index < options.RuntimeResourcePaths.Length; index++)
            {
                identities[index] = GetResourceIdentity(options.RuntimeResourcePaths[index]);
            }
            options.RuntimeResourceIdentities = identities;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < identities.Length; index++)
            {
                if (!seen.Add(identities[index]))
                {
                    throw new InvalidOperationException("Runtime resource path collision: " + options.RuntimeResourcePaths[index]);
                }
            }
        }

        private static string CanonicalizePhysicalPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Runtime resource path is empty.", "path");
            }

            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                return GetFinalPathByHandle(fullPath, false);
            }

            string directory = Path.GetDirectoryName(fullPath);
            string leaf = Path.GetFileName(fullPath);
            if (String.IsNullOrEmpty(directory) || String.IsNullOrEmpty(leaf))
            {
                throw new InvalidOperationException("Runtime resource path is invalid: " + path);
            }

            string existingDirectory = directory;
            var suffix = new Stack<string>();
            while (!Directory.Exists(existingDirectory))
            {
                string parent = Path.GetDirectoryName(existingDirectory);
                string name = Path.GetFileName(existingDirectory);
                if (String.IsNullOrEmpty(parent) || String.IsNullOrEmpty(name))
                {
                    throw new DirectoryNotFoundException("Runtime resource directory not found: " + directory);
                }
                suffix.Push(name);
                existingDirectory = parent;
            }

            string canonicalDirectory = GetFinalPathByHandle(existingDirectory, true);
            while (suffix.Count > 0)
            {
                canonicalDirectory = Path.Combine(canonicalDirectory, suffix.Pop());
            }
            return Path.Combine(canonicalDirectory, leaf);
        }

        private static string GetResourceIdentity(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                const uint GenericRead = 0x80000000;
                const uint FileShareRead = 0x00000001;
                const uint FileShareWrite = 0x00000002;
                const uint FileShareDelete = 0x00000004;
                const uint OpenExisting = 3;
                IntPtr handle = CreateFile(fullPath, GenericRead, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle != new IntPtr(-1))
                {
                    try
                    {
                        BY_HANDLE_FILE_INFORMATION information;
                        if (GetFileInformationByHandle(handle, out information))
                        {
                            return "file:" + information.VolumeSerialNumber.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":" +
                                information.FileIndexHigh.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) +
                                information.FileIndexLow.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }
            return CanonicalizePhysicalPath(path);
        }

        private static string GetFinalPathByHandle(string path, bool directory)
        {
            const uint GenericRead = 0x80000000;
            const uint FileShareRead = 0x00000001;
            const uint FileShareWrite = 0x00000002;
            const uint FileShareDelete = 0x00000004;
            const uint OpenExisting = 3;
            const uint FileFlagBackupSemantics = 0x02000000;
            const uint FileAttributeNormal = 0x00000080;
            IntPtr handle = CreateFile(
                path,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                directory ? FileFlagBackupSemantics : FileAttributeNormal,
                IntPtr.Zero);
            if (handle == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open runtime resource for physical identity: " + path);
            }

            try
            {
                var builder = new StringBuilder(512);
                uint length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
                if (length == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve physical runtime resource identity: " + path);
                }
                if (length >= builder.Capacity)
                {
                    builder = new StringBuilder((int)length + 1);
                    length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
                    if (length == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve physical runtime resource identity: " + path);
                    }
                }
                string result = builder.ToString();
                if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    result = result.Substring(4);
                }
                return result;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder path, uint bufferLength, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(IntPtr file, out BY_HANDLE_FILE_INFORMATION information);

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

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
        private static int RunInteractiveWithLifecycle(WatcherConfiguration configuration, RuntimeOptions options, Func<DurableStateStore, Func<bool>, int> uiRunner)
        {
            bool isNewMutex;
            InstanceMutexLease instanceMutex = null;
            try
            {
                instanceMutex = AcquireInstanceMutex(options.RuntimeResourceIdentities, out isNewMutex);
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
            try
            {
                Process currentProcess = Process.GetCurrentProcess();
                DateTime procStartUtc;
                try { procStartUtc = currentProcess.StartTime.ToUniversalTime(); } catch { procStartUtc = DateTime.UtcNow; }
                string exePath = Assembly.GetExecutingAssembly().Location;
                TryDelete(options.StopFilePath);
                string instanceError;
                if (!TryWriteInstanceFile(options.InstanceFilePath, currentProcess.Id, procStartUtc, options.Mode, exePath, instanceToken, out instanceError))
                {
                    Console.Error.WriteLine("Could not create atomic instance record: " + instanceError);
                    return 1;
                }
                string pidError;
                if (!TryAcquirePidFile(options.PidFilePath, out pidError))
                {
                    TryDelete(options.InstanceFilePath);
                    Console.Error.WriteLine(pidError);
                    return 1;
                }
                sharedStateStore = new DurableStateStore(options.StateFilePath);
                string stateError;
                if (!sharedStateStore.TryPreflight(out stateError))
                {
                    Console.Error.WriteLine("Interactive lifecycle state preflight failed: " + stateError);
                    return 1;
                }
            }
            catch (Exception setupEx)
            {
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
                return uiRunner(sharedStateStore, shouldStop);
            }
            finally
            {
                try { TryDelete(options.PidFilePath); } catch { }
                try { TryDelete(options.InstanceFilePath); } catch { }
                try
                {
                    if (instanceMutex != null)
                    {
                        instanceMutex.Dispose();
                    }
                }
                catch { }
            }
        }

        private static int RunContinuous(WatcherConfiguration configuration, RuntimeOptions options, bool allowInput)
        {
            bool isNewMutex;
            InstanceMutexLease instanceMutex = null;
            try
            {
                instanceMutex = AcquireInstanceMutex(options.RuntimeResourceIdentities, out isNewMutex);
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
                if (instanceMutex != null) { instanceMutex.Dispose(); }
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
            string instanceError;
            if (!TryWriteInstanceFile(options.InstanceFilePath, currentProcess.Id, procStartUtc, options.Mode, exePath, instanceToken, out instanceError))
            {
                if (instanceMutex != null) { instanceMutex.Dispose(); }
                Console.Error.WriteLine("Could not create atomic instance record: " + instanceError);
                return 1;
            }

            string pidError;
            if (!TryAcquirePidFile(options.PidFilePath, out pidError))
            {
                TryDelete(options.InstanceFilePath);
                if (instanceMutex != null) { instanceMutex.Dispose(); }
                log.TryWrite("ERROR", pidError);
                Console.Error.WriteLine(pidError);
                return 1;
            }

            if (!log.TryWrite("INFO", "started mode=" + options.Mode + " pid=" + currentProcess.Id + " token=" + instanceToken + " config=" + options.ConfigurationPath))
            {
                ReleasePidFile(options.PidFilePath);
                TryDelete(options.InstanceFilePath);
                if (instanceMutex != null) { instanceMutex.Dispose(); }
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
                if (instanceMutex != null) { instanceMutex.Dispose(); }
                return 1;
            }

            try
            {
                DurableStateStore stateStore = (!String.IsNullOrEmpty(options.StateFilePath) && allowInput) ? new DurableStateStore(options.StateFilePath) : null;
                var engine = new WatcherEngine(configuration, stateStore);
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
            string line = FormatPollResult(result);
            if (result.Sent)
            {
                return log.TryWrite("INFO", line);
            }

            if (!String.IsNullOrEmpty(result.Error))
            {
                string logMessage = line.StartsWith("ERROR ", StringComparison.Ordinal) ? line.Substring(6) : line;
                return log.TryWriteDeduplicated("error:" + result.Target + ":" + result.ProcessId + ":" + result.Error, "ERROR", logMessage);
            }

            if (result.Triggered)
            {
                return log.TryWriteDeduplicated("trigger:" + result.Target + ":" + result.ProcessId + ":" + result.TriggerToken + ":" + result.Reason, "INFO", line);
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

        private static int RunSelfTests()
        {
            _selfTestCount = 0;
            int failures = 0;
            failures += AssertEqual("cline", ProcessDiscovery.NormalizeName("CLINE.EXE"), "normalize executable name");
            failures += AssertEqual("codex", ProcessDiscovery.NormalizeName("codex"), "normalize plain name");

            var fake = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 10, ParentId = 1, Name = "powershell.exe" },
                new ProcessEntry { Id = 11, ParentId = 10, Name = "node.exe" },
                new ProcessEntry { Id = 12, ParentId = 11, Name = "cline.exe" },
                new ProcessEntry { Id = 20, ParentId = 1, Name = "powershell.exe" },
                new ProcessEntry { Id = 21, ParentId = 20, Name = "codex.exe" }
            };
            var byId = fake.ToDictionary(item => item.Id);

            failures += AssertEqual("12,11,10", JoinChain(ProcessDiscovery.BuildAttachCandidates(fake[2], byId)), "attach candidate lineage matched-first");
            failures += AssertEqual("21,20", JoinChain(ProcessDiscovery.BuildAttachCandidates(fake[4], byId)), "attach candidate lineage codex");

            var orphan = new List<ProcessEntry> { new ProcessEntry { Id = 30, ParentId = 999, Name = "cline.exe" } };
            failures += AssertEqual("30", JoinChain(ProcessDiscovery.BuildAttachCandidates(orphan[0], orphan.ToDictionary(x => x.Id))), "orphan process yields only itself");

            var cycle = new List<ProcessEntry> { new ProcessEntry { Id = 50, ParentId = 50, Name = "cline.exe" } };
            failures += AssertEqual("50", JoinChain(ProcessDiscovery.BuildAttachCandidates(cycle[0], cycle.ToDictionary(x => x.Id))), "ancestry cycle terminates");

            var wrapper = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 60, ParentId = 1, Name = "cmd.exe" },
                new ProcessEntry { Id = 61, ParentId = 60, Name = "node.exe" },
                new ProcessEntry { Id = 62, ParentId = 61, Name = "cline.exe" }
            };
            failures += AssertEqual("62,61,60", JoinChain(ProcessDiscovery.BuildAttachCandidates(wrapper[2], wrapper.ToDictionary(x => x.Id))), "wrapper/intermediate topology lineage");

            var wrapperWithChild = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 100, ParentId = 1, Name = "powershell.exe" },
                new ProcessEntry { Id = 101, ParentId = 100, Name = "node.exe" },
                new ProcessEntry { Id = 102, ParentId = 101, Name = "cline.exe" },
                new ProcessEntry { Id = 103, ParentId = 101, Name = "worker.exe" }
            };
            var wrapperById = wrapperWithChild.ToDictionary(item => item.Id);
            failures += AssertEqual("101,100,102,103", JoinChain(ProcessDiscovery.BuildAttachCandidates(wrapperWithChild[1], wrapperById)), "wrapper with child process candidate inclusion");

            var dupTree = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 110, ParentId = 111, Name = "cline.exe" },
                new ProcessEntry { Id = 111, ParentId = 110, Name = "node.exe" }
            };
            failures += AssertEqual("110,111", JoinChain(ProcessDiscovery.BuildAttachCandidates(dupTree[0], dupTree.ToDictionary(x => x.Id))), "candidate deduplication in cyclic tree");

            failures += AssertEqual(true, ProcessDiscovery.ConsoleServesMatchedProcess(new[] { 1, 2, 3 }, 2), "console membership contains target");
            failures += AssertEqual(false, ProcessDiscovery.ConsoleServesMatchedProcess(new[] { 1, 3 }, 2), "console membership rejects foreign console");
            failures += AssertEqual(false, ProcessDiscovery.ConsoleServesMatchedProcess(new int[0], 2), "empty console list fails closed");
            failures += AssertEqual(false, ProcessDiscovery.ConsoleServesMatchedProcess(null, 2), "null console list fails closed");
            failures += AssertEqual(ConsoleProcessListDisposition.Failed, NativeConsole.ClassifyProcessListCount(0, 64, 1024), "membership API zero count is failure");
            failures += AssertEqual(ConsoleProcessListDisposition.Complete, NativeConsole.ClassifyProcessListCount(64, 64, 1024), "membership API exact buffer is complete");
            failures += AssertEqual(ConsoleProcessListDisposition.Retry, NativeConsole.ClassifyProcessListCount(65, 64, 1024), "membership API oversized result requests retry");
            failures += AssertEqual(ConsoleProcessListDisposition.OverSafetyLimit, NativeConsole.ClassifyProcessListCount(1025, 64, 1024), "membership API refuses safety-relevant truncation");

            var session1 = new ProcessSessionIdentity { ProcessId = 100, StartTimeUtc = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc), ProcessName = "cline" };
            var session2 = new ProcessSessionIdentity { ProcessId = 100, StartTimeUtc = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc), ProcessName = "cline" };
            var session3 = new ProcessSessionIdentity { ProcessId = 100, StartTimeUtc = new DateTime(2026, 8, 26, 11, 0, 0, DateTimeKind.Utc), ProcessName = "cline" };
            failures += AssertEqual(true, session1.Equals(session2), "process session identity matching");
            failures += AssertEqual(false, session1.Equals(session3), "process session PID reuse detection");
            failures += AssertEqual(false, new ProcessSessionIdentity { ProcessId = 100, ProcessName = "cline", StartTimeUtc = DateTime.MinValue }.IsStrong, "missing creation time is weak process identity");

            var snapWithWin = new ConsoleSnapshot { WindowHandle = new IntPtr(0x1234), ConsoleProcessIds = new[] { 100 } };
            failures += AssertEqual("win:4660", ProcessDiscovery.ComputeStableConsoleId(snapWithWin, 100), "stable console id with window");
            var snapNoWin = new ConsoleSnapshot { WindowHandle = IntPtr.Zero, ConsoleProcessIds = new[] { 100, 200 } };
            failures += AssertEqual("pids:100,200", ProcessDiscovery.ComputeStableConsoleId(snapNoWin, 100), "stable console id without window");

            // Transactional Send Safety Tests
            TargetRule safetyRule = new TargetRule
            {
                Name = "test-target",
                Enabled = true,
                ProcessNames = new[] { "cline" },
                Command = "cc",
                ScanLines = 180,
                MaximumTriggerDistanceLines = 150,
                InitialDelaySeconds = 10,
                RetryIntervalSeconds = 10,
                ParseRetryTime = false,
                TriggerPatterns = new[] { @"(?i)rate limited[^\r\n]*" },
                ReadyPatterns = new[] { "^.*Ask.*$" },
                BusyPatterns = new[] { "(?i)working" }
            };
            var safetyConfig = new WatcherConfiguration
            {
                PollIntervalMilliseconds = 2000,
                Targets = new List<TargetRule> { safetyRule }
            };

            DateTime testNow = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
            var initialProcesses = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 500, ParentId = 1, Name = "cline.exe" }
            };
            var initialSession = new ProcessSessionIdentity { ProcessId = 500, ProcessName = "cline", StartTimeUtc = testNow.AddMinutes(-5) };

            // Scenario 1: Target disappeared before pre-send write
            int writeCallCount = 0;
            int snapshotCallCount = 0;
            DateTime transactionClock = testNow;
            var engineDisappeared = new WatcherEngine(
                safetyConfig,
                delegate
                {
                    snapshotCallCount++;
                    return snapshotCallCount <= 2 ? initialProcesses : new List<ProcessEntry>();
                },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = new ConsoleSnapshot { ProcessId = 500, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return transactionClock; });

            engineDisappeared.PollOnce(true);
            transactionClock = testNow.AddSeconds(15);
            IList<PollResult> resDisappeared = engineDisappeared.PollOnce(true);
            failures += AssertEqual(1, resDisappeared.Count, "disappeared scenario has poll result");
            failures += AssertEqual(false, resDisappeared[0].Sent, "disappeared target prevented send");
            failures += AssertEqual(0, writeCallCount, "disappeared target resulted in zero writes");
            failures += AssertEqual("send_blocked=target_disappeared", resDisappeared[0].Reason, "disappeared negative entered write-eligible transaction");

            // Scenario 2: PID reused with different start time
            writeCallCount = 0;
            snapshotCallCount = 0;
            transactionClock = testNow;
            var reusedSession = new ProcessSessionIdentity { ProcessId = 500, ProcessName = "cline", StartTimeUtc = testNow.AddSeconds(1) };
            var engineReused = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name)
                {
                    snapshotCallCount++;
                    return snapshotCallCount <= 2 ? initialSession : reusedSession;
                },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = new ConsoleSnapshot { ProcessId = 500, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return transactionClock; });

            engineReused.PollOnce(true);
            transactionClock = testNow.AddSeconds(15);
            IList<PollResult> resReused = engineReused.PollOnce(true);
            failures += AssertEqual(false, resReused[0].Sent, "PID reuse prevented send");
            failures += AssertEqual(0, writeCallCount, "PID reuse resulted in zero writes");
            failures += AssertEqual("send_blocked=process_session_changed", resReused[0].Reason, "PID reuse negative entered write-eligible transaction");

            // Scenario 3: Console identity changed at pre-send
            writeCallCount = 0;
            int readCount = 0;
            transactionClock = testNow;
            var engineConsoleChanged = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    readCount++;
                    s = new ConsoleSnapshot
                    {
                        ProcessId = 500,
                        WindowHandle = readCount <= 2 ? new IntPtr(0x100) : new IntPtr(0x200),
                        Text = "Rate limited\nAsk anything",
                        CursorLine = "Ask anything",
                        ConsoleProcessIds = new[] { 500 },
                        StartRow = 0,
                        CursorRow = 1
                    };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return transactionClock; });

            engineConsoleChanged.PollOnce(true);
            transactionClock = testNow.AddSeconds(15);
            IList<PollResult> resConsoleChanged = engineConsoleChanged.PollOnce(true);
            failures += AssertEqual(false, resConsoleChanged[0].Sent, "console change prevented send");
            failures += AssertEqual(0, writeCallCount, "console change resulted in zero writes");
            failures += AssertEqual("send_blocked=console_changed", resConsoleChanged[0].Reason, "console-change negative entered write-eligible transaction");

            // Scenario 4: Target not in console at write time
            writeCallCount = 0;
            readCount = 0;
            transactionClock = testNow;
            var engineTargetNotInConsole = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    readCount++;
                    s = new ConsoleSnapshot
                    {
                        ProcessId = 500,
                        Text = "Rate limited\nAsk anything",
                        CursorLine = "Ask anything",
                        ConsoleProcessIds = readCount <= 2 ? new[] { 500 } : new[] { 600 },
                        StartRow = 0,
                        CursorRow = 1
                    };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return transactionClock; });

            engineTargetNotInConsole.PollOnce(true);
            transactionClock = testNow.AddSeconds(15);
            IList<PollResult> resNotInConsole = engineTargetNotInConsole.PollOnce(true);
            failures += AssertEqual(false, resNotInConsole[0].Sent, "target missing from console prevented send");
            failures += AssertEqual(0, writeCallCount, "target missing from console resulted in zero writes");
            failures += AssertEqual(true, resNotInConsole[0].Reason.IndexOf("re-resolution_failed", StringComparison.Ordinal) >= 0, "membership-negative setup reached fresh console resolution");

            // Scenario 5: Prompt changed to busy at recheck
            writeCallCount = 0;
            readCount = 0;
            transactionClock = testNow;
            var engineBusyChanged = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    readCount++;
                    s = new ConsoleSnapshot
                    {
                        ProcessId = 500,
                        Text = readCount <= 2 ? "Rate limited\nAsk anything" : "Rate limited\nWorking...\nAsk anything",
                        CursorLine = "Ask anything",
                        ConsoleProcessIds = new[] { 500 },
                        StartRow = 0,
                        CursorRow = 1
                    };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return transactionClock; });

            engineBusyChanged.PollOnce(true);
            transactionClock = testNow.AddSeconds(15);
            IList<PollResult> resBusyChanged = engineBusyChanged.PollOnce(true);
            failures += AssertEqual(false, resBusyChanged[0].Sent, "busy state at recheck prevented send");
            failures += AssertEqual(0, writeCallCount, "busy state at recheck resulted in zero writes");
            failures += AssertEqual("send_blocked=target_busy", resBusyChanged[0].Reason, "busy negative entered write-eligible transaction");

            // Scenario 6: Verified send succeeds transactionally
            writeCallCount = 0;
            DateTime currentClock = testNow;
            var engineSuccess = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = new ConsoleSnapshot
                    {
                        ProcessId = 500,
                        WindowHandle = new IntPtr(0x555),
                        Text = "Rate limited\nAsk anything",
                        CursorLine = "Ask anything",
                        ConsoleProcessIds = new[] { 500 },
                        StartRow = 0,
                        CursorRow = 1
                    };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return currentClock; });

            // First poll detects trigger and starts initial delay
            engineSuccess.PollOnce(true);
            // Second poll after delay fires verified send
            currentClock = testNow.AddSeconds(15);
            IList<PollResult> resSuccess = engineSuccess.PollOnce(true);
            failures += AssertEqual(true, resSuccess[0].Sent, "verified send succeeded");
            failures += AssertEqual(1, writeCallCount, "exactly one verified write executed");

            // Scenario 7: A different trigger at the safety reread cannot inherit eligibility.
            writeCallCount = 0;
            readCount = 0;
            currentClock = testNow;
            var engineEventChanged = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return initialSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    readCount++;
                    string eventText = readCount <= 2 ? "Rate limited event A\nAsk anything" : "Rate limited event B\nAsk anything";
                    s = new ConsoleSnapshot { ProcessId = 500, WindowHandle = new IntPtr(0x555), Text = eventText, CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return currentClock; });
            engineEventChanged.PollOnce(true);
            currentClock = testNow.AddSeconds(15);
            IList<PollResult> eventChangedResults = engineEventChanged.PollOnce(true);
            failures += AssertEqual(0, writeCallCount, "changed event at safety reread caused zero writes");
            failures += AssertEqual("send_blocked=event_changed", eventChangedResults[0].Reason, "changed event negative entered write-eligible transaction");

            // Scenario 8: Missing process creation time can be observed but never authorize input.
            writeCallCount = 0;
            currentClock = testNow;
            var weakSession = new ProcessSessionIdentity { ProcessId = 500, ProcessName = "cline", StartTimeUtc = DateTime.MinValue };
            var engineWeakIdentity = new WatcherEngine(
                safetyConfig,
                delegate { return initialProcesses; },
                delegate(int pid, string name) { return weakSession; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = new ConsoleSnapshot { ProcessId = 500, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                    e = null;
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                {
                    writeCallCount++;
                    e = null;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return currentClock; });
            engineWeakIdentity.PollOnce(true);
            currentClock = testNow.AddSeconds(15);
            IList<PollResult> weakIdentityResults = engineWeakIdentity.PollOnce(true);
            failures += AssertEqual(0, writeCallCount, "weak process identity caused zero writes");
            failures += AssertEqual("send_blocked=target_identity_unavailable", weakIdentityResults[0].Reason, "weak identity blocked at write eligibility");

            // TryWriteLineVerified basic validation
            string verifyErr;
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(null, initialSession, "cc", out verifyErr), "verified write rejects null session");
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(new ResolvedConsoleSession(), null, "cc", out verifyErr), "verified write rejects null expected target");
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(new ResolvedConsoleSession(), initialSession, "", out verifyErr), "verified write rejects empty command");
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(new ResolvedConsoleSession(), initialSession, "a\nb", out verifyErr), "verified write rejects multiline command");
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(new ResolvedConsoleSession { MatchedTargetSession = session3 }, session1, "cc", out verifyErr), "verified write rejects mismatched resolved target identity");
            failures += AssertEqual(NativeWriteOutcome.NoInputCommitted, NativeConsole.TryWriteLineVerified(new ResolvedConsoleSession { MatchedTargetSession = session1 }, session1, new string('x', 513), out verifyErr), "verified write rejects oversized command");
            failures += AssertEqual(true, NativeConsole.IsCompleteInputWrite(6, 6), "complete native input write accepted");
            failures += AssertEqual(false, NativeConsole.IsCompleteInputWrite(6, 5), "partial native input write fails closed");
            failures += AssertEqual(false, NativeConsole.IsCompleteInputWrite(0, 0), "zero-record native input write is not success");

            var attachList = new List<int> { 70, 71, 72 };
            int selectedPid;
            ConsoleSnapshot selectedSnapshot;
            string selectError;
            bool selected = ProcessDiscovery.TrySelectConsole(
                attachList,
                62,
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    if (pid == 70)
                    {
                        s = null;
                        e = "first candidate failed";
                        return false;
                    }
                    s = new ConsoleSnapshot { ConsoleProcessIds = new[] { 62, 71 } };
                    e = null;
                    return true;
                },
                out selectedPid,
                out selectedSnapshot,
                out selectError);
            failures += AssertEqual(true, selected, "later valid attach candidate accepted after first failure");
            failures += AssertEqual(71, selectedPid, "selected pid is the working candidate");

            bool membershipRejected = ProcessDiscovery.TrySelectConsole(
                attachList,
                62,
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = new ConsoleSnapshot { ConsoleProcessIds = new[] { 63, 72 } };
                    e = null;
                    return true;
                },
                out selectedPid,
                out selectedSnapshot,
                out selectError);
            failures += AssertEqual(false, membershipRejected, "console that lacks the matched process is rejected");
            failures += AssertEqual(true, selectError != null && selectError.IndexOf("does not contain", StringComparison.Ordinal) >= 0, "membership rejection reason");

            var disappearingList = new List<int> { 120 };
            int disappearedPid;
            ConsoleSnapshot disappearedSnapshot;
            string disappearedError;
            bool disappearedResult = ProcessDiscovery.TrySelectConsole(
                disappearingList,
                120,
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                {
                    s = null;
                    e = "AttachConsole failed for PID " + pid + ": The handle is invalid (6)";
                    return false;
                },
                out disappearedPid,
                out disappearedSnapshot,
                out disappearedError);
            failures += AssertEqual(false, disappearedResult, "unreadable candidate correctly reports failure");
            failures += AssertEqual(true, disappearedError != null && disappearedError.IndexOf("The handle is invalid (6)", StringComparison.Ordinal) >= 0, "unreadable candidate error retained");

            // PERF-006: ProcessSnapshotIndex reuses one index across rules.
            var idxProcesses = new List<ProcessEntry>
            {
                new ProcessEntry { Id = 200, ParentId = 1, Name = "powershell.exe" },
                new ProcessEntry { Id = 201, ParentId = 200, Name = "node.exe" },
                new ProcessEntry { Id = 202, ParentId = 201, Name = "cline.exe" }
            };
            var snapshotIndex = new ProcessSnapshotIndex(idxProcesses);
            failures += AssertEqual(3, snapshotIndex.ById.Count, "index ById contains all processes");
            failures += AssertEqual(true, snapshotIndex.ByName.ContainsKey("powershell"), "index ByName has powershell");
            failures += AssertEqual(true, snapshotIndex.ByName.ContainsKey("cline"), "index ByName has cline");
            failures += AssertEqual(1, snapshotIndex.ByName["cline"].Count, "index ByName cline has one entry");
            var idxTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cline" };
            var idxCandidates = ProcessDiscovery.FindCandidates(snapshotIndex, idxTargetNames);
            failures += AssertEqual(1, idxCandidates.Count, "index FindCandidates finds one cline");
            failures += AssertEqual(202, idxCandidates[0].MatchedProcessId, "index FindCandidates matched PID 202");
            failures += AssertEqual(true, idxCandidates[0].AttachProcessIds.Count >= 2, "index FindCandidates has attach chain");

            OK            // PERF-004: membership-first path. Wrong-console candidates are
            // rejected via cheap AttachConsole+GetConsoleProcessList before
            // the expensive per-row screen extraction.
            int mfReadCount = 0;
            int mfPid;
            ConsoleSnapshot mfSnap;
            string mfErr;
            bool mfOk = ProcessDiscovery.TrySelectConsole(attachList, 62,
                delegate(int pid, int lc, out ConsoleSnapshot s, out string e)
                {
                    mfReadCount++;
                    s = new ConsoleSnapshot { ConsoleProcessIds = new[] { 62 } };
                    e = null;
                    return true;
                },
                delegate(int pid, out IList<int> pids, out string err)
                {
                    if (pid == 70) { pids = new[] { 63 }; err = null; return true; }
                    pids = new[] { 62 };
                    err = null;
                    return true;
                },
                180, out mfPid, out mfSnap, out mfErr);
            failures += AssertEqual(true, mfOk, "membership-first accepts correct candidate");
            failures += AssertEqual(1, mfReadCount, "membership-first reads only accepted candidate");
            failures += AssertEqual(71, mfPid, "membership-first selects PID 71");

            var emptyReads = new List<PollResult>();
            int probeReadable;
            int probeUnreadable;
            failures += AssertEqual("SKIP", ClassifyProbe(emptyReads, out probeReadable, out probeUnreadable), "probe with zero matches is SKIP");
            failures += AssertEqual("PASS", ClassifyProbe(new[] { new PollResult { Read = true }, new PollResult { Read = true } }, out probeReadable, out probeUnreadable), "probe with all readable is PASS");
            failures += AssertEqual("FAIL_ALL", ClassifyProbe(new[] { new PollResult { Read = false, Error = "attach failed" } }, out probeReadable, out probeUnreadable), "probe with only console errors is FAIL_ALL");
            failures += AssertEqual("FAIL_MIXED", ClassifyProbe(new[] { new PollResult { Read = true }, new PollResult { Read = false, Error = "attach failed" } }, out probeReadable, out probeUnreadable), "probe with mixed success and error is FAIL_MIXED");
            failures += AssertEqual("FAIL_ALL", ClassifyProbe(new[] { new PollResult { Read = true, Error = "rule_evaluation_failed=regex_timeout" } }, out probeReadable, out probeUnreadable), "probe fails when readable console rule evaluation fails");

            string writeError;
            bool accepted = NativeConsole.TryWriteLine(0, "", out writeError);
            failures += AssertEqual(false, accepted, "empty input rejected");
            failures += AssertEqual(true, writeError != null && writeError.IndexOf("empty", StringComparison.OrdinalIgnoreCase) >= 0, "empty input reason");
            failures += AssertEqual(true, WatcherEngine.IsNonConsoleDiscoveryFailure("AttachConsole failed for PID 1: invalid handle (6)"), "ignore non-console discovery candidate");
            failures += AssertEqual(false, WatcherEngine.IsNonConsoleDiscoveryFailure("AttachConsole failed for PID 1: access denied (5)"), "keep real discovery read failure");

            DateTime due;
            failures += AssertEqual(true, RetryTimeParser.TryParseDue("try again at 4:40 PM", new DateTime(2026, 8, 26, 16, 0, 0), out due), "parse Codex reset clock");
            failures += AssertEqual(new DateTime(2026, 8, 26, 16, 40, 0), due, "future Codex reset clock");
            failures += AssertEqual(true, RetryTimeParser.TryParseDue("try again at 4:40 PM", new DateTime(2026, 8, 26, 17, 0, 0), out due), "parse expired reset clock");
            failures += AssertEqual(new DateTime(2026, 8, 26, 17, 0, 0), due, "expired reset runs now");
            failures += AssertEqual(true, RetryTimeParser.TryParseDue("again at Aug 27th, 2026 2:44 PM", new DateTime(2026, 8, 27, 1, 0, 0), out due), "parse full date Codex reset clock");
            failures += AssertEqual(new DateTime(2026, 8, 27, 14, 44, 0), due, "full date Codex reset clock due time");
            failures += AssertEqual(true, RetryTimeParser.TryParseDue("Try again in 8h 57m", new DateTime(2026, 8, 26, 17, 0, 0), out due), "parse Cline compact limit duration");
            failures += AssertEqual(new DateTime(2026, 8, 27, 1, 57, 0), due, "Cline compact limit due time");
            failures += AssertEqual(false, RetryTimeParser.TryParseDue("Try again in 999999h", new DateTime(2026, 8, 26, 17, 0, 0), out due), "reject absurd retry duration");

            DateTime start = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
            TargetRule clineLimitRule = WatcherConfiguration.CreateTestSample().Targets[1];
            var clineLimitSnapshot = new ConsoleSnapshot
            {
                Text = "Daily free model limit reached\nYou've reached today's free usage limit for this model.\nTry again in 8h 57m\nWhat can I do for you?",
                CursorLine = "What can I do for you?",
                StartRow = 0,
                CursorRow = 3
            };
            RuleObservation clineLimitObservation = RuleMatcher.Inspect(clineLimitRule, clineLimitSnapshot, start);
            failures += AssertEqual(true, clineLimitObservation.Triggered, "detect Cline daily free-model limit");
            failures += AssertEqual(true, clineLimitObservation.Ready, "detect current Cline empty prompt");
            failures += AssertEqual(start.AddHours(8).AddMinutes(57).AddSeconds(3), clineLimitObservation.DueUtc, "honor Cline compact retry deadline");

            TargetRule retryRule = clineLimitRule;
            var retryState = new RetrySessionState();
            var retryObservation = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "T1", DueUtc = start.AddSeconds(60) };
            failures += AssertEqual(false, retryState.Observe(retryObservation, retryRule, start).Send, "Cline first retry waits");
            failures += AssertEqual(false, retryState.Observe(retryObservation, retryRule, start.AddSeconds(59)).Send, "Cline cooldown blocks early send");
            RetryDecision sendDecision = retryState.Observe(retryObservation, retryRule, start.AddSeconds(60));
            failures += AssertEqual(true, sendDecision.Send, "Cline retry due at 60 seconds");
            retryState.RecordAttempt(true, sendDecision.TriggerToken, retryRule, start.AddSeconds(60));
            failures += AssertEqual(false, retryState.Observe(retryObservation, retryRule, start.AddSeconds(119)).Send, "Cline repeat cooldown blocks spam");
            failures += AssertEqual(true, retryState.Observe(retryObservation, retryRule, start.AddSeconds(120)).Send, "Cline repeats at next interval");

            var guardedState = new RetrySessionState();
            var typedPrompt = new RuleObservation { Triggered = true, Ready = false, TriggerToken = "T2", DueUtc = start };
            failures += AssertEqual(false, guardedState.Observe(typedPrompt, retryRule, start).Send, "typed prompt blocks injection");

            // W2-002: ambiguous native-write outcomes must never be re-dispatched
            // by elapsed retry time alone; only console evidence resolves them.
            var ambiguousState = new RetrySessionState();
            var ambTrigger = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "AMB-EVENT", DueUtc = start.AddSeconds(60) };
            failures += AssertEqual(true, ambiguousState.Observe(ambTrigger, retryRule, start.AddSeconds(60)).Send, "ambiguous: initial due send eligible");
            ambiguousState.ReserveAttempt("AMB-EVENT", retryRule, start.AddSeconds(60));
            // Restart restore: session appears as AttemptInFlightReserved.
            var restoredAmb = new RetrySessionState();
            restoredAmb.RestoreFrom(new StateRecord
            {
                RuleName = "cline-limits",
                ProcessId = 500,
                ProcessStartUtc = start,
                TriggerFingerprint = "AMB-EVENT",
                LastObservedUtc = start.AddSeconds(60),
                LastWriteUtc = start.AddSeconds(60),
                NextAllowedAttemptUtc = start.AddSeconds(60),
                AwaitingOutcome = false,
                SawBusyAfterWrite = false,
                SuppressedFingerprint = null,
                AttemptCount = 1,
                RecoveryState = RecoveryState.AttemptInFlightReserved.ToString()
            }, start.AddSeconds(120));
            // Elapsed time far past the retry deadline must NOT authorize a send.
            var sameTriggerReady = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "AMB-EVENT", DueUtc = start.AddSeconds(60) };
            failures += AssertEqual(false, restoredAmb.Observe(sameTriggerReady, retryRule, start.AddSeconds(600)).Send, "ambiguous in-flight reservation cannot re-send by elapsed time alone");
            failures += AssertEqual(RecoveryState.AmbiguousFailClosed, restoredAmb.State, "ambiguous unresolved state is fail-closed");
            // A different (new) occurrence starts a fresh lifecycle and may send.
            var newOccurrence = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "AMB-EVENT-2", DueUtc = start.AddSeconds(60) };
            RetryDecision newDecision = restoredAmb.Observe(newOccurrence, retryRule, start.AddSeconds(600));
            failures += AssertEqual(true, newDecision.Send, "new occurrence after ambiguous write starts fresh lifecycle");
            // Trigger clearing proves the previous write succeeded -> recovery.
            var triggerCleared = new RuleObservation { Triggered = false, Ready = true, Busy = false, TriggerToken = null, DueUtc = DateTime.MinValue };
            failures += AssertEqual(false, restoredAmb.Observe(triggerCleared, retryRule, start.AddSeconds(601)).Send, "trigger-clear resolves ambiguous write without send");
            failures += AssertEqual(RecoveryState.IdleNoEvent, restoredAmb.State, "new occurrence clears after fresh lifecycle");

            // W2-002: partial accepted write (AmbiguousOrPartialInput) enters
            // AmbiguousFailClosed and must not become timer-authorized.
            var partialState = new RetrySessionState();
            partialState.ReserveAttempt("P-EVENT", retryRule, start.AddSeconds(60));
            partialState.CommitAttempt(NativeWriteOutcome.AmbiguousOrPartialInput, retryRule, start.AddSeconds(60));
            failures += AssertEqual(RecoveryState.AmbiguousFailClosed, partialState.State, "partial write enters ambiguous fail-closed state");
            var partialSameTrigger = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "P-EVENT", DueUtc = start.AddSeconds(60) };
            failures += AssertEqual(false, partialState.Observe(partialSameTrigger, retryRule, start.AddSeconds(3600)).Send, "partial-write ambiguity cannot re-send by elapsed time alone");
            // Definitely-no-input may retry per policy.
            var retryState2 = new RetrySessionState();
            retryState2.ReserveAttempt("N-EVENT", retryRule, start.AddSeconds(60));
            retryState2.CommitAttempt(NativeWriteOutcome.NoInputCommitted, retryRule, start.AddSeconds(60));
            failures += AssertEqual(RecoveryState.BackoffWait, retryState2.State, "no-input-committed enters ordinary backoff");
            var noInputRetry = new RuleObservation { Triggered = true, Ready = true, TriggerToken = "N-EVENT", DueUtc = start.AddSeconds(60) };
            failures += AssertEqual(true, retryState2.Observe(noInputRetry, retryRule, start.AddSeconds(120)).Send, "definitely-no-input may retry per policy");

            try
            {
                WatcherConfiguration loaded = WatcherConfiguration.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SAICONT.config.xml"));
                failures += AssertEqual(2, loaded.Targets.Count, "load editable configuration");
            }
            catch (Exception exception)
            {
                failures += AssertEqual(true, false, "load editable configuration: " + exception.Message);
            }

            // Wave 2: Event Correlation and Bounded Context Tests
            var multiEventSnapshot = new ConsoleSnapshot
            {
                Text = "Rate limit exceeded\nTry again in 1 hour\n---\nSome intermediate work...\nDaily free model limit reached\nTry again in 8h 57m\nWhat can I do for you?",
                CursorLine = "What can I do for you?",
                StartRow = 0,
                CursorRow = 6
            };
            RuleObservation multiObs = RuleMatcher.Inspect(clineLimitRule, multiEventSnapshot, start);
            failures += AssertEqual(true, multiObs.Triggered, "multi-event picks latest trigger");
            failures += AssertEqual(4, multiObs.TriggerRow, "multi-event trigger row is latest event");
            failures += AssertEqual(start.AddHours(8).AddMinutes(57).AddSeconds(3), multiObs.DueUtc, "multi-event bounded context parses latest deadline");
            var scrolledEventSnapshot = new ConsoleSnapshot
            {
                Text = multiEventSnapshot.Text,
                CursorLine = multiEventSnapshot.CursorLine,
                StartRow = 500,
                CursorRow = 506
            };
            RuleObservation scrolledObs = RuleMatcher.Inspect(clineLimitRule, scrolledEventSnapshot, start);
            failures += AssertEqual(multiObs.TriggerToken, scrolledObs.TriggerToken, "event fingerprint remains stable when buffer rows scroll");
            var laterEventSnapshot = new ConsoleSnapshot
            {
                Text = "Daily free model limit reached\nTry again in 9h 1m\nWhat can I do for you?",
                CursorLine = "What can I do for you?",
                StartRow = 0,
                CursorRow = 2
            };
            RuleObservation laterEventObs = RuleMatcher.Inspect(clineLimitRule, laterEventSnapshot, start);
            // CORE-001: trigger pattern matches "Daily free model limit reached ... try again in" (line ~14 in SAICONT.config.xml)
            // which does not capture the time digits. Both 8h 57m and 9h 1m share the same matched text and therefore the
            // same stable event identity. The deadline moves into DueUtc and is anchored in RetrySessionState on first
            // acceptance; a different time does not make this a different event from the matcher's perspective. The
            // occurrence discriminator for byte-identical later occurrences lives in the session state, not the matcher.
            failures += AssertEqual(true, String.Equals(multiObs.TriggerToken, laterEventObs.TriggerToken, StringComparison.Ordinal), "matched text identity is stable across deadline changes; deadline carries via DueUtc");

            // Wave 2: Current-tail busy matching
            TargetRule codexBusyRule = WatcherConfiguration.CreateTestSample().Targets[0];
            var historicalBusySnapshot = new ConsoleSnapshot
            {
                Text = "> Working (10%)\nHistorical line 1\nHistorical line 2\nHistorical line 3\nHistorical line 4\nHistorical line 5\nHistorical line 6\nHistorical line 7\nTask completed.\n› Ask Codex to do anything",
                CursorLine = "› Ask Codex to do anything",
                StartRow = 0,
                CursorRow = 9
            };
            RuleObservation histBusyObs = RuleMatcher.Inspect(codexBusyRule, historicalBusySnapshot, start);
            failures += AssertEqual(false, histBusyObs.Busy, "historical busy outside tail ignored");

            var currentBusySnapshot = new ConsoleSnapshot
            {
                Text = "Task completed.\n› Working (50%)...",
                CursorLine = "› Working (50%)...",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation currBusyObs = RuleMatcher.Inspect(codexBusyRule, currentBusySnapshot, start);
            failures += AssertEqual(true, currBusyObs.Busy, "current tail busy detected");

            // Wave 2: DurableStateStore & Restart Semantics
            string stateTestDir = Path.Combine(Path.GetTempPath(), "SAICONT-state-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stateTestDir);
            try
            {
                string stateFilePath = Path.Combine(stateTestDir, "SAICONT.state.xml");
                var stateStore = new DurableStateStore(stateFilePath);

                var rec1 = new StateRecord
                {
                    RuleName = "cline-limits",
                    ProcessId = 500,
                    ProcessStartUtc = testNow,
                    TriggerFingerprint = "T-FINGERPRINT-1",
                    RuleSemanticFingerprint = safetyConfig.Targets[0].SemanticFingerprint,
                    LastObservedUtc = testNow,
                    LastWriteUtc = testNow,
                    NextAllowedAttemptUtc = testNow.AddSeconds(60),
                    AwaitingOutcome = true,
                    SawBusyAfterWrite = false,
                    SuppressedFingerprint = null,
                    AttemptCount = 1,
                    RecoveryState = RecoveryState.EventWaitingDeadline.ToString()
                };
                var ancientRec = new StateRecord
                {
                    RuleName = "cline-limits",
                    ProcessId = 501,
                    ProcessStartUtc = testNow.AddDays(-500),
                    TriggerFingerprint = "T-ANCIENT",
                    LastObservedUtc = testNow.AddDays(-500),
                    LastWriteUtc = testNow.AddDays(-500),
                    NextAllowedAttemptUtc = testNow.AddDays(-500),
                    AwaitingOutcome = false,
                    SawBusyAfterWrite = false,
                    SuppressedFingerprint = null,
                    AttemptCount = 1,
                    RecoveryState = RecoveryState.IdleNoEvent.ToString()
                };

                stateStore.Save(new[] { rec1, ancientRec }, testNow);
                failures += AssertEqual(true, File.Exists(stateFilePath), "state store created XML file");

                List<StateRecord> loadedRecords = stateStore.Load(testNow);
                failures += AssertEqual(1, loadedRecords.Count, "state store pruned ancient record on save");
                if (loadedRecords.Count > 0)
                {
                    failures += AssertEqual(500, loadedRecords[0].ProcessId, "loaded record pid");
                    failures += AssertEqual("T-FINGERPRINT-1", loadedRecords[0].TriggerFingerprint, "loaded record fingerprint");
                    failures += AssertEqual(true, loadedRecords[0].AwaitingOutcome, "loaded record awaiting outcome");
                    failures += AssertEqual(testNow.AddSeconds(60), loadedRecords[0].NextAllowedAttemptUtc, "loaded record next allowed time");
                }
                bool unchangedState;
                string unchangedError;
                bool unchangedSaved = stateStore.TrySave(new[] { rec1 }, testNow.AddMinutes(1), out unchangedState, out unchangedError);
                failures += AssertEqual(true, unchangedSaved, "unchanged durable state save succeeds");
                failures += AssertEqual(false, unchangedState, "unchanged durable state does not rewrite file every poll");
                failures += AssertEqual(1, stateStore.SuccessfulWriteCount, "durable state write count changes only for meaningful state");
                failures += AssertEqual(0, Directory.GetFiles(stateTestDir, "*.tmp.*").Length, "atomic state save leaves no temp artifact");

                // Restart semantics: WatcherEngine with restored state preserves cooldown
                var engineRestored = new WatcherEngine(
                    safetyConfig,
                    delegate { return initialProcesses; },
                    delegate(int pid, string name) { return initialSession; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                    {
                        s = new ConsoleSnapshot
                        {
                            ProcessId = 500,
                            Text = "Rate limited\nAsk anything",
                            CursorLine = "Ask anything",
                            ConsoleProcessIds = new[] { 500 },
                            StartRow = 0,
                            CursorRow = 1
                        };
                        e = null;
                        return true;
                    },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                    {
                        e = null;
                        return NativeWriteOutcome.CompleteInputCommitted;
                    },
                    delegate { return testNow.AddSeconds(10); },
                    stateStore);

                IList<PollResult> restartPoll1 = engineRestored.PollOnce(true);
                failures += AssertEqual(false, restartPoll1[0].Sent, "restored state preserves active cooldown on restart");
                failures += AssertEqual(false, restartPoll1[0].WouldSend, "restored state would not send during preserved cooldown");

                var restoredSuppression = new RetrySessionState();
                restoredSuppression.RestoreFrom(new StateRecord
                {
                    TriggerFingerprint = "OLD-EVENT",
                    SuppressedFingerprint = "OLD-EVENT",
                    LastObservedUtc = testNow,
                    NextAllowedAttemptUtc = testNow,
                    RecoveryState = RecoveryState.RecoveryConfirmed.ToString()
                }, testNow);
                RetryDecision suppressedAfterRestart = restoredSuppression.Observe(
                    new RuleObservation { Triggered = true, Ready = true, TriggerToken = "OLD-EVENT", DueUtc = testNow },
                    retryRule,
                    testNow);
                failures += AssertEqual(false, suppressedAfterRestart.Send, "restart preserves stale-event suppression");

                var futureState = new RetrySessionState();
                futureState.RestoreFrom(new StateRecord
                {
                    TriggerFingerprint = "FUTURE",
                    LastObservedUtc = testNow.AddDays(30),
                    NextAllowedAttemptUtc = testNow.AddDays(30),
                    RecoveryState = RecoveryState.BackoffWait.ToString()
                }, testNow);
                failures += AssertEqual(testNow.AddDays(30), futureState.NextAttemptUtc, "legitimate long retry deadline survives restart unchanged");

                var corruptFutureState = new RetrySessionState();
                corruptFutureState.RestoreFrom(new StateRecord
                {
                    TriggerFingerprint = "CORRUPT-FUTURE",
                    LastObservedUtc = testNow,
                    NextAllowedAttemptUtc = testNow.AddYears(50),
                    RecoveryState = RecoveryState.BackoffWait.ToString()
                }, testNow);
                DateTime horizonMaximum = testNow.AddDays(RetryConstants.MaximumRetryHorizonDays);
                failures += AssertEqual(true, corruptFutureState.NextAttemptUtc <= horizonMaximum, "out-of-contract future timestamp is clamped to supported horizon");

                // Corrupted state file test
                File.WriteAllText(stateFilePath, "<malformed_xml");
                List<StateRecord> recoveredRecords = stateStore.Load();
                failures += AssertEqual(0, recoveredRecords.Count, "corrupt state safely yields empty records without crash");
                failures += AssertEqual(StateLoadDisposition.Corrupt, stateStore.LastLoadDisposition, "corrupt state is classified explicitly");
                failures += AssertEqual(true, Directory.GetFiles(stateTestDir, "SAICONT.state.xml.corrupt.*").Length == 1, "corrupt state is quarantined");

                File.WriteAllText(stateFilePath, "<saicontState version=\"999\" updatedUtc=\"2026-08-26T12:00:00Z\" />");
                List<StateRecord> futureSchemaRecords = stateStore.Load();
                failures += AssertEqual(0, futureSchemaRecords.Count, "future state schema loads no unsafe records");
                failures += AssertEqual(StateLoadDisposition.UnsupportedSchema, stateStore.LastLoadDisposition, "future state schema is classified explicitly");
                failures += AssertEqual(true, Directory.GetFiles(stateTestDir, "SAICONT.state.xml.unsupported.*").Length == 1, "future state schema is quarantined");

                var manyRecords = new List<StateRecord>();
                for (int index = 0; index < DurableStateStore.MaximumRecords + 25; index++)
                {
                    manyRecords.Add(new StateRecord
                    {
                        RuleName = "rule-" + index,
                        ProcessId = 1000 + index,
                        ProcessStartUtc = testNow.AddSeconds(index),
                        TriggerFingerprint = "event-" + index,
                        LastObservedUtc = testNow,
                        RecoveryState = RecoveryState.BackoffWait.ToString()
                    });
                }
                stateStore.Save(manyRecords, testNow);
                failures += AssertEqual(DurableStateStore.MaximumRecords, stateStore.Load(testNow).Count, "durable state record count is hard bounded");

                string attemptStatePath = Path.Combine(stateTestDir, "attempt.state.xml");
                var attemptStore = new DurableStateStore(attemptStatePath);
                DateTime attemptClock = testNow;
                var attemptEngine = new WatcherEngine(
                    safetyConfig,
                    delegate { return initialProcesses; },
                    delegate(int pid, string name) { return initialSession; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                    {
                        s = new ConsoleSnapshot { ProcessId = 500, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                        e = null;
                        return true;
                    },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                    {
                        e = null;
                        return NativeWriteOutcome.CompleteInputCommitted;
                    },
                    delegate { return attemptClock; },
                    attemptStore);
                attemptEngine.PollOnce(true);
                attemptClock = testNow.AddSeconds(15);
                IList<PollResult> persistedAttemptResult = attemptEngine.PollOnce(true);
                List<StateRecord> persistedAttemptRecords = new DurableStateStore(attemptStatePath).Load(testNow);
                failures += AssertEqual(true, persistedAttemptResult.Count > 0 && persistedAttemptResult[0].Sent, "state persistence test actually performed verified send");
                failures += AssertEqual(1, persistedAttemptRecords.Count, "successful write persisted one session record in same poll");
                if (persistedAttemptRecords.Count > 0)
                {
                    failures += AssertEqual(true, persistedAttemptRecords[0].AwaitingOutcome, "successful write persisted awaiting-outcome immediately");
                    failures += AssertEqual(1, persistedAttemptRecords[0].AttemptCount, "successful write persisted attempt count immediately");
                }

                string blockedParent = Path.Combine(stateTestDir, "not-a-directory");
                File.WriteAllText(blockedParent, "x");
                var blockedStore = new DurableStateStore(Path.Combine(blockedParent, "state.xml"));
                DateTime blockedClock = testNow;
                int blockedWriterCalls = 0;
                var blockedStateEngine = new WatcherEngine(
                    safetyConfig,
                    delegate { return initialProcesses; },
                    delegate(int pid, string name) { return initialSession; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                    {
                        s = new ConsoleSnapshot { ProcessId = 500, Text = "Rate limited\nAsk anything", CursorLine = "Ask anything", ConsoleProcessIds = new[] { 500 }, StartRow = 0, CursorRow = 1 };
                        e = null;
                        return true;
                    },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                    {
                        blockedWriterCalls++;
                        e = null;
                        return NativeWriteOutcome.CompleteInputCommitted;
                    },
                    delegate { return blockedClock; },
                    blockedStore);
                blockedStateEngine.PollOnce(true);
                blockedClock = testNow.AddSeconds(15);
                IList<PollResult> blockedStateResults = blockedStateEngine.PollOnce(true);
                failures += AssertEqual(0, blockedWriterCalls, "unwritable durable state prevents input");
                failures += AssertEqual("send_blocked=state_store_unavailable", blockedStateResults[0].Reason, "unwritable durable state is surfaced explicitly");
            }
            finally
            {
                if (Directory.Exists(stateTestDir))
                {
                    Directory.Delete(stateTestDir, true);
                }
            }

            // Wave 3: Timeline Simulator & Recovery State Machine Tests
            var backoffRule = new TargetRule
            {
                Name = "backoff-test",
                Enabled = true,
                ProcessNames = new[] { "cline" },
                Command = "cc",
                ScanLines = 180,
                MaximumTriggerDistanceLines = 150,
                InitialDelaySeconds = 10,
                RetryIntervalSeconds = 10,
                BackoffMultiplier = 2.0,
                MaximumRetryIntervalSeconds = 80,
                MaximumAttemptsPerEvent = 4,
                ParseRetryTime = false,
                TriggerPatterns = new[] { "(?i)rate limit" },
                ReadyPatterns = new[] { "^.*Ask.*$" },
                BusyPatterns = new[] { "(?i)working" }
            };

            var simState = new RetrySessionState();
            DateTime t0 = new DateTime(2026, 8, 26, 14, 0, 0, DateTimeKind.Utc);
            var trigObs = new RuleObservation { Triggered = true, Ready = true, Busy = false, TriggerToken = "TRIG-1", DueUtc = t0.AddSeconds(10) };

            // Step 1: Initial discovery -> EventWaitingDeadline
            RetryDecision d1 = simState.Observe(trigObs, backoffRule, t0);
            failures += AssertEqual(false, d1.Send, "sim: initial discovery waits for initial delay");
            failures += AssertEqual(RecoveryState.EventWaitingDeadline, simState.State, "sim: initial state is EventWaitingDeadline");

            // Step 2: Initial delay expires -> EventReadyToAttempt -> Send
            DateTime t1 = t0.AddSeconds(10);
            RetryDecision d2 = simState.Observe(trigObs, backoffRule, t1);
            failures += AssertEqual(true, d2.Send, "sim: delay expired triggers send");
            failures += AssertEqual(RecoveryState.EventReadyToAttempt, simState.State, "sim: state is EventReadyToAttempt");

            // Step 3: Record write -> CommandWrittenAwaitingOutcome (Attempt 1)
            simState.RecordAttempt(true, "TRIG-1", backoffRule, t1);
            failures += AssertEqual(RecoveryState.CommandWrittenAwaitingOutcome, simState.State, "sim: after write state is CommandWrittenAwaitingOutcome");
            failures += AssertEqual(1, simState.AttemptCount, "sim: attempt count is 1");
            failures += AssertEqual(t1.AddSeconds(10), simState.NextAttemptUtc, "sim: attempt 1 backoff is 10s");

            // Step 4: Immediate next poll -> no progress, triggers backoff wait
            DateTime t2 = t1.AddSeconds(2);
            RetryDecision d3 = simState.Observe(trigObs, backoffRule, t2);
            failures += AssertEqual(false, d3.Send, "sim: cooldown blocks immediate re-send");
            failures += AssertEqual(RecoveryState.BackoffWait, simState.State, "sim: state transitioned to BackoffWait");

            // Step 5: Backoff 1 expires at t1 + 10s -> Send attempt 2
            DateTime t3 = t1.AddSeconds(10);
            RetryDecision d4 = simState.Observe(trigObs, backoffRule, t3);
            failures += AssertEqual(true, d4.Send, "sim: backoff 1 expired triggers attempt 2");
            simState.RecordAttempt(true, "TRIG-1", backoffRule, t3);
            failures += AssertEqual(2, simState.AttemptCount, "sim: attempt count is 2");
            failures += AssertEqual(t3.AddSeconds(20), simState.NextAttemptUtc, "sim: attempt 2 backoff is 20s (10 * 2^1)");

            // Step 6: Backoff 2 expires at t3 + 20s -> Send attempt 3
            DateTime t4 = t3.AddSeconds(20);
            RetryDecision d5 = simState.Observe(trigObs, backoffRule, t4);
            failures += AssertEqual(true, d5.Send, "sim: backoff 2 expired triggers attempt 3");
            simState.RecordAttempt(true, "TRIG-1", backoffRule, t4);
            failures += AssertEqual(3, simState.AttemptCount, "sim: attempt count is 3");
            failures += AssertEqual(t4.AddSeconds(40), simState.NextAttemptUtc, "sim: attempt 3 backoff is 40s (10 * 2^2)");

            // Step 7: Backoff 3 expires at t4 + 40s -> Send attempt 4
            DateTime t5 = t4.AddSeconds(40);
            RetryDecision d6 = simState.Observe(trigObs, backoffRule, t5);
            failures += AssertEqual(true, d6.Send, "sim: backoff 3 expired triggers attempt 4");
            simState.RecordAttempt(true, "TRIG-1", backoffRule, t5);
            failures += AssertEqual(4, simState.AttemptCount, "sim: attempt count is 4 (max)");

            // Step 8: Next poll with same event after max attempts reached -> RecoveryExhausted
            DateTime t6 = t5.AddSeconds(2);
            RetryDecision d7 = simState.Observe(trigObs, backoffRule, t6);
            failures += AssertEqual(false, d7.Send, "sim: max attempts reached blocks send");
            failures += AssertEqual(RecoveryState.RecoveryExhausted, simState.State, "sim: state is RecoveryExhausted");

            // Step 9: Genuinely new trigger event arrives -> resets attempts and starts new recovery
            var newTrigObs = new RuleObservation { Triggered = true, Ready = true, Busy = false, TriggerToken = "TRIG-NEW-2", DueUtc = t6.AddSeconds(10) };
            RetryDecision d8 = simState.Observe(newTrigObs, backoffRule, t6);
            failures += AssertEqual(RecoveryState.EventWaitingDeadline, simState.State, "sim: new event resets exhausted state");
            failures += AssertEqual(0, simState.AttemptCount, "sim: new event resets attempt count to 0");

            // Step 10: Full success recovery path
            DateTime t7 = t6.AddSeconds(10);
            RetryDecision d9 = simState.Observe(newTrigObs, backoffRule, t7);
            failures += AssertEqual(true, d9.Send, "sim: new event fires send");
            simState.RecordAttempt(true, "TRIG-NEW-2", backoffRule, t7);

            // Target becomes busy
            var busyObs = new RuleObservation { Triggered = true, Ready = false, Busy = true, TriggerToken = "TRIG-NEW-2" };
            simState.Observe(busyObs, backoffRule, t7.AddSeconds(2));
            failures += AssertEqual(RecoveryState.TargetBusyOrProgressing, simState.State, "sim: observed busy progress");

            // Target completes and clears trigger -> RecoveryConfirmed
            var clearObs = new RuleObservation { Triggered = false, Ready = true, Busy = false };
            simState.Observe(clearObs, backoffRule, t7.AddSeconds(15));
            failures += AssertEqual(RecoveryState.RecoveryConfirmed, simState.State, "sim: trigger cleared confirms recovery");
            failures += AssertEqual("TRIG-NEW-2", simState.SuppressedToken, "sim: old trigger token suppressed");

            // Stale trigger reappears in scrollback -> suppressed
            RetryDecision d10 = simState.Observe(newTrigObs, backoffRule, t7.AddSeconds(20));
            failures += AssertEqual(false, d10.Send, "sim: stale trigger in scrollback suppressed");

            var failedWriteState = new RetrySessionState();
            DateTime failedWriteClock = t0;
            var failedWriteObservation = new RuleObservation { Triggered = true, Ready = true, Busy = false, TriggerToken = "WRITE-FAIL", DueUtc = t0 };
            for (int attempt = 0; attempt < backoffRule.SafeMaximumAttemptsPerEvent; attempt++)
            {
                RetryDecision failedWriteDecision = failedWriteState.Observe(failedWriteObservation, backoffRule, failedWriteClock);
                failures += AssertEqual(true, failedWriteDecision.Send, "native write failure attempt " + (attempt + 1) + " reached write boundary");
                failedWriteState.RecordAttempt(false, true, "WRITE-FAIL", backoffRule, failedWriteClock);
                RetryDecision adjacentDecision = failedWriteState.Observe(failedWriteObservation, backoffRule, failedWriteClock.AddSeconds(1));
                failures += AssertEqual(false, adjacentDecision.Send, "native write failure attempt " + (attempt + 1) + " cannot tight-loop");
                failedWriteClock = failedWriteState.NextAttemptUtc;
            }
            RetryDecision exhaustedWriteFailures = failedWriteState.Observe(failedWriteObservation, backoffRule, failedWriteClock);
            failures += AssertEqual(false, exhaustedWriteFailures.Send, "repeated native write failures are bounded");
            failures += AssertEqual(RecoveryState.RecoveryExhausted, failedWriteState.State, "native write failures reach recovery exhausted");

            // Wave 4: Configuration Validation Hardening Tests
            string configTestDir = Path.Combine(Path.GetTempPath(), "SAICONT-cfg-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(configTestDir);
            try
            {
                // Test duplicate target name rejection
                string dupNameXml = Path.Combine(configTestDir, "dup.xml");
                File.WriteAllText(dupNameXml, @"<saicont pollIntervalMilliseconds=""2000"">
  <logging path=""test.log"" maxBytes=""1048576"" retainedFiles=""5"" duplicateWindowSeconds=""60"" />
  <targets>
    <target name=""dup-rule"" enabled=""true"" command=""cc"" scanLines=""180"" maximumTriggerDistanceLines=""150"" initialDelaySeconds=""60"" retryIntervalSeconds=""60"" parseRetryTime=""false"">
      <processNames><process>cline</process></processNames>
      <triggerPatterns><pattern>(?i)rate limit</pattern></triggerPatterns>
      <readyPatterns><pattern>^.*Ask.*$</pattern></readyPatterns>
      <busyPatterns />
    </target>
    <target name=""dup-rule"" enabled=""true"" command=""cc"" scanLines=""180"" maximumTriggerDistanceLines=""150"" initialDelaySeconds=""60"" retryIntervalSeconds=""60"" parseRetryTime=""false"">
      <processNames><process>cline</process></processNames>
      <triggerPatterns><pattern>(?i)rate limit</pattern></triggerPatterns>
      <readyPatterns><pattern>^.*Ask.*$</pattern></readyPatterns>
      <busyPatterns />
    </target>
  </targets>
</saicont>");
                bool dupRejected = false;
                try { WatcherConfiguration.Load(dupNameXml); } catch (FormatException) { dupRejected = true; }
                failures += AssertEqual(true, dupRejected, "duplicate target name rejected");

                // Test oversized command rejection (>512 chars)
                string longCmdXml = Path.Combine(configTestDir, "longcmd.xml");
                string oversizedCmd = new string('x', 513);
                File.WriteAllText(longCmdXml, @"<saicont pollIntervalMilliseconds=""2000"">
  <logging path=""test.log"" maxBytes=""1048576"" retainedFiles=""5"" duplicateWindowSeconds=""60"" />
  <targets>
    <target name=""long-rule"" enabled=""true"" command=""" + oversizedCmd + @""" scanLines=""180"" maximumTriggerDistanceLines=""150"" initialDelaySeconds=""60"" retryIntervalSeconds=""60"" parseRetryTime=""false"">
      <processNames><process>cline</process></processNames>
      <triggerPatterns><pattern>(?i)rate limit</pattern></triggerPatterns>
      <readyPatterns><pattern>^.*Ask.*$</pattern></readyPatterns>
      <busyPatterns />
    </target>
  </targets>
</saicont>");
                bool longCmdRejected = false;
                try { WatcherConfiguration.Load(longCmdXml); } catch (FormatException) { longCmdRejected = true; }
                failures += AssertEqual(true, longCmdRejected, "oversized command rejected");

                // Test empty process names rejection
                string emptyProcXml = Path.Combine(configTestDir, "emptyproc.xml");
                File.WriteAllText(emptyProcXml, @"<saicont pollIntervalMilliseconds=""2000"">
  <logging path=""test.log"" maxBytes=""1048576"" retainedFiles=""5"" duplicateWindowSeconds=""60"" />
  <targets>
    <target name=""empty-proc-rule"" enabled=""true"" command=""cc"" scanLines=""180"" maximumTriggerDistanceLines=""150"" initialDelaySeconds=""60"" retryIntervalSeconds=""60"" parseRetryTime=""false"">
      <processNames />
      <triggerPatterns><pattern>(?i)rate limit</pattern></triggerPatterns>
      <readyPatterns><pattern>^.*Ask.*$</pattern></readyPatterns>
      <busyPatterns />
    </target>
  </targets>
</saicont>");
                bool emptyProcRejected = false;
                try { WatcherConfiguration.Load(emptyProcXml); } catch (FormatException) { emptyProcRejected = true; }
                failures += AssertEqual(true, emptyProcRejected, "empty process names rejected");

                // Test read-only validate-config mode
                string validXml = Path.Combine(configTestDir, "valid.xml");
                File.WriteAllText(validXml, @"<saicont pollIntervalMilliseconds=""2000"">
  <logging path=""test.log"" maxBytes=""1048576"" retainedFiles=""5"" duplicateWindowSeconds=""60"" />
  <targets>
    <target name=""valid-rule"" enabled=""true"" command=""cc"" scanLines=""180"" maximumTriggerDistanceLines=""150"" initialDelaySeconds=""60"" retryIntervalSeconds=""60"" parseRetryTime=""false"">
      <processNames><process>cline</process></processNames>
      <triggerPatterns><pattern>(?i)rate limit</pattern></triggerPatterns>
      <readyPatterns><pattern>^.*Ask.*$</pattern></readyPatterns>
      <busyPatterns />
    </target>
  </targets>
</saicont>");
                WatcherConfiguration validCfg = WatcherConfiguration.Load(validXml);
                int validateResult = RunValidateConfig(validCfg, validXml);
                failures += AssertEqual(0, validateResult, "validate-config exits 0 on valid configuration");

                string validConfigText = File.ReadAllText(validXml);
                var invalidConfigCases = new Dictionary<string, string>
                {
                    { "unknown XML attribute rejected", validConfigText.Replace("<saicont pollIntervalMilliseconds=\"2000\">", "<saicont pollIntervalMilliseconds=\"2000\" mystery=\"x\">") },
                    { "trigger distance beyond scan lines rejected", validConfigText.Replace("maximumTriggerDistanceLines=\"150\"", "maximumTriggerDistanceLines=\"181\"") },
                    { "invalid optional backoff rejected", validConfigText.Replace("parseRetryTime=\"false\"", "parseRetryTime=\"false\" backoffMultiplier=\"0\"") },
                    { "unsafe process basename rejected", validConfigText.Replace("<process>cline</process>", "<process>..\\cline</process>") },
                    { "multiline command rejected", validConfigText.Replace("command=\"cc\"", "command=\"cc&#10;evil\"") },
                    { "malformed regex rejected with configuration error", validConfigText.Replace("(?i)rate limit", "[") },
                    { "unknown XML element rejected", validConfigText.Replace("<busyPatterns />", "<busyPatterns /><mystery />") }
                };
                int invalidCaseIndex = 0;
                foreach (var invalidCase in invalidConfigCases)
                {
                    string invalidPath = Path.Combine(configTestDir, "invalid-" + invalidCaseIndex + ".xml");
                    File.WriteAllText(invalidPath, invalidCase.Value);
                    bool rejected = false;
                    try { WatcherConfiguration.Load(invalidPath); } catch (FormatException) { rejected = true; }
                    failures += AssertEqual(true, rejected, invalidCase.Key);
                    invalidCaseIndex++;
                }
            }
            finally
            {
                if (Directory.Exists(configTestDir))
                {
                    Directory.Delete(configTestDir, true);
                }
            }

            // Wave 4: Pathological regex / timeout defense
            var timeoutRule = new TargetRule
            {
                Name = "timeout-test",
                Enabled = true,
                ProcessNames = new[] { "cline" },
                Command = "cc",
                ScanLines = 180,
                MaximumTriggerDistanceLines = 150,
                InitialDelaySeconds = 10,
                RetryIntervalSeconds = 10,
                ParseRetryTime = false,
                TriggerPatterns = new[] { @"^(([a-z])+.)+[A-Z]([a-z])+$" }, // Catastrophic backtracking pattern
                ReadyPatterns = new[] { "^.*Ask.*$" },
                BusyPatterns = new string[0]
            };
            timeoutRule.CompileRegexes();
            var pathologicalSnapshot = new ConsoleSnapshot
            {
                Text = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!",
                CursorLine = "Ask anything",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation timeoutObs = RuleMatcher.Inspect(timeoutRule, pathologicalSnapshot, start);
            // On catastrophic regex timeout, RuleMatcher must fail-closed (Busy=true, Triggered=false, Ready=false) without throwing or crashing
            failures += AssertEqual(false, timeoutObs.Triggered, "pathological regex fails closed (not triggered)");
            failures += AssertEqual(true, timeoutObs.Busy, "pathological regex fails closed (marked busy)");
            failures += AssertEqual("regex_timeout", timeoutObs.EvaluationError, "pathological regex proves timeout path executed");

            // Wave 4: Rule Fixtures (Positive & Near-Miss Negative)
            TargetRule clineRule = WatcherConfiguration.CreateTestSample().Targets[1];
            TargetRule codexRule = WatcherConfiguration.CreateTestSample().Targets[0];

            // Cline OpenRouter 429 positive fixture
            var cline429Snapshot = new ConsoleSnapshot
            {
                Text = "generate_stream from OpenRouter: failed to invoke model openai/gpt-4o: Provider returned error: {\"error\":{\"code\":429,\"message\":\"Rate limit reached\"}}\nAsk anything...",
                CursorLine = "Ask anything...",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation c429Obs = RuleMatcher.Inspect(clineRule, cline429Snapshot, start);
            failures += AssertEqual(true, c429Obs.Triggered, "fixture: cline openrouter 429 triggered");
            failures += AssertEqual(true, c429Obs.Ready, "fixture: cline openrouter 429 ready");

            // Cline 429 near-miss negative fixture (429 appears in user file or git commit log, not provider error)
            var clineNegativeSnapshot = new ConsoleSnapshot
            {
                Text = "Compiled src/error_handler.cs: HTTP response status 429 is handled in line 42.\nWhat can I do for you?",
                CursorLine = "What can I do for you?",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation cNegObs = RuleMatcher.Inspect(clineRule, clineNegativeSnapshot, start);
            failures += AssertEqual(false, cNegObs.Triggered, "fixture: cline unrelated 429 not triggered");

            // Codex usage limit positive fixture
            var codexUsageSnapshot = new ConsoleSnapshot
            {
                Text = "You've hit your usage limit. Try again at 4:40 PM.\n› Ask Codex to do anything",
                CursorLine = "› Ask Codex to do anything",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation codexUsageObs = RuleMatcher.Inspect(codexRule, codexUsageSnapshot, start);
            failures += AssertEqual(true, codexUsageObs.Triggered, "fixture: codex usage limit triggered");
            failures += AssertEqual(true, codexUsageObs.Ready, "fixture: codex prompt ready");

            var codexLimitVariants = new[]
            {
                "You've hit your usage limit.\n> Ask Codex to do anything",
                "You've reached your usage limit.\n› Ask Codex to do anything",
                "Usage limit reached. Try again later.\n> Ask Codex to do anything",
                "Rate limit exceeded.\n> Ask Codex to do anything",
                "You've hit your usage limit. Try again at 12:42 PM.\n> Ask Codex to do anything",
                "You've hit your usage limit.\ncc\n"
            };
            for (int variantIndex = 0; variantIndex < codexLimitVariants.Length; variantIndex++)
            {
                string[] variantLines = codexLimitVariants[variantIndex].Split(new[] { '\n' }, StringSplitOptions.None);
                var variantSnapshot = new ConsoleSnapshot
                {
                    Text = codexLimitVariants[variantIndex],
                    CursorLine = variantLines[variantLines.Length - 1],
                    StartRow = 0,
                    CursorRow = variantLines.Length - 1
                };
                RuleObservation variantObservation = RuleMatcher.Inspect(codexRule, variantSnapshot, start);
                failures += AssertEqual(true, variantObservation.Triggered, "fixture: codex limit variant " + variantIndex + " triggered");
                failures += AssertEqual(true, variantObservation.Ready, "fixture: codex limit variant " + variantIndex + " ready");
            }

            var codexNegSnapshot = new ConsoleSnapshot
            {
                Text = "The server usage limit is monitored by Prometheus metric.\n› Ask Codex to do anything",
                CursorLine = "› Ask Codex to do anything",
                StartRow = 0,
                CursorRow = 1
            };
            RuleObservation codexNegObs = RuleMatcher.Inspect(codexRule, codexNegSnapshot, start);
            failures += AssertEqual(false, codexNegObs.Triggered, "fixture: codex unrelated usage text not triggered");

            // Wave 5: Named Mutex and Single-Instance Lock Tests
            bool firstCreated;
            string testCfgPath = Path.Combine(Path.GetTempPath(), "test-cfg-" + Guid.NewGuid().ToString("N") + ".xml");
            using (var mutex1 = AcquireInstanceMutex(testCfgPath, out firstCreated))
            {
                failures += AssertEqual(true, firstCreated, "first mutex acquisition succeeds as new");
                bool secondCreated;
                using (var mutex2 = AcquireInstanceMutex(testCfgPath, out secondCreated))
                {
                    failures += AssertEqual(false, secondCreated, "second mutex acquisition detects existing instance");
                }
            }

            // Wave 5: Instance Record & Tokenized Stop Tests
            string lifecycleDir = Path.Combine(Path.GetTempPath(), "SAICONT-life-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(lifecycleDir);
            try
            {
                string instFile = Path.Combine(lifecycleDir, "SAICONT.instance.xml");
                string testToken = "TEST-TOKEN-12345";
                DateTime testStart = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
                string instanceWriteError;
                bool instanceWritten = TryWriteInstanceFile(instFile, 1234, testStart, "--watch", "V:\\bin\\SAICONT & test.exe", testToken, out instanceWriteError);
                failures += AssertEqual(true, instanceWritten, "instance.xml atomic write succeeded");
                failures += AssertEqual(true, File.Exists(instFile), "instance.xml file written");
                string instXmlContent = File.ReadAllText(instFile);
                failures += AssertEqual(true, instXmlContent.IndexOf("<instanceToken>TEST-TOKEN-12345</instanceToken>", StringComparison.Ordinal) >= 0, "instance.xml contains token");
                failures += AssertEqual(true, instXmlContent.IndexOf("<pid>1234</pid>", StringComparison.Ordinal) >= 0, "instance.xml contains pid");
                failures += AssertEqual(true, instXmlContent.IndexOf("SAICONT &amp; test.exe", StringComparison.Ordinal) >= 0, "instance.xml escapes executable path");

                // Test tokenized stop predicate
                string stopFile = Path.Combine(lifecycleDir, "SAICONT.stop");
                File.WriteAllText(stopFile, "FOREIGN-TOKEN-999");
                bool foreignStop = String.Equals(File.ReadAllText(stopFile).Trim(), testToken, StringComparison.Ordinal);
                failures += AssertEqual(false, foreignStop, "foreign stop token does not match instance");

                File.WriteAllText(stopFile, testToken);
                bool matchingStop = String.Equals(File.ReadAllText(stopFile).Trim(), testToken, StringComparison.Ordinal);
                failures += AssertEqual(true, matchingStop, "matching stop token accepted");
            }
            finally
            {
                if (Directory.Exists(lifecycleDir))
                {
                    Directory.Delete(lifecycleDir, true);
                }
            }

            // Wave 6: Accelerated Soak & Scaled Engine Simulation
            int totalSoakSends = 0;
            DateTime simClock = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc);
            var soakConfig = WatcherConfiguration.CreateTestSample();
            int scenarioStep = 0;

            Func<IList<ProcessEntry>> soakSnapshot = delegate
            {
                return new[]
                {
                    new ProcessEntry { Id = 5001, ParentId = 1000, Name = "codex" }
                };
            };

            ConsoleReadAttempt soakReader = delegate(int pid, int lineCount, out ConsoleSnapshot s, out string err)
            {
                err = null;
                // Cycle of 30 steps (10s each = 300s per cycle):
                // Step 0-4 (50s): idle
                // Step 5-7 (30s): usage limit -> sends at step 7 (reset time reached)
                // Step 8-15 (80s): busy progressing (Working 3s...) -> transitions to TargetBusyOrProgressing
                // Step 16-29 (140s): recovered/cleared -> confirms recovery and suppresses old token
                int phase = scenarioStep % 30;
                int soakCycle = scenarioStep / 30;
                // Generate a consistent reset time for this cycle at step 7 (70s from cycle base)
                DateTime cycleBase = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc).AddSeconds(soakCycle * 300);
                DateTime resetTime = cycleBase.AddSeconds(60);
                string timeStr = resetTime.ToLocalTime().ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

                if (phase < 5)
                {
                    s = new ConsoleSnapshot { Text = "› Ask Codex to do anything\n", CursorLine = "› Ask Codex to do anything", StartRow = 0, CursorRow = 1, ProcessId = pid, ConsoleProcessIds = new[] { pid }, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent };
                }
                else if (phase < 7)
                {
                    s = new ConsoleSnapshot { Text = "You've hit your usage limit. Try again at " + timeStr + ".\n› Ask Codex to do anything\n", CursorLine = "› Ask Codex to do anything", StartRow = 0, CursorRow = 2, ProcessId = pid, ConsoleProcessIds = new[] { pid }, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent };
                }
                else if (phase < 16)
                {
                    s = new ConsoleSnapshot { Text = "Working (3s)...\n› Working (3s)...\n", CursorLine = "› Working (3s)...", StartRow = 0, CursorRow = 1, ProcessId = pid, ConsoleProcessIds = new[] { pid }, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent };
                }
                else
                {
                    s = new ConsoleSnapshot { Text = "All tasks completed.\n› Ask Codex to do anything\n", CursorLine = "› Ask Codex to do anything", StartRow = 0, CursorRow = 2, ProcessId = pid, ConsoleProcessIds = new[] { pid }, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent };
                }
                return true;
            };

            VerifiedConsoleWriter soakWriter = delegate(ResolvedConsoleSession sess, ProcessSessionIdentity expected, string cmd, out string err)
            {
                err = null;
                totalSoakSends++;
                return NativeWriteOutcome.CompleteInputCommitted;
            };

            var soakEngine = new WatcherEngine(
                soakConfig,
                soakSnapshot,
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc) }; },
                soakReader,
                soakWriter,
                delegate { return simClock; },
                null);

            const int SoakPolls = 100000;
            long memoryBeforeSoak = GC.GetTotalMemory(true);
            Stopwatch soakTimer = Stopwatch.StartNew();
            for (int poll = 0; poll < SoakPolls; poll++)
            {
                scenarioStep = poll;
                simClock = simClock.AddSeconds(10); // Advance clock 10s per poll
                soakEngine.PollOnce(true);
            }
            soakTimer.Stop();
            long memoryAfterSoak = GC.GetTotalMemory(true);

            // 100,000 polls contain 3,333 full cycles plus the send in the final partial cycle.
            failures += AssertEqual(3334, totalSoakSends, "accelerated soak: exact sends in 100000 simulated polls");
            failures += AssertEqual(true, soakEngine.SessionStateCount <= 1, "accelerated soak keeps watcher session state bounded");
            failures += AssertEqual(true, memoryAfterSoak - memoryBeforeSoak < 16L * 1024L * 1024L, "accelerated soak managed-memory growth stays below 16 MiB");
            Console.WriteLine("MEASURE: soak polls=" + SoakPolls + " elapsed_ms=" + soakTimer.ElapsedMilliseconds + " managed_delta_bytes=" + (memoryAfterSoak - memoryBeforeSoak));
            // PERF-010: Enhanced performance gates — controlled scenario budgets.

            // Gate 1: Poll-cycle timing. A single idle poll (no target found)
            // must complete well within the 2-second production cadence.
            {
                var perfConfig = new WatcherConfiguration
                {
                    PollIntervalMilliseconds = 2000,
                    Targets = new List<TargetRule>
                    {
                        new TargetRule
                        {
                            Name = "perf-idle",
                            Enabled = true,
                            ProcessNames = new[] { "saicont-no-such-process" },
                            Command = "cc",
                            ScanLines = 180,
                            MaximumTriggerDistanceLines = 150,
                            InitialDelaySeconds = 60,
                            RetryIntervalSeconds = 60,
                            ParseRetryTime = false,
                            TriggerPatterns = new[] { @"(?i)never-match" },
                            ReadyPatterns = new[] { @"^ready$" },
                            BusyPatterns = new string[0]
                        }
                    }
                };
                var perfEngine = new WatcherEngine(
                    perfConfig,
                    delegate { return new List<ProcessEntry>(); },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = DateTime.MinValue }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "no real console"; return false; },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                    delegate { return DateTime.UtcNow; });

                const int PerfPolls = 1000;
                Stopwatch perfTimer = Stopwatch.StartNew();
                for (int i = 0; i < PerfPolls; i++)
                {
                    perfEngine.PollOnce(false);
                }
                perfTimer.Stop();
                double avgPollMs = (double)perfTimer.ElapsedMilliseconds / PerfPolls;
                failures += AssertEqual(true, avgPollMs < 50.0, "PERF-010: average idle poll < 50ms (actual=" + avgPollMs.ToString("F2") + "ms)");
                Console.WriteLine("MEASURE: perf_poll avg_ms=" + avgPollMs.ToString("F2") + " total_ms=" + perfTimer.ElapsedMilliseconds);
            }

            // Gate 2: Multi-rule scaling. Five rules targeting the same process
            // must produce exactly five writes and keep state bounded.
            {
                var multiRuleTargets = new List<TargetRule>();
                for (int r = 0; r < 5; r++)
                {
                    multiRuleTargets.Add(new TargetRule
                    {
                        Name = "multi-" + r,
                        Enabled = true,
                        ProcessNames = new[] { "codex" },
                        Command = "cc",
                        ScanLines = 180,
                        MaximumTriggerDistanceLines = 150,
                        InitialDelaySeconds = 5,
                        RetryIntervalSeconds = 10,
                        ParseRetryTime = false,
                        TriggerPatterns = new[] { @"(?i)rate limited" },
                        ReadyPatterns = new[] { @"^.*Ask.*$" },
                        BusyPatterns = new[] { @"(?i)working" }
                    });
                }
                var multiConfig = new WatcherConfiguration
                {
                    PollIntervalMilliseconds = 2000,
                    Targets = multiRuleTargets
                };

                int multiWriteCount = 0;
                DateTime multiClock = new DateTime(2026, 8, 26, 10, 0, 0, DateTimeKind.Utc);
                var multiEngine = new WatcherEngine(
                    multiConfig,
                    delegate { return new[] { new ProcessEntry { Id = 9001, ParentId = 100, Name = "codex" } }; },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc) }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e)
                    {
                        s = new ConsoleSnapshot
                        {
                            ProcessId = pid,
                            Text = "Rate limited\nAsk anything",
                            CursorLine = "Ask anything",
                            ConsoleProcessIds = new[] { pid },
                            StartRow = 0,
                            CursorRow = 1,
                            MembershipStatus = ConsoleMembershipStatus.VerifiedPresent
                        };
                        e = null;
                        return true;
                    },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e)
                    {
                        multiWriteCount++;
                        e = null;
                        return NativeWriteOutcome.CompleteInputCommitted;
                    },
                    delegate { return multiClock; });

                // First poll: triggers detected, initial delay starts.
                multiEngine.PollOnce(true);
                // Second poll: delay elapsed, sends fire.
                multiClock = multiClock.AddSeconds(10);
                multiEngine.PollOnce(true);

                failures += AssertEqual(5, multiWriteCount, "PERF-010: five rules produce exactly five writes");
                failures += AssertEqual(true, multiEngine.SessionStateCount <= 5, "PERF-010: multi-rule state bounded by rule count");
                Console.WriteLine("MEASURE: perf_multi rules=5 writes=" + multiWriteCount + " states=" + multiEngine.SessionStateCount);
            }

            // Gate 3: Memory scaling. A 10,000-poll idle run with a larger
            // process snapshot must not leak managed memory.
            {
                var bigSnapshot = new List<ProcessEntry>();
                for (int p = 0; p < 500; p++)
                {
                    bigSnapshot.Add(new ProcessEntry { Id = 10000 + p, ParentId = 1, Name = "process-" + p + ".exe" });
                }
                var memConfig = new WatcherConfiguration
                {
                    PollIntervalMilliseconds = 2000,
                    Targets = new List<TargetRule>
                    {
                        new TargetRule
                        {
                            Name = "mem-idle",
                            Enabled = true,
                            ProcessNames = new[] { "saicont-no-such-process" },
                            Command = "cc",
                            ScanLines = 180,
                            MaximumTriggerDistanceLines = 150,
                            InitialDelaySeconds = 60,
                            RetryIntervalSeconds = 60,
                            ParseRetryTime = false,
                            TriggerPatterns = new[] { @"(?i)never-match" },
                            ReadyPatterns = new[] { @"^ready$" },
                            BusyPatterns = new string[0]
                        }
                    }
                };
                var memEngine = new WatcherEngine(
                    memConfig,
                    delegate { return bigSnapshot; },
                    delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = DateTime.MinValue }; },
                    delegate(int pid, int lineCount, out ConsoleSnapshot s, out string e) { s = null; e = "no real console"; return false; },
                    delegate(ResolvedConsoleSession sess, ProcessSessionIdentity exp, string cmd, out string e) { e = null; return NativeWriteOutcome.NoInputCommitted; },
                    delegate { return DateTime.UtcNow; });

                const int MemPolls = 10000;
                GC.Collect();
                long memBefore = GC.GetTotalMemory(true);
                Stopwatch memTimer = Stopwatch.StartNew();
                for (int i = 0; i < MemPolls; i++)
                {
                    memEngine.PollOnce(false);
                }
                memTimer.Stop();
                GC.Collect();
                long memAfter = GC.GetTotalMemory(true);
                long memDelta = memAfter - memBefore;
                failures += AssertEqual(true, memDelta < 8L * 1024L * 1024L, "PERF-010: 10K-poll memory growth < 8 MiB (actual=" + memDelta + " bytes)");
                Console.WriteLine("MEASURE: perf_mem polls=" + MemPolls + " elapsed_ms=" + memTimer.ElapsedMilliseconds + " managed_delta_bytes=" + memDelta);
            }


            // Wave 7: XML Parser DTD Prohibited Security Test
            string dtdTestDir = Path.Combine(Path.GetTempPath(), "SAICONT-dtd-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dtdTestDir);
            try
            {
                string dtdXml = Path.Combine(dtdTestDir, "dtd.xml");
                File.WriteAllText(dtdXml, "<!DOCTYPE saicont [ <!ENTITY ext SYSTEM \"http://127.0.0.1/evil\"> ]>\n<saicont pollIntervalMilliseconds=\"2000\">\n  <logging path=\"test.log\" maxBytes=\"1048576\" retainedFiles=\"5\" duplicateWindowSeconds=\"60\" />\n  <targets />\n</saicont>");
                bool dtdBlocked = false;
                try
                {
                    WatcherConfiguration.Load(dtdXml);
                }
                catch (Exception)
                {
                    dtdBlocked = true;
                }
                failures += AssertEqual(true, dtdBlocked, "security: XML DTD processing prohibited");
            }
            finally
            {
                if (Directory.Exists(dtdTestDir))
                {
                    Directory.Delete(dtdTestDir, true);
                }
            }

            // Wave 7: Pre-Send Stop Abort Test
            bool stopWriterCalled = false;
            var stopTestConfig = new WatcherConfiguration
            {
                PollIntervalMilliseconds = 2000,
                Targets = new List<TargetRule>
                {
                    new TargetRule
                    {
                        Name = "stop-test-rule",
                        Enabled = true,
                        ProcessNames = new[] { "codex" },
                        Command = "cc",
                        ScanLines = 180,
                        MaximumTriggerDistanceLines = 150,
                        InitialDelaySeconds = 10,
                        RetryIntervalSeconds = 10,
                        ParseRetryTime = false,
                        TriggerPatterns = new[] { @"(?i)usage limit" },
                        ReadyPatterns = new[] { @"^\s*›\s*Ask Codex to do anything\s*$" },
                        BusyPatterns = new string[0]
                    }
                }
            };
            stopTestConfig.Targets[0].CompileRegexes();

            DateTime stopClock = start;
            var stopTestEngine = new WatcherEngine(
                stopTestConfig,
                soakSnapshot,
                delegate(int pid, string name) { return new ProcessSessionIdentity { ProcessId = pid, ProcessName = name, StartTimeUtc = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc) }; },
                delegate(int pid, int lineCount, out ConsoleSnapshot s, out string err)
                {
                    err = null;
                    s = new ConsoleSnapshot { Text = "You've hit your usage limit.\n› Ask Codex to do anything\n", CursorLine = "› Ask Codex to do anything", StartRow = 0, CursorRow = 2, ProcessId = pid, ConsoleProcessIds = new[] { pid }, MembershipStatus = ConsoleMembershipStatus.VerifiedPresent };
                    return true;
                },
                delegate(ResolvedConsoleSession sess, ProcessSessionIdentity expected, string cmd, out string err)
                {
                    err = null;
                    stopWriterCalled = true;
                    return NativeWriteOutcome.CompleteInputCommitted;
                },
                delegate { return stopClock; },
                null);

            // First poll at t=0: initial discovery records event and sets nextAllowed = start + 10s
            stopTestEngine.PollOnce(true, delegate { return false; });

            // Advance clock past initial delay
            stopClock = start.AddSeconds(20);

            // Second poll at t=20s with shouldStop returning true: send is due, but stop request aborts write
            IList<PollResult> stopResults = stopTestEngine.PollOnce(true, delegate { return true; });
            failures += AssertEqual(false, stopWriterCalled, "pre-send stop request aborted write execution");
            failures += AssertEqual(1, stopResults.Count, "stop test has 1 poll result");
            if (stopResults.Count > 0)
            {
                failures += AssertEqual("send_blocked=stop_requested", stopResults[0].Reason, "poll result recorded stop_requested reason");
            }

            string temporaryDirectory = Path.Combine(Path.GetTempPath(), "SAICONT-self-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string logPath = Path.Combine(temporaryDirectory, "rotate.log");
                var testLog = new OperationalLog(logPath, 256, 2, 1);
                for (int index = 0; index < 8; index++)
                {
                    testLog.Write("TEST", new string('x', 96));
                }
                failures += AssertEqual(true, File.Exists(logPath), "rotating log keeps active file");
                failures += AssertEqual(true, File.Exists(logPath + ".1"), "rotating log keeps backup");

                // Test hard-bounded deduplication cache with many distinct keys
                for (int index = 0; index < 500; index++)
                {
                    testLog.TryWriteDeduplicated("key-" + index, "TEST", "dedup message");
                }
                failures += AssertEqual(true, File.Exists(logPath), "deduplication map handled 500 distinct keys with pruning");
                failures += AssertEqual(true, testLog.DeduplicationEntryCount <= OperationalLog.MaximumDeduplicationEntries, "deduplication map has hard entry bound");
                using (var lockedLog = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    failures += AssertEqual(false, testLog.TryWrite("TEST", "must fail while locked"), "locked log failure is explicit and non-throwing");
                }
                RuntimeOptions guiOpts;
                string guiErr;
                failures += AssertEqual(false, TryParseOptions(new string[0], out guiOpts, out guiErr), "parse empty args returns false");
                failures += AssertEqual(true, TryParseOptions(new[] { "--gui" }, out guiOpts, out guiErr), "parse --gui option");
                failures += AssertEqual("--gui", guiOpts.Mode, "--gui option mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "--tui" }, out guiOpts, out guiErr), "parse --tui option");
                failures += AssertEqual("--gui", guiOpts.Mode, "--tui alias mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "-g" }, out guiOpts, out guiErr), "parse -g option");
                failures += AssertEqual("--gui", guiOpts.Mode, "-g alias mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "--app" }, out guiOpts, out guiErr), "parse --app option");
                failures += AssertEqual("--app", guiOpts.Mode, "--app option mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "--win-gui" }, out guiOpts, out guiErr), "parse --win-gui option");
                failures += AssertEqual("--app", guiOpts.Mode, "--win-gui alias mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "--window" }, out guiOpts, out guiErr), "parse --window option");
                failures += AssertEqual("--app", guiOpts.Mode, "--window alias mode");
                failures += AssertEqual(true, TryParseOptions(new[] { "--terminal" }, out guiOpts, out guiErr), "parse --terminal option");
                failures += AssertEqual("--terminal", guiOpts.Mode, "--terminal option mode");

                // Test TUI poll result formatting
                var samplePoll = new PollResult
                {
                    Target = "tui-test",
                    ProcessId = 1234,
                    AttachProcessId = 1234,
                    Title = "Test Window",
                    Read = true,
                    Ready = true,
                    Triggered = false,
                    Reason = "prompt ready"
                };
                string formatted = TerminalUi.FormatPollResult(samplePoll);
                failures += AssertEqual(true, formatted.Contains("MATCH target=tui-test"), "TUI formatted poll result contains match");
                failures += AssertEqual(true, formatted.Contains("pid=1234"), "TUI formatted poll result contains PID");
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, true);
                }
            }

            // Reliability fail-safe tests: crash report persistence and interrupt flag.
            string crashDirectory = Path.Combine(Path.GetTempPath(), "saicont-selftest-crash-" + Guid.NewGuid().ToString("N"));
            string crashPath = Path.Combine(crashDirectory, "crash.log");
            _crashReportPathOverride = crashPath;
            try
            {
                TryWriteCrashReport("selftest headline", "boom-marker-details", false);
                string crashContent = File.Exists(crashPath) ? File.ReadAllText(crashPath) : String.Empty;
                failures += AssertEqual(true, crashContent.Contains("boom-marker-details"), "crash report persists details");
                failures += AssertEqual(true, crashContent.Contains("terminating=false"), "crash report records termination state");

                ResetInterruptForTests();
                failures += AssertEqual(false, CancelRequested, "interrupt flag defaults clear");
                cancelRequested = true;
                failures += AssertEqual(true, CancelRequested, "interrupt flag registers request");
                ResetInterruptForTests();
                failures += AssertEqual(false, CancelRequested, "interrupt flag resets clean");
            }
            finally
            {
                _crashReportPathOverride = null;
                ResetInterruptForTests();
                try
                {
                    if (Directory.Exists(crashDirectory))
                    {
                        Directory.Delete(crashDirectory, true);
                    }
                }
                catch { }
            }

            if (String.Equals(Environment.GetEnvironmentVariable("SAICONT_SELF_TEST_INJECT_FAILURE"), "1", StringComparison.Ordinal))
            {
                failures += AssertEqual(true, false, "injected negative control");
            }

            if (failures == 0)
            {
                Console.WriteLine("PASS: " + _selfTestCount + " self-tests");
                return 0;
            }

            Console.Error.WriteLine("FAIL: " + failures + " self-test(s)");
            return 1;
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
                return String.Format("ERROR target={0} pid={1} attach={2} error={3}", result.Target, result.ProcessId, result.AttachProcessId, Quote(result.Error));
            }

            return String.Format(
                "MATCH target={0} pid={1} attach={2} title={3} trigger={4} ready={5} busy={6} would_send={7} sent={8} next={9} reason={10}",
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
                Quote(result.Reason));
        }
    }
}
