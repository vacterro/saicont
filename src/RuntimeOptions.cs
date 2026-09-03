using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SaiCont
{
    internal static partial class Program
    {
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
            public string[] RuntimeResourceMutexKeys;
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
            var mutexKeys = new List<string>();
            for (int index = 0; index < options.RuntimeResourcePaths.Length; index++)
            {
                identities[index] = GetResourceIdentity(options.RuntimeResourcePaths[index]);
                // CORE-002: the canonical physical path is always the stable
                // ownership key. A file-object identity (hard-link alias) is
                // acquired additionally so aliases conflict, never instead of
                // the stable path key -- the lock namespace must not change
                // when a missing resource is created or replaced.
                mutexKeys.Add(options.RuntimeResourcePaths[index]);
                if (!String.Equals(identities[index], options.RuntimeResourcePaths[index], StringComparison.OrdinalIgnoreCase))
                {
                    mutexKeys.Add(identities[index]);
                }
            }
            options.RuntimeResourceIdentities = identities;
            options.RuntimeResourceMutexKeys = mutexKeys.ToArray();

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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

    }
}
