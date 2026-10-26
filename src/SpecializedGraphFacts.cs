using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Captured adjacency has no overlay readers or catalog-owned lazy state.
    // Both on-demand queries and bounded preparation use these same traversals.
    private sealed class SpecializedGraphFacts(
        Dictionary<FormKey, FormKey[]> parents, Dictionary<FormKey, FormKey[]> children)
    {
        public HashSet<FormKey> Up(FormKey key)
        {
            var found = new HashSet<FormKey>(); var todo = new Stack<FormKey>(); todo.Push(key);
            while (todo.TryPop(out var current))
                if (found.Add(current)) foreach (var parent in parents.GetValueOrDefault(current, [])) todo.Push(parent);
            return found;
        }

        public HashSet<FormKey> Down(FormKey key)
        {
            var seen = new HashSet<FormKey>(); var found = new HashSet<FormKey>();
            var todo = new Stack<FormKey>(); todo.Push(key);
            while (todo.TryPop(out var current))
                if (seen.Add(current))
                {
                    if (children.TryGetValue(current, out var entries))
                        foreach (var child in entries) todo.Push(child);
                    else found.Add(current);
                }
            return found;
        }

        public bool HasAncestorCycle(FormKey key)
        {
            var active = new HashSet<FormKey>(); var done = new HashSet<FormKey>();
            bool Visit(FormKey current)
            {
                if (done.Contains(current)) return false;
                if (!active.Add(current)) return true;
                if (parents.GetValueOrDefault(current, []).Any(Visit)) return true;
                active.Remove(current); done.Add(current); return false;
            }
            return Visit(key);
        }

        public sealed record Result(FormKey Key, HashSet<FormKey> Ancestors, HashSet<FormKey> Leaves,
            bool Cycle, TypedLootUse[] Consumers);

        public Result[] Analyze(FormKey[] roots, Dictionary<FormKey, TypedLootUse[]> uses, int maxWorkers)
        {
            var results = new Result[roots.Length];
            void AnalyzeRoot(int index)
            {
                var key = roots[index];
                var ancestors = Up(key);
                results[index] = new(key, ancestors, Down(key), HasAncestorCycle(key),
                    ancestors.SelectMany(k => uses.GetValueOrDefault(k, [])).Distinct().ToArray());
            }
            if (maxWorkers <= 1 || roots.Length < 2)
                for (var i = 0; i < roots.Length; i++) AnalyzeRoot(i);
            else Parallel.For(0, roots.Length, new ParallelOptions { MaxDegreeOfParallelism = maxWorkers }, AnalyzeRoot);
            // A failed worker throws before any result is published by the caller.
            return results;
        }
    }
}
