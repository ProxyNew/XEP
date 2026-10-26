using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Where an isolated list copy is reached: its boundary, and for a recipient or merchant
    // copy only that edge's consumer. Context copies have neither edge.
    private sealed record IsolatedCopyScope(FormKey Boundary, LootRecipientEdge? Recipient = null, MerchantInventoryEdge? Merchant = null);

    private sealed record LootRecipientEdge(FormKey Consumer, string Role, int Index, FormKey Reference,
        IContainerEntryGetter? Original = null, IOutfitGetter? Outfit = null, bool Sleeping = false, INpcGetter? InventoryProvider = null, int Count = 1)
    {
        private sealed record IdentityText(string Value, string NegativeSign);
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LootRecipientEdge, IdentityText> identities = new();
        // The descriptor fields and referenced record keys are immutable. Keep
        // memo state outside the record's value equality and its with-copy state.
        public string Identity
        {
            get
            {
                var text = identities.GetValue(this, static edge => new(edge.FormatIdentity(),
                    System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NegativeSign));
                // Integer identity fields use the original current-culture format.
                // A descriptor reused under another numeric format renders afresh.
                return text.NegativeSign == System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NegativeSign
                    ? text.Value : FormatIdentity();
            }
        }
        private string FormatIdentity() => $"{Role}|{Consumer}|{(Outfit is null ? "" : $"{Outfit.FormKey}|{Sleeping}|")}entry:{Index}|{Reference}|count:{Count}|provider:{InventoryProvider?.FormKey}";
    }
    private sealed record LootRecipientContext(FormKey[] Path, LootRecipientEdge Edge);
    private sealed partial class Catalog
    {
        public HashSet<string> ReportedRecipientNotes { get; } = new(StringComparer.Ordinal);
        public IReadOnlySet<FormKey> SpidProtectedOutfits { get; set; } = new HashSet<FormKey>();
    }
    private sealed record LootRecipientRewrite(LootRecipientEdge Edge, FormKey Root);

    private static IEnumerable<LootRecipientEdge> LootRecipientReferences(TypedLootUse use, Catalog catalog, bool captureMetadata = true,
        FormKey? reference = null)
    {
        if (use.Role == "Container inventory" && catalog.ConsumerRecords.Containers.TryGetValue(use.Consumer, out var container))
            foreach (var (entry, index, item, count) in catalog.ConsumerRecords.ContainerEntries(container))
                if (count > 0 && !item.IsNull && (reference is null || item == reference))
                    yield return new(use.Consumer, use.Role, index, item, captureMetadata ? entry.DeepCopy() : null, Count: count);
        foreach (var edge in NpcInventoryRecipientReferences(use, catalog, captureMetadata, reference)) yield return edge;
        if (use.Role == "NPC death loot" && use.Known && catalog.ConsumerRecords.Npcs.TryGetValue(use.Consumer, out var deathNpc)
            && !deathNpc.DeathItem.IsNull)
            yield return new(use.Consumer, use.Role, -1, deathNpc.DeathItem.FormKey);
        if (use.Role == "NPC outfit" && SpecializedGraph(catalog).InventoryProviders.TryGetValue(use.Consumer, out var npc))
            foreach (var sleeping in new[] { false, true })
            {
                var key = sleeping ? npc.SleepingOutfit.FormKey : npc.DefaultOutfit.FormKey;
                if (!SpecializedGraph(catalog).Outfits.TryGetValue(key, out var outfit)) continue;
                // SPID compares outfit identity (replacements and outfit filters); a private
                // outfit redirect would change which SPID rules match this actor.
                if (catalog.SpidProtectedOutfits.Contains(key)) continue;
                foreach (var (item, index) in (outfit.Items ?? []).Select((e, i) => (e, i)))
                    if (!item.IsNull) yield return new(use.Consumer, use.Role, index, item.FormKey, Outfit: outfit, Sleeping: sleeping, InventoryProvider: npc);
            }
    }

    private static IEnumerable<LootRecipientEdge> NpcInventoryRecipientReferences(TypedLootUse use, Catalog catalog, bool captureMetadata,
        FormKey? reference = null)
    {
        if (use.Role != "NPC inventory" || !SpecializedGraph(catalog).InventoryProviders.TryGetValue(use.Consumer, out var provider)) yield break;
        foreach (var (entry, index, item, count) in catalog.ConsumerRecords.NpcEntries(provider))
            if (count > 0 && !item.IsNull && (reference is null || item == reference))
                yield return new(use.Consumer, use.Role, index, item, captureMetadata ? entry.DeepCopy() : null, InventoryProvider: provider, Count: count);
    }

    // The breadth-first walk is independent of the caller's matcher: the frozen
    // graph and fixed copy guards decide which lists are visited and from where.
    // Keep only visited lists with uses, in visit order; each list once, because a
    // later, longer path to the same list finds the same consumers. Past 8192
    // dequeued lists no partial recipient proof is used.
    private sealed record RecipientWalk(FormKey[] UseRoots, Dictionary<FormKey, FormKey> ReachedFrom, bool Exceeded)
    {
        // A root's path is fixed by the frozen walk; parallel searches share one array
        // so path-keyed text caches hit. Path arrays are never written after creation.
        public System.Collections.Concurrent.ConcurrentDictionary<FormKey, FormKey[]> Paths { get; } = new();
    }

    private static RecipientWalk RecipientWalkFrom(FormKey point, SpecializedEvidence graph, Catalog catalog)
    {
        if (graph.RecipientWalks.TryGetValue(point, out var cached)) return cached;
        var reachedFrom = new Dictionary<FormKey, FormKey> { [point] = default };
        var roots = new List<FormKey>();
        var todo = new Queue<FormKey>(); todo.Enqueue(point);
        var work = 0; var exceeded = false;
        while (todo.TryDequeue(out var root))
        {
            if (++work > 8192) { exceeded = true; break; }
            if (!CanCopyRecipientNode(root, catalog)) continue;
            if (graph.Uses.GetValueOrDefault(root, []).Count > 0) roots.Add(root);
            foreach (var parent in graph.ContextParents(root, completeEntry: true))
                if (reachedFrom.TryAdd(parent, root)) todo.Enqueue(parent);
        }
        return graph.RecipientWalks.GetOrAdd(point, new RecipientWalk(roots.ToArray(), reachedFrom, exceeded));
    }

    // Source authorization belongs to the caller's shared consumer/availability
    // decision. This adapter finds the exact authored reference to redirect.
    private static List<LootRecipientContext> LootRecipientContexts(FormKey point, Func<TypedLootUse, bool> matches,
        Catalog catalog, IEnumerable<FormKey[]> existing, ICollection<string> evidence)
    {
        using var timing = PhaseTiming.Measure("review.recipient-contexts");
        var graph = SpecializedGraph(catalog);
        var result = new List<LootRecipientContext>();
        // Consumers already served by the caller's paths; most searches match no use,
        // so build this only when one does.
        HashSet<(FormKey, string)>? selected = null;
        HashSet<(FormKey, string)> Selected() => selected ??= existing.SelectMany(p => graph.Endpoints(p[^1])).Where(matches)
            .Select(u => (u.Consumer, u.Role)).ToHashSet();
        var walk = RecipientWalkFrom(point, graph, catalog);
        FormKey[] PathTo(FormKey key) => walk.Paths.GetOrAdd(key, static (key, walk) =>
        {
            var path = new List<FormKey>();
            for (var next = key; !next.IsNull; next = walk.ReachedFrom[next]) path.Add(next);
            path.Reverse();
            return path.ToArray();
        }, walk);
        // Most roots match no use: closures over the root are built only for a match.
        static bool UnprovedInherited(SpecializedEvidence graph, FormKey root, TypedLootUse use, Func<TypedLootUse, bool> matches)
            => graph.InventoryDependents.GetValueOrDefault(use.Consumer, []).Any(consumer =>
                !graph.Uses.GetValueOrDefault(root, []).Any(u => u.Consumer == consumer && u.Role == use.Role && matches(u))
                || graph.Uses.GetValueOrDefault(root, []).Any(u => u.Consumer == consumer && u.Role == use.Role && !matches(u)));
        static LootRecipientEdge[] EdgesAt(TypedLootUse use, Catalog catalog, FormKey root)
            => LootRecipientReferences(use, catalog, reference: root).Where(e => e.Reference == root).ToArray();
        foreach (var root in walk.UseRoots)
        {
            FormKey[]? path = null;
            foreach (var use in graph.DistinctSortedUses(root))
            {
                if (!matches(use)) continue;
                if (Selected().Contains((use.Consumer, use.Role))) continue;
                if (use.Role is "NPC inventory" or "NPC outfit" && UnprovedInherited(graph, root, use, matches))
                { evidence.Add($"Recipient {use.Consumer} {use.Role} withheld: inventory rewrite would affect an unproved inherited consumer."); continue; }
                var edges = EdgesAt(use, catalog, root);
                if (edges.Length == 0) evidence.Add($"Recipient {use.Consumer} {use.Role} withheld: no single resolved, writable reference/provider.");
                if (edges.Length == 0) continue;
                path ??= PathTo(root);
                foreach (var edge in edges) result.Add(new(path, edge));
                Selected().Add((use.Consumer, use.Role));
                evidence.Add($"Recipient isolation: {use.Role} {use.Consumer}, {edges.Length} reference(s); clone {path.Length} LVLI node(s), retaining authored stock and entry metadata.");
            }
        }
        if (walk.Exceeded)
        { evidence.Add("Recipient isolation search exceeded 8192 lists; no partial recipient proof is used."); return []; }
        return result;
    }

    private static bool CanCopyRecipientNode(FormKey key, Catalog catalog)
    {
        var graph = SpecializedGraph(catalog);
        return graph.Lists.TryGetValue(key, out var list) && !list.IsDeleted && ((int)list.Flags & ~15) == 0
            && !graph.HasAncestorCycle(key) && !catalog.RuntimeControlledLists.ContainsKey(key)
            && !catalog.ScriptAddTargets.Contains(key) && !catalog.ScriptUnlocks.ContainsKey(key)
            && !catalog.UnsafeAvailability.ContainsKey(key);
    }

    private static string LootIsolationIdentity(IReadOnlyList<FormKey> path, LootRecipientEdge edge)
        => $"recipient|{edge.Identity}|{PathText(path)}";

    // Context paths are shared arrays that recur for every variant of a source;
    // format each once. A path's text is a pure function of its keys.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<FormKey>, string> PathTexts = new();
    private static string PathText(IReadOnlyList<FormKey> path)
        => PathTexts.TryGetValue(path, out var known) ? known : PathTexts.GetValue(path, static p => string.Join('>', p));

    private static FormKey LootRecipientCopy(IReadOnlyList<FormKey> path, LootRecipientEdge edge, string category, Catalog catalog)
    {
        // The same edge and path objects recur for every variant of a source; once a
        // copy exists it is stable until path clones are withdrawn (which clears this).
        if (catalog.RecipientCopies.TryGetValue((edge, path), out var known)) return known;
        FormKey target;
        if (edge.Role == "Container inventory")
        {
            // All container rewrites share the existing complete-entry machinery,
            // including preserved-stock coordination. Admission remains generic.
            target = MerchantIsolatedCopy(path, new(edge.Consumer, edge.Index, edge.Original!), category, catalog);
            catalog.SpecializedPathClones[LootIsolationIdentity(path, edge)] = target;
        }
        else target = RecipientBranchCopy(path, LootIsolationIdentity(path, edge), $"recipient-node|{edge.Identity}",
            "RecipientIsolated", category, catalog, new(path[^1], Recipient: edge), root => catalog.LootRecipientRewrites[edge.Identity] = new(edge, root));
        catalog.RecipientCopies[(edge, path)] = target;
        return target;
    }
    private static string SpecializedIsolationIdentity(SpecializedRoute route)
        => route.RecipientIsolation is { } recipient ? LootIsolationIdentity(route.IsolationPath!, recipient)
            : route.MerchantIsolation is { } merchant ? IsolationIdentity(route.IsolationPath!, merchant)
            : route.IsolationPath is { Count: > 1 } path ? string.Join('>', path) : "";

    private static FormKey SpecializedIsolationTarget(SpecializedRoute route, string category, Catalog catalog)
        => route.RecipientIsolation is { } recipient ? LootRecipientCopy(route.IsolationPath!, recipient, category, catalog)
            : route.MerchantIsolation is { } merchant ? MerchantIsolatedCopy(route.IsolationPath!, merchant, category, catalog)
            : route.IsolationPath is { Count: > 1 } path ? IsolatedCopy(path, category, catalog) : route.Destination.List;

    // NPC references come from the frozen graph; only their rewrites change during planning.
    private static (string Identity, FormKey Reference, FormKey Consumer, string Role)[] NpcRecipientReferences(Catalog catalog)
    {
        var graph = SpecializedGraph(catalog);
        return SharedOnce(graph, () => graph.NpcRecipientReferences, value => graph.NpcRecipientReferences = value,
            () => graph.Uses.Values.SelectMany(u => u).Where(u => u.Role.StartsWith("NPC ", StringComparison.Ordinal))
            .DistinctBy(u => (u.Consumer, u.Role)).SelectMany(use => LootRecipientReferences(use, catalog, captureMetadata: false))
            .Select(edge => (edge.Identity, edge.Reference, edge.Consumer, edge.Role)).ToArray());
    }

    // Frozen recipient references by authored reference. Private rewrites are few
    // and change during planning; coverage checks them separately.
    private sealed class LootRootIndex(Catalog catalog)
    {
        public readonly ILookup<FormKey, (FormKey Container, int Index)> Containers = ContainerRecipientReferences(catalog)
            .ToLookup(r => r.Item, r => (r.Container, r.Index));
        public readonly HashSet<(FormKey Container, int Index)> Slots = ContainerRecipientReferences(catalog)
            .Select(r => (r.Container, r.Index)).ToHashSet();
        public readonly ILookup<FormKey, (string Identity, FormKey Consumer, string Role)> Npcs = NpcRecipientReferences(catalog)
            .ToLookup(r => r.Reference, r => (r.Identity, r.Consumer, r.Role));
        public readonly ILookup<string, (FormKey Consumer, string Role)> Identities = NpcRecipientReferences(catalog)
            .ToLookup(r => r.Identity, r => (r.Consumer, r.Role), StringComparer.Ordinal);
    }

    private static IEnumerable<(FormKey Root, (FormKey Consumer, string Role) Context)> LootRecipientCoverageRoots(Catalog catalog)
    {
        foreach (var (root, consumer) in MerchantInventoryCoverageRoots(catalog)) yield return (root, (consumer, "Container inventory"));
        foreach (var (identity, reference, consumer, role) in NpcRecipientReferences(catalog))
        {
            var root = catalog.LootRecipientRewrites.TryGetValue(identity, out var rewrite) ? rewrite.Root : reference;
            yield return (root, (consumer, role));
            // The same actor's living equipment also supplies its death context, never the reverse.
            if (IsLivingNpcRole(role)) yield return (root, (consumer, "NPC death loot"));
        }
    }

    // Same set as testing every effective root against the reachable lists, but
    // visits only reachable references plus the current private rewrites.
    private static HashSet<(FormKey Consumer, string Role)> LootRecipientCoverage(IEnumerable<FormKey> starts, Catalog catalog)
    {
        var reachable = RecipientReach(starts, catalog);
        if (reachable.Count == 0) return [];
        var graph = SpecializedGraph(catalog);
        var index = SharedOnce(graph, () => graph.LootRoots, value => graph.LootRoots = value, () => new LootRootIndex(catalog));
        var result = new HashSet<(FormKey Consumer, string Role)>();
        void Add(FormKey consumer, string role)
        {
            result.Add((consumer, role));
            // The same actor's living equipment also supplies its death context, never the reverse.
            if (IsLivingNpcRole(role)) result.Add((consumer, "NPC death loot"));
        }
        foreach (var key in reachable)
        {
            foreach (var slot in index.Containers[key])
                if (!catalog.MerchantInventoryRewrites.ContainsKey(slot)) result.Add((slot.Container, "Container inventory"));
            foreach (var (identity, consumer, role) in index.Npcs[key])
                if (!catalog.LootRecipientRewrites.ContainsKey(identity)) Add(consumer, role);
        }
        foreach (var (slot, rewrite) in catalog.MerchantInventoryRewrites)
            if (index.Slots.Contains(slot) && reachable.Contains(rewrite.Root)) result.Add((slot.Container, "Container inventory"));
        foreach (var (identity, rewrite) in catalog.LootRecipientRewrites)
            if (reachable.Contains(rewrite.Root))
                foreach (var (consumer, role) in index.Identities[identity]) Add(consumer, role);
        return result;
    }
    private static int LootRecipientRecordCount(Catalog catalog) => catalog.LootRecipientRewrites.Values
        .Where(r => r.Edge.Outfit is not null).Select(r => (r.Edge.Consumer, r.Edge.Sleeping, r.Edge.Outfit!.FormKey)).Distinct().Count();

    private static void WriteLootRecipientIsolations(Catalog catalog, SkyrimMod output,
        IReadOnlyDictionary<FormKey, FormKey> remapped, Func<IOutfitGetter, string, Outfit> allocateOutfit)
    {
        var rewrites = catalog.LootRecipientRewrites.Values.OrderBy(r => r.Edge.Identity, StringComparer.Ordinal).ToArray();
        var initialized = new HashSet<FormKey>();
        Npc WrittenNpc(LootRecipientEdge edge)
        {
            var npc = output.Npcs.GetOrAddAsOverride(catalog.ConsumerRecords.Npcs[edge.Consumer]);
            if (initialized.Add(edge.Consumer) && edge.InventoryProvider is { } provider && provider.FormKey != edge.Consumer)
            {
                // The provider's plugin inventory: LLOS swaps its small-inventory entries at startup.
                var physical = catalog.LlosLists is { HasContainerEdits: true }
                    ? catalog.Mods.SelectMany(m => m.Npcs).LastOrDefault(n => n.FormKey == provider.FormKey)?.Items ?? provider.Items : provider.Items;
                npc.Items = new((physical ?? []).Select(e => e.DeepCopy()));
                catalog.LlosLists?.MarkPhysicalInventory(npc.FormKey);
                npc.DefaultOutfit.SetTo(provider.DefaultOutfit.FormKey);
                npc.SleepingOutfit.SetTo(provider.SleepingOutfit.FormKey);
                npc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Inventory;
            }
            return npc;
        }
        foreach (var rewrite in rewrites.Where(r => r.Edge.Outfit is null))
        {
            var edge = rewrite.Edge;
            if (edge.Role == "NPC death loot")
            {
                var original = catalog.ConsumerRecords.Npcs[edge.Consumer];
                if (original.IsDeleted || original.DeathItem.FormKey != edge.Reference)
                    throw new InvalidDataException($"Recipient death-loot reference changed: {edge.Identity}. No output was installed.");
                output.Npcs.GetOrAddAsOverride(original).DeathItem.SetTo(remapped[rewrite.Root]);
                continue;
            }
            var npc = catalog.ConsumerRecords.Npcs[edge.Consumer];
            var entries = edge.InventoryProvider?.Items;
            if (npc.IsDeleted || entries is null
                || edge.Index < 0 || edge.Index >= entries.Count || edge.Original is null
                || entries[edge.Index].Item.Item.FormKey != edge.Reference
                || entries[edge.Index].Item.Count != edge.Original.Item.Count)
                throw new InvalidDataException($"Recipient inventory entry changed: {edge.Identity}. No output was installed.");
            var written = WrittenNpc(edge).Items!;
            written[edge.Index] = edge.Original.DeepCopy();
            written[edge.Index].Item.Item.SetTo(remapped[rewrite.Root]);
            catalog.DistributionNotes.Add($"Written recipient {edge.Identity}: {edge.Reference} → {remapped[rewrite.Root]}; original inventory metadata retained.");
        }
        foreach (var group in rewrites.Where(r => r.Edge.Outfit is not null)
            .GroupBy(r => (r.Edge.Consumer, r.Edge.Sleeping, Outfit: r.Edge.Outfit!.FormKey)))
        {
            var edge = group.First().Edge;
            var npc = catalog.ConsumerRecords.Npcs[edge.Consumer];
            var original = edge.Outfit!;
            if (npc.IsDeleted || (edge.Sleeping ? edge.InventoryProvider!.SleepingOutfit.FormKey : edge.InventoryProvider!.DefaultOutfit.FormKey) != original.FormKey
                || group.Any(r => original.Items is null || r.Edge.Index >= original.Items.Count
                    || original.Items[r.Edge.Index].FormKey != r.Edge.Reference))
                throw new InvalidDataException($"Recipient outfit changed: {edge.Identity}. No output was installed.");
            var clone = allocateOutfit(original, $"recipient-outfit|{edge.Consumer}|{edge.Sleeping}|{original.FormKey}");
            foreach (var rewrite in group)
                clone.Items![rewrite.Edge.Index] = new FormLink<IOutfitTargetGetter>(remapped[rewrite.Root]);
            var written = WrittenNpc(edge);
            if (edge.Sleeping) written.SleepingOutfit.SetTo(clone);
            else written.DefaultOutfit.SetTo(clone);
            catalog.DistributionNotes.Add($"Written private outfit {clone.FormKey} for {edge.Consumer}; shared outfit {original.FormKey} retained.");
        }
    }
}
