using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // A list LLOS generates at startup (protocol 170) has no plugin record. A planned
    // placement into it is written to one XEP sibling list S_G placed beside it at every
    // parent occurrence instead: the parent keeps its target entry, LLOS swaps that for
    // G, and S_G sits next to G at G's level and count. G's consumers and windows are
    // kept; only the selection share changes. A UseAll generated list or a UseAll parent
    // would draw every entry, so neither receives a sibling.
    // Returns whether an attachment was refused: its isolation clone may then be orphaned.
    private static bool RedirectLlosGeneratedPlacements(PlanningAdditions<VariantPlan> plans, Catalog catalog)
    {
        if (catalog.LlosLists is not { HasGenerated: true } llos) return false;
        var refusedAttachment = false;
        var targeted = plans.SelectMany(p => p.Parents).Select(p => p.List)
            .Concat(catalog.FallbackEdges.Select(e => e.Parent.List)).Where(llos.IsGenerated).ToHashSet();
        foreach (var generated in targeted.Order())
        {
            var list = catalog.Lists[generated];
            var name = $"{list.EditorID} [{generated}]";
            var occurrences = catalog.Lists.Where(p => !llos.IsGenerated(p.Key))
                .SelectMany(p => (p.Value.Entries ?? []).Where(e => e.Data?.Reference.FormKey == generated)
                    .Select(e => (List: p.Key, Flags: p.Value.Flags, Level: e.Data!.Level, Count: e.Data.Count)))
                .OrderBy(o => o.List).ThenBy(o => o.Level).ToArray();
            var usable = occurrences.Where(o => !o.Flags.HasFlag(LeveledItem.Flag.UseAll)).ToArray();
            // XEP isolation copies of G receive mirrored additions; a sibling beside the
            // original cannot reach them.
            string? refusal = list.Flags.HasFlag(LeveledItem.Flag.UseAll)
                ? "LLOS generates it with UseAll, so a sibling list would change what it gives"
                : catalog.SpecializedCloneOriginals.Values.Contains(generated) ? "XEP isolation copies of it exist, which a sibling list cannot reach"
                : occurrences.Length == 0 ? "no leveled list holds it (an outfit entry would equip a sibling list every time)"
                : usable.Length == 0 ? "every list holding it uses UseAll, so a sibling list would always be given" : null;
            FormKey sibling = default;
            if (refusal is null)
            {
                sibling = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                var node = new LeveledItem(sibling, SkyrimRelease.SkyrimSE)
                {
                    EditorID = "LlosGeneratedSibling_" + list.EditorID,
                    Flags = list.Flags & (LeveledItem.Flag)3,
                    Entries = new()
                };
                catalog.AddList(sibling, node);
                catalog.Fallbacks.Add(sibling, new($"llos-generated-sibling|{llos.Generated[generated].Identity}", "LLOS generated sibling", node));
                foreach (var occurrence in usable)
                    catalog.FallbackEdges.Add(new(new(occurrence.List, occurrence.Level, occurrence.Count), sibling));
                if (usable.Length < occurrences.Length)
                    catalog.DistributionNotes.Add($"{name}: UseAll lists holding this LLOS-generated list receive no sibling for XEP's additions.");
            }
            ParentEntry? Redirect(ParentEntry parent) => parent.List != generated ? parent
                : refusal is null ? parent with { List = sibling } : null;
            for (var i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                if (!plan.Parents.Any(p => p.List == generated)) continue;
                var parents = plan.Parents.Select(Redirect).OfType<ParentEntry>().ToArray();
                if (refusal is not null)
                    catalog.DistributionNotes.Add($"{plan.Source.FormKey}: placement into {name} withheld. {refusal}.");
                plans[i] = plan with { Parents = parents, PlacementReason = parents.Length == 0 && refusal is not null
                    ? "LLOS generates the destination at startup: " + refusal : plan.PlacementReason };
            }
            foreach (var edge in catalog.FallbackEdges.Where(e => e.Parent.List == generated).ToArray())
            {
                catalog.FallbackEdges.Remove(edge);
                if (refusal is null) catalog.FallbackEdges.Add(edge with { Parent = edge.Parent with { List = sibling } });
                else
                {
                    catalog.DistributionNotes.Add($"{catalog.Lists[edge.Child].EditorID} [{edge.Child}]: attachment to {name} withheld. {refusal}.");
                    refusedAttachment = true;
                }
            }
        }
        return refusedAttachment;
    }
}
