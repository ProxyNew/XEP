using System.Collections.ObjectModel;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

// One frozen load-order operation owns these record references. Do not filter deleted
// winners here: evidence excludes them, while review-name resolution historically does not.
internal sealed class WinningConsumerRecords
{
    public IReadOnlyDictionary<FormKey, INpcGetter> Npcs { get; }
    public IReadOnlyDictionary<FormKey, IContainerGetter> Containers { get; }
    private readonly IReadOnlyDictionary<FormKey, IMajorRecordGetter> consumers;

    // LLOS protocol 750 swaps target entries in small containers and NPC inventories at
    // startup (only records with fewer than 64 entries); consumers read those effective records.
    public WinningConsumerRecords(IReadOnlyList<ISkyrimModGetter> mods) : this(mods, null, null) { }
    public WinningConsumerRecords(IReadOnlyList<ISkyrimModGetter> mods, Func<FormKey, FormKey?>? containerSwap, Func<FormKey, bool>? containerWithheld)
    {
        var npcs = new Dictionary<FormKey, INpcGetter>();
        var containers = new Dictionary<FormKey, IContainerGetter>();
        var combined = new Dictionary<FormKey, IMajorRecordGetter>();
        foreach (var mod in mods)
        {
            // Assignment keeps the first key position and the last record, as GroupBy
            // did. Interleaving types per plugin also preserves cross-type name winners.
            foreach (var npc in mod.Npcs) { npcs[npc.FormKey] = npc; combined[npc.FormKey] = npc; }
            foreach (var container in mod.Containers) { containers[container.FormKey] = container; combined[container.FormKey] = container; }
        }
        if (containerSwap is not null)
        {
            bool Swaps(IReadOnlyList<IContainerEntryGetter>? items) => items is { Count: < 64 }
                && items.Any(e => containerSwap(e.Item.Item.FormKey) is not null || containerWithheld?.Invoke(e.Item.Item.FormKey) == true);
            void Swap(IList<ContainerEntry> items)
            {
                foreach (var entry in items)
                    if (containerSwap(entry.Item.Item.FormKey) is { } swapped) entry.Item.Item.SetTo(swapped);
                    // A withheld swap replaces the entry with an unmodelled list: it supplies nothing.
                    else if (containerWithheld?.Invoke(entry.Item.Item.FormKey) == true) { entry.Item.Item.SetTo(FormKey.Null); entry.Item.Count = 0; }
            }
            foreach (var (key, container) in containers.ToArray())
                if (Swaps(container.Items)) { var effective = container.DeepCopy(); Swap(effective.Items!); containers[key] = effective; combined[key] = effective; }
            foreach (var (key, npc) in npcs.ToArray())
                if (Swaps(npc.Items)) { var effective = npc.DeepCopy(); Swap(effective.Items!); npcs[key] = effective; combined[key] = effective; }
        }
        Npcs = new ReadOnlyDictionary<FormKey, INpcGetter>(npcs);
        Containers = new ReadOnlyDictionary<FormKey, IContainerGetter>(containers);
        consumers = new ReadOnlyDictionary<FormKey, IMajorRecordGetter>(combined);
    }

    // Inventory entries parsed once per winning record: binary overlays otherwise
    // re-create entry objects on every enumeration. Entries keep their index.
    // Keyed by the record instance, so a cached inventory always belongs to the record asked about.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IContainerGetter, (IContainerEntryGetter Entry, int Index, FormKey Item, int Count)[]> containerEntries = new();
    public (IContainerEntryGetter Entry, int Index, FormKey Item, int Count)[] ContainerEntries(IContainerGetter container)
        => containerEntries.TryGetValue(container, out var known) ? known
            : containerEntries.GetValue(container, static c => (c.Items ?? []).Select((e, i) => (e, i, e.Item.Item.FormKey, e.Item.Count)).ToArray());
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<INpcGetter, (IContainerEntryGetter Entry, int Index, FormKey Item, int Count)[]> npcEntries = new();
    public (IContainerEntryGetter Entry, int Index, FormKey Item, int Count)[] NpcEntries(INpcGetter npc)
        => npcEntries.TryGetValue(npc, out var known) ? known
            : npcEntries.GetValue(npc, static n => (n.Items ?? []).Select((e, i) => (e, i, e.Item.Item.FormKey, e.Item.Count)).ToArray());

    // Defer EditorID reads until names are requested; retain null versus empty names.
    public Dictionary<FormKey, string> CreateNames() =>
        consumers.ToDictionary(p => p.Key, p => p.Value.EditorID ?? "(no EditorID)");
}
