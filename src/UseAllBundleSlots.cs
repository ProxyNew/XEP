using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

// Direct equipment slots of Give All bundles (UseAll contracts C2/C3,
// .scratch/use-all-support/assets/contracts.md). A bundle awards every entry, so a
// new bundle entry would be one more co-award. A slot is instead a one-entry choice:
// the bundle entry is rewritten in place (position, level and count kept) to
// reference a generated choice list holding the authored item at level 1, and the
// existing planners extend that choice as they extend any flat list.
public static partial class PatcherEngine
{
    private sealed partial class Catalog
    {
        // Bundle contents list -> authored slot item -> its slot choice.
        public Dictionary<FormKey, Dictionary<FormKey, FormKey>> BundleSlots { get; } = [];
    }

    // Known selection flags with Give All: such a bundle gives each slot on every award
    // that passes its own gate (engine facts 1 and 3). A bundle's own Global or Chance None
    // gates the whole award once, so it is an ordinary availability gate on every path
    // through the bundle and stays on the bundle unchanged (UseAll contract C4); a pool
    // inside a slot choice inherits exactly that gate. 6/7 and unknown bits never qualify.
    private static bool SlotBundle(ILeveledItemGetter list)
        => KnownSelectionFlags(list) && GivesAllEntries(list);

    // Null when every entry of the bundle referencing slot can be wrapped; otherwise
    // the precise refusal.
    private static string? BundleSlotProblem(ILeveledItemGetter bundle, FormKey slot)
    {
        if (bundle.IsDeleted) return "list is deleted";
        if (!KnownSelectionFlags(bundle) || !GivesAllEntries(bundle)) return $"unsupported selection flags ({(int)bundle.Flags})";
        // A closed gate (Chance None 100 with no Global) never awards, so it supplies no availability to inherit.
        if (bundle.Global.IsNull && bundle.ChanceNone.Value >= 1) return "Give All bundle has Chance None 100% and never awards";
        var found = false;
        foreach (var row in LeveledEntryRows(bundle))
        {
            if (row.Reference != slot) continue;
            found = true;
            // An entry count N on a list entry is one roll multiplied by N (engine fact 4), the same awards the authored N copies
            // of the item give, so the rewritten entry keeps N and the slot choice stays a single-count choice (contract C5).
            // A non-positive count awards nothing and gives no authored meaning to carry.
            if (row.Count < 1) return $"bundle slot entry count {row.Count} is not positive";
            if (row.HasExtraData) return "bundle slot entry has extra data";
        }
        return found ? null : "bundle has no entry for this slot";
    }

    // The slot choice already made for slot inside owner (a bundle or a copy of one), or null.
    private static FormKey ExistingBundleSlot(FormKey owner, FormKey slot, Catalog catalog)
        => catalog.BundleSlots.TryGetValue(MaterialContents(owner, catalog), out var slots) && slots.TryGetValue(slot, out var choice)
            && catalog.Fallbacks.ContainsKey(choice) ? choice : default;

    // The slot choice inside owner (an authored bundle, or its private copy) for the
    // authored item slot. Owner keeps every other entry, its order, flags and controls.
    private static FormKey BundleSlotWrapper(FormKey owner, FormKey slot, string category, Catalog catalog)
    {
        var contents = MaterialContents(owner, catalog);
        if (ExistingBundleSlot(contents, slot, catalog) is { IsNull: false } known) return known;
        LeveledItem list;
        if ((catalog.Fallbacks.ContainsKey(contents) || catalog.SpecializedRewrites.ContainsKey(contents))
            && catalog.Lists[contents] is LeveledItem mutable) list = mutable;
        else
        {
            // Authored bundles are rewritten as a planning copy; the frozen evidence keeps the original.
            list = catalog.Lists[contents].DeepCopy();
            catalog.SetList(contents, list);
            catalog.SpecializedRewrites[contents] = list;
        }
        if (!catalog.Fallbacks.ContainsKey(contents)) catalog.BundleSlotOwners.Add(contents);
        var positions = (list.Entries ?? []).Where(e => e.Data?.Reference.FormKey == slot).ToArray();
        if (positions.Length == 0 || BundleSlotProblem(list, slot) is not null)
            throw new InvalidDataException($"Bundle slot {slot} changed beneath {list.EditorID} [{owner}]. No output was installed.");
        var key = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
        IMajorRecordGetter? item = catalog.Weapons.GetValueOrDefault(slot) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(slot);
        var wrapper = new LeveledItem(key, SkyrimRelease.SkyrimSE)
        {
            EditorID = $"Slot_{item?.EditorID ?? list.EditorID}", Flags = (LeveledItem.Flag)3, ChanceNone = new Percent(0),
            Entries = new() { new LeveledItemEntry { Data = new() { Reference = new FormLink<IItemGetter>(slot), Level = 1, Count = 1 } } }
        };
        catalog.AddList(key, wrapper);
        var ownerIdentity = catalog.Fallbacks.TryGetValue(contents, out var node) ? node.Identity : contents.ToString();
        catalog.Fallbacks.Add(key, new($"bundle-slot|{ownerIdentity}|{slot}", category, wrapper));
        // Rewrite in place: every other entry, and this entry's level and count, stay put.
        foreach (var position in positions) position.Data!.Reference.SetTo(key);
        MarkEntryReferencesChanged(contents, catalog);
        if (!catalog.BundleSlots.TryGetValue(contents, out var slots)) catalog.BundleSlots[contents] = slots = [];
        slots[slot] = key;
        CoordinateBundleSlot(contents, slot, key, category, catalog);
        return key;
    }

    // A copy of a bundle serves a subset of the original's consumers, so it inherits
    // every addition made to the original (CopyTopology). Slot choices follow the same
    // rule: a copy's choice is registered as a copy of the original's choice, whichever
    // was made first, so the original's counterparts also reach the copy's consumers
    // while the copy's own additions stay private to it.
    private static void CoordinateBundleSlot(FormKey contents, FormKey slot, FormKey choice, string category, Catalog catalog)
    {
        if (catalog.SpecializedCloneOriginals.TryGetValue(contents, out var original)
            && ExistingBundleSlot(original, slot, catalog) is { IsNull: false } originalChoice)
            catalog.CopyTopology.RegisterClone(choice, originalChoice);
        foreach (var copy in catalog.CopyTopology.Copies(catalog)[contents].ToArray())
            catalog.CopyTopology.RegisterClone(BundleSlotWrapper(copy, slot, category, catalog), choice);
    }

    // A new copy of a bundle whose slots were already wrapped gets its own coordinated
    // slot choices, as CoordinateMaterialCopy does for material families.
    private static void CoordinateBundleSlotCopy(FormKey original, FormKey copy, string category, Catalog catalog)
    {
        if (catalog.BundleSlots.TryGetValue(MaterialContents(original, catalog), out var slots))
            foreach (var slot in slots.Keys.ToArray()) BundleSlotWrapper(copy, slot, category, catalog);
    }

    // The list a specialized route writes to: its (possibly private) flat target, or
    // for a bundle slot the slot choice inside that target.
    private static FormKey SpecializedPlacementTarget(SpecializedRoute route, string category, Catalog catalog)
    {
        var target = SpecializedIsolationTarget(route, category, catalog);
        return route.Slot.IsNull ? target : BundleSlotWrapper(target, route.Slot, category, catalog);
    }

    // Projects count additions on the list a specialized route will write to, before any
    // copy or slot choice is allocated; target names that list (or its prototype). A bundle
    // slot's additions join its slot choice, never the bundle: an existing choice (with the
    // copies' choices that mirror it), or a new one holding only its authored item plus what
    // it inherits from the original bundle's choice or passes on to its copies' choices.
    private static bool SpecializedCanAdd(SpecializedRoute route, DirectPeerBudget budget, int count, Catalog catalog,
        out FormKey target, out int positions)
    {
        target = SpecializedFlatTarget(route, catalog);
        var fresh = SpecializedFreshCopy(route, catalog);
        if (route.Slot.IsNull) return budget.CanAddMultiple(target, route.Destination.List, fresh, count, out positions);
        if (!fresh && ExistingBundleSlot(target, route.Slot, catalog) is { IsNull: false } choice)
        {
            target = choice;
            // A copy's choice made after the budget was captured starts as a copy of the original bundle's
            // choice (CoordinateBundleSlot), so the original's captured statics are its baseline.
            return budget.CanAddMultiple(choice, catalog.SpecializedCloneOriginals.GetValueOrDefault(choice, choice), false, count, out positions);
        }
        var copy = fresh || target != route.Destination.List;
        if (copy && ExistingBundleSlot(route.Destination.List, route.Slot, catalog) is { IsNull: false } originalChoice)
        {
            // The copy's new choice starts as a copy of the original's choice.
            target = originalChoice;
            return budget.CanAddMultiple(originalChoice, originalChoice, true, count, out positions);
        }
        positions = 1;
        var fits = positions + count <= 255;
        if (copy) return fits;
        // The original's new choice is mirrored into the choices its copies already hold.
        foreach (var copyList in catalog.CopyTopology.Copies(catalog)[MaterialContents(target, catalog)])
            if (ExistingBundleSlot(copyList, route.Slot, catalog) is { IsNull: false } copyChoice)
            {
                fits &= budget.CanAddMultiple(copyChoice, copyChoice, false, count, out var copyPositions);
                positions = Math.Max(positions, copyPositions);
            }
        return fits;
    }
}
