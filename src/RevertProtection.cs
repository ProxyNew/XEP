using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // A Revert-only script is different from a script that changes flags, fields or
    // unknown receivers. Never infer reset ownership from a list's defining plugin.
    private static bool ForeignRevert(FormKey target, ScriptUnlock unlock, Catalog catalog) =>
        catalog.VerifiedRevertLists.TryGetValue(target, out var owners) && owners.Count > 0
        && owners.All(p => p != (unlock.OwnerQuest.IsNull ? unlock.Quest : unlock.OwnerQuest).ModKey)
        && !catalog.OtherRuntimeMutations.Contains(target) && !catalog.RuntimeAddPayloadProblems.ContainsKey(target);

    private static string? RevertSwitchProblem(FormKey target, Catalog catalog)
    {
        if (catalog.NativeLists?.HasBroadRules == true)
            return "SkyPatcher broad selectors have no verified selective Revert-switch output composition";
        // This writer builds an inactive copy as well as an original-list override.
        // Until both are projected against native inputs, do not bake imported
        // entries into either side of a selective Revert switch.
        if (catalog.NativeLists?.Appended.ContainsKey(target) == true)
            return "SkyPatcher additions on this destination have no verified selective Revert-switch output composition";
        if (catalog.LlosLists?.Changes(target) == true)
            return "LLOS changes this destination at startup; no selective Revert-switch output composition is verified";
        if (catalog.RuntimeAddPayloadProblems.TryGetValue(target, out var bindingProblem)) return "destination runtime frame is unproved: " + bindingProblem;
        var list = catalog.Lists[target];
        if (list.IsDeleted || list.Entries is not { Count: > 0 }) return "empty or deleted destination";
        // Appending an inactive copy to a bundle would award the originals twice.
        // Keep ordinary parent flags, and put GiveAll only on the new switch.
        if ((int)list.Flags is not (0 or 1 or 2 or 3)) return "Give All or unsupported parent flags would duplicate inactive awards";
        if (list.Entries.Any(e => e.Data is not { Count: 1 } || e.ExtraData is not null))
            return "destination has non-unit counts or entry conditions";
        var ancestors = new HashSet<FormKey> { target };
        var pending = new Queue<FormKey>(); pending.Enqueue(target);
        while (pending.TryDequeue(out var key))
        {
            bool PermanentEdge(FormKey parent) => catalog.Lists.TryGetValue(parent, out var containing)
                && (containing.Entries ?? []).Any(e => e.Data?.Reference.FormKey == key);
            // A reset on an ancestor cannot recurse down an ESP-written edge.
            // A script-only incoming edge is not protected by this destination's switch.
            var scriptParents = catalog.ScriptInsertionParents.GetValueOrDefault(key, [])
                .Concat(catalog.RuntimePlacements.Where(r => r.Child == key).Select(r => r.Target.List));
            if (scriptParents.Any(parent => !PermanentEdge(parent)))
                return "a containing connection is established by AddForm rather than a permanent entry";
            foreach (var parent in catalog.DirectParents.GetValueOrDefault(key, []))
            {
                if (parent.Count != 1) return "a containing list requests multiple copies";
                if (catalog.RuntimeControlledLists.ContainsKey(parent.List)
                    && (!PermanentEdge(parent.List)
                        || !catalog.VerifiedRevertLists.TryGetValue(parent.List, out var resetters) || resetters.Count == 0
                        || catalog.OtherRuntimeMutations.Contains(parent.List) || catalog.RuntimeAddPayloadProblems.ContainsKey(parent.List)))
                    return "containing list has mutations other than a verified Revert across a permanent entry";
                if (ancestors.Add(parent.List)) pending.Enqueue(parent.List);
            }
        }
        if (catalog.ConsumerRecords.Containers.Values.Any(c => (c.Items ?? []).Any(e => ancestors.Contains(e.Item.Item.FormKey) && e.Item.Count != 1))
            || catalog.ConsumerRecords.Npcs.Values.Any(c => (c.Items ?? []).Any(e => ancestors.Contains(e.Item.Item.FormKey) && e.Item.Count != 1)))
            return "an inventory consumer requests multiple copies";
        return null;
    }

    private static string? SwitchLevelProblem(ILeveledItemGetter list, RuntimePlacement[] routes)
    {
        if (routes.Any(r => r.Target.Count != 1) || routes.Select(r => r.Target.Level).Distinct().Count() != 1)
            return "one switch position requires a common insertion level and unit count";
        var levels = (list.Entries ?? []).Where(e => e.Data is not null).Select(e => e.Data!.Level).ToArray();
        if (levels.Length == 0) return "inactive selection is empty";
        var level = routes[0].Target.Level;
        if (((int)list.Flags & 1) == 0 && !levels.Contains(level))
            return "switch would introduce a new highest-eligible tier and suppress existing selections";
        if (level < levels.Min()) return "switch becomes eligible before its inactive selection";
        return null;
    }

    private static string? RevertSwitchGroupProblem(FormKey target, RuntimePlacement[] routes, Catalog catalog,
        int? projectedPhysicalEntries = null)
    {
        var reason = routes.Any(r => !ForeignRevert(target, r.Unlock, catalog))
            ? "source-owned reset or other unverified mutations" : RevertSwitchProblem(target, catalog);
        if (reason is not null) return reason;
        if (SwitchFamilies(routes).Count() > 255) return "active selection exceeds 255 entries";
        if (SwitchFamilies(routes).Any(f => FamilyUnlocks(f).Length > 128))
            return "one family exceeds the supported 128 alternative unlocks";
        reason = SwitchLevelProblem(catalog.Lists[target], routes);
        if (reason is not null) return reason;
        if (projectedPhysicalEntries is { } count && count + 1 > 255)
            return $"destination has {count} written positions and no room for the permanent switch";
        return null;
    }

    // Unsupported protected destinations are a route refusal, not a reason for an
    // otherwise valid preview or generation to abort. This runs after every planner
    // has contributed its routes, because common levels, family/trigger limits and
    // final physical capacity are properties of the complete destination group.
    private static void FinalizeProtectedRuntimePlacements(PlanningAdditions<VariantPlan> plans, Catalog catalog, int warningLimit)
    {
        // Returns the reservations when nothing was rejected, so they still apply.
        StaticCapacityReservations? RejectDirectOverflow()
        {
            if (catalog.ShapePreservingDirectPlacements.Count == 0) return null;
            var reservations = EffectiveStaticEntries.Reserve(catalog, plans);
            var copies = catalog.SpecializedCloneOriginals.ToLookup(p => p.Value, p => p.Key);
            var rejected = catalog.ShapePreservingDirectPlacements.Where(p =>
                InheritedCopyTargets(p.Target, copies, catalog).Prepend(p.Target)
                    .Any(t => reservations.ReservationFor(t) is { } reservation
                        && (reservation.ProposedEntries > 255 || reservation.GeneratedSublists > 0))).ToArray();
            if (rejected.Length == 0) return reservations;
            foreach (var placement in rejected)
            {
                for (var i = 0; i < plans.Count; i++)
                {
                    var plan = plans[i];
                    if (plan.Source.FormKey != placement.Source
                        || VariantIdentity(plan.Template.Item, plan.Template.Enchantment, catalog) != placement.Variant) continue;
                    var parents = plan.Parents.Where(p => p.List != placement.Target).ToArray();
                    if (parents.Length != plan.Parents.Count)
                        plans[i] = plan with { Parents = parents,
                            PlacementReason = parents.Length == 0
                                ? "Flat enchanted terminal lacks capacity for a direct peer entry without probability-changing overflow"
                                : plan.PlacementReason };
                }
                catalog.DistributionNotes.Add($"{placement.Source}: flat enchanted terminal {catalog.Lists[placement.Target].EditorID} "
                    + "would exceed 255 entries and require probability-changing overflow; direct material expansion withheld.");
                catalog.ShapePreservingDirectPlacements.Remove(placement);
            }
            return null;
        }

        if (RedirectLlosGeneratedPlacements(plans, catalog)) PruneUnreachableFallbacks(plans, catalog, []);
        var unchanged = RejectDirectOverflow();
        var reservations = WithdrawNativeConflicts(plans, catalog) || unchanged is null
            ? EffectiveStaticEntries.Reserve(catalog, plans) : unchanged;
        var rejectedRoutes = new List<(RuntimePlacement Route, string Reason)>();
        foreach (var group in catalog.RuntimePlacements.GroupBy(r => r.Target.List))
        {
            // A list LLOS creates at startup has no plugin record a quest property can name.
            if (catalog.LlosLists?.IsGenerated(group.Key) == true)
            {
                rejectedRoutes.AddRange(group.Select(r => (r, "LLOS creates this list at startup, so XEP cannot target it")));
                continue;
            }
            if (!catalog.VerifiedRevertLists.ContainsKey(group.Key)) continue;
            var routes = group.ToArray();
            var physical = reservations.ReservationFor(group.Key)?.EntriesAfterSplitting
                ?? catalog.Lists[group.Key].Entries?.Count ?? 0;
            var reason = RevertSwitchGroupProblem(group.Key, routes, catalog, physical);
            if (reason is not null) rejectedRoutes.AddRange(routes.Select(r => (r, reason)));
        }
        if (rejectedRoutes.Count == 0) return;

        foreach (var (route, reason) in rejectedRoutes)
        {
            catalog.RuntimePlacements.Remove(route);
            catalog.DistributionNotes.Add($"{route.Source}: runtime placement into {catalog.Lists[route.Target.List].EditorID} withheld. "
                + $"Light switch unavailable: {reason}.");
        }
        PruneUnreachableFallbacks(plans, catalog, rejectedRoutes.Select(r => r.Route.Source).ToHashSet());
    }

    private static void PruneUnreachableFallbacks(PlanningAdditions<VariantPlan> plans, Catalog catalog, HashSet<FormKey> rejectedSources)
    {
        var reachable = new HashSet<FormKey>(); var todo = new Queue<FormKey>();
        void Root(FormKey key) { if (catalog.Fallbacks.ContainsKey(key) && reachable.Add(key)) todo.Enqueue(key); }
        foreach (var list in catalog.Lists.Where(p => !catalog.Fallbacks.ContainsKey(p.Key)).Select(p => p.Value))
            foreach (var entry in list.Entries ?? []) if (entry.Data is { } data) Root(data.Reference.FormKey);
        foreach (var edge in catalog.FallbackEdges.Where(e => !catalog.Fallbacks.ContainsKey(e.Parent.List))) Root(edge.Child);
        foreach (var route in catalog.RuntimePlacements) Root(route.Child);
        foreach (var root in catalog.MerchantContainerRoots.Values) Root(root);
        foreach (var rewrite in catalog.MerchantInventoryRewrites.Values) Root(rewrite.Root);
        foreach (var rewrite in catalog.LootRecipientRewrites.Values) Root(rewrite.Root);
        // One parent index instead of an edge scan per visited list.
        var edgesByParent = catalog.FallbackEdges.ToLookup(e => e.Parent.List);
        while (todo.TryDequeue(out var key))
        {
            foreach (var entry in catalog.Lists[key].Entries ?? []) if (entry.Data is { } data) Root(data.Reference.FormKey);
            foreach (var edge in edgesByParent[key]) Root(edge.Child);
        }
        var removed = catalog.Fallbacks.Keys.Where(k => !reachable.Contains(k)).ToHashSet();
        if (removed.Count == 0) return;
        foreach (var key in removed) { catalog.Fallbacks.Remove(key); catalog.RemoveList(key); }
        catalog.FallbackEdges.RemoveAll(e => removed.Contains(e.Parent.List) || removed.Contains(e.Child));
        catalog.RuntimePlacements.RemoveAll(r => removed.Contains(r.Child) || removed.Contains(r.Target.List));
        catalog.RebuiltFallbackRoots.RemoveWhere(removed.Contains);
        foreach (var key in catalog.MaterialChanceApplications.Keys.Where(removed.Contains).ToArray()) catalog.MaterialChanceApplications.Remove(key);
        foreach (var key in catalog.MaterialGlobalApplications.Keys.Where(removed.Contains).ToArray()) catalog.MaterialGlobalApplications.Remove(key);
        foreach (var key in catalog.MaterialChanceWrappers.Keys.Where(k => removed.Contains(k.Child)
                     || removed.Contains(catalog.MaterialChanceWrappers[k])).ToArray()) catalog.MaterialChanceWrappers.Remove(key);
        foreach (var key in catalog.MaterialGlobalWrappers.Keys.Where(k => removed.Contains(k.Child)
                     || removed.Contains(catalog.MaterialGlobalWrappers[k])).ToArray()) catalog.MaterialGlobalWrappers.Remove(key);
        foreach (var key in catalog.MaterialWindowWrappers.Keys.Where(k => removed.Contains(k.Child)
                     || removed.Contains(catalog.MaterialWindowWrappers[k])).ToArray()) catalog.MaterialWindowWrappers.Remove(key);
        foreach (var key in catalog.SharedAvailabilityChains.Keys.Where(k => removed.Contains(catalog.SharedAvailabilityChains[k].Container)
                     || removed.Contains(catalog.SharedAvailabilityChains[k].Top)).ToArray()) catalog.SharedAvailabilityChains.Remove(key);
        if (removed.Contains(catalog.AvailabilityEmptyLeaf)) catalog.AvailabilityEmptyLeaf = default;
        foreach (var key in catalog.SpecializedPathClones.Keys.Where(k => removed.Contains(catalog.SpecializedPathClones[k])).ToArray()) catalog.SpecializedPathClones.Remove(key);
        catalog.RecipientCopies.Clear();
        foreach (var key in catalog.IsolatedCopyScopes.Keys.Where(removed.Contains).ToArray()) catalog.IsolatedCopyScopes.Remove(key);
        catalog.CopyTopology.Prune(removed);
        foreach (var key in catalog.ReplicatedSelectors.Keys.Where(k => removed.Contains(catalog.ReplicatedSelectors[k])).ToArray()) catalog.ReplicatedSelectors.Remove(key);
        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i]; var parents = plan.Parents.Where(p => !removed.Contains(p.List)).ToArray();
            if (parents.Length != plan.Parents.Count)
                plans[i] = plan with { Parents = parents, PlacementReason = parents.Length == 0
                    ? "Protected runtime destination cannot be represented safely; generated family was pruned" : plan.PlacementReason };
        }
        foreach (var source in rejectedSources)
            if (!plans.Any(p => p.Source.FormKey == source && p.Parents.Count > 0))
                foreach (var plan in plans.Where(p => p.Source.FormKey == source))
                    plan.Candidate.Reason = "Light switch unavailable for the only authorized runtime destination; variants withheld";
    }

    private static Dictionary<FormKey, RuntimePlacement[]> RevertSwitchGroups(Catalog catalog)
    {
        var result = new Dictionary<FormKey, RuntimePlacement[]>();
        foreach (var group in catalog.RuntimePlacements.GroupBy(r => r.Target.List))
        {
            if (!catalog.VerifiedRevertLists.ContainsKey(group.Key)) continue;
            var routes = group.ToArray();
            var reason = RevertSwitchGroupProblem(group.Key, routes, catalog);
            if (reason is not null)
                throw new InvalidDataException($"Revert protection cannot safely wrap {catalog.Lists[group.Key].EditorID} [{group.Key}]: {reason}. No output was installed.");
            result.Add(group.Key, routes);
        }
        return result;
    }

    private static int RuntimeRecordCount(Catalog catalog)
    {
        var groups = RevertSwitchGroups(catalog);
        return catalog.RuntimePlacements.Count - groups.Values.Sum(r => r.Length)
            + groups.Sum(g => 7 + SwitchFamilies(g.Value).Count()) + ScriptGateRecordCount(catalog);
    }

    // A single controller owns each family, even if several quests can unlock it.
    // This avoids check-then-add races between separate controllers for one child.
    private static IEnumerable<IGrouping<FormKey, RuntimePlacement>> SwitchFamilies(IEnumerable<RuntimePlacement> routes) =>
        routes.GroupBy(r => r.Child).OrderBy(g => g.Key);

    private static ScriptUnlock[] FamilyUnlocks(IEnumerable<RuntimePlacement> routes) =>
        routes.Select(r => r.Unlock).DistinctBy(u => (u.Quest, u.Stage, u.AtStart))
            .OrderBy(u => u.Quest).ThenBy(u => u.Stage).ThenBy(u => u.AtStart).ToArray();

    private static List<ListImpact> RevertSwitchImpacts(List<ListImpact> impacts, Catalog catalog, int warningLimit)
    {
        var result = impacts.ToDictionary(i => FormKey.Factory(i.FormKey));
        foreach (var target in RevertSwitchGroups(catalog).Keys)
        {
            var list = catalog.Lists[target];
            var before = catalog.WinningLists.GetValueOrDefault(target)?.Entries?.Count ?? list.Entries!.Count;
            var previous = result.GetValueOrDefault(target);
            var after = (previous?.After ?? list.Entries!.Count) + 1;
            result[target] = new(target.ToString(), list.EditorID ?? "", before, after - before, after,
                after > warningLimit, previous?.GeneratedSublists ?? 0,
                (previous?.EntriesAfterSplitting ?? previous?.After ?? list.Entries!.Count) + 1);
        }
        return result.Values.OrderByDescending(i => i.After).ThenBy(i => i.EditorId).ToList();
    }

    // Allocate globals first and controllers last. All shared destination overrides
    // live in the final output plugin, just as the normal distribution writer does.
    private static void WriteRevertSwitches(Catalog catalog, SkyrimMod[] patches,
        HashSet<uint>[] usedIds, HashSet<string> editorIds, Dictionary<FormKey, FormKey> remapped,
        ref int nextRecord, HashSet<FormKey> changedLists)
    {
        var ordinal = nextRecord;
        (SkyrimMod Mod, FormKey Key) Allocate(string identity)
        {
            var index = OutputLayout.For(patches[0]).PatchIndex(ordinal++);
            return (patches[index], StableKey(patches[index], identity, usedIds[index]));
        }
        foreach (var (target, routes) in RevertSwitchGroups(catalog))
        {
            var identity = $"revert-switch-v3|{target}";
            var name = catalog.Lists[target].EditorID ?? "List";
            var publicKey = remapped.GetValueOrDefault(target, target);
            var current = patches.Select(p => p.LeveledItems.TryGetValue(publicKey, out var l) ? l : null).FirstOrDefault(l => l is not null)
                ?? catalog.Lists[target];
            var originalEntries = (current.Entries ?? []).Select(e => e.DeepCopy()).ToArray();
            if (originalEntries.Length >= 255 || SwitchFamilies(routes).Count() > 255)
                throw new InvalidDataException($"Revert switch {name} exceeds the supported parent or active-selection capacity.");
            var problem = SwitchLevelProblem(current, routes);
            if (problem is not null || (int)current.Flags > 3 || originalEntries.Any(e => e.Data is not { Count: 1 } || e.ExtraData is not null))
                throw new InvalidDataException($"Revert switch {name}: final written selection is unsupported ({problem ?? "flags/counts/entry data"}). No output was installed.");
            GlobalFloat Global(string suffix, float value)
            {
                var a = Allocate(identity + suffix);
                var g = new GlobalFloat(a.Key, SkyrimRelease.SkyrimSE) { EditorID = EditorId($"Switch_{name}_{suffix}", editorIds), Data = value };
                a.Mod.Globals.Add(g); return g;
            }
            var off = Global("OriginalChance", 0); var on = Global("ExpandedChance", 100);
            LeveledItem List(string suffix, LeveledItem.Flag flags, IEnumerable<LeveledItemEntry> entries)
            {
                var a = Allocate(identity + suffix);
                var l = new LeveledItem(a.Key, SkyrimRelease.SkyrimSE)
                { EditorID = EditorId($"Switch_{name}_{suffix}", editorIds), Flags = flags, Entries = new ExtendedList<LeveledItemEntry>(entries) };
                a.Mod.LeveledItems.Add(l); return l;
            }
            var original = List("Original", current.Flags, originalEntries.Select(e => e.DeepCopy()));
            var expanded = List("ActiveVariants", current.Flags, []);
            var originalGate = List("OriginalGate", (LeveledItem.Flag)1, [SwitchEntry(original.FormKey)]);
            var expandedGate = List("ExpandedGate", (LeveledItem.Flag)1, [SwitchEntry(expanded.FormKey)]);
            originalGate.Global.SetTo(off); expandedGate.Global.SetTo(on);
            var selector = List("Switch", (LeveledItem.Flag)4, [SwitchEntry(originalGate.FormKey), SwitchEntry(expandedGate.FormKey)]);
            var root = patches[^1].LeveledItems.GetOrAddAsOverride(current);
            root.Entries = new(originalEntries.Select(e => e.DeepCopy()));
            root.Entries.Add(SwitchEntry(selector.FormKey, routes[0].Target.Level));
            // The parent keeps flags, entries and its original chance/global roll.
            // The inactive copy duplicates entries, not that outer chance roll.
            // Existing child controls remain on their original referenced records.
            changedLists.Add(target);
            foreach (var family in SwitchFamilies(routes))
            {
                var a = Allocate(identity + "|Controller|" + family.Key);
                var unlocks = FamilyUnlocks(family);
                var q = new Quest(a.Key, SkyrimRelease.SkyrimSE)
                { EditorID = EditorId($"Switch_{name}_Controller", editorIds), Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce, VirtualMachineAdapter = new() };
                if (unlocks.Any(u => u.AtStart)) q.Stages.Add(new() { Index = 0, Flags = QuestStage.Flag.StartUpStage });
                var script = new ScriptEntry { Name = "XEP_QuestInsertion" };
                void Link(string n, FormKey k) => script.Properties.Add(new ScriptObjectProperty { Name = n, Flags = ScriptProperty.Flag.Edited, Object = new FormLink<ISkyrimMajorRecordGetter>(k) });
                Link("TargetList", expanded.FormKey);
                Link("VariantList", remapped.GetValueOrDefault(family.Key, family.Key));
                Link("OriginalChance", off.FormKey); Link("ExpandedChance", on.FormKey);
                script.Properties.Add(new ScriptIntProperty { Name = "EntryLevel", Flags = ScriptProperty.Flag.Edited, Data = family.First().Target.Level });
                script.Properties.Add(new ScriptObjectListProperty { Name = "SourceQuests", Flags = ScriptProperty.Flag.Edited,
                    Objects = new(unlocks.Select(u => new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(u.AtStart ? q.FormKey : u.Quest) })) });
                script.Properties.Add(new ScriptIntListProperty { Name = "SourceStages", Flags = ScriptProperty.Flag.Edited,
                    Data = new(unlocks.Select(u => u.AtStart ? 0 : (int)u.Stage)) });
                q.VirtualMachineAdapter.Scripts.Add(script); a.Mod.Quests.Add(q);
            }
        }
        nextRecord = ordinal;
    }

    private static LeveledItemEntry SwitchEntry(FormKey key, short level = 1, short count = 1) =>
        new() { Data = new() { Reference = new FormLink<IItemGetter>(key), Level = level, Count = count } };
}
