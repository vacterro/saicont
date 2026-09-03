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
}
