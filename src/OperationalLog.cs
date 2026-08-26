using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SaiCont
{
    internal sealed class OperationalLog
    {
        internal const int MaximumDeduplicationEntries = 256;
        private readonly string _path;
        private readonly long _maximumBytes;
        private readonly int _retainedFiles;
        private readonly TimeSpan _duplicateWindow;
        private readonly Dictionary<string, DateTime> _lastWrites = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly UTF8Encoding _encoding = new UTF8Encoding(false);

        public OperationalLog(string path, long maximumBytes, int retainedFiles, int duplicateWindowSeconds)
        {
            _path = Path.GetFullPath(path);
            _maximumBytes = Math.Max(256, maximumBytes);
            _retainedFiles = Math.Max(1, retainedFiles);
            _duplicateWindow = TimeSpan.FromSeconds(Math.Max(1, duplicateWindowSeconds));

            string directory = Path.GetDirectoryName(_path);
            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        internal int DeduplicationEntryCount { get { return _lastWrites.Count; } }

        public void Write(string level, string message)
        {
            string line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + level + " " + Sanitize(message) + Environment.NewLine;
            byte[] bytes = _encoding.GetBytes(line);
            RotateIfNeeded(bytes.Length);
            using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        public bool TryWrite(string level, string message)
        {
            try
            {
                Write(level, message);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public bool TryWriteDeduplicated(string key, string level, string message)
        {
            DateTime nowUtc = DateTime.UtcNow;
            PruneDeduplicationCache(nowUtc);

            DateTime previous;
            if (_lastWrites.TryGetValue(key, out previous) && nowUtc - previous < _duplicateWindow)
            {
                return true;
            }

            if (!TryWrite(level, message))
            {
                return false;
            }

            _lastWrites[key] = nowUtc;
            return true;
        }

        public bool TryWriteOnce(string key, string level, string message)
        {
            DateTime nowUtc = DateTime.UtcNow;
            PruneDeduplicationCache(nowUtc);

            if (_lastWrites.ContainsKey(key))
            {
                return true;
            }

            if (!TryWrite(level, message))
            {
                return false;
            }

            _lastWrites[key] = nowUtc;
            return true;
        }

        private void PruneDeduplicationCache(DateTime nowUtc)
        {
            if (_lastWrites.Count < 200)
            {
                return;
            }

            var expiredKeys = new List<string>();
            TimeSpan maxAge = TimeSpan.FromSeconds(_duplicateWindow.TotalSeconds * 2);
            foreach (var pair in _lastWrites)
            {
                if (nowUtc - pair.Value > maxAge)
                {
                    expiredKeys.Add(pair.Key);
                }
            }

            for (int i = 0; i < expiredKeys.Count; i++)
            {
                _lastWrites.Remove(expiredKeys[i]);
            }

            while (_lastWrites.Count >= MaximumDeduplicationEntries)
            {
                string oldestKey = null;
                DateTime oldestTime = DateTime.MaxValue;
                foreach (var pair in _lastWrites)
                {
                    if (pair.Value < oldestTime)
                    {
                        oldestKey = pair.Key;
                        oldestTime = pair.Value;
                    }
                }
                if (oldestKey == null)
                {
                    break;
                }
                _lastWrites.Remove(oldestKey);
            }
        }

        private void RotateIfNeeded(int incomingBytes)
        {
            var current = new FileInfo(_path);
            if (!current.Exists || current.Length + incomingBytes <= _maximumBytes)
            {
                return;
            }

            string oldest = _path + "." + _retainedFiles.ToString(CultureInfo.InvariantCulture);
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (int index = _retainedFiles - 1; index >= 1; index--)
            {
                string source = _path + "." + index.ToString(CultureInfo.InvariantCulture);
                string destination = _path + "." + (index + 1).ToString(CultureInfo.InvariantCulture);
                if (File.Exists(source))
                {
                    File.Move(source, destination);
                }
            }

            File.Move(_path, _path + ".1");
        }

        private static string Sanitize(string message)
        {
            return (message ?? String.Empty).Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
