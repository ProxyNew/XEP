using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

// Insertion-site shape and payload proofs shared by script distribution's
// admission and its review evidence. They were extracted unchanged from the
// retired mode 7 planner file that held them.
public static partial class PatcherEngine
{
    // Candidate identities, not binding/execution/availability certificates.
    // Runtime observation validates the actual receiver and payload contents.
    private sealed record MirrorSite(FormKey Receiver, FormKey Payload, short Level, short Count, string Evidence);
    // A list LLOS withholds (Problems) keeps plugin contents that LLOS may still change at startup, so Script distribution
    // reads no evidence from its entries: no source membership, level window, presence or shape.
    private static bool LlosWithheld(FormKey list, Catalog catalog) => catalog.LlosLists?.Problems.ContainsKey(list) == true;
    private static bool MirrorPayloadContains(MirrorSite site, FormKey source, Catalog catalog)
    {
        if (catalog.MirrorPayloadMembership.TryGetValue((site.Payload, source), out var membership)) return membership;
        var graph = SpecializedGraph(catalog); var seen = new HashSet<FormKey>();
        bool Visit(FormKey key, int depth)
        {
            if (key == source) return true;
            if (LlosWithheld(key, catalog)) return false;
            if (depth > 16 || seen.Count >= 8192 || !seen.Add(key) || !graph.Lists.TryGetValue(key, out var list) || list.IsDeleted) return false;
            foreach (var d in LeveledEntryRows(list))
                if (d.Count == 1 && !d.HasExtraData && Visit(d.Reference, depth + 1)) return true;
            return false;
        }
        return catalog.MirrorPayloadMembership[(site.Payload, source)] = Visit(site.Payload, 0);
    }

    private static FormKey[] MirrorPayloadTree(MirrorSite site, Catalog catalog, out string? problem)
    {
        var graph = SpecializedGraph(catalog);
        if (graph.MirrorPayloadTrees.TryGetValue(site.Payload, out var cached))
        { problem = cached.Problem; return cached.Nodes; }
        using var timing = PhaseTiming.Measure("script.payload-tree");
        var nodes = new List<FormKey>(); var active = new HashSet<FormKey>(); var entryCount = 0;
        string? error = null;
        void Visit(FormKey key, int depth)
        {
            if (error is not null) return;
            if (active.Contains(key)) { error = "cyclic payload graph"; return; }
            if (depth > 16) { error = "payload graph exceeds 16 edges"; return; }
            if (LlosWithheld(key, catalog))
            { error = $"payload list {key} is withheld by LLOS, so it cannot supply source evidence"; return; }
            if (nodes.Contains(key)) return;
            if (!graph.Lists.TryGetValue(key, out var list))
            {
                if (!(catalog.Armors.TryGetValue(key, out var armor) && !armor.IsDeleted)
                    && !(catalog.Weapons.TryGetValue(key, out var weapon) && !weapon.IsDeleted)) error = "payload has an unresolved or nonequipment leaf";
                return;
            }
            if (list.IsDeleted || !KnownSelectionFlags(list) || list.Entries is not { Count: > 0 })
            { error = "payload graph has deleted/empty lists or unsupported flags"; return; }
            if (list.Entries.Any(e => e.Data is not { Count: 1, Level: > 0 } || e.ExtraData is not null))
            { error = "payload requires count one, positive levels and no extra data"; return; }
            entryCount += list.Entries.Count;
            if (nodes.Count >= 128 || entryCount > 128) { error = "payload snapshot exceeds 128 nodes or total entries"; return; }
            nodes.Add(key); active.Add(key);
            foreach (var entry in list.Entries) Visit(entry.Data!.Reference.FormKey, depth + 1);
            active.Remove(key);
        }
        Visit(site.Payload, 0);
        problem = error;
        if (error is not null) { graph.MirrorPayloadTrees[site.Payload] = ([], problem); return []; }
        // A shared descendant must follow every parent, not merely its first DFS visit.
        var pending = nodes.ToHashSet(); var ordered = new List<FormKey>();
        while (pending.Count > 0)
        {
            var ready = nodes.FirstOrDefault(k => pending.Contains(k) && !pending.Any(p => p != k
                && graph.Lists[p].Entries!.Any(e => e.Data!.Reference.FormKey == k)));
            if (ready.IsNull) { problem = "cyclic payload topological order"; graph.MirrorPayloadTrees[site.Payload] = ([], problem); return []; }
            ordered.Add(ready); pending.Remove(ready);
        }
        var depths = ordered.ToDictionary(k => k, _ => 0);
        foreach (var parent in ordered)
        foreach (var child in graph.Lists[parent].Entries!.Select(e => e.Data!.Reference.FormKey).Where(depths.ContainsKey))
        {
            depths[child] = Math.Max(depths[child], depths[parent] + 1);
            if (depths[child] > 16) { problem = "payload graph exceeds 16 edges through a shared descendant"; graph.MirrorPayloadTrees[site.Payload] = ([], problem); return []; }
        }
        var result = ordered.ToArray();
        graph.MirrorPayloadTrees[site.Payload] = (result, null);
        return result;
    }

    private static string? MirrorShapeProblem(MirrorSite site, Catalog catalog)
    {
        var graph = SpecializedGraph(catalog);
        if (!graph.Lists.TryGetValue(site.Receiver, out var receiver) || receiver.IsDeleted) return "receiver is not a winning LVLI";
        if (LlosWithheld(site.Receiver, catalog)) return WithheldReceiver(site.Receiver);
        if (!MirrorReceiverFlagsSupported(site.Receiver, catalog)) return $"receiver selection flags are outside the observer contract: {receiver.Flags} (0x{(int)receiver.Flags:X2})";
        if ((receiver.Entries?.Count ?? 0) > 255 || (receiver.Entries ?? []).Any(e => e.Data is null || e.ExtraData is not null))
            return "receiver exceeds 255 entries or has unobservable extra entry data";
        var nodes = MirrorPayloadTree(site, catalog, out var payloadProblem);
        if (payloadProblem is not null) return payloadProblem;
        if (site.Payload == site.Receiver || nodes.Contains(site.Receiver))
            return "payload aliases or contains its observed receiver";
        return null;
    }

    private static string WithheldReceiver(FormKey receiver) => $"receiver {receiver} is withheld by LLOS, so its entries cannot supply level-window, presence or shape evidence";

    private static bool MirrorReceiverFlagsSupported(FormKey receiver, Catalog catalog)
        // SKSE reads length/entries directly, independently of selection flags.
    {
        var list = SpecializedGraph(catalog).Lists[receiver];
        return ((int)list.Flags & ~11) == 0 || (KnownSelectionFlags(list) && GivesAllEntries(list));
    }
}
