using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Measured on SSE 1.6.1170: CalculateForEachItem overrides Give All.
    // SpecialLoot (8) is read with the ordinary rules of the other bits by maintainer
    // decision (.scratch/script-route-coverage, ticket 03); its native formula is not
    // modeled. Unknown bits are not evidence for widening an availability route.
    private static bool KnownSelectionFlags(ILeveledItemGetter list) => ((int)list.Flags & ~15) == 0;
    private static bool GivesAllEntries(ILeveledItemGetter list) => ((int)list.Flags & 7) is 4 or 5;
    // A stored level of 32768 or more (negative as Int16) starts above any real level.
    private static short SelectionEntryMinimum(ILeveledItemGetter list, short level)
        => GivesAllEntries(list) ? (short)1 : level < 0 ? short.MaxValue : level;
    private static bool HasHighestEligibleCutoff(ILeveledItemGetter list)
        => !GivesAllEntries(list) && ((int)list.Flags & 1) == 0;
    // The game stores entry levels as unsigned 16-bit values; Mutagen reads them as Int16.
    // The queried level stays signed: an entry stored at 32768 or more is cut off by every row.
    private static int SelectionEntryMaximum(ILeveledItemGetter list, short level, IEnumerable<short>? additionalLevels = null)
    {
        if (!HasHighestEligibleCutoff(list)) return ushort.MaxValue;
        var maximum = (int)ushort.MaxValue;
        foreach (var row in LeveledEntryRows(list)) if ((ushort)row.Level > level) maximum = Math.Min(maximum, (ushort)row.Level - 1);
        foreach (var candidate in additionalLevels ?? []) if ((ushort)candidate > level) maximum = Math.Min(maximum, (ushort)candidate - 1);
        return maximum;
    }

    // Entries with data, in entry order; Index is the entry's position among all
    // entries. Overlay entries are re-parsed on every enumeration; read-only records
    // are cached by instance, and a mutable record is always read afresh.
    private readonly record struct LeveledEntryRow(short Level, FormKey Reference, short Count, bool HasExtraData, int Index);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ILeveledItemGetter, LeveledEntryRow[]> EntryRows = new();
    private static LeveledEntryRowSet LeveledEntryRows(ILeveledItemGetter list)
    {
        static LeveledEntryRow[] Read(ILeveledItemGetter list) => new LeveledEntryRowSet(null, list.Entries).ToArray();
        // Mutable lists (planning copies) are enumerated live, without a row array.
        return list is ILeveledItem ? new(null, list.Entries) : new(EntryRows.GetValue(list, Read), null);
    }

    // Foreach over the struct allocates nothing; LINQ over it boxes one enumerator.
    private readonly struct LeveledEntryRowSet(LeveledEntryRow[]? cached, IReadOnlyList<ILeveledItemEntryGetter>? live)
        : IEnumerable<LeveledEntryRow>
    {
        public Enumerator GetEnumerator() => new(cached, live);
        IEnumerator<LeveledEntryRow> IEnumerable<LeveledEntryRow>.GetEnumerator() => GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator(LeveledEntryRow[]? cached, IReadOnlyList<ILeveledItemEntryGetter>? live) : IEnumerator<LeveledEntryRow>
        {
            private int index = -1;
            public LeveledEntryRow Current { get; private set; }
            object System.Collections.IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (cached is not null)
                {
                    if (++index >= cached.Length) return false;
                    Current = cached[index];
                    return true;
                }
                if (live is null) return false;
                while (++index < live.Count)
                {
                    var entry = live[index];
                    if (entry.Data is not { } data) continue;
                    Current = new(data.Level, data.Reference.FormKey, data.Count, entry.ExtraData is not null, index);
                    return true;
                }
                return false;
            }
            public void Reset() => index = -1;
            public readonly void Dispose() { }
        }
    }
}
