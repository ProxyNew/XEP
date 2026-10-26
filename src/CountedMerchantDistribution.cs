using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record CountedMerchantRequest(CandidateItem Candidate, SpecializedRoute[] Routes);
    private static void AssessCountedMerchantRoutes(FormKey sourceKey, EquipmentClass cls, bool expandMaterials,
        Catalog catalog, SpecializedAssessment result)
    {
        using var timing = PhaseTiming.Measure("review.counted-merchant");
        var graph = SpecializedGraph(catalog);
        // No scalar caller is opted in. This planner owns a separate additive
        // bundle and can realize the complete source kernel, including no-source
        // outcomes, without competing with the preserved encrypted stock choice.
        if (!graph.Up(sourceKey).Any(k => graph.Lists.TryGetValue(k, out var list)
            && (GivesAllEntries(list) && (!list.Global.IsNull || list.ChanceNone.Value != 0)
                || (list.Entries ?? []).Any(e => e.Data is { Count: > 1 })))) return;
        if (catalog.UnsafeAvailability.ContainsKey(sourceKey) || catalog.ScriptUnlocks.ContainsKey(sourceKey)) return;
        var cached = SharedMemo(catalog.MerchantStructuredSourceRoutes, sourceKey, () =>
        {
            var evidence = new List<string>();
            var paths = MerchantSourceRoutes(sourceKey, catalog, evidence, allowStructured: true);
            return (Routes: paths, Evidence: evidence.Distinct().ToArray());
        });
        result.Evidence.AddRange(cached.Evidence);
        var sources = cached.Routes.Where(r => r.Proof.Availability.Structure is not null)
            .DistinctBy(r => r.Proof.Availability.Structure!.Identity).ToList();
        if (sources.Count == 0) return;
        IMajorRecordGetter source = catalog.Weapons.GetValueOrDefault(sourceKey) ?? (IMajorRecordGetter)catalog.Armors[sourceKey];
        var shape = CachedShape(source, catalog);
        if (shape is null) return;
        if (DistributionSubtype(source, catalog) is null)
        { result.Evidence.Add("Counted merchant route withheld: unknown or conflicting source subtype."); return; }
        var vendors = MerchantContainersFor(source, catalog).ToHashSet();
        var selected = catalog.Templates.Select(t => t.Item.FormKey).ToHashSet();
        var templates = graph.AllTemplates.Where(t => selected.Contains(t.Item.FormKey) && TemplateAllowed(source.FormKey, t, catalog) && ShapeMatches(shape, t.Class)
            && (t.Class.Material == cls.Material || expandMaterials && !IsOwnBaseMaterial(cls.Material) && !IsOwnBaseMaterial(t.Class.Material))).ToArray();
        foreach (var sourceRoute in sources.Where(r => vendors.Contains(r.Container)).OrderBy(r => r.Container).ThenBy(r => r.InventoryIndex))
        {
            var container = catalog.ConsumerRecords.Containers[sourceRoute.Container];
            var isolationProblem = MerchantContainerIsolationProblem(sourceRoute.Container, catalog);
            if (isolationProblem is not null)
            { result.Evidence.Add($"Counted merchant route withheld for {container.EditorID}: {isolationProblem}."); continue; }
            var positions = (container.Items ?? []).Select((e, i) => (Entry: e, Index: i))
                .Where(p => p.Entry.Item.Count > 0 && graph.Lists.ContainsKey(p.Entry.Item.Item.FormKey))
                .OrderBy(p => p.Index).ToArray();
            var accepted = false;
            foreach (var position in positions)
            {
                var root = position.Entry.Item.Item.FormKey;
                var rootList = graph.Lists[root];
                if (rootList.IsDeleted || graph.HasAncestorCycle(root) || ((int)rootList.Flags & ~15) != 0
                    || catalog.RuntimeControlledLists.ContainsKey(root) || catalog.ScriptAddTargets.Contains(root)
                    || catalog.UnsafeAvailability.ContainsKey(root) || catalog.UnresolvedScriptPayloads.Contains(root)) continue;
                var matches = templates.SelectMany(t => t.Parents.Select(p => (Template: t, Parent: p)))
                    .Where(t => graph.Up(t.Parent.List).Contains(root)
                        && CountedDestinationTerminalSafe(t.Parent.List, source, cls, catalog)
                        && CountedDestinationPathSafe(t.Parent.List, root, catalog)).ToArray();
                if (matches.Length == 0) continue;
                if (position.Entry.Item.Count != 1)
                { result.Evidence.Add($"Counted merchant route withheld for {container.EditorID}, entry {position.Index}: destination inventory count {position.Entry.Item.Count} would multiply source quantities."); continue; }
                // Root controls cannot be moved outside their original branch or
                // independently applied twice. This first bundle boundary accepts
                // an uncontrolled original root; deeper authored controls remain
                // inside that unchanged root, with their original correlation.
                if (!rootList.Global.IsNull || rootList.ChanceNone.Value != 0)
                { result.Evidence.Add($"Counted merchant route withheld for {container.EditorID}, entry {position.Index}: inventory root chance/global needs a joint destination-control proof."); continue; }
                var edge = new MerchantInventoryEdge(sourceRoute.Container, position.Index, position.Entry.DeepCopy());
                foreach (var match in matches)
                    result.Routes.Add(new(match.Template, match.Parent, default, SourceProof: sourceRoute.Proof,
                        MerchantIsolation: edge, CountedMerchantSource: sourceRoute));
                result.Evidence.Add($"Counted merchant inventory route {container.EditorID} [{sourceRoute.Container}], entry {position.Index}: preserved original stock plus an additional {sourceRoute.Proof.Availability.Structure!.ProjectionScope} opportunity. The selected source sampler retains levels, flags, weights, counts and gates; non-source outcomes produce no extra family. Total inventory quantity and joint original/source correlation are not asserted unchanged.");
                accepted = true;
                // Retain all authorized exact positions. Final joint planning
                // chooses one common boundary before allocating any families.
            }
            if (!accepted) result.Evidence.Add($"Counted merchant source proven for {container.EditorID} [{sourceRoute.Container}], entry {sourceRoute.InventoryIndex}, but no safe matching encrypted-stock inventory boundary is supported.");
        }
    }

    private static bool CountedDestinationTerminalSafe(FormKey terminal, IMajorRecordGetter source, EquipmentClass cls, Catalog catalog)
    {
        using var timing = PhaseTiming.Measure("review.counted-destination-terminal");
        var list = SpecializedGraph(catalog).Lists[terminal];
        var entries = list.Entries ?? [];
        if (list.IsDeleted || ((int)list.Flags & ~11) != 0 || entries.Count == 0
            || entries.Any(e => e.Data is not { Count: 1 } || e.ExtraData is not null)) return false;
        var equipment = entries.Select(e => catalog.Weapons.GetValueOrDefault(e.Data!.Reference.FormKey)
            ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(e.Data!.Reference.FormKey)).ToArray();
        if (equipment.Any(e => e is null || !TryEnchantment(e, out _) || !OrdinaryEquipment(e, catalog.KeywordNames))) return false;
        var classes = equipment.Select(e => CachedClass(e!, catalog)).ToArray();
        if (classes.Any(c => c is null || SpecializedGroup(c) != SpecializedGroup(cls))) return false;
        var mixedCategory = classes.Select(c => c!.Type).Distinct().Count() > 1;
        return mixedCategory || equipment.Any(e => DistributionSubtype(e!, catalog) == DistributionSubtype(source, catalog));
    }

    private static bool CountedDestinationPathSafe(FormKey terminal, FormKey root, Catalog catalog)
    {
        using var timing = PhaseTiming.Measure("review.counted-destination-path");
        var graph = SpecializedGraph(catalog);
        var seen = new HashSet<FormKey>(); var todo = new Queue<FormKey>(); todo.Enqueue(terminal);
        while (todo.TryDequeue(out var key))
        {
            if (!seen.Add(key)) continue;
            if (seen.Count > 8192) return false;
            var list = graph.Lists[key];
            if (list.IsDeleted || ((int)list.Flags & ~15) != 0 || graph.HasAncestorCycle(key)
                || catalog.RuntimeControlledLists.ContainsKey(key) || catalog.ScriptAddTargets.Contains(key)
                || catalog.UnsafeAvailability.ContainsKey(key) || catalog.UnresolvedScriptPayloads.Contains(key)
                || list.Global.IsNull && list.ChanceNone.Value >= 1) continue;
            if (key == root) return true;
            foreach (var parent in graph.Parents.GetValueOrDefault(key, []).Select(p => p.List).Distinct())
                if (graph.Up(parent).Contains(root) && (graph.Lists[parent].Entries ?? [])
                    .Any(e => e.Data is { Count: > 0 } d && d.Reference.FormKey == key)) todo.Enqueue(parent);
        }
        return false;
    }

    private static bool PlannedTreeContains(FormKey root, FormKey target, Catalog catalog)
    {
        var seen = new HashSet<FormKey>(); var todo = new Stack<FormKey>(); todo.Push(root);
        while (todo.TryPop(out var key))
        {
            if (key == target) return true;
            if (!seen.Add(key)) continue;
            if (seen.Count > 8192) return true; // uncertain overlap is never permission for another copy
            if (catalog.Lists.TryGetValue(key, out var list))
                foreach (var entry in list.Entries ?? []) if (entry.Data is { } d) todo.Push(d.Reference.FormKey);
            foreach (var edge in catalog.FallbackEdges.Where(e => e.Parent.List == key)) todo.Push(edge.Child);
        }
        return false;
    }

    private static void BuildCountedMerchantPlans(CandidateItem candidate, Catalog catalog, ProfileSettings profile,
        PlanningAdditions<VariantPlan> plans, SpecializedRoute[] routes)
    {
        if (routes.Length > 0) catalog.CountedMerchantRequests.Add(new(candidate, routes));
    }

    private static void FinalizeCountedMerchantPlans(Catalog catalog, ProfileSettings profile, PlanningAdditions<VariantPlan> plans)
    {
        var rows = catalog.CountedMerchantRequests.SelectMany(request => request.Routes.Select(route =>
            (request.Candidate, Route: route, Source: FormKey.Factory(request.Candidate.FormKey)))).ToArray();
        foreach (var authorEvent in rows.GroupBy(r => (r.Route.CountedMerchantSource!.Container, r.Route.CountedMerchantSource.InventoryIndex))
                     .OrderBy(g => g.Key.Container).ThenBy(g => g.Key.InventoryIndex))
        {
            // One evaluation for this exact source inventory occurrence across
            // ALL source families and destination positions. Distinct components
            // may share a probabilistic ancestor and cannot be rolled separately.
            var choices = authorEvent.GroupBy(r => (Sampler: r.Route.CountedMerchantSource!.Proof.Availability.Structure!.SamplingIdentity,
                    r.Route.MerchantIsolation!.Container, r.Route.MerchantIsolation.Index))
                .OrderByDescending(g => g.Select(r => r.Source).Distinct().Count())
                .ThenBy(g => g.First().Route.CountedMerchantSource!.Proof.Availability.Structure!.ProjectionScope.StartsWith("whole-", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(g => g.Key.Index).ThenBy(g => g.Key.Sampler, StringComparer.Ordinal)
                .ToArray();
            var chosen = choices[0]; var first = chosen.First(); var edge = first.Route.MerchantIsolation!;
            var envelope = first.Route.CountedMerchantSource!.Proof.Availability.Structure!;
            var included = chosen.Select(r => r.Source).ToHashSet();
            foreach (var withheld in authorEvent.Where(r => !included.Contains(r.Source)).DistinctBy(r => r.Source))
            {
                var reason = $"requires one compatible shared source sampler/common exact destination for source inventory entry {authorEvent.Key.InventoryIndex}; no independent second roll was added";
                catalog.DistributionNotes.Add($"{withheld.Candidate.Name} [{withheld.Source}]: counted merchant composition withheld: {reason}.");
                withheld.Candidate.Reason += "; counted merchant composition withheld: " + reason;
            }
            catalog.MerchantInventoryRewrites.TryGetValue((edge.Container, edge.Index), out var prior);
            var uncovered = chosen.GroupBy(r => r.Source).Select(g =>
            {
                var candidate = g.First().Candidate;
                var inventory = catalog.SourceEquipment!.GetValueOrDefault(g.Key, []);
                var flavors = g.GroupBy(r => VariantIdentity(r.Route.Template.Item, r.Route.Template.Enchantment, catalog))
                    .Where(f =>
                    {
                        var covered = inventory.Where(i => OrdinaryEquipment(i, catalog.KeywordNames) && TryEnchantment(i, out var e)
                            && VariantIdentity(i, e, catalog) == f.Key).Any(i => SpecializedGraph(catalog).Up(i.FormKey).Contains(edge.Original.Item.Item.FormKey)
                                && CdfCoveredRecipients(i.FormKey, [(edge.Container, "Container inventory")], catalog).Count > 0);
                        if (covered) catalog.AuthoredCounterpartCoverage.Add(g.Key);
                        return !covered;
                    })
                    .Where(f => !plans.Any(p => p.Source.FormKey == g.Key && VariantIdentity(p.Template.Item, p.Template.Enchantment, catalog) == f.Key
                        && prior is not null && p.Parents.Any(parent => PlannedTreeContains(prior.Root, parent.List, catalog))))
                    .ToArray();
                return (Source: g.Key, Candidate: candidate, Inventory: inventory, Flavors: flavors,
                    Envelope: g.First().Route.CountedMerchantSource!.Proof.Availability.Structure!);
            }).Where(g => g.Flavors.Length > 0).ToArray();
            if (uncovered.Length == 0) continue;
            void RefuseChosen(string reason)
            {
                var detail = $"Selected counted merchant opportunity at {edge.Container}, inventory entry {edge.Index}, source inventory entry {authorEvent.Key.InventoryIndex}: {reason}. Other sampling/destination alternatives were not attempted";
                catalog.DistributionNotes.Add(detail + ".");
                foreach (var group in uncovered) group.Candidate.Reason += "; " + detail;
            }
            if (uncovered.Any(g => g.Flavors.Length > 255))
            { RefuseChosen("a semantic flavor choice exceeds 255 entries; shared opportunity withheld"); continue; }
            var problem = TryJointCountedSourceEnvelope(uncovered.Select(g => g.Envelope).ToArray(), catalog, out var joint);
            if (problem is not null)
            { RefuseChosen($"shared sampler withheld: {problem}"); continue; }
            foreach (var runtimeEvidence in joint!.RuntimeDisjointEvidence)
                catalog.DistributionNotes.Add($"Counted merchant event {authorEvent.Key}: {runtimeEvidence}.");
            var families = new Dictionary<FormKey, FormKey>();
            foreach (var sourceGroup in uncovered)
            {
                var sourceKey = sourceGroup.Source; var candidate = sourceGroup.Candidate;
                IMajorRecordGetter source = catalog.Weapons.GetValueOrDefault(sourceKey) ?? (IMajorRecordGetter)catalog.Armors[sourceKey];
                var index = IndexSourcePlans(plans, sourceKey, catalog);
                var family = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                var list = new LeveledItem(family, SkyrimRelease.SkyrimSE)
                { EditorID = $"CountedMerchantVariants_{DisplayName(source)}", Flags = (LeveledItem.Flag)3, ChanceNone = new Percent(0), Entries = new() };
                catalog.AddList(family, list);
                catalog.Fallbacks.Add(family, new($"counted-merchant-family|{sourceKey}|{edge.Identity}|{sourceGroup.Envelope.Identity}", candidate.Category, list));
                families.Add(sourceKey, family);
                foreach (var flavor in sourceGroup.Flavors)
                {
                    var route = flavor.Select(r => r.Route).OrderBy(r => profile.EnchantmentProviderPlugins.FindIndex(p => p.Equals(r.Template.Enchantment.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase)))
                        .ThenBy(r => r.Template.Item.FormKey).First();
                    var parent = new ParentEntry(family, flavor.Min(r => r.Route.Destination.Level), 1);
                    if (index.TryGetValue(flavor.Key, out var old))
                        plans[old] = plans[old] with { Parents = plans[old].Parents.Append(parent).Distinct().ToArray(),
                            ParentTemplates = MergeParentTemplates(plans[old], new Dictionary<FormKey, EnchantedTemplate> { [parent.List] = route.Template }) };
                    else
                    {
                        var reused = sourceGroup.Inventory.FirstOrDefault(i => OrdinaryEquipment(i, catalog.KeywordNames) && TryEnchantment(i, out var e)
                            && VariantIdentity(i, e, catalog) == flavor.Key);
                        index[flavor.Key] = plans.Count;
                        plans.Add(new(candidate, source, route.Template, [parent], reused,
                            "Additional exact-merchant opportunity preserves joint selected-source sampling, quantities and controls beside original stock"));
                    }
                }
                candidate.Reason += $"; {sourceGroup.Flavors.Length} counted merchant semantic variant(s) use one shared {envelope.ProjectionScope} sampler";
            }
            var projection = MaterializeJointCountedSourceEnvelope(families, joint!, catalog);
            var bundle = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
            var stock = new LeveledItem(bundle, SkyrimRelease.SkyrimSE)
            { EditorID = $"MerchantStockBundle_{catalog.ConsumerRecords.Containers[edge.Container].EditorID}", Flags = (LeveledItem.Flag)4, Entries = new() };
            var preserved = prior?.Root ?? edge.Original.Item.Item.FormKey;
            stock.Entries.Add(new() { Data = new() { Reference = new FormLink<IItemGetter>(preserved), Level = 1, Count = 1 } });
            stock.Entries.Add(new() { Data = new() { Reference = new FormLink<IItemGetter>(projection), Level = 1, Count = 1 } });
            catalog.AddList(bundle, stock);
            catalog.Fallbacks.Add(bundle, new($"counted-merchant-bundle|{edge.Identity}|{envelope.SamplingIdentity}", first.Candidate.Category, stock));
            catalog.MerchantInventoryRewrites[(edge.Container, edge.Index)] = new(edge, bundle,
                prior is not null && !prior.PreservedStockParent.IsNull ? prior.PreservedStockParent : bundle);
            catalog.DistributionNotes.Add($"Written plan for exact merchant {edge.Container}, inventory entry {edge.Index}: preserved original stock reference {edge.Original.Item.Item.FormKey}, one shared {envelope.ProjectionScope} sampler for {families.Count} source families [{string.Join(", ", families.Keys.Order())}]. Source inventory entry {authorEvent.Key.InventoryIndex}; selected source edges {SourceStepsIdentity(envelope.OuterSteps)}; omitted source-bearing siblings {string.Join("; ", uncovered.SelectMany(g => g.Envelope.OmittedSourceBranches).Distinct())}; runtime targets confined to omitted siblings [{string.Join(", ", uncovered.SelectMany(g => g.Envelope.OmittedRuntimeTargets).Distinct().Order())}]. Common source selections, counts and gates are evaluated once between new families. Original/added stock joint correlation is not asserted unchanged; extra quantities follow the selected sampling opportunity with enchantment-tier eligibility and increase total stock.");
        }
    }
}
