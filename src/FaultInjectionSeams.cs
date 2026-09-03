using System;
using System.IO;
using System.Threading;

namespace SaiCont
{
    // T-145: narrow deterministic seams for safety-relevant lifecycle faults.
    // Production defaults are direct framework calls; tests replace one
    // delegate at a time and always restore defaults.
    internal static class FaultInjectionSeams
    {
        internal static Action<string> DeleteLifecycleFile = File.Delete;
        internal static Func<Thread, int, bool> JoinWorker = delegate(Thread worker, int timeout) { return worker.Join(timeout); };
        internal static Action<ManualResetEventSlim> WakeWorker = delegate(ManualResetEventSlim wakeup) { wakeup.Set(); };

        internal static void DeleteIfPresent(string path)
        {
            if (!String.IsNullOrEmpty(path) && File.Exists(path)) DeleteLifecycleFile(path);
        }

        internal static void Reset()
        {
            DeleteLifecycleFile = File.Delete;
            JoinWorker = delegate(Thread worker, int timeout) { return worker.Join(timeout); };
            WakeWorker = delegate(ManualResetEventSlim wakeup) { wakeup.Set(); };
        }
    }
}
