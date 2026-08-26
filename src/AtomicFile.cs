using System;
using System.IO;

namespace SaiCont
{
    internal static class AtomicFile
    {
        public static bool TryWrite(string path, Action<Stream> writer, out string error)
        {
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
                        File.Delete(backupPath);
                    }
                    File.Replace(tempPath, fullPath, backupPath, true);
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
            finally
            {
                TryDelete(tempPath);
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
