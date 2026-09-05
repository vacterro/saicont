using System;
using System.Threading;

namespace SaiCont
{
    /// <summary>
    /// T-137: shared runtime-control generation contract. One monotonic
    /// generation counter owned by the lifecycle boundary and observed by
    /// every poll path (GUI background thread, TUI poll loop, terminal).
    /// A stop invalidates the current generation atomically; any in-flight
    /// poll that captured a generation sees it is no longer current and
    /// cancels before it can mutate durable state or release the mutex.
    ///
    /// Contract:
    /// - the lifecycle owner (RunInteractiveWithLifecycle) holds the single
    ///   instance and increments it on stop / mode change;
    /// - GUI/TUI capture the current generation at poll start and pass a
    ///   predicate that returns true once that generation is stale;
    /// - cancellation is observable BEFORE mutex release: the poll loop checks
    ///   IsCurrent(generation) and exits, then the lifecycle finally block
    ///   releases the mutex only after the poll worker has joined.
    /// </summary>
    internal sealed class RuntimeControlGeneration
    {
        private int _current;

        internal RuntimeControlGeneration()
        {
            _current = 0;
        }

        /// <summary>The current generation value. Poll paths capture this once.</summary>
        internal int Current
        {
            get { return Thread.VolatileRead(ref _current); }
        }

        /// <summary>True iff <paramref name="generation"/> is still the current generation.</summary>
        internal bool IsCurrent(int generation)
        {
            return Thread.VolatileRead(ref _current) == generation;
        }

        /// <summary>
        /// Invalidate the current generation. Any in-flight poll that captured
        /// the previous value observes IsCurrent() == false and cancels.
        /// </summary>
        /// <returns>The new current generation.</returns>
        internal int Increment()
        {
            return Interlocked.Increment(ref _current);
        }
    }

    /// <summary>
    /// W2L1-004: the single worker-retirement primitive. Engine lifetime must be
    /// bound to poll-worker lifetime: TUI reload, TUI shutdown and GUI shutdown
    /// each used to invalidate the generation, call Thread.Join with a fixed
    /// budget, IGNORE the returned bool, and then dispose the engine or release
    /// the installation mutex regardless. A poll blocked inside sequential REST
    /// calls outlives that budget, so a replacement process could take ownership
    /// while the previous worker was still able to touch retry/durable state.
    ///
    /// Retire() reports honestly:
    ///  - Retired          the worker is proven gone; disposal is safe;
    ///  - AlreadyIdle      there was no worker;
    ///  - TimedOut         the worker is STILL RUNNING -- callers must treat this
    ///                     as a lifecycle failure, not as success.
    /// Cancellation runs first (generation invalidated, transports aborted via
    /// the supplied callback, wakeup signalled), then the join is retried once
    /// after cancellation has had a chance to unblock the worker.
    /// </summary>
    internal enum WorkerRetirement
    {
        AlreadyIdle = 0,
        Retired = 1,
        TimedOut = 2
    }

    internal static class WorkerRetirementPolicy
    {
        internal const int FirstJoinMs = 5000;
        internal const int PostCancelJoinMs = 10000;

        internal static WorkerRetirement Retire(
            Thread worker,
            RuntimeControlGeneration generation,
            Action signalCancellation,
            Action cancelBlockingWork)
        {
            if (generation != null) generation.Increment();
            if (signalCancellation != null)
            {
                try { signalCancellation(); } catch { }
            }
            if (worker == null) return WorkerRetirement.AlreadyIdle;

            if (JoinOnce(worker, FirstJoinMs)) return WorkerRetirement.Retired;

            // The worker is still inside blocking work (typically a synchronous
            // REST sequence). Abort that work, then join again with a longer
            // budget. Only after this second failure is the retirement a real
            // lifecycle failure.
            if (cancelBlockingWork != null)
            {
                try { cancelBlockingWork(); } catch { }
            }
            return JoinOnce(worker, PostCancelJoinMs) ? WorkerRetirement.Retired : WorkerRetirement.TimedOut;
        }

        private static bool JoinOnce(Thread worker, int timeoutMs)
        {
            try
            {
                return FaultInjectionSeams.JoinWorker(worker, timeoutMs);
            }
            catch (Exception ex)
            {
                Program.TryReportTeardownFailure("worker join", ex);
                return false;
            }
        }
    }
}
