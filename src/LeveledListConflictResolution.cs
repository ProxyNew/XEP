using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record LeveledEntryIdentity(FormKey Reference, short Level, short Count, bool HasData);
    private sealed record ProvenancedLeveledEntry(LeveledItemEntry Entry, ModKey Provenance);
    private sealed record LeveledListConflictResolution(
        FormKey List,
        ILeveledItemGetter Winning,
        LeveledItem Resolved,
        int RestoredEntries,
        ModKey WinningPlugin,
        IReadOnlyDictionary<ModKey, int> RestoredByPlugin)
    {
        public string Detail =>
            $"{Resolved.EditorID ?? Winning.EditorID ?? List.ToString()} [{List}]: restored {RestoredEntries} entr{(RestoredEntries == 1 ? "y" : "ies")} from " +
            string.Join(", ", RestoredByPlugin.OrderBy(x => x.Key.FileName.String)
                .Select(x => $"{x.Key.FileName.String} ({x.Value})"));
    }

    // Treat every override as a delta against the plugins it actually knows.
    // An omitted entry can be removed only when the overriding plugin depends on
    // the plugin that introduced that entry. Entries from unrelated plugins are
    // retained as unaware conflict victims.
    private static Dictionary<FormKey, LeveledListConflictResolution> ResolveLeveledListConflicts(
        IReadOnlyList<ISkyrimModGetter> mods,
        IReadOnlyDictionary<FormKey, ILeveledItemGetter> winning,
        IReadOnlyDictionary<FormKey, IWeaponGetter> weapons,
        IReadOnlyDictionary<FormKey, IArmorGetter> armors)
    {
        var histories = mods
            .SelectMany(mod => mod.LeveledItems.Select(list => (Mod: mod, List: list)))
            .GroupBy(x => x.List.FormKey).ToArray();
        var allReferences = histories.ToDictionary(history => history.Key,
            history => history.SelectMany(version => version.List.Entries ?? [])
                .Where(entry => entry.Data is not null).Select(entry => entry.Data!.Reference.FormKey).Distinct().ToArray());
        var equipmentLists = allReferences.Where(pair => pair.Value.Any(key => weapons.ContainsKey(key) || armors.ContainsKey(key)))
            .Select(pair => pair.Key).ToHashSet();
        var enchantedLists = allReferences.Where(pair => pair.Value.Any(key =>
                weapons.TryGetValue(key, out var weapon) && !weapon.ObjectEffect.FormKey.IsNull ||
                armors.TryGetValue(key, out var armor) && !armor.ObjectEffect.FormKey.IsNull))
            .Select(pair => pair.Key).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in allReferences)
            {
                if (!equipmentLists.Contains(pair.Key) && pair.Value.Any(equipmentLists.Contains))
                    changed |= equipmentLists.Add(pair.Key);
                if (!enchantedLists.Contains(pair.Key) && pair.Value.Any(enchantedLists.Contains))
                    changed |= enchantedLists.Add(pair.Key);
            }
        } while (changed);
        var resolved = new Dictionary<FormKey, LeveledListConflictResolution>();

        foreach (var history in histories)
        {
            // XEP resolves the ordinary equipment graph only. Enchanted provider
            // structures remain governed by the user's origin priority, and
            // unrelated food, spell, creature and utility lists stay out of scope.
            if (!equipmentLists.Contains(history.Key) || enchantedLists.Contains(history.Key)) continue;
            var versions = history.ToArray();
            if (versions.Length < 2) continue;

            var state = (versions[0].List.Entries ?? [])
                .Select(entry => new ProvenancedLeveledEntry(entry.DeepCopy(), versions[0].Mod.ModKey))
                .ToList();

            foreach (var version in versions.Skip(1))
            {
                var masters = version.Mod.ModHeader.MasterReferences.Select(x => x.Master).ToHashSet();
                var aware = state.Where(slot => slot.Provenance == version.Mod.ModKey || masters.Contains(slot.Provenance)).ToList();
                var unaware = state.Where(slot => slot.Provenance != version.Mod.ModKey && !masters.Contains(slot.Provenance)).ToList();
                var available = aware.GroupBy(slot => EntryIdentity(slot.Entry))
                    .ToDictionary(group => group.Key, group => new Queue<ProvenancedLeveledEntry>(group));
                var next = new List<ProvenancedLeveledEntry>();

                foreach (var entry in version.List.Entries ?? [])
                {
                    var identity = EntryIdentity(entry);
                    if (available.TryGetValue(identity, out var matches) && matches.Count > 0)
                    {
                        var existing = matches.Dequeue();
                        next.Add(new(entry.DeepCopy(), existing.Provenance));
                    }
                    else
                    {
                        next.Add(new(entry.DeepCopy(), version.Mod.ModKey));
                    }
                }

                // Keep the winning override's order first, followed by entries it
                // could not have seen. This keeps ordinary selection weights while
                // avoiding arbitrary reordering of the winning plugin's own rows.
                next.AddRange(unaware.Select(slot => new ProvenancedLeveledEntry(slot.Entry.DeepCopy(), slot.Provenance)));
                state = next;
            }

            var winningList = winning[history.Key];
            var winningCounts = (winningList.Entries ?? []).GroupBy(EntryIdentity)
                .ToDictionary(group => group.Key, group => group.Count());
            var restoredSlots = new List<ProvenancedLeveledEntry>();
            foreach (var slot in state)
            {
                var identity = EntryIdentity(slot.Entry);
                if (winningCounts.TryGetValue(identity, out var count) && count > 0)
                    winningCounts[identity] = count - 1;
                else
                    restoredSlots.Add(slot);
            }
            if (restoredSlots.Count == 0) continue;

            var merged = winningList.DeepCopy();
            merged.Entries = new(state.Select(slot => slot.Entry.DeepCopy()).ToList());
            resolved[history.Key] = new(
                history.Key,
                winningList,
                merged,
                restoredSlots.Count,
                versions[^1].Mod.ModKey,
                restoredSlots.GroupBy(slot => slot.Provenance).ToDictionary(group => group.Key, group => group.Count()));
        }

        return resolved;
    }

    private static LeveledEntryIdentity EntryIdentity(ILeveledItemEntryGetter entry)
        => entry.Data is { } data
            ? new(data.Reference.FormKey, data.Level, data.Count, true)
            : new(FormKey.Null, 0, 0, false);
}
