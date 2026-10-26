using System.IO;

namespace XEnchantmentPatcher;

internal static class OverflowLists
{
    internal sealed record Branch<T>(short Level, Node<T> Child);
    internal sealed record Node<T>(List<T> Leaves, List<Branch<T>> Branches)
    {
        public int ChildCount => Branches.Sum(x => 1 + x.Child.ChildCount);
        public int EntryCount => Leaves.Count + Branches.Count;
    }

    internal static Node<T> Plan<T>(List<T> entries, Func<T, short?> level)
    {
        if (entries.Count <= 255) return new(entries, []);
        if (entries.Any(x => level(x) is null))
            throw new InvalidDataException("Cannot split a leveled list containing an entry without DATA.");
        // Each branch has a single entry level, preserving highest-level tiers.
        var groups = entries.GroupBy(x => level(x)!.Value).OrderBy(x => x.Key).ToArray();
        if (groups.Length > 255)
            throw new InvalidDataException("Cannot preserve level thresholds when splitting a list with more than 255 distinct entry levels.");
        var low = 255;
        var high = entries.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (groups.Sum(g => (g.Count() + middle - 1) / middle) <= 255) high = middle;
            else low = middle + 1;
        }
        var leaves = new List<T>();
        var branches = new List<Branch<T>>();
        foreach (var group in groups)
        {
            var values = group.ToArray();
            var buckets = (values.Length + low - 1) / low;
            var offset = 0;
            for (var i = 0; i < buckets; i++)
            {
                var size = values.Length / buckets + (i < values.Length % buckets ? 1 : 0);
                var chunk = values.Skip(offset).Take(size).ToList();
                offset += size;
                if (size == 1) leaves.Add(chunk[0]);
                else branches.Add(new(group.Key, Plan(chunk, level)));
            }
        }
        return new(leaves, branches);
    }
}
