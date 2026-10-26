using System.Threading;

namespace XEnchantmentPatcher;

// Sequential scan stages, in run order.
internal enum ScanStage { LoadOrder, Catalog, Availability, Candidates, Review, Selection }

// Weighted scan fractions are reported only while a caller has begun a scan
// observer; stage captions also reach the optional operation observer. Stage
// shares follow measured elapsed time for each scan phase on a large load order
// (12 October 2026 private scan, 174 s): an estimate, never a decision input.
internal static class ScanProgress
{
    private static readonly double[] Starts = [0, 0.01, 0.05, 0.39, 0.42, 0.71, 1];
    private static readonly AsyncLocal<Reporter?> Current = new();

    // Reports a monotonic fraction in [0, 1] whenever it advances by at least 0.5%.
    public static IDisposable Begin(Action<double> report) => Current.Value = new Reporter(report);

    public static void Enter(ScanStage stage)
    {
        Current.Value?.Move((int)stage, 0);
        OperationProgress.Stage(stage switch
        {
            ScanStage.LoadOrder => "Loading active plugins…",
            ScanStage.Catalog => "Indexing equipment and enchantments…",
            ScanStage.Availability => "Checking quest and script availability…",
            ScanStage.Candidates => "Scanning equipment…",
            ScanStage.Review => "Reviewing equipment distribution…",
            _ => "Checking selected equipment…"
        });
    }

    // Fraction of the current stage completed.
    public static void Advance(int done, int total)
    {
        if (Current.Value is { } reporter && total > 0) reporter.Move(reporter.Stage, (double)done / total);
    }

    private sealed class Reporter(Action<double> report) : IDisposable
    {
        private double reported = -1;
        public int Stage { get; private set; }

        public void Move(int stage, double within)
        {
            if (stage < Stage) return;
            Stage = stage;
            var value = Starts[stage] + (Starts[stage + 1] - Starts[stage]) * Math.Clamp(within, 0, 1);
            if (value - reported < 0.005) return;
            reported = value;
            report(value);
        }

        public void Dispose() { if (Current.Value == this) Current.Value = null; }
    }
}
