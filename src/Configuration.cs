using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
                             @"(?i)you.ve (?:hit|reached) your usage limit",
                             @"(?i)hit your usage limit",
                             @"(?i)usage limit[^\r\n]{0,256}(?:try\s+again|reset|renew)",
                             @"(?i)(?:rate|request) limit[^\r\n]{0,256}(?:exceeded|reached|try\s+again)",
                             @"(?i)(?:try\s+)?again\s+at\s+[A-Za-z0-9]"
                        },
                        ReadyPatterns = new[]
                        {
                             @"^\s*›\s*Ask Codex to do anything\s*$",
                             @"^\s*>\s*Ask Codex to do anything\s*$",
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
                             @"(?i)reached today.s free usage limit(?:[^\r\n]*\r?\n){0,4}[^\r\n]*try again in",
                             @"(?i)""code""\s*:\s*""internal_error""",
                             @"(?i)Cannot read\s+""clipboard""\s+\(this model does not support image input\)"
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
                PollIntervalMilliseconds = ReadInteger(root, "pollIntervalMilliseconds", WatcherRuleLimits.PollIntervalMillisecondsMin, WatcherRuleLimits.PollIntervalMillisecondsMax),
                Targets = new List<TargetRule>()
            };

            XmlElement logging = RequireChild(root, "logging");
            ValidateAttributes(logging, "path", "maxBytes", "retainedFiles", "duplicateWindowSeconds");
            ValidateChildElements(logging);
            string configuredLogPath = ReadString(logging, "path");
            configuration.LogFilePath = ResolvePath(Path.GetDirectoryName(fullPath), configuredLogPath);
            configuration.LogMaximumBytes = ReadLong(logging, "maxBytes", WatcherRuleLimits.LogMaximumBytesMin, WatcherRuleLimits.LogMaximumBytesMax);
            configuration.LogRetainedFiles = ReadInteger(logging, "retainedFiles", WatcherRuleLimits.LogRetainedFilesMin, WatcherRuleLimits.LogRetainedFilesMax);
            configuration.LogDuplicateWindowSeconds = ReadInteger(logging, "duplicateWindowSeconds", WatcherRuleLimits.LogDuplicateWindowSecondsMin, WatcherRuleLimits.LogDuplicateWindowSecondsMax);

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
                    "transport",
                    "endpoint",
                    "projectDir",
                    "command",
                    "scanLines",
                    "maximumTriggerDistanceLines",
                    "initialDelaySeconds",
                    "retryIntervalSeconds",
                    "parseRetryTime",
                    "backoffMultiplier",
                    "maxRetryIntervalSeconds",
                    "maxAttempts");
                string targetTransport = ReadOptionalString(targetElement, "transport", "console");
                ValidateChildElements(
                    targetElement,
                    targetTransport == "opencode-http"
                        ? new[] { "triggerPatterns", "readyPatterns", "busyPatterns" }
                        : new[] { "processNames", "triggerPatterns", "readyPatterns", "busyPatterns" });

                var target = new TargetRule
                {
                    Name = name,
                    Enabled = ReadBoolean(targetElement, "enabled"),
                    Transport = targetTransport,
                    Endpoint = ReadOptionalString(targetElement, "endpoint", null),
                    ProjectDirectory = ReadOptionalString(targetElement, "projectDir", null),
                    Command = ReadString(targetElement, "command"),
                    ScanLines = String.Equals(targetTransport, "opencode-http", StringComparison.OrdinalIgnoreCase)
                        ? ReadOptionalInteger(targetElement, "scanLines", 180, WatcherRuleLimits.ScanLinesMin, NativeConsole.MaximumScanLines)
                        : ReadInteger(targetElement, "scanLines", WatcherRuleLimits.ScanLinesMin, NativeConsole.MaximumScanLines),
                    MaximumTriggerDistanceLines = String.Equals(targetTransport, "opencode-http", StringComparison.OrdinalIgnoreCase)
                        ? ReadOptionalInteger(targetElement, "maximumTriggerDistanceLines", 150, WatcherRuleLimits.MaximumTriggerDistanceLinesMin, WatcherRuleLimits.MaximumTriggerDistanceLinesMax)
                        : ReadInteger(targetElement, "maximumTriggerDistanceLines", WatcherRuleLimits.MaximumTriggerDistanceLinesMin, WatcherRuleLimits.MaximumTriggerDistanceLinesMax),
                    InitialDelaySeconds = ReadInteger(targetElement, "initialDelaySeconds", WatcherRuleLimits.InitialDelaySecondsMin, WatcherRuleLimits.InitialDelaySecondsMax),
                    RetryIntervalSeconds = ReadInteger(targetElement, "retryIntervalSeconds", WatcherRuleLimits.RetryIntervalSecondsMin, WatcherRuleLimits.RetryIntervalSecondsMax),
                    ParseRetryTime = ReadBoolean(targetElement, "parseRetryTime"),
                    BackoffMultiplier = ReadOptionalDouble(targetElement, "backoffMultiplier", WatcherRuleLimits.BackoffMultiplierDefault),
                    MaximumRetryIntervalSeconds = ReadOptionalInteger(targetElement, "maxRetryIntervalSeconds", WatcherRuleLimits.MaxRetryIntervalSecondsDefault, WatcherRuleLimits.MaxRetryIntervalSecondsMin, WatcherRuleLimits.MaxRetryIntervalSecondsMax),
                    MaximumAttemptsPerEvent = ReadOptionalInteger(targetElement, "maxAttempts", WatcherRuleLimits.MaxAttemptsDefault, WatcherRuleLimits.MaxAttemptsMin, WatcherRuleLimits.MaxAttemptsMax),
                    ProcessNames = targetTransport == "opencode-http"
                        ? new string[0]
                        : ReadValues(targetElement, "processNames", "process", true),
                    TriggerPatterns = ReadValues(targetElement, "triggerPatterns", "pattern", true),
                    ReadyPatterns = ReadValues(targetElement, "readyPatterns", "pattern", targetTransport != "opencode-http", targetTransport != "opencode-http"),
                    BusyPatterns = ReadValues(targetElement, "busyPatterns", "pattern", false, targetTransport != "opencode-http")
                };

                ValidateTargetImpl(target);
                configuration.Targets.Add(target);
            }

            if (configuration.Targets.Count == 0)
            {
                throw new FormatException("Configuration must contain at least one <target>.");
            }

            return configuration;
        }

        internal static void ValidateTarget(TargetRule target)
        {
            ValidateTargetImpl(target);
        }

        private static void ValidateTargetImpl(TargetRule target)
        {
            if (!Regex.IsMatch(target.Name, @"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant))
            {
                throw new FormatException("Target name '" + target.Name + "' must contain only letters, digits, dot, underscore, or hyphen.");
            }

            string transport = target.Transport ?? "console";
            if (transport != "console" && transport != "opencode-http")
            {
                throw new FormatException("Target '" + target.Name + "' transport must be 'console' or 'opencode-http'.");
            }
            if (transport == "opencode-http")
            {
                if (String.IsNullOrWhiteSpace(target.Endpoint))
                {
                    throw new FormatException("Target '" + target.Name + "' transport='opencode-http' requires endpoint=\"http://host:port\".");
                }
                if (target.Enabled && String.IsNullOrWhiteSpace(target.ProjectDirectory))
                {
                    throw new FormatException("Target '" + target.Name + "' transport='opencode-http' requires projectDir=\"path\".");
                }
                try
                {
                    if (!String.IsNullOrWhiteSpace(target.ProjectDirectory))
                    {
                        target.ProjectDirectory = Path.GetFullPath(target.ProjectDirectory);
                    }
                }
                catch (Exception exception)
                {
                    throw new FormatException("Target '" + target.Name + "' projectDir is invalid: " + exception.Message);
                }
                Uri endpointUri;
                if (!Uri.TryCreate(target.Endpoint, UriKind.Absolute, out endpointUri) || (endpointUri.Scheme != "http" && endpointUri.Scheme != "https"))
                {
                    throw new FormatException("Target '" + target.Name + "' endpoint must be an absolute http/https URL.");
                }
            }

            if (target.Command.Length > WatcherRuleLimits.CommandMaxLength)
            {
                throw new FormatException("Target '" + target.Name + "' command exceeds maximum length of " + WatcherRuleLimits.CommandMaxLength + ".");
            }

            if (target.Command.IndexOf('\r') >= 0 || target.Command.IndexOf('\n') >= 0)
            {
                throw new FormatException("Target '" + target.Name + "' command must be one line.");
            }

            if (transport == "console" && (target.ProcessNames == null || target.ProcessNames.Length == 0))
            {
                throw new FormatException("Target '" + target.Name + "' must have at least one process name.");
            }

            if (transport == "console")
            {
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
            }

            if (target.MaximumTriggerDistanceLines > target.ScanLines)
            {
                throw new FormatException("Target '" + target.Name + "' maximumTriggerDistanceLines cannot exceed scanLines.");
            }
            if (Double.IsNaN(target.BackoffMultiplier) || Double.IsInfinity(target.BackoffMultiplier) || target.BackoffMultiplier < WatcherRuleLimits.BackoffMultiplierMin || target.BackoffMultiplier > WatcherRuleLimits.BackoffMultiplierMax)
            {
                throw new FormatException("Target '" + target.Name + "' backoffMultiplier must be a finite value between " + WatcherRuleLimits.BackoffMultiplierMin + " and " + WatcherRuleLimits.BackoffMultiplierMax + ".");
            }
            if (target.MaximumRetryIntervalSeconds < target.RetryIntervalSeconds)
            {
                throw new FormatException("Target '" + target.Name + "' maxRetryIntervalSeconds cannot be less than retryIntervalSeconds.");
            }

            ValidatePatternGroup(target.Name, "triggerPatterns", target.TriggerPatterns, WatcherRuleLimits.TriggerPatternsMax, true);
            // Console rules need prompt markers; OpenCode lifecycle truth comes
            // from SSE/REST and must not depend on TUI text regexes.
            ValidatePatternGroup(target.Name, "readyPatterns", target.ReadyPatterns, WatcherRuleLimits.ReadyPatternsMax, transport == "console");
            ValidatePatternGroup(target.Name, "busyPatterns", target.BusyPatterns, WatcherRuleLimits.BusyPatternsMax, false);

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
                if (patterns[index].Length > WatcherRuleLimits.PatternMaxLength)
                {
                    throw new FormatException("Target '" + targetName + "' " + groupName + "[" + index + "] exceeds " + WatcherRuleLimits.PatternMaxLength + " characters.");
                }
            }
        }

        private static XmlElement RequireChild(XmlElement parent, string name)
        {
            XmlElement found = null;
            int count = 0;
            foreach (XmlNode node in parent.ChildNodes)
            {
                XmlElement child = node as XmlElement;
                if (child != null && String.Equals(child.Name, name, StringComparison.Ordinal))
                {
                    found = child;
                    count++;
                }
            }
            if (count == 0)
            {
                throw new FormatException("Missing <" + name + "> inside <" + parent.Name + ">.");
            }
            if (count != 1)
            {
                throw new FormatException("Duplicate <" + name + "> inside <" + parent.Name + ">.");
            }
            return found;
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

        private static string ReadOptionalString(XmlElement element, string attributeName, string fallback)
        {
            if (!element.HasAttribute(attributeName))
            {
                return fallback;
            }
            string value = element.GetAttribute(attributeName);
            if (value == null)
            {
                return fallback;
            }
            value = value.Trim();
            return value.Length == 0 ? fallback : value;
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
            if (Double.IsNaN(value) || Double.IsInfinity(value))
            {
                throw new FormatException("'" + attributeName + "' on <" + element.Name + "> must be a finite number.");
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

        private static string[] ReadValues(XmlElement parent, string containerName, string itemName, bool requireAny, bool requireContainer = true)
        {
            XmlElement container = null;
            int containerCount = 0;
            foreach (XmlNode node in parent.ChildNodes)
            {
                XmlElement child = node as XmlElement;
                if (child != null && String.Equals(child.Name, containerName, StringComparison.Ordinal))
                {
                    container = child;
                    containerCount++;
                }
            }
            if (containerCount == 0 && !requireContainer) return new string[0];
            if (containerCount == 0) throw new FormatException("Missing <" + containerName + "> inside <" + parent.Name + ">.");
            if (containerCount != 1) throw new FormatException("Duplicate <" + containerName + "> inside <" + parent.Name + ">.");
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
        public string Transport;
        public string Endpoint;
        public string ProjectDirectory;
        internal string ActiveAgentForSend;
        internal string ModelProviderForSend;
        internal string ModelIdForSend;
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

        public string SemanticFingerprint
        {
            get
            {
                var builder = new StringBuilder();
                builder.Append(Name).Append('|').Append(Enabled).Append('|').Append(Command).Append('|')
                    .Append(Transport ?? "console").Append('|').Append(Endpoint ?? String.Empty).Append('|').Append(ProjectDirectory ?? String.Empty).Append('|')
                    .Append(ScanLines).Append('|').Append(MaximumTriggerDistanceLines).Append('|')
                    .Append(InitialDelaySeconds).Append('|').Append(RetryIntervalSeconds).Append('|')
                    .Append(ParseRetryTime).Append('|').Append(BackoffMultiplier.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                    .Append(MaximumRetryIntervalSeconds).Append('|').Append(MaximumAttemptsPerEvent).Append('|');
                AppendValues(builder, ProcessNames);
                AppendValues(builder, TriggerPatterns);
                AppendValues(builder, ReadyPatterns);
                AppendValues(builder, BusyPatterns);
                using (var sha = SHA256.Create())
                {
                    byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                    return BitConverter.ToString(digest).Replace("-", String.Empty).ToLowerInvariant();
                }
            }
        }

        private static void AppendValues(StringBuilder builder, string[] values)
        {
            builder.Append('[');
            foreach (string value in values ?? new string[0])
            {
                builder.Append(value == null ? String.Empty : value.Trim()).Append('\u001f');
            }
            builder.Append(']');
        }

        public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);

        public int SafeInitialDelaySeconds
        {
            get { return Math.Max(WatcherRuleLimits.InitialDelaySecondsMin, InitialDelaySeconds); }
        }

        public int SafeRetryIntervalSeconds
        {
            get { return Math.Max(WatcherRuleLimits.RetryIntervalSecondsMin, RetryIntervalSeconds); }
        }

        public double SafeBackoffMultiplier
        {
            get { return BackoffMultiplier <= 0 ? WatcherRuleLimits.BackoffMultiplierDefault : Math.Max(WatcherRuleLimits.BackoffMultiplierMin, Math.Min(WatcherRuleLimits.BackoffMultiplierMax, BackoffMultiplier)); }
        }

        public int SafeMaximumRetryIntervalSeconds
        {
            get { return MaximumRetryIntervalSeconds <= 0 ? WatcherRuleLimits.MaxRetryIntervalSecondsDefault : Math.Max(SafeRetryIntervalSeconds, Math.Min(WatcherRuleLimits.MaxRetryIntervalSecondsMax, MaximumRetryIntervalSeconds)); }
        }

        public int SafeMaximumAttemptsPerEvent
        {
            get { return MaximumAttemptsPerEvent <= 0 ? WatcherRuleLimits.MaxAttemptsDefault : Math.Max(WatcherRuleLimits.MaxAttemptsMin, Math.Min(WatcherRuleLimits.MaxAttemptsMax, MaximumAttemptsPerEvent)); }
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
