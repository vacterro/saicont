using System;
using System.IO;

namespace SaiCont
{
    // CORE-008 / T-103: structured commit disambiguation. Callers must be able
    // to distinguish a pre-commit failure from a successful destination
    // replacement whose backup cleanup step later failed; the second case must
    // never be downgraded to "not committed" because that permanently
    // fail-closes the watcher while the new bytes are already authoritative.
    internal enum AtomicFileCommit
    {
        PreCommitFailed = 0,
        Committed = 1,
        CommittedWithCleanupWarning = 2
    }

    internal static class AtomicFile
    {
        public static bool TryWrite(string path, Action<Stream> writer, out string error)
        {
            AtomicFileCommit commit;
            return TryWrite(path, writer, out commit, out error);
        }

        public static bool TryWrite(string path, Action<Stream> writer, out AtomicFileCommit commit, out string error)
        {
            commit = AtomicFileCommit.PreCommitFailed;
            error = null;
            if (String.IsNullOrWhiteSpace(path))
            {
                error = "path is empty";
                return false;
            }
            if (writer == null)
            {
                error = "writer is null";
                return false;
            }

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            string tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
            string backupPath = fullPath + ".replace-backup";
            bool destinationReplaced = false;
            try
            {
                if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    writer(stream);
                    stream.Flush(true);
                }

                if (File.Exists(fullPath))
                {
                    if (File.Exists(backupPath))
                    {
                        try { File.Delete(backupPath); }
                        catch { /* backup cleanup failure is handled below */ }
                    }
                    File.Replace(tempPath, fullPath, backupPath, true);
                    destinationReplaced = true;
                    if (File.Exists(backupPath))
                    {
                        try
                        {
                            File.Delete(backupPath);
                        }
                        catch (Exception cleanup)
                        {
                            // Destination is already authoritative. Surface the
                            // cleanup failure but do NOT downgrade the commit.
                            commit = AtomicFileCommit.CommittedWithCleanupWarning;
                            error = "cleanup_warning: " + cleanup.GetType().Name + ": " + cleanup.Message;
                            return true;
                        }
                    }
                }
                else
                {
                    File.Move(tempPath, fullPath);
                    destinationReplaced = true;
                }
                commit = AtomicFileCommit.Committed;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                commit = AtomicFileCommit.PreCommitFailed;
                return false;
            }
            finally
            {
                // If the destination was not yet replaced and the temp file
                // somehow still exists, remove it. A successful replace moves
                // the temp atomically, so this is a no-op on the success path.
                if (!destinationReplaced)
                {
                    TryDelete(tempPath);
                }
                else
                {
                    // Stale backup from a previous successful replace must
                    // not accumulate; clean it independently.
                    TryDelete(backupPath);
                }
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
            catch
            {
            }
        }
    }
}
