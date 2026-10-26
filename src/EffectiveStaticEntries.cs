using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Proposed positions retain admission's accounting, including positions which
    // the writer may suppress when they reference reused equipment.
    private sealed record ProvisionalCapacityReservation(FormKey List, string EditorId,
        int Before, int ProposedEntries, int GeneratedSublists, int EntriesAfterSplitting);

    private sealed class StaticCapacityReservations(IEnumerable<ProvisionalCapacityReservation> reservations)
    {
        // Built several times per plan and read a few times: a frozen table costs more to build than it saves.
        private readonly Dictionary<FormKey, ProvisionalCapacityReservation> lists = reservations.ToDictionary(r => r.List);
        public IEnumerable<ProvisionalCapacityReservation> Reservations => lists.Values;
        public ProvisionalCapacityReservation this[FormKey key] => lists[key];
        public ProvisionalCapacityReservation? ReservationFor(FormKey key) => lists.GetValueOrDefault(key);
    }

    private readonly record struct StaticEntryReference(FormKey Existing, int? GeneratedOccurrence = null);

    private sealed class ProjectedStaticEntry
    {
        private readonly LeveledItemEntry? authored;
        private readonly short level, count;
        public ProjectedStaticEntry(ILeveledItemEntryGetter payload, StaticEntryReference reference)
        { authored = payload.DeepCopy(); Reference = reference; }
        public ProjectedStaticEntry(ParentEntry parent, StaticEntryReference reference)
        { level = parent.Level; count = parent.Count; Reference = reference; }
        public StaticEntryReference Reference { get; }
        public bool HasData => authored is null || authored.Data is not null;
        public bool HasExtraData => authored?.ExtraData is not null;
        public short? Level => authored is null ? level : authored.Data?.Level;
        public short Count => authored?.Data?.Count ?? count;
        // Callers own this complete copy, including auxiliary metadata and absent DATA.
        public LeveledItemEntry Payload => authored?.DeepCopy() ?? new()
        { Data = new() { Level = level, Count = count, Reference = new FormLink<IItemGetter>(Reference.Existing) } };
    }

    private sealed record StaticOverflowBranch(short Level, StaticOverflowNode Child);
    private sealed record StaticOverflowNode(ImmutableArray<ProjectedStaticEntry> Leaves,
        ImmutableArray<StaticOverflowBranch> Branches)
    {
        public int ChildCount => Branches.Sum(b => 1 + b.Child.ChildCount);
        public int EntryCount => Leaves.Length + Branches.Length;
    }

    private sealed class StaticListProjection(ProvisionalCapacityReservation reservation,
        ImmutableArray<ProjectedStaticEntry> entries, ImmutableArray<ProjectedStaticEntry> writingEntries = default)
    {
        public ProvisionalCapacityReservation ProvisionalCapacityReservation { get; } = reservation;
        public FormKey List => ProvisionalCapacityReservation.List;
        public string EditorId => ProvisionalCapacityReservation.EditorId;
        public int Before => ProvisionalCapacityReservation.Before;
        public ImmutableArray<ProjectedStaticEntry> Entries { get; } = entries;
        public int EffectiveCount => Entries.Length;
        public void ValidateForWriting()
        {
            for (var i = 0; i < Entries.Length; i++)
                if (!Entries[i].HasData)
                    throw new InvalidDataException($"Cannot write leveled list {EditorId} [{List}]: entry {i + 1} is missing DATA. No output was installed.");
        }
        // Effective splitting can reject malformed resolved output that provisional
        // accounting never inspected. Defer it without retaining mutable inputs.
        private readonly Lazy<StaticOverflowNode> tree = new(() => Partition(writingEntries.IsDefault ? entries : writingEntries));
        public StaticOverflowNode Tree => tree.Value;

        public static StaticOverflowNode Partition(ImmutableArray<ProjectedStaticEntry> entries)
        {
            StaticOverflowNode Freeze(OverflowLists.Node<ProjectedStaticEntry> node) => new(
                node.Leaves.ToImmutableArray(),
                node.Branches.Select(b => new StaticOverflowBranch(b.Level, Freeze(b.Child))).ToImmutableArray());
            return Freeze(OverflowLists.Plan(entries.ToList(), static e => e.Level));
        }
    }

    private sealed class StaticEntryProjection(ImmutableArray<StaticListProjection> projections,
        StaticCapacityReservations reservations)
    {
        public ImmutableArray<StaticListProjection> ChangedLists { get; } = projections;
        public IReadOnlyDictionary<FormKey, string> GeneratedEditorIds { get; init; } = new Dictionary<FormKey, string>();
        public IReadOnlyDictionary<FormKey, ImmutableArray<string>> OverflowEditorIds { get; init; } = new Dictionary<FormKey, ImmutableArray<string>>();
        // LLOS's known startup changes to written lists (ticket 25), collected while projecting.
        public IReadOnlyDictionary<FormKey, LlosLists.StartupChange> LlosStartupChanges { get; init; } = new Dictionary<FormKey, LlosLists.StartupChange>();
        private readonly FrozenDictionary<FormKey, StaticListProjection> lists = projections
            .ToFrozenDictionary(r => r.ProvisionalCapacityReservation.List);

        public StaticListProjection this[FormKey key] => lists[key];
        public IEnumerable<ProvisionalCapacityReservation> Reservations => reservations.Reservations;
        public ProvisionalCapacityReservation? ReservationFor(FormKey key)
            => reservations.ReservationFor(key);
        public int? RuntimeEntryCount(FormKey key) => lists.TryGetValue(key, out var list)
            ? list.Tree.ChildCount == 0 ? list.EffectiveCount : list.Tree.EntryCount : null;
    }

    private static class EffectiveStaticEntries
    {
        // Capture the current planning instant; never retain mutable catalog or
        // ordered-plan inputs. A caller must project again after changing them.
        public static StaticCapacityReservations Reserve(Catalog catalog, IEnumerable<VariantPlan> plans)
        {
            using var timing = PhaseTiming.Measure("capacity-reservations");
            // The pass's own plans: PlanningCapacity already maintains their additions.
            return Reserve(catalog, ReferenceEquals(plans, catalog.Plans)
                ? catalog.Capacity.StaticAdditionLevels : AdditionLevels(catalog, plans));
        }

        // The same additions rebuilt from any plan sequence.
        private static Dictionary<FormKey, Dictionary<short, int>> AdditionLevels(Catalog catalog, IEnumerable<VariantPlan> plans)
        {
            var parents = plans.SelectMany(p => p.Parents).Select(p => p with { List = MaterialContents(p.List, catalog) }).ToArray();
            var additions = new Dictionary<FormKey, Dictionary<short, int>>();
            foreach (var parent in parents.Concat(catalog.FallbackEdges.Select(e => e.Parent)).Concat(MirroredCloneAdditions(parents, catalog)))
            {
                if (!additions.TryGetValue(parent.List, out var levels)) additions[parent.List] = levels = [];
                levels[parent.Level] = levels.GetValueOrDefault(parent.Level) + 1;
            }
            return additions;
        }

        private static StaticCapacityReservations Reserve(Catalog catalog, IReadOnlyDictionary<FormKey, Dictionary<short, int>> additions)
        {
            var reservations = additions.Keys.Concat(catalog.Fallbacks.Keys)
                .Concat(catalog.ConflictResolutions.Keys).Concat(catalog.BundleSlotOwners).Distinct().Select(key =>
                {
                    var list = catalog.Lists[key];
                    var before = (catalog.WinningLists.GetValueOrDefault(key) ?? list).Entries?.Count ?? 0;
                    var authored = catalog.RebuiltFallbackRoots.Contains(key) ? [] : list.Entries ?? [];
                    var added = additions.GetValueOrDefault(key);
                    var proposed = authored.Count + (added?.Values.Sum() ?? 0);
                    // Splitting depends only on the level multiset, and never applies up to the limit.
                    if (proposed <= 255) return new ProvisionalCapacityReservation(key, list.EditorID ?? "", before, proposed, 0, proposed);
                    var levels = authored.Select(e => e.Data?.Level).ToList();
                    foreach (var (level, count) in added ?? []) levels.AddRange(Enumerable.Repeat((short?)level, count));
                    var tree = OverflowLists.Plan(levels, static level => level);
                    return new ProvisionalCapacityReservation(key, list.EditorID ?? "", before,
                        levels.Count, tree.ChildCount, tree.EntryCount);
                });
            return new(reservations);
        }

        public static StaticEntryProjection Project(Catalog catalog, IEnumerable<VariantPlan> plans)
        {
            using var timing = PhaseTiming.Measure("list-impacts");
            var reserved = Reserve(catalog, plans);
            var orderedPlans = plans.ToArray();
            var (entriesByList, generatedNames) = Collect(catalog, orderedPlans);
            var usedNames = NativeNames(catalog, generatedNames);
            var overflowNames = new Dictionary<FormKey, ImmutableArray<string>>();
            // LLOS keyword rules read a generated variant's keywords from its source.
            using var sources = LlosLists.GeneratedSources(occurrence => orderedPlans[occurrence].Source.FormKey);
            var startupChanges = new Dictionary<FormKey, LlosLists.StartupChange>();
            using var changes = LlosLists.CollectStartupChanges(startupChanges);
            using var appends = LlosLaterAppends(catalog);
            return new(entriesByList.Select(kv =>
            {
                var entries = kv.Value.ToImmutableArray();
                var composed = Compose(catalog, kv.Key, entries,
                    generatedNames.GetValueOrDefault(kv.Key) ?? catalog.Lists[kv.Key].EditorID, usedNames);
                if (!composed.Names.IsDefault) overflowNames[kv.Key] = composed.Names;
                return new StaticListProjection(reserved[kv.Key], composed.Effective, composed.Physical);
            }).ToImmutableArray(), reserved) { GeneratedEditorIds = generatedNames, OverflowEditorIds = overflowNames, LlosStartupChanges = startupChanges };
        }

        // The writer's own native composition decision, as a planning-time answer.
        // Lists without a problem are omitted; no output is projected or written.
        public static Dictionary<FormKey, string> NativeCompositionProblems(Catalog catalog, IEnumerable<VariantPlan> plans)
        {
            if (catalog.NativeLists is not { HasInputs: true } && catalog.LlosLists is not { HasInputs: true }) return [];
            using var timing = PhaseTiming.Measure("native-composition");
            var orderedPlans = plans.ToArray();
            var (entriesByList, generatedNames) = Collect(catalog, orderedPlans);
            var usedNames = NativeNames(catalog, generatedNames);
            using var sources = LlosLists.GeneratedSources(occurrence => orderedPlans[occurrence].Source.FormKey);
            using var appends = LlosLaterAppends(catalog);
            var problems = new Dictionary<FormKey, string>();
            var skyPatcher = catalog.NativeLists is { HasInputs: true };
            foreach (var (key, collected) in entriesByList)
            {
                if (!skyPatcher && catalog.LlosLists?.MayAffect(key, collected) != true) continue;
                var entries = collected.ToImmutableArray();
                var list = catalog.Lists[key];
                var name = generatedNames.GetValueOrDefault(key) ?? list.EditorID;
                try
                {
                    Compose(catalog, key, entries, name, usedNames);
                }
                catch (InvalidDataException ex) { problems[key] = ex.Message.Replace(" No output was installed.", "", StringComparison.Ordinal); }
            }
            return problems;
        }

        private static HashSet<string> NativeNames(Catalog catalog, Dictionary<FormKey, string> generatedNames)
            => new((catalog.NativeLists?.ReferencedEditorIds ?? []).Concat(catalog.LlosLists?.ReferencedEditorIds ?? [])
                .Concat(generatedNames.Values), StringComparer.OrdinalIgnoreCase);

        private static (ImmutableArray<ProjectedStaticEntry> Effective, ImmutableArray<ProjectedStaticEntry> Physical,
            ImmutableArray<string> Names) Compose(Catalog catalog, FormKey key, ImmutableArray<ProjectedStaticEntry> entries,
            string? name, HashSet<string> usedNames)
        {
            var list = catalog.Lists[key];
            var (effective, physical) = catalog.WinningLists.ContainsKey(key)
                ? (entries, ExistingPhysical(catalog, key, entries)) : ProjectGenerated(catalog, list, entries, name);
            if (physical.Length <= 255 || catalog.NativeLists is not { HasInputs: true } native)
                return (effective, physical, default);
            // Reserve names before other generated records can occupy them. The
            // writer allocates overflow children in this same postorder sequence.
            var split = StaticListProjection.Partition(physical);
            var names = Enumerable.Range(1, split.ChildCount)
                .Select(i => EditorId($"Sublist_{list.EditorID}_{i}", usedNames)).ToImmutableArray();
            native.ValidateOverflow(key, name, split, names,
                catalog.LlosLists is not { HasInputs: true } llos || llos.SkyPatcherBeforeLlos);
            return (effective, physical, names);
        }

        // Entries XEP appends to a list after its projection: one runtime AddForm per runtime placement on it, which
        // also bounds the single Revert selector a protected destination gets. Counted once, on the first query.
        private static IDisposable LlosLaterAppends(Catalog catalog)
        {
            Dictionary<FormKey, int>? counts = null;
            return LlosLists.LaterAppends(list => (counts ??= catalog.RuntimePlacements.GroupBy(r => r.Target.List)
                .ToDictionary(g => g.Key, g => g.Count())).GetValueOrDefault(list));
        }

        // Invert startup in reverse order. Planning asks this exact writer
        // question rather than checking SkyPatcher against the post-LLOS plan.
        public static string? ExistingWritingProblem(Catalog catalog, FormKey key, ImmutableArray<ProjectedStaticEntry> desired)
        {
            try { ExistingPhysical(catalog, key, desired); return null; }
            catch (InvalidDataException ex) { return ex.Message.Replace(" No output was installed.", "", StringComparison.Ordinal); }
        }

        private static ImmutableArray<ProjectedStaticEntry> ExistingPhysical(Catalog catalog, FormKey key,
            ImmutableArray<ProjectedStaticEntry> desired)
        {
            var list = catalog.Lists[key];
            var afterSky = catalog.LlosLists?.ForWriting(key, desired, list) ?? desired;
            return catalog.NativeLists?.ForProjection(key, afterSky, list) ?? afterSky;
        }

        private static (ImmutableArray<ProjectedStaticEntry> Effective, ImmutableArray<ProjectedStaticEntry> Physical)
            ProjectGenerated(Catalog catalog, ILeveledItemGetter list, ImmutableArray<ProjectedStaticEntry> desired, string? name)
        {
            var sky = catalog.NativeLists?.ProjectGeneratedForProjection(list, desired, name) ?? (desired, desired);
            if (catalog.LlosLists is not { } llos) return sky;
            if (llos.SkyPatcherBeforeLlos)
            {
                // LLOS sees SkyPatcher's additions too. First find the entries
                // needed before LLOS, then ask SkyPatcher to reproduce exactly
                // that intermediate state using its existing composition proof.
                var (_, afterSky) = llos.ProjectGenerated(list, sky.Item1);
                var composed = catalog.NativeLists?.ProjectGeneratedForProjection(list, afterSky, name) ?? (afterSky, afterSky);
                if (composed.Item1.Length != afterSky.Length || !composed.Item1.Zip(afterSky).All(p => SkyPatcherLists.SameProjected(p.First, p.Second)))
                    throw new InvalidDataException($"LLOS generated list {list.EditorID} [{list.FormKey}]: SkyPatcher cannot reproduce the entries required before LLOS. No output was installed.");
                return (sky.Item1, composed.Item2);
            }
            var (_, physical) = llos.ProjectGenerated(list, desired);
            if (physical.SequenceEqual(desired)) return sky;
            if (!sky.Item2.SequenceEqual(desired))
                throw new InvalidDataException($"LLOS generated list {list.EditorID} [{list.FormKey}]: SkyPatcher and LLOS would both change it; their native ordering is unverified. No output was installed.");
            return (sky.Item1, physical);
        }

        private static (Dictionary<FormKey, List<ProjectedStaticEntry>> Entries, Dictionary<FormKey, string> GeneratedNames)
            Collect(Catalog catalog, VariantPlan[] orderedPlans)
        {
            using var timing = PhaseTiming.Measure("static-entries.collect");
            var entriesByList = new Dictionary<FormKey, List<ProjectedStaticEntry>>();
            var referencesByList = new Dictionary<FormKey, HashSet<StaticEntryReference>>();
            List<ProjectedStaticEntry> Entries(FormKey key)
            {
                if (!entriesByList.TryGetValue(key, out var entries))
                    entriesByList[key] = entries = Baseline(catalog.RebuiltFallbackRoots.Contains(key)
                        ? [] : catalog.Lists[key].Entries ?? []);
                return entries;
            }
            static List<ProjectedStaticEntry> Baseline(IEnumerable<ILeveledItemEntryGetter> entries)
                => entries.Select(e => new ProjectedStaticEntry(e, new(e.Data?.Reference.FormKey ?? FormKey.Null))).ToList();
            foreach (var resolution in catalog.ConflictResolutions.Values)
                entriesByList[resolution.List] = Baseline((catalog.SpecializedRewrites.GetValueOrDefault(resolution.List)
                    ?? catalog.LlosLists?.BaselineFor(resolution.List) ?? catalog.NativeLists?.BaselineFor(resolution.Resolved) ?? resolution.Resolved).Entries ?? []);
            var copies = catalog.SpecializedCloneOriginals.ToLookup(x => x.Value, x => x.Key);
            for (var occurrence = 0; occurrence < orderedPlans.Length; occurrence++)
            {
                var plan = orderedPlans[occurrence];
                var reference = plan.Reused is { } reused ? new StaticEntryReference(reused.FormKey)
                    : new StaticEntryReference(FormKey.Null, occurrence);
                foreach (var parent in plan.Parents.Select(p => p with { List = MaterialContents(p.List, catalog) }))
                foreach (var target in InheritedCopyTargets(parent.List, copies, catalog).Prepend(parent.List))
                {
                    var entries = Entries(target);
                    if (!referencesByList.TryGetValue(target, out var references))
                        referencesByList[target] = references = entries.Select(e => e.Reference).ToHashSet();
                    if (!references.Add(reference) && !plan.PreserveParentMultiplicity) continue;
                    entries.Add(new(parent, reference));
                }
            }
            foreach (var edge in catalog.FallbackEdges)
            {
                var inherited = !InheritableEdge(edge, catalog)
                    ? [] : InheritedCopyTargets(edge.Parent.List, copies, catalog);
                foreach (var target in inherited.Prepend(edge.Parent.List))
                    Entries(target).Add(new(edge.Parent, new(edge.Child)));
            }
            foreach (var key in catalog.Fallbacks.Keys) Entries(key);
            // A bundle whose slot was rewritten in place changes without any addition.
            foreach (var key in catalog.BundleSlotOwners) Entries(key);
            // Bind list names before selector evaluation. Writer allocation order
            // and collisions with other generated record types cannot change them.
            var generatedNames = new Dictionary<FormKey, string>();
            if (catalog.NativeLists?.HasEditorIdRules == true)
            {
                var used = new HashSet<string>(catalog.NativeLists.ReferencedEditorIds, StringComparer.OrdinalIgnoreCase);
                foreach (var key in catalog.Fallbacks.Keys.Order()) generatedNames[key] = EditorId(catalog.Lists[key].EditorID ?? "", used);
            }
            return (entriesByList, generatedNames);
        }
    }
}
