using System.Diagnostics;
using System.Threading;

namespace XEnchantmentPatcher;

internal readonly record struct OperationProgressUpdate(string Message, double? Fraction);
internal enum ProgressKind { Preview, Scan, Generation }
internal enum GenerationStage { Planning, FinalizingPlans, Records, Cloning, CheckingRecords, Lists, Writing, References, Reachability, Report, Install }

// An optional GUI observer. Engine decisions never read this state. AsyncLocal
// keeps concurrent private runs and test operations independent.
internal static class OperationProgress
{
    private static readonly AsyncLocal<Reporter?> Current = new();
    // Fixed UI shares, not timing predictions. Counted work fills its own share;
    // uncounted work holds at the stage boundary until the next stage begins.
    private static readonly double[] GenerationStarts = [0, .25, .30, .50, .70, .75, .85, .90, .94, .97, .99, 1];

    public static IDisposable Begin(Action<OperationProgressUpdate> report, ProgressKind kind = ProgressKind.Preview, double initialFraction = 0)
    {
        var reporter = new Reporter(report, Current.Value, kind, initialFraction);
        Current.Value = reporter;
        return reporter;
    }

    public static void Stage(string message, GenerationStage? stage = null) => Current.Value?.Stage(message, stage);
    public static void Fraction(double value) => Current.Value?.Advance(value);
    public static void Detail(string message) => Current.Value?.Detail(message);

    // The position is one-based: show the current record, including the final
    // record even when the remaining updates in that stage were coalesced.
    public static void Record(string stage, int position, int total, string plugin, string name)
    {
        if (Current.Value is { } reporter)
            reporter.Record($"{stage} ({position:N0}/{total:N0}): {plugin}: {name}",
                total > 0 ? (double)position / total : null, position == total);
    }

    private sealed class Reporter(Action<OperationProgressUpdate> report, Reporter? previous, ProgressKind kind, double initialFraction) : IDisposable
    {
        private string message = "";
        private double? fraction = kind == ProgressKind.Preview ? null : Math.Clamp(initialFraction, 0, 1);
        private double stageStart;
        private double stageEnd;
        private long lastReport;
        private bool hasDetail;
        private bool disposed;

        public void Stage(string text, GenerationStage? stage)
        {
            message = text;
            if (kind == ProgressKind.Preview) fraction = null;
            else if (kind == ProgressKind.Generation && stage is { } known)
            {
                stageStart = GenerationStarts[(int)known];
                stageEnd = GenerationStarts[(int)known + 1];
                fraction = Math.Max(fraction ?? 0, stageStart);
            }
            hasDetail = false;
            Publish(force: true);
        }

        public void Advance(double value)
        {
            var next = Math.Clamp(value, 0, 1);
            fraction = kind == ProgressKind.Preview ? next : Math.Max(fraction ?? 0, next);
            Publish(force: fraction == 1);
        }

        public void Detail(string text)
        {
            message = text;
            Publish(force: !hasDetail);
            hasDetail = true;
        }

        public void Record(string text, double? value, bool complete)
        {
            message = text;
            if (kind == ProgressKind.Preview)
                fraction = value is { } known ? Math.Clamp(known, 0, 1) : null;
            else if (kind == ProgressKind.Generation && value is { } within)
                fraction = Math.Max(fraction ?? 0, stageStart + (stageEnd - stageStart) * Math.Clamp(within, 0, 1));
            Publish(force: !hasDetail || complete);
            hasDetail = true;
        }

        private void Publish(bool force)
        {
            if (Volatile.Read(ref disposed)) return;
            var now = Stopwatch.GetTimestamp();
            if (!force && lastReport != 0 && Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds < 100) return;
            lastReport = now;
            report(new(message, fraction));
        }

        public void Dispose()
        {
            Volatile.Write(ref disposed, true);
            if (Current.Value == this) Current.Value = previous;
        }
    }
}
