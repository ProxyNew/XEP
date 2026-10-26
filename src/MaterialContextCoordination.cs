using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // The outer selector owns new material families; Preserved owns the original
    // contents and their later additions. Coordinate both layers of every copy.
    private static FormKey EnsureMaterialFamily(FormKey target, string category, Catalog catalog)
    {
        using var timing = PhaseTiming.Measure("planning.material-family");
        if (!catalog.MaterialFamilyOriginals.TryGetValue(target, out var preservedKey))
        {
            preservedKey = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
            var basis = catalog.Lists[target];
            var preserved = new LeveledItem(preservedKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = $"Preserved_{basis.EditorID}", Flags = basis.Flags,
                ChanceNone = new Percent(0), Entries = new((basis.Entries ?? []).Select(e => e.DeepCopy()))
            };
            catalog.AddList(preservedKey, preserved);
            catalog.Fallbacks.Add(preservedKey, new($"material-original|{target}", category, preserved));
            var rewritten = basis.DeepCopy();
            rewritten.Entries = new(); rewritten.Flags = (LeveledItem.Flag)3;
            catalog.SetList(target, rewritten);
            if (catalog.Fallbacks.TryGetValue(target, out var generated))
                catalog.Fallbacks[target] = generated with { Original = rewritten };
            else catalog.SpecializedRewrites[target] = rewritten;
            // Pending additions already in this selector belong to its old family.
            for (var i = 0; i < catalog.FallbackEdges.Count; i++)
                if (catalog.FallbackEdges[i].Parent.List == target)
                    catalog.FallbackEdges[i] = catalog.FallbackEdges[i] with
                        { Parent = catalog.FallbackEdges[i].Parent with { List = preservedKey } };
            for (var i = 0; i < catalog.RuntimePlacements.Count; i++)
                if (catalog.RuntimePlacements[i].Target.List == target)
                    catalog.RuntimePlacements[i] = catalog.RuntimePlacements[i] with
                        { Target = catalog.RuntimePlacements[i].Target with { List = preservedKey } };
            catalog.CopyTopology.PreserveFamily(target, preservedKey);
            catalog.FallbackEdges.Add(new(new(target, 1, 1), preservedKey));
        }
        if (catalog.SpecializedCloneOriginals.TryGetValue(target, out var original)
            && catalog.MaterialFamilyOriginals.TryGetValue(original, out var originalPreserved))
            catalog.CopyTopology.RegisterClone(preservedKey, originalPreserved);
        // Take a snapshot: coordinating a copy creates another content-copy mapping.
        foreach (var copy in catalog.SpecializedCloneOriginals.Where(p => p.Value == target).Select(p => p.Key).ToArray())
        {
            var copyPreserved = EnsureMaterialFamily(copy, category, catalog);
            catalog.CopyTopology.RegisterClone(copyPreserved, preservedKey);
        }
        return preservedKey;
    }

    private static void CoordinateMaterialCopy(FormKey original, FormKey copy, string category, Catalog catalog)
    {
        if (catalog.MaterialFamilyOriginals.ContainsKey(original)) EnsureMaterialFamily(copy, category, catalog);
    }

    private static FormKey MaterialContents(FormKey list, Catalog catalog)
        => catalog.MaterialFamilyOriginals.GetValueOrDefault(list, list);

    // A preserved edge belongs to its selector alone: each coordinated copy has
    // its own preserved edge. Isolation edges likewise must not reach sibling copies.
    private static bool InheritableEdge(FallbackEdge edge, Catalog catalog)
        => !catalog.SpecializedCloneOriginals.ContainsKey(edge.Child)
            && catalog.MaterialFamilyOriginals.GetValueOrDefault(edge.Parent.List) != edge.Child;
}
