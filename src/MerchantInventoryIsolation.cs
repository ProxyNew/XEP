using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record MerchantInventoryEdge(FormKey Container, int Index, IContainerEntryGetter Original)
    {
        // The identity is a pure function of the frozen entry and its slot; it is
        // cached on the entry object, outside the record's value equality.
        private sealed record IdentityText(FormKey Container, int Index, string NegativeSign, string Value);
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IContainerEntryGetter, IdentityText> identities = new();
        public string Identity
        {
            get
            {
                var negativeSign = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NegativeSign;
                if (identities.TryGetValue(Original, out var known) && known.Container == Container && known.Index == Index
                    && known.NegativeSign == negativeSign) return known.Value;
                var value = $"{Container}|entry:{Index}|{Original.Item.Item.FormKey}|count:{Original.Item.Count}";
                identities.AddOrUpdate(Original, new(Container, Index, negativeSign, value));
                return value;
            }
        }
    }
    private sealed record MerchantContextRoute(FormKey[] Path, MerchantInventoryEdge Edge, MaterialSourceProof Proof);
    private sealed record MerchantInventoryRewrite(MerchantInventoryEdge Edge, FormKey Root, FormKey PreservedStockParent = default);

    // Container stock is frozen consumer data; only its private rewrites change.
    private static (FormKey Container, int Index, FormKey Item)[] ContainerRecipientReferences(Catalog catalog)
        => SharedOnce(SpecializedGraph(catalog), () => SpecializedGraph(catalog).ContainerRecipientReferences,
            value => SpecializedGraph(catalog).ContainerRecipientReferences = value, () => catalog.ConsumerRecords.Containers.Values.Where(c => !c.IsDeleted)
            .SelectMany(container => (container.Items ?? []).Select((entry, index) => (entry, index))
                .Where(x => x.entry.Item.Count > 0 && !x.entry.Item.Item.IsNull)
                .Select(x => (container.FormKey, x.index, x.entry.Item.Item.FormKey))).ToArray());

    private static IEnumerable<(FormKey Root, FormKey Container)> MerchantInventoryCoverageRoots(Catalog catalog)
    {
        foreach (var (container, index, item) in ContainerRecipientReferences(catalog))
            yield return (catalog.MerchantInventoryRewrites.TryGetValue((container, index), out var rewrite) ? rewrite.Root : item, container);
    }

    // Coverage follows effective stock, not a clone's frozen original ancestry.
    // A private rewrite belongs only to its own winning CONT inventory entry.
    private static HashSet<FormKey> MerchantInventoryCoverage(IEnumerable<FormKey> starts, Catalog catalog)
        => RecipientCoverage(starts, MerchantInventoryCoverageRoots(catalog), catalog);

    private static HashSet<T> RecipientCoverage<T>(IEnumerable<FormKey> starts,
        IEnumerable<(FormKey Root, T Consumer)> roots, Catalog catalog)
    {
        var reachable = RecipientReach(starts, catalog);
        return reachable.Count == 0 ? [] : roots.Where(r => reachable.Contains(r.Root)).Select(r => r.Consumer).ToHashSet();
    }

    // Effective loot lists reachable upward from the selected equipment.
    private static HashSet<FormKey> RecipientReach(IEnumerable<FormKey> starts, Catalog catalog)
    {
        // Most callers have no existing equivalents; skip the copy topology then.
        var startKeys = starts.ToArray();
        if (startKeys.Length == 0) return [];
        var copies = catalog.CopyTopology.Copies(catalog);
        var targets = startKeys.SelectMany(k => InheritedCopyTargets(k, copies, catalog).Prepend(k)).ToHashSet();
        if (targets.Count == 0) return [];
        var graph = SpecializedGraph(catalog);
        // Frozen parents cover unchanged authored lists. Read changed/generated
        // entries from their current records so replaced stock never counts twice.
        var changed = (catalog.ChangedListParents ??= new(PlanningListScope.ChangedStock)).Refresh(catalog);
        var edges = (catalog.PlanningEdges ??= new()).Refresh(catalog);
        IEnumerable<FormKey> Additions(FormKey child)
        {
            foreach (var parent in changed.Parents(child)) yield return parent;
            foreach (var edge in edges.EdgesTo(child))
            {
                yield return edge.Parent.List;
                if (InheritableEdge(edge, catalog))
                    foreach (var target in InheritedCopyTargets(edge.Parent.List, copies, catalog)) yield return target;
            }
            foreach (var insertion in edges.PlacementsTo(child))
                foreach (var target in InheritedCopyTargets(insertion.Target.List, copies, catalog).Prepend(insertion.Target.List))
                    yield return target;
        }
        // Walk upward once from the selected equipment, then test all effective
        // recipient roots. Do not repeat the whole loot traversal per recipient.
        var reachable = new HashSet<FormKey>();
        var todo = new Stack<FormKey>(targets);
        while (todo.TryPop(out var next))
        {
            if (!reachable.Add(next)) continue;
            foreach (var parent in graph.Parents.GetValueOrDefault(next, []))
                if (parent.Count > 0 && !changed.Contains(parent.List)
                    && catalog.Lists.TryGetValue(parent.List, out var list) && !list.IsDeleted) todo.Push(parent.List);
            foreach (var parent in Additions(next)) todo.Push(parent);
        }
        return reachable;
    }

    // A CONT override affects every placed instance. Even an accepting vendor
    // filter cannot authorize another placed context of the same base container.
    private static string? MerchantContainerIsolationProblem(FormKey container, Catalog catalog)
    {
        var placed = SharedOnce(catalog, () => catalog.MerchantPlacedReferences, value => catalog.MerchantPlacedReferences = value, () =>
        {
            var winners = new Dictionary<FormKey, IPlacedObjectGetter>();
            foreach (var mod in catalog.Mods)
                foreach (var reference in mod.EnumerateMajorRecords<IPlacedObjectGetter>())
                    winners[reference.FormKey] = reference;
            return winners.Values.Where(r => !r.IsDeleted && !r.Base.IsNull)
                .GroupBy(r => r.Base.FormKey).ToDictionary(g => g.Key, g => g.Select(r => r.FormKey).Order().ToArray());
        });
        var references = placed.GetValueOrDefault(container, []);
        return references.Length == 1 ? null : references.Length == 0
            ? "no winning placed merchant container reference"
            : $"CONT serves {references.Length} distinct placed references; an inventory override cannot isolate one placed merchant";
    }

    private static List<MerchantContextRoute> MerchantInventoryContexts(FormKey source, FormKey point, Catalog catalog,
        ICollection<string> evidence, IEnumerable<FormKey> alreadyIsolatedRoots)
    {
        using var timing = PhaseTiming.Measure("review.merchant-contexts");
        var graph = SpecializedGraph(catalog);
        if (!graph.Endpoints(point).Any(u => u.Role == "Container inventory")) return [];
        var sources = SharedMemo(catalog.MerchantExactSourceRoutes, source, () => MerchantSourceRoutes(source, catalog, evidence));
        if (sources.Count == 0) return [];
        IMajorRecordGetter item = catalog.Weapons.GetValueOrDefault(source) ?? (IMajorRecordGetter)catalog.Armors[source];
        var vendors = MerchantContainersFor(item, catalog).ToHashSet();
        var covered = alreadyIsolatedRoots.SelectMany(graph.Endpoints)
            .Where(u => u.Role == "Container inventory").Select(u => u.Consumer).ToHashSet();
        var permitted = sources.Where(r => vendors.Contains(r.Container) && !covered.Contains(r.Container))
            .GroupBy(r => r.Container).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Proof.Level).ThenBy(r => r.InventoryIndex).ToArray());
        var result = new List<MerchantContextRoute>();
        var selected = new HashSet<FormKey>();
        var todo = new Queue<FormKey[]>(); todo.Enqueue([point]);
        var examined = 0;
        while (todo.TryDequeue(out var path))
        {
            if (++examined > 8192)
            { evidence.Add("Exact merchant destination search exceeded 8192 paths; no partial merchant proof is used."); return []; }
            var root = path[^1];
            if (!CanCopyRecipientNode(root, catalog)) continue;
            foreach (var (containerKey, sourceRoutes) in permitted.OrderBy(p => p.Key))
            {
                if (selected.Contains(containerKey)) continue;
                var container = catalog.ConsumerRecords.Containers[containerKey];
                var positions = (container.Items ?? []).Select((entry, index) => (Entry: entry, Index: index))
                    .Where(e => e.Entry.Item.Item.FormKey == root && e.Entry.Item.Count > 0).ToArray();
                if (positions.Length == 0) continue;
                var problem = MerchantContainerIsolationProblem(containerKey, catalog);
                if (problem is not null)
                { evidence.Add($"Exact merchant route withheld for {container.EditorID} [{containerKey}]: {problem}."); selected.Add(containerKey); continue; }
                // The first version isolates already-supported scalar routes.
                // A gated/counting kernel must be realized at a bundle boundary,
                // rather than creating an inactive choice inside this selector.
                var proof = sourceRoutes.Select(r => r.Proof).FirstOrDefault(p => p.Unlock is null && p.Availability.Controls.Length == 0
                    && p.Availability.Structure is null);
                if (proof is null)
                { evidence.Add($"Exact merchant route withheld for {container.EditorID} [{containerKey}]: source controls require a structural bundle attachment; no inactive random-choice position is added."); selected.Add(containerKey); continue; }
                var position = positions[0];
                result.Add(new(path, new(containerKey, position.Index, position.Entry.DeepCopy()), proof));
                selected.Add(containerKey);
                evidence.Add($"Exact merchant inventory isolation: {container.EditorID} [{containerKey}] entry {position.Index}, reference {root}, count {position.Entry.Item.Count}; complete source inventory-entry proof retained. Clone {path.Length} LVLI node(s), preserve entry ownership and other stock.");
            }
            if (path.Length >= 16) continue;
            foreach (var parent in graph.Parents.GetValueOrDefault(root, []).Select(p => p.List).Distinct().Order())
            {
                if (path.Contains(parent)) continue;
                var edges = (graph.Lists[parent].Entries ?? []).Where(e => e.Data?.Reference.FormKey == root).ToArray();
                if (edges.Length == 0 || edges.Any(e => e.Data is not { Count: > 0 })) continue;
                todo.Enqueue(path.Append(parent).ToArray());
            }
        }
        return result;
    }

    private static string IsolationIdentity(IReadOnlyList<FormKey> path, MerchantInventoryEdge? edge)
        => edge is null ? PathText(path) : $"merchant|{edge.Identity}|{PathText(path)}";

    private static FormKey MerchantIsolatedCopy(IReadOnlyList<FormKey> path, MerchantInventoryEdge edge,
        string category, Catalog catalog)
        => RecipientBranchCopy(path, IsolationIdentity(path, edge), $"merchant-node|{edge.Identity}", "MerchantIsolated",
            category, catalog, new(path[^1], Merchant: edge), root =>
            {
                if (catalog.MerchantInventoryRewrites.TryGetValue((edge.Container, edge.Index), out var prior)
                    && !prior.PreservedStockParent.IsNull)
                {
                    var stock = (LeveledItem)catalog.Lists[prior.PreservedStockParent];
                    var preserved = stock.Entries![0].Data!;
                    if (preserved.Reference.FormKey != path[^1])
                        throw new InvalidDataException($"Merchant preserved stock changed beneath {prior.Root}.");
                    preserved.Reference.SetTo(root);
                    MarkEntryReferencesChanged(prior.PreservedStockParent, catalog);
                }
                else catalog.MerchantInventoryRewrites[(edge.Container, edge.Index)] = new(edge, root);
            });

    private static FormKey RecipientBranchCopy(IReadOnlyList<FormKey> path, string identity, string nodePrefix, string editorPrefix,
        string category, Catalog catalog, IsolatedCopyScope scope, Action<FormKey> redirectRoot)
    {
        if (catalog.SpecializedPathClones.TryGetValue(identity, out var found)) return found;
        var graph = SpecializedGraph(catalog);
        LeveledItem? parent = null;
        FormKey childKey = default;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var originalKey = path[i];
            var nodeIdentity = $"{nodePrefix}|{string.Join('>', path.Skip(i))}";
            if (!catalog.SpecializedPathClones.TryGetValue(nodeIdentity, out childKey))
            {
                var original = graph.Lists[originalKey];
                childKey = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                var clone = new LeveledItem(childKey, SkyrimRelease.SkyrimSE)
                {
                    EditorID = $"{editorPrefix}_{original.EditorID}", Flags = original.Flags, ChanceNone = original.ChanceNone,
                    Entries = new((original.Entries ?? []).Select(e => e.DeepCopy()))
                };
                clone.Global.SetTo(original.Global.FormKey);
                catalog.AddList(childKey, clone);
                catalog.Fallbacks.Add(childKey, new(nodeIdentity, category, clone));
                catalog.SpecializedPathClones[nodeIdentity] = childKey;
                catalog.IsolatedCopyScopes[childKey] = scope;
                catalog.CopyTopology.RegisterClone(childKey, originalKey);
                CoordinateMaterialCopy(originalKey, childKey, category, catalog);
                CoordinateBundleSlotCopy(originalKey, childKey, category, catalog);
                if (parent is null) redirectRoot(childKey);
                else
                {
                    var positions = (parent.Entries ?? []).Where(e => e.Data?.Reference.FormKey == originalKey).ToArray();
                    if (positions.Length == 0) throw new InvalidDataException($"Recipient isolation lost {originalKey} beneath {parent.FormKey}.");
                    // Rewrite in place: COED, count, level, repeated weights and
                    // every unselected sibling remain at their original positions.
                    foreach (var position in positions) position.Data!.Reference.SetTo(childKey);
                    MarkEntryReferencesChanged(parent.FormKey, catalog);
                }
            }
            parent = (LeveledItem)catalog.Lists[MaterialContents(childKey, catalog)];
        }
        catalog.SpecializedPathClones[identity] = childKey;
        return childKey;
    }

    private static void WriteMerchantInventoryIsolations(Catalog catalog, SkyrimMod output, IReadOnlyDictionary<FormKey, FormKey> remapped)
    {
        foreach (var rewrite in catalog.MerchantInventoryRewrites.Values.OrderBy(r => r.Edge.Container).ThenBy(r => r.Edge.Index))
        {
            var edge = rewrite.Edge;
            if (!catalog.ConsumerRecords.Containers.TryGetValue(edge.Container, out var original) || original.IsDeleted
                || original.Items is null || edge.Index < 0 || edge.Index >= original.Items.Count
                || original.Items[edge.Index].Item.Item.FormKey != edge.Original.Item.Item.FormKey
                || original.Items[edge.Index].Item.Count != edge.Original.Item.Count)
                throw new InvalidDataException($"Exact merchant inventory entry changed: {edge.Identity}. No output was installed.");
            var container = output.Containers.GetOrAddAsOverride(original);
            // Restore this exact complete frozen entry before changing its link.
            container.Items![edge.Index] = edge.Original.DeepCopy();
            container.Items[edge.Index].Item.Item.SetTo(remapped[rewrite.Root]);
            catalog.DistributionNotes.Add($"Written container recipient {original.EditorID} [{edge.Container}], inventory entry {edge.Index}: {edge.Original.Item.Item.FormKey} → {remapped[rewrite.Root]}, count {edge.Original.Item.Count}; original ownership retained.");
        }
    }
}
