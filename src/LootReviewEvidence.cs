using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record LootConsumerEvidence(FormKey Record, string EditorId, string Role);

    private static int LootConsumerCount(FormKey list, Catalog catalog)
    {
        if (catalog.LootConsumerCountCache.TryGetValue(list, out var cached)) return cached;
        return catalog.LootConsumerCountCache[list] = LootAncestors(list, catalog)
            .SelectMany(k => catalog.LootConsumerEvidenceIndex!.GetValueOrDefault(k, []))
            .Select(c => c.Record).Distinct().Count();
    }

    private static void DescribeLootReview(CandidateItem item, FormKey source, HashSet<FormKey> ancestry, Catalog catalog)
    {
        // Source-side evidence for the review details; the review reason itself comes from DescribeAccess.
        if (!item.RequiresLootReview) { item.LootReviewDetails = ""; return; }
        var index = catalog.LootConsumerEvidenceIndex!;
        catalog.LootAvailabilityNames ??= catalog.Mods.SelectMany(m => m.Globals.Cast<IMajorRecordGetter>().Concat(m.Quests))
            .GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.Last().EditorID ?? "(no EditorID)");
        string ControlName(FormKey k) => $"{catalog.LootAvailabilityNames.GetValueOrDefault(k, "(unresolved)")} [{k}]";
        var hits = ancestry.OrderBy(k => k).SelectMany(k => index.GetValueOrDefault(k, [])
            .Select(c => (Target: k, Consumer: c))).OrderBy(h => h.Consumer.Role)
            .ThenBy(h => h.Consumer.EditorId, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Target).ToArray();
        var consumers = hits.Select(h => h.Consumer).Distinct().ToArray();
        var parents = catalog.DirectParents.GetValueOrDefault(source, []);
        var lists = ancestry.Where(catalog.Lists.ContainsKey).Select(k => catalog.Lists[k]).OrderBy(l => l.EditorID).ThenBy(l => l.FormKey).ToArray();
        var controls = lists.Where(l => !l.Global.IsNull || l.ChanceNone.Value > 0 || ((int)l.Flags & 4) != 0).ToArray();
        var unlocks = catalog.ScriptUnlocks.GetValueOrDefault(source, []);
        string ListName(FormKey k) => catalog.Lists.TryGetValue(k, out var l) ? $"{l.EditorID ?? "(no EditorID)"} [{k}]" : $"{item.EditorId} [{k}]";
        var roleCounts = consumers.GroupBy(c => c.Role).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}").ToArray();
        var usage = consumers.Length > 0 ? string.Join(", ", roleCounts)
            : "no winning NPC inventory, outfit, container inventory or death-loot consumers";

        var sourceParents = parents.GroupBy(p => p.List).Select(g => g.OrderBy(p => p.Level).First())
            .OrderBy(p => p.List).ThenBy(p => p.Level).ToArray();
        var sourceRows = sourceParents.Take(5).Select(p =>
        {
            var reached = LootConsumerCount(p.List, catalog);
            return $"{catalog.Lists[p.List].EditorID ?? "(no EditorID)"} — from level {p.Level} — "
                + (reached == 0 ? "no recognized NPC/container consumer" : $"{reached} NPC/container consumer record(s)");
        }).ToList();
        if (sourceParents.Length > 5) sourceRows.Add($"+{sourceParents.Length - 5} more direct source list(s)");
        item.SourceDistributionSummary = sourceRows.Count == 0
            ? "No direct leveled-list source was found."
            : $"{sourceParents.Length} direct list(s); {consumers.Length} recognized consumer record(s) across their ancestry.\n  "
              + string.Join("\n  ", sourceRows);

        var availability = new List<string>();
        if (controls.Length > 0)
        {
            string ControlLabel(ILeveledItemGetter list)
            {
                var parts = new List<string>();
                if (!list.Global.IsNull) parts.Add("global");
                if (list.ChanceNone.Value > 0) parts.Add($"{list.ChanceNone.Value:P0} Chance None");
                if (((int)list.Flags & 4) != 0) parts.Add("gives every entry");
                return $"{list.EditorID ?? "(no EditorID)"} ({string.Join(", ", parts)})";
            }
            availability.Add($"{controls.Length} ancestor list control(s): " + string.Join(", ", controls.Take(3).Select(ControlLabel)));
        }
        if (unlocks.Count > 0) availability.Add($"{unlocks.Count} verified quest/script unlock route(s)");
        if (catalog.UnsafeAvailability.TryGetValue(source, out var summaryUnsafe)) availability.Add("unsupported runtime control: " + summaryUnsafe);
        item.AvailabilitySummary = availability.Count == 0
            ? "No global, nonzero Chance None, gives-every-entry flag, or verified quest insertion was found in the source paths."
            : string.Join("; ", availability) + ".";

        var text = new StringBuilder();
        text.AppendLine($"{item.Name} — {item.EditorId} [{source}]");
        text.AppendLine("SOURCE DETAILS");
        text.AppendLine().AppendLine("DIRECT SOURCE LISTS (winning records)");
        if (parents.Count == 0) text.AppendLine("None. A direct inventory/outfit assignment is not an LVLI that the specialized-list option can patch.");
        foreach (var p in parents.OrderBy(p => p.List).ThenBy(p => p.Level))
            text.AppendLine($"{ListName(p.List)} — entry level {p.Level}, count {p.Count}");

        // A shortest upward path gives concrete, bounded evidence even for graphs with cycles or reconvergence.
        var paths = new Dictionary<FormKey, (FormKey Child, ParentEntry Edge)>();
        var seen = new HashSet<FormKey> { source }; var queue = new Queue<FormKey>(); queue.Enqueue(source);
        while (queue.TryDequeue(out var child))
            foreach (var p in catalog.DirectParents.GetValueOrDefault(child, []).OrderBy(p => p.List))
                if (ancestry.Contains(p.List) && seen.Add(p.List)) { paths[p.List] = (child, p); queue.Enqueue(p.List); }
        string Path(FormKey target)
        {
            var parts = new List<string>(); var next = target;
            while (paths.TryGetValue(next, out var step))
            {
                parts.Add($" --[level {step.Edge.Level}, count {step.Edge.Count}]--> {ListName(next)}");
                next = step.Child;
            }
            parts.Reverse();
            return ListName(source) + string.Concat(parts);
        }
        text.AppendLine().AppendLine($"OBSERVED CONSUMERS ({consumers.Length} distinct records; one representative path per referenced item/list)");
        if (hits.Length == 0) text.AppendLine(usage + " Lists may be unused or reached by unsupported runtime logic; the patcher cannot distinguish those cases here.");
        foreach (var hit in hits)
        {
            text.AppendLine($"{hit.Consumer.Role}: {hit.Consumer.EditorId} [{hit.Consumer.Record}]");
            text.AppendLine("  " + Path(hit.Target));
        }
        if (lists.Length > 0 && hits.Length == 0)
        {
            text.AppendLine("Uppermost source lists:");
            var roots = lists.Where(l => !catalog.DirectParents.GetValueOrDefault(l.FormKey, []).Any(p => ancestry.Contains(p.List))).ToArray();
            foreach (var root in roots) text.AppendLine("  " + Path(root.FormKey));
            if (roots.Length == 0) text.AppendLine("  No terminal ancestor (cyclic list graph).");
        }
        text.AppendLine().AppendLine("AVAILABILITY CONTROLS OBSERVED IN THE ANCESTOR GRAPH");
        if (controls.Length == 0) text.AppendLine("No ancestor LVLG, nonzero Chance None or UseAll flag detected. This is not proof that scripts or quests cannot control the item.");
        foreach (var list in controls)
            text.AppendLine($"{ListName(list.FormKey)} — Global: {(list.Global.IsNull ? "none" : ControlName(list.Global.FormKey))}; Chance None: {list.ChanceNone.Value:P0}; UseAll: {(((int)list.Flags & 4) != 0 ? "yes" : "no")}");
        foreach (var unlock in unlocks)
            text.AppendLine($"Verified quest insertion: quest {ControlName(unlock.Quest)}, {(unlock.AtStart ? "at game start" : $"stage {unlock.Stage}")}, level {unlock.Level}, target {ListName(unlock.OriginalTarget)}. Evidence: {unlock.Evidence}");
        if (catalog.UnsafeAvailability.TryGetValue(source, out var unsafeReason)) text.AppendLine("Availability blocker: " + unsafeReason);
        text.AppendLine().AppendLine("CHOICE IMPLICATIONS");
        text.AppendLine("Off: generate no variants for this source. It is the recommendation whenever the carriers have no same-material enchanted access, or no supported specialized route exists.");
        text.AppendLine("For Specialized enchanted lists only, the shared 'Allow new material lists for specialized enchanted distribution' checkbox permits expansion where the selection point lacks the source material. Existing same-material equipment leaves the decision to the specialized planner, even on conditional branches. An isolated own-base family can only use templates derived from that base; it cannot cross-expand into another material. Unresolved material or subtype remains a classification refusal; it is not proof of a missing material family.");
        text.AppendLine("Specialized enchanted lists only: same-material enchanted lists whose consumers match the source contexts, copied where shared.");
        text.AppendLine("Specialized source lists: add an enchanted pool inside supported source lists and therefore mix ordinary and enchanted equipment.");
        text.AppendLine("Best available: try specialized enchanted lists, then source lists, then general enchanted loot, and stop after the first productive family.");
        text.AppendLine("Merchant fallback: add occasional enchanted stock only to record-compatible merchants; retain proven source globals/chance controls, but block quest unlocks and ambiguous alternative paths.");
        text.AppendLine("General enchanted loot: expert route that can reach consumers which never receive the base item.");
        text.AppendLine("These are record connections, not proof that a particular NPC/container spawns or that the author intended enchantments. Outfit evidence does not prove the outfit is assigned to an active NPC. Observed controls can belong to different paths; they are not asserted to govern every route.");
        item.LootReviewDetails = text.ToString();
    }
}
