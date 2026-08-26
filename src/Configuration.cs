using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace SaiCont
{
    internal sealed class WatcherConfiguration
    {
        public int PollIntervalMilliseconds;
        public string LogFilePath;
        public long LogMaximumBytes;
        public int LogRetainedFiles;
        public int LogDuplicateWindowSeconds;
        public IList<TargetRule> Targets;

        public static WatcherConfiguration CreateTestSample()
        {
            var config = new WatcherConfiguration
            {
                PollIntervalMilliseconds = 2000,
                LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "SAICONT.log"),
                LogMaximumBytes = 1048576,
                LogRetainedFiles = 5,
                LogDuplicateWindowSeconds = 60,
                Targets = new List<TargetRule>
                {
                    new TargetRule
                    {
                        Name = "codex-usage-limit",
                        Enabled = true,
                        ProcessNames = new[] { "codex" },
                        Command = "cc",
                        ScanLines = 180,
                        MaximumTriggerDistanceLines = 150,
                        InitialDelaySeconds = 60,
                        RetryIntervalSeconds = 60,
                        ParseRetryTime = true,
                        BackoffMultiplier = 2.0,
                        MaximumRetryIntervalSeconds = 3600,
                        MaximumAttemptsPerEvent = 5,
                        TriggerPatterns = new[]
                        {
                            @"(?i)you.ve hit your usage limit",
                            @"(?i)hit your usage limit",
                            @"(?i)usage limit[^\r\n]{0,256}(?:try\s+)?again",
                            @"(?i)(?:try\s+)?again\s+at\s+[A-Za-z0-9]"
                        },
                        ReadyPatterns = new[]
                        {
                            @"^\s*›\s*Ask Codex to do anything\s*$",
                            @"^\s*>\s*$"
                        },
                        BusyPatterns = new[]
                        {
                            @"(?im)^\s*[›>]\s*Working\s*\("
                        }
                    },
                    new TargetRule
                    {
                        Name = "cline-limits",
                        Enabled = true,
                        ProcessNames = new[] { "cline" },
                        Command = "cc",
                        ScanLines = 180,
                        MaximumTriggerDistanceLines = 150,
                        InitialDelaySeconds = 60,
                        RetryIntervalSeconds = 60,
                        ParseRetryTime = true,
                        BackoffMultiplier = 2.0,
                        MaximumRetryIntervalSeconds = 3600,
                        MaximumAttemptsPerEvent = 5,
                        TriggerPatterns = new[]
                        {
                            @"(?i)generate_stream from OpenRouter:\s*failed to invoke model(?:[^\r\n]*\r?\n){0,4}[^\r\n]*\b429\b",
                            @"(?i)provider returned error[^\r\n]{0,512}\bcode\D{0,8}429\b",
                            @"(?i)temporarily rate[- ]limited upstream",
                            @"(?i)rate[- ]limited upstream",
                            @"(?i)daily free model limit reached(?:[^\r\n]*\r?\n){0,4}[^\r\n]*try again in",
                            @"(?i)reached today.s free usage limit(?:[^\r\n]*\r?\n){0,4}[^\r\n]*try again in"
                        },
                        ReadyPatterns = new[]
                        {
                            @"^.*Ask anything\.\.\.\s*$",
                            @"^.*What can I do for you\?\s*$",
                            @"^\s*\?\s*$"
                        },
                        BusyPatterns = new string[0]
                    }
                }
            };
            foreach (var t in config.Targets)
            {
                t.CompileRegexes();
            }
            return config;
        }

        public static WatcherConfiguration Load(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Configuration path is empty.", "path");
            }

            string fullPath = Path.GetFullPath(path);
            var document = new XmlDocument();
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using (var reader = XmlReader.Create(fullPath, settings))
            {
                document.Load(reader);
            }

            XmlElement root = document.DocumentElement;
            if (root == null || !String.Equals(root.Name, "saicont", StringComparison.Ordinal))
            {
                throw new FormatException("Configuration root must be <saicont>.");
            }
            ValidateAttributes(root, "pollIntervalMilliseconds");
            ValidateChildElements(root, "logging", "targets");

            var configuration = new WatcherConfiguration
            {
                PollIntervalMilliseconds = ReadInteger(root, "pollIntervalMilliseconds", 250, 3600000),
                Targets = new List<TargetRule>()
            };

            XmlElement logging = RequireChild(root, "logging");
            ValidateAttributes(logging, "path", "maxBytes", "retainedFiles", "duplicateWindowSeconds");
            ValidateChildElements(logging);
            string configuredLogPath = ReadString(logging, "path");
            configuration.LogFilePath = ResolvePath(Path.GetDirectoryName(fullPath), configuredLogPath);
            configuration.LogMaximumBytes = ReadLong(logging, "maxBytes", 4096, Int32.MaxValue);
            configuration.LogRetainedFiles = ReadInteger(logging, "retainedFiles", 1, 20);
            configuration.LogDuplicateWindowSeconds = ReadInteger(logging, "duplicateWindowSeconds", 1, 3600);

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            XmlElement targets = RequireChild(root, "targets");
            ValidateAttributes(targets);
            ValidateChildElements(targets, "target");
            foreach (XmlNode node in targets.ChildNodes)
            {
                XmlElement targetElement = node as XmlElement;
                if (targetElement == null || !String.Equals(targetElement.Name, "target", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = ReadString(targetElement, "name");
                if (!seenNames.Add(name))
                {
                    throw new FormatException("Duplicate target name: '" + name + "'.");
                }

                ValidateAttributes(
                    targetElement,
                    "name",
                    "enabled",
                    "command",
                    "scanLines",
                    "maximumTriggerDistanceLines",
                    "initialDelaySeconds",
                    "retryIntervalSeconds",
                    "parseRetryTime",
                    "backoffMultiplier",
                    "maxRetryIntervalSeconds",
                    "maxAttempts");
                ValidateChildElements(targetElement, "processNames", "triggerPatterns", "readyPatterns", "busyPatterns");

                var target = new TargetRule
                {
                    Name = name,
                    Enabled = ReadBoolean(targetElement, "enabled"),
                    Command = ReadString(targetElement, "command"),
                    ScanLines = ReadInteger(targetElement, "scanLines", 10, 5000),
                    MaximumTriggerDistanceLines = ReadInteger(targetElement, "maximumTriggerDistanceLines", 1, 5000),
                    InitialDelaySeconds = ReadInteger(targetElement, "initialDelaySeconds", 10, 86400),
                    RetryIntervalSeconds = ReadInteger(targetElement, "retryIntervalSeconds", 10, 86400),
                    ParseRetryTime = ReadBoolean(targetElement, "parseRetryTime"),
                    BackoffMultiplier = ReadOptionalDouble(targetElement, "backoffMultiplier", 2.0),
                    MaximumRetryIntervalSeconds = ReadOptionalInteger(targetElement, "maxRetryIntervalSeconds", 3600, 10, 86400),
                    MaximumAttemptsPerEvent = ReadOptionalInteger(targetElement, "maxAttempts", 5, 1, 50),
                    ProcessNames = ReadValues(targetElement, "processNames", "process", true),
                    TriggerPatterns = ReadValues(targetElement, "triggerPatterns", "pattern", true),
                    ReadyPatterns = ReadValues(targetElement, "readyPatterns", "pattern", true),
                    BusyPatterns = ReadValues(targetElement, "busyPatterns", "pattern", false)
                };

                ValidateTarget(target);
                configuration.Targets.Add(target);
            }

            if (configuration.Targets.Count == 0)
            {
                throw new FormatException("Configuration must contain at least one <target>.");
            }

            return configuration;
        }

        private static void ValidateTarget(TargetRule target)
        {
            if (!Regex.IsMatch(target.Name, @"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant))
            {
                throw new FormatException("Target name '" + target.Name + "' must contain only letters, digits, dot, underscore, or hyphen.");
            }
            if (target.Command.Length > 512)
            {
                throw new FormatException("Target '" + target.Name + "' command exceeds maximum length of 512.");
            }

            if (target.Command.IndexOf('\r') >= 0 || target.Command.IndexOf('\n') >= 0)
            {
                throw new FormatException("Target '" + target.Name + "' command must be one line.");
            }

            if (target.ProcessNames == null || target.ProcessNames.Length == 0)
            {
                throw new FormatException("Target '" + target.Name + "' must have at least one process name.");
            }

            foreach (string processName in target.ProcessNames)
            {
                string normalized = ProcessDiscovery.NormalizeName(processName);
                if (String.IsNullOrEmpty(normalized) ||
                    !Regex.IsMatch(processName, @"^[A-Za-z0-9._-]+(?:\.exe)?$", RegexOptions.CultureInvariant) ||
                    processName.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':' }) >= 0)
                {
                    throw new FormatException("Target '" + target.Name + "' has unsafe process name '" + processName + "'.");
                }
            }

            if (target.MaximumTriggerDistanceLines > target.ScanLines)
            {
                throw new FormatException("Target '" + target.Name + "' maximumTriggerDistanceLines cannot exceed scanLines.");
            }
            if (target.BackoffMultiplier < 1.0 || target.BackoffMultiplier > 10.0)
            {
                throw new FormatException("Target '" + target.Name + "' backoffMultiplier must be between 1 and 10.");
            }
            if (target.MaximumRetryIntervalSeconds < target.RetryIntervalSeconds)
            {
                throw new FormatException("Target '" + target.Name + "' maxRetryIntervalSeconds cannot be less than retryIntervalSeconds.");
            }

            ValidatePatternGroup(target.Name, "triggerPatterns", target.TriggerPatterns, 32, true);
            ValidatePatternGroup(target.Name, "readyPatterns", target.ReadyPatterns, 16, true);
            ValidatePatternGroup(target.Name, "busyPatterns", target.BusyPatterns, 16, false);

            target.CompileRegexes();
        }

        private static void ValidatePatternGroup(string targetName, string groupName, string[] patterns, int maximumCount, bool requireAny)
        {
            int count = patterns == null ? 0 : patterns.Length;
            if (requireAny && count == 0)
            {
                throw new FormatException("Target '" + targetName + "' must define " + groupName + ".");
            }
            if (count > maximumCount)
            {
                throw new FormatException("Target '" + targetName + "' " + groupName + " exceeds the limit of " + maximumCount + ".");
            }
            for (int index = 0; index < count; index++)
            {
                if (patterns[index].Length > 2048)
                {
                    throw new FormatException("Target '" + targetName + "' " + groupName + "[" + index + "] exceeds 2048 characters.");
                }
            }
        }

        private static XmlElement RequireChild(XmlElement parent, string name)
        {
            XmlElement child = parent[name];
            if (child == null)
            {
                throw new FormatException("Missing <" + name + "> inside <" + parent.Name + ">.");
            }
            return child;
        }

        private static string ReadString(XmlElement element, string attributeName)
        {
            string value = element.GetAttribute(attributeName);
            if (String.IsNullOrWhiteSpace(value))
            {
                throw new FormatException("Missing or empty '" + attributeName + "' on <" + element.Name + ">.");
            }
            return value.Trim();
        }

        private static bool ReadBoolean(XmlElement element, string attributeName)
        {
            bool value;
            if (!Boolean.TryParse(ReadString(element, attributeName), out value))
            {
                throw new FormatException("'" + attributeName + "' on <" + element.Name + "> must be true or false.");
            }
            return value;
        }

        private static int ReadInteger(XmlElement element, string attributeName, int minimum, int maximum)
        {
            int value;
            if (!Int32.TryParse(ReadString(element, attributeName), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
            {
                throw new FormatException("'" + attributeName + "' on <" + element.Name + "> must be between " + minimum + " and " + maximum + ".");
            }
            return value;
        }

        private static int ReadOptionalInteger(XmlElement element, string attributeName, int defaultValue, int minimum, int maximum)
        {
            string raw = element.GetAttribute(attributeName);
            if (String.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }
            int value;
            if (!Int32.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
            {
                throw new FormatException("'" + attributeName + "' on <" + element.Name + "> must be between " + minimum + " and " + maximum + ".");
            }
            return value;
        }

        private static double ReadOptionalDouble(XmlElement element, string attributeName, double defaultValue)
        {
            string raw = element.GetAttribute(attributeName);
            if (String.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }
            double value;
            if (!Double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value <= 0)
            {
                throw new FormatException("'" + attributeName + "' on <" + element.Name + "> must be a positive number.");
            }
            return value;
        }

        private static long ReadLong(XmlElement element, string attributeName, long minimum, long maximum)
        {
            long value;
            if (!Int64.TryParse(ReadString(element, attributeName), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
            {
                throw new FormatException("'" + attributeName + "' on <" + element.Name + "> must be between " + minimum + " and " + maximum + ".");
            }
            return value;
        }

        private static string[] ReadValues(XmlElement parent, string containerName, string itemName, bool requireAny)
        {
            XmlElement container = RequireChild(parent, containerName);
            ValidateAttributes(container);
            ValidateChildElements(container, itemName);
            var values = new List<string>();
            foreach (XmlNode node in container.ChildNodes)
            {
                XmlElement item = node as XmlElement;
                if (item == null || !String.Equals(item.Name, itemName, StringComparison.Ordinal))
                {
                    continue;
                }

                string value = (item.InnerText ?? String.Empty).Trim();
                if (value.Length == 0)
                {
                    throw new FormatException("Empty <" + itemName + "> inside <" + containerName + ">.");
                }
                values.Add(value);
            }

            if (requireAny && values.Count == 0)
            {
                throw new FormatException("<" + containerName + "> must contain at least one <" + itemName + ">.");
            }
            return values.ToArray();
        }

        private static string ResolvePath(string baseDirectory, string path)
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
        }

        private static void ValidateAttributes(XmlElement element, params string[] allowedNames)
        {
            var allowed = new HashSet<string>(allowedNames ?? new string[0], StringComparer.Ordinal);
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (!allowed.Contains(attribute.Name))
                {
                    throw new FormatException("Unknown attribute '" + attribute.Name + "' on <" + element.Name + ">.");
                }
            }
        }

        private static void ValidateChildElements(XmlElement element, params string[] allowedNames)
        {
            var allowed = new HashSet<string>(allowedNames ?? new string[0], StringComparer.Ordinal);
            foreach (XmlNode node in element.ChildNodes)
            {
                XmlElement child = node as XmlElement;
                if (child != null && !allowed.Contains(child.Name))
                {
                    throw new FormatException("Unknown element <" + child.Name + "> inside <" + element.Name + ">.");
                }
            }
        }
    }

    internal sealed class TargetRule
    {
        public string Name;
        public bool Enabled;
        public string[] ProcessNames;
        public string Command;
        public int ScanLines;
        public int MaximumTriggerDistanceLines;
        public int InitialDelaySeconds;
        public int RetryIntervalSeconds;
        public bool ParseRetryTime;
        public double BackoffMultiplier;
        public int MaximumRetryIntervalSeconds;
        public int MaximumAttemptsPerEvent;
        public string[] TriggerPatterns;
        public string[] ReadyPatterns;
        public string[] BusyPatterns;

        public Regex[] CompiledTriggerPatterns;
        public Regex[] CompiledReadyPatterns;
        public Regex[] CompiledBusyPatterns;
        public HashSet<string> ProcessNameSet;

        public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);

        public int SafeInitialDelaySeconds
        {
            get { return Math.Max(10, InitialDelaySeconds); }
        }

        public int SafeRetryIntervalSeconds
        {
            get { return Math.Max(10, RetryIntervalSeconds); }
        }

        public double SafeBackoffMultiplier
        {
            get { return BackoffMultiplier <= 0 ? 2.0 : Math.Max(1.0, Math.Min(10.0, BackoffMultiplier)); }
        }

        public int SafeMaximumRetryIntervalSeconds
        {
            get { return MaximumRetryIntervalSeconds <= 0 ? 3600 : Math.Max(SafeRetryIntervalSeconds, Math.Min(86400, MaximumRetryIntervalSeconds)); }
        }

        public int SafeMaximumAttemptsPerEvent
        {
            get { return MaximumAttemptsPerEvent <= 0 ? 5 : Math.Max(1, Math.Min(50, MaximumAttemptsPerEvent)); }
        }

        public void CompileRegexes()
        {
            CompiledTriggerPatterns = CompileArray(Name, "triggerPatterns", TriggerPatterns);
            CompiledReadyPatterns = CompileArray(Name, "readyPatterns", ReadyPatterns);
            CompiledBusyPatterns = CompileArray(Name, "busyPatterns", BusyPatterns);

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ProcessNames != null)
            {
                foreach (string p in ProcessNames)
                {
                    if (!String.IsNullOrWhiteSpace(p))
                    {
                        set.Add(ProcessDiscovery.NormalizeName(p));
                    }
                }
            }
            ProcessNameSet = set;
        }

        private static Regex[] CompileArray(string targetName, string groupName, string[] patterns)
        {
            if (patterns == null || patterns.Length == 0)
            {
                return new Regex[0];
            }

            var list = new Regex[patterns.Length];
            for (int i = 0; i < patterns.Length; i++)
            {
                try
                {
                    list[i] = new Regex(patterns[i], RegexOptions.CultureInvariant, DefaultRegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    throw new FormatException("Target '" + targetName + "' has invalid " + groupName + "[" + i + "] regex: " + ex.Message, ex);
                }
            }
            return list;
        }
    }
}
