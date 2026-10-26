using System.Diagnostics;
using System.Threading;

namespace XEnchantmentPatcher;

// Per-operation, synchronous phase accounting. Nested timings are inclusive;
// self_ms excludes instrumented children. Repeated item phases are aggregated.
// Allocation uses the calling thread's counter, so other threads are not counted.
// Hot leaves (hundreds of thousands to millions of calls) use Count instead of
// Measure: it allocates nothing and reads no allocation counter, but still
// reports calls and time, and its time is still excluded from the enclosing
// phase's self time. Its allocation stays in the enclosing phase's self figures.
internal static class PhaseTiming
{
    private static readonly AsyncLocal<Session?> Current = new();
    private static int nextId;

    public static IDisposable? Begin(string operation)
    {
        if (Current.Value is not null) return null;
        return Current.Value = new Session(operation, Interlocked.Increment(ref nextId));
    }

    public static IDisposable? Measure(string phase) => Current.Value?.Open(phase);

    public static Counter Count(string phase) => Current.Value?.Count(phase) ?? default;

    // Sessions are single-threaded. Parallel workers run detached; the caller
    // measures the parallel region as a whole.
    public static IDisposable Detach()
    {
        var session = Current.Value;
        Current.Value = null;
        return new Restore(session);
    }

    private sealed class Restore(Session? session) : IDisposable
    {
        public void Dispose() => Current.Value = session;
    }

    internal sealed class Totals(bool counter)
    {
        public readonly bool IsCounter = counter;
        public double Milliseconds, SelfMilliseconds;
        public long AllocatedBytes, SelfAllocatedBytes;
        public int Calls;
    }

    // A struct disposed by `using` is neither boxed nor allocated.
    public readonly struct Counter : IDisposable
    {
        private readonly Totals? totals;
        private readonly Scope? parent;
        private readonly double parentChildren;
        private readonly long start;
        internal Counter(Totals totals, Scope? parent)
        {
            this.totals = totals; this.parent = parent;
            parentChildren = parent?.Children ?? 0;
            start = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (totals is null) return;
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            // Scopes and counters nested inside this one also reported to the
            // parent; replace their share with this whole interval.
            var nested = parent is null ? 0 : parent.Children - parentChildren;
            totals.Calls++;
            totals.Milliseconds += elapsed;
            totals.SelfMilliseconds += Math.Max(0, elapsed - nested);
            if (parent is not null) parent.Children = parentChildren + elapsed;
        }
    }

    internal sealed class Session(string operation, int id) : IDisposable
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        private readonly Dictionary<string, Totals> totals = [];
        public Scope? Active;
        public long RootChildAllocatedBytes;

        public IDisposable Open(string phase) => Active = new Scope(this, phase, Active);
        public Counter Count(string phase)
        {
            if (!totals.TryGetValue(phase, out var total)) totals[phase] = total = new(counter: true);
            Debug.Assert(total.IsCounter, "A phase is either measured or counted.");
            return new(total, Active);
        }
        public void Record(string phase, double elapsed, double children, long allocated, long childAllocated)
        {
            if (!totals.TryGetValue(phase, out var total)) totals[phase] = total = new(counter: false);
            Debug.Assert(!total.IsCounter, "A phase is either measured or counted.");
            total.Calls++;
            total.Milliseconds += elapsed;
            total.SelfMilliseconds += Math.Max(0, elapsed - children);
            total.AllocatedBytes += allocated;
            total.SelfAllocatedBytes += Math.Max(0, allocated - childAllocated);
        }

        public void Dispose()
        {
            if (Current.Value != this) return;
            Current.Value = null;
            var allocated = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart);
            // An exception also disposes the session: these are elapsed timings,
            // never a success marker. CLI's existing EXIT line remains authoritative.
            foreach (var (phase, total) in totals)
                Console.WriteLine(total.IsCounter
                    ? FormattableString.Invariant($"TIMING operation={operation} run={id} phase={phase} counter=1 calls={total.Calls} elapsed_ms={total.Milliseconds:F3} self_ms={total.SelfMilliseconds:F3}")
                    : FormattableString.Invariant($"TIMING operation={operation} run={id} phase={phase} calls={total.Calls} elapsed_ms={total.Milliseconds:F3} self_ms={total.SelfMilliseconds:F3} allocated_bytes={total.AllocatedBytes} self_allocated_bytes={total.SelfAllocatedBytes}"));
            Console.WriteLine(FormattableString.Invariant($"TIMING operation={operation} run={id} phase=total elapsed_ms={watch.Elapsed.TotalMilliseconds:F3} allocated_bytes={allocated} self_allocated_bytes={Math.Max(0, allocated - RootChildAllocatedBytes)}"));
        }
    }

    internal sealed class Scope(Session session, string phase, Scope? parent) : IDisposable
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly long allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        public double Children;
        private long childAllocated;
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var allocated = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart);
            session.Record(phase, elapsed, Children, allocated, childAllocated);
            if (parent is not null) { parent.Children += elapsed; parent.childAllocated += allocated; }
            else session.RootChildAllocatedBytes += allocated;
            session.Active = parent;
        }
    }
}
