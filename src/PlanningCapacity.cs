using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed partial class Catalog
    {
        private Dictionary<FormKey, ILeveledItemGetter> planningLists = [];
        public required IReadOnlyDictionary<FormKey, ILeveledItemGetter> Lists
        {
            get => listView;
            init
            {
                planningLists = new(value);
                listView = new ReadOnlyDictionary<FormKey, ILeveledItemGetter>(planningLists);
            }
        }
        private IReadOnlyDictionary<FormKey, ILeveledItemGetter> listView = ImmutableDictionary<FormKey, ILeveledItemGetter>.Empty;
        public PlanningAdditions<VariantPlan> Plans { get; private set; } = [];
        private PlanningCapacity? capacity;
        public PlanningCapacity Capacity => capacity ??= new(this);

        public void AddList(FormKey key, ILeveledItemGetter list)
        { planningLists.Add(key, list); capacity?.AddList(key); ListReferencesChanged(key); }
        public void SetList(FormKey key, ILeveledItemGetter list)
        { planningLists[key] = list; capacity?.AddList(key); ListReferencesChanged(key); }
        public void RemoveList(FormKey key)
        { if (planningLists.Remove(key)) { capacity?.RemoveList(key); ListReferencesChanged(key); } }
        public PlanningAdditions<VariantPlan> BeginPlans()
        {
            // Replanning starts a new sequence, without changing a retained bundle.
            // Planning changes the graph the last review read.
            ReviewedChoices = null;
            if (capacity is not null) foreach (var plan in Plans) capacity.ChangePlan(plan, -1);
            Plans = [];
            ObservePlans(Plans);
            return Plans;
        }
        private void ObservePlans(PlanningAdditions<VariantPlan> plans)
        {
            plans.Added += plan => { if (ReferenceEquals(plans, Plans)) capacity?.ChangePlan(plan, 1); };
            plans.Removed += plan => { if (ReferenceEquals(plans, Plans)) capacity?.ChangePlan(plan, -1); };
            plans.Updated += (before, after) => { if (ReferenceEquals(plans, Plans)) capacity?.ReplacePlan(before, after); };
        }

        private void ObservePlanningChanges()
        {
            ObservePlans(Plans);
            FallbackEdges.Added += edge => capacity?.ChangeEdge(edge, 1);
            FallbackEdges.Removed += edge => capacity?.ChangeEdge(edge, -1);
            FallbackEdges.Updated += (before, after) => capacity?.ReplaceEdge(before, after);
            RuntimePlacements.Added += placement => capacity?.ChangeRuntime(placement.Target.List, 1);
            RuntimePlacements.Removed += placement => capacity?.ChangeRuntime(placement.Target.List, -1);
            RuntimePlacements.Updated += (before, after) =>
            {
                if (before.Target.List == after.Target.List) return;
                capacity?.ChangeRuntime(before.Target.List, -1);
                capacity?.ChangeRuntime(after.Target.List, 1);
            };
            CopyTopology.CloneChanged += (copy, before, after) =>
            {
                capacity?.CloneChanged(copy, before, after);
                ExpansionCloneCopies?.Changed(copy);
            };
            CopyTopology.FamilyChanged += selector => capacity?.FamilyChanged(selector);
        }
    }

    // A pass owns these counts. Collection/topology mutations update only affected
    // contributions; capturing a budget retains persistent roots rather than walking
    // all preceding plans. Physical multiplicity is distinct from output deduplication.
    private sealed class PlanningCapacity
    {
        private readonly Catalog catalog;
        private enum Kind { Plan, Edge, Runtime }
        // Static occurrences are also keyed by entry level, so each target keeps the
        // level multiset its capacity reservation needs; positions still count once.
        private readonly record struct GroupKey(Kind Kind, FormKey Parent, FormKey Child = default, short Level = 0);
        private sealed class Contribution(int count, FormKey[] targets, HashSet<FormKey> dependencies)
        {
            public int Count = count;
            public readonly FormKey[] Targets = targets;
            public readonly HashSet<FormKey> Dependencies = dependencies;
        }
        private readonly Dictionary<GroupKey, Contribution> groups = [];
        private readonly Dictionary<FormKey, HashSet<GroupKey>> dependents = [];
        // Budgets read these as of their capture; capture and writes stay O(1).
        private readonly EpochTable<int> statics = new();
        private readonly EpochTable<bool> known = new();
        private readonly Dictionary<FormKey, int> runtime = [];
        private readonly Dictionary<FormKey, Dictionary<short, int>> staticLevels = [];
        private CapacityCopies copies = CapacityCopies.Empty;
        private readonly Dictionary<FormKey, FormKey> indexedCopies = [];
        private readonly Dictionary<FormKey, Dictionary<FormKey, FormKey>> originals = [];

        public PlanningCapacity(Catalog catalog)
        {
            using var timing = PhaseTiming.Measure("planning.capacity-bootstrap");
            this.catalog = catalog;
            foreach (var key in catalog.Lists.Keys) known.Set(key, true);
            foreach (var (copy, original) in catalog.SpecializedCloneOriginals)
            { copies = copies.Add(original, copy); ReindexOriginal(copy); }
            foreach (var plan in catalog.Plans) ChangePlan(plan, 1);
            foreach (var edge in catalog.FallbackEdges) ChangeEdge(edge, 1);
            foreach (var placement in catalog.RuntimePlacements) ChangeRuntime(placement.Target.List, 1);
        }
        public void AddList(FormKey key)
        {
            using var timing = PhaseTiming.Count("planning.capacity-update");
            known.Set(key, true);
        }
        public void RemoveList(FormKey key)
        {
            using var timing = PhaseTiming.Count("planning.capacity-update");
            known.Set(key, false);
        }
        public void ChangePlan(VariantPlan plan, int amount)
        {
            using var timing = PhaseTiming.Count("planning.capacity-update");
            foreach (var parent in plan.Parents) Change(new(Kind.Plan, parent.List, Level: parent.Level), amount);
        }
        public void ReplacePlan(VariantPlan before, VariantPlan after)
        {
            if (ReferenceEquals(before.Parents, after.Parents)) return;
            using var timing = PhaseTiming.Count("planning.capacity-update");
            if (before.Parents.Count == 1 && after.Parents.Count == 1)
            {
                // Same delta order as the general path: the removed key first.
                var (old, next) = (before.Parents[0], after.Parents[0]);
                if (old.List == next.List && old.Level == next.Level) return;
                Change(new(Kind.Plan, old.List, Level: old.Level), -1);
                Change(new(Kind.Plan, next.List, Level: next.Level), 1);
                return;
            }
            // One position per occurrence, independent of entry level/count.
            // Preserve multiplicity while avoiding removal/reprojection of the
            // contribution for parents whose physical count did not change.
            var deltas = new Dictionary<(FormKey List, short Level), int>();
            foreach (var parent in before.Parents) deltas[(parent.List, parent.Level)] = deltas.GetValueOrDefault((parent.List, parent.Level)) - 1;
            foreach (var parent in after.Parents) deltas[(parent.List, parent.Level)] = deltas.GetValueOrDefault((parent.List, parent.Level)) + 1;
            foreach (var ((parent, level), delta) in deltas)
                if (delta != 0) Change(new(Kind.Plan, parent, Level: level), delta);
        }
        public void ReplaceEdge(FallbackEdge before, FallbackEdge after)
        {
            // Inheritance depends on parent/child identity and owned topology, and
            // the reserved level on the entry level, not on this occurrence's stack count.
            if (before.Parent.List == after.Parent.List && before.Child == after.Child && before.Parent.Level == after.Parent.Level) return;
            ChangeEdge(before, -1); ChangeEdge(after, 1);
        }
        public void ChangeEdge(FallbackEdge edge, int amount)
        {
            using var timing = PhaseTiming.Count("planning.capacity-update");
            Change(new(Kind.Edge, edge.Parent.List, edge.Child, edge.Parent.Level), amount);
        }
        public void ChangeRuntime(FormKey target, int amount)
        {
            using var timing = PhaseTiming.Count("planning.capacity-update");
            Change(new(Kind.Runtime, target), amount);
        }
        public void CloneChanged(FormKey copy, FormKey? before, FormKey? after)
        {
            using var timing = PhaseTiming.Measure("planning.capacity-topology");
            if (before is { } old) copies = copies.Remove(old, copy);
            if (after is { } next) copies = copies.Add(next, copy);
            ReindexOriginal(copy);
            Invalidate(new[] { copy }.Concat(before is { } a ? [a] : []).Concat(after is { } b ? [b] : []));
        }
        public void FamilyChanged(FormKey selector)
        {
            using var timing = PhaseTiming.Measure("planning.capacity-topology");
            foreach (var copy in copies[selector].Append(selector).Distinct()) ReindexOriginal(copy);
            Invalidate([selector]);
        }
        private void ReindexOriginal(FormKey copy)
        {
            if (indexedCopies.Remove(copy, out var prior))
            {
                var bucket = originals[prior]; bucket.Remove(copy);
                if (bucket.Count == 0) originals.Remove(prior);
            }
            if (!catalog.SpecializedCloneOriginals.TryGetValue(copy, out var original)) return;
            var target = MaterialContents(copy, catalog); var source = MaterialContents(original, catalog);
            indexedCopies[copy] = target;
            if (!originals.TryGetValue(target, out var entries)) originals[target] = entries = [];
            entries[copy] = source;
        }
        private FormKey Original(FormKey target)
        {
            if (!originals.TryGetValue(target, out var entries)) return target;
            if (entries.Count == 1) return entries.First().Value;
            // Preserve the previous first-match rule for aliased normalized copies,
            // including the dictionary's ordering after keys are removed/re-added.
            foreach (var pair in catalog.SpecializedCloneOriginals)
                if (MaterialContents(pair.Key, catalog) == target) return MaterialContents(pair.Value, catalog);
            return target;
        }
        private Contribution Project(GroupKey key)
        {
            var parent = key.Kind == Kind.Plan ? MaterialContents(key.Parent, catalog) : key.Parent;
            var dependencies = new HashSet<FormKey> { key.Parent, parent };
            if (key.Kind == Kind.Edge) dependencies.Add(key.Child);
            var inherit = key.Kind != Kind.Edge || InheritableEdge(new(new(key.Parent, 1, 1), key.Child), catalog);
            foreach (var copy in copies[parent]) dependencies.Add(copy);
            var targets = (inherit ? InheritedCopyTargets(parent, copies, catalog) : []).Prepend(parent);
            // One runtime insertion counts once even when several copies normalize
            // to the same target. Static additions retain every physical occurrence.
            return new(0, (key.Kind == Kind.Runtime ? targets.Distinct() : targets).ToArray(), dependencies);
        }
        private void Subscribe(GroupKey key, Contribution contribution, bool add)
        {
            foreach (var dependency in contribution.Dependencies)
            {
                if (add)
                {
                    if (!dependents.TryGetValue(dependency, out var keys)) dependents[dependency] = keys = [];
                    keys.Add(key);
                }
                else if (dependents.TryGetValue(dependency, out var keys))
                { keys.Remove(key); if (keys.Count == 0) dependents.Remove(dependency); }
            }
        }
        private void Write(GroupKey key, Contribution contribution, int amount)
        {
            foreach (var target in contribution.Targets)
            {
                if (key.Kind == Kind.Runtime)
                {
                    var value = runtime.GetValueOrDefault(target) + amount;
                    if (value == 0) runtime.Remove(target); else runtime[target] = value;
                }
                else
                {
                    statics.Set(target, statics.Get(target) + amount);
                    if (!staticLevels.TryGetValue(target, out var levels)) staticLevels[target] = levels = [];
                    var atLevel = levels.GetValueOrDefault(key.Level) + amount;
                    if (atLevel != 0) levels[key.Level] = atLevel;
                    else if (levels.Remove(key.Level) && levels.Count == 0) staticLevels.Remove(target);
                }
            }
        }
        private void Change(GroupKey key, int amount)
        {
            if (!groups.TryGetValue(key, out var contribution))
            {
                contribution = Project(key);
                groups.Add(key, contribution); Subscribe(key, contribution, true);
            }
            Write(key, contribution, amount); contribution.Count += amount;
            if (contribution.Count == 0) { Subscribe(key, contribution, false); groups.Remove(key); }
        }
        private void Invalidate(IEnumerable<FormKey> changed)
        {
            var affected = changed.SelectMany(k => dependents.TryGetValue(k, out var keys) ? (IEnumerable<GroupKey>)keys : []).Distinct().ToArray();
            foreach (var key in affected)
            {
                var old = groups[key]; Write(key, old, -old.Count); Subscribe(key, old, false);
                var updated = Project(key); updated.Count = old.Count; groups[key] = updated;
                Write(key, updated, updated.Count); Subscribe(key, updated, true);
            }
        }
        // Static additions per target list (plan parents after material redirection,
        // fallback edges, and their inherited copy targets), as entry level -> positions.
        public IReadOnlyDictionary<FormKey, Dictionary<short, int>> StaticAdditionLevels => staticLevels;

        public DirectPeerBudget Capture()
        {
            // Freeze physical static additions and known membership. Authored entry
            // counts, rebuilt-root flags, runtime insertions and original identities
            // remain live. A later copy uses the caller's prototype in the snapshot.
            var capturedStatics = statics.Capture(); var capturedKnown = known.Capture();
            var copyCount = -1; var budgetCopies = CapacityCopies.Empty;
            CapacityCopies Copies()
            {
                // Existing budgets refresh mirror membership lazily by clone count.
                // Preserve held-budget behavior even for same-count topology remaps.
                if (copyCount != catalog.SpecializedCloneOriginals.Count)
                { budgetCopies = copies; copyCount = catalog.SpecializedCloneOriginals.Count; }
                return budgetCopies;
            }
            int Runtime(FormKey target)
            {
                var direct = groups.GetValueOrDefault(new(Kind.Runtime, target))?.Count ?? 0;
                if (catalog.RuntimePlacements.Count == direct) return direct;
                var view = Copies();
                if (ReferenceEquals(view, copies)) return runtime.GetValueOrDefault(target);
                return groups.Where(p => p.Key.Kind == Kind.Runtime && (p.Key.Parent == target
                    || InheritedCopyTargets(p.Key.Parent, view, catalog).Contains(target))).Sum(p => p.Value.Count);
            }
            int Baseline(FormKey target, FormKey prototype)
            {
                var basis = known.At(target, capturedKnown) ? target : prototype;
                return (catalog.RebuiltFallbackRoots.Contains(basis) ? 0 : catalog.Lists[basis].Entries?.Count ?? 0)
                    + statics.At(basis, capturedStatics) + Runtime(target);
            }
            return new(Baseline, Original, target => InheritedCopyTargets(target, Copies(), catalog))
            { Ceiling = target => catalog.NativeLists?.EntryCeiling(target) ?? 255 };
        }
    }

    // A FormKey table whose earlier states stay readable by capture number. Capture
    // is O(1); a write after a capture records the key's prior value once per
    // capture interval. An absent key reads as default, as a missing entry did.
    private sealed class EpochTable<T>
    {
        private sealed class Entry(int epoch, T value)
        {
            public int Epoch = epoch;
            public T Value = value;
            // (epoch, value) in increasing epoch order: the value held from that
            // epoch until the next recorded one.
            public List<(int Epoch, T Value)>? Older;
        }
        private readonly Dictionary<FormKey, Entry> entries = [];
        private int epoch;

        public int Capture() => epoch++;
        public T Get(FormKey key) => entries.TryGetValue(key, out var entry) ? entry.Value : default!;
        public void Set(FormKey key, T value)
        {
            if (!entries.TryGetValue(key, out var entry)) { entries[key] = new(epoch, value); return; }
            if (entry.Epoch != epoch)
            {
                (entry.Older ??= []).Add((entry.Epoch, entry.Value));
                entry.Epoch = epoch;
            }
            entry.Value = value;
        }
        public T At(FormKey key, int capture)
        {
            if (!entries.TryGetValue(key, out var entry)) return default!;
            if (entry.Epoch <= capture) return entry.Value;
            var older = entry.Older;
            if (older is null) return default!;
            // The last recorded epoch at or before the capture.
            int low = 0, high = older.Count - 1, found = -1;
            while (low <= high)
            {
                var mid = (low + high) >> 1;
                if (older[mid].Epoch <= capture) { found = mid; low = mid + 1; } else high = mid - 1;
            }
            return found < 0 ? default! : older[found].Value;
        }
    }

    // Persistent mirror membership makes budget capture constant work while the
    // live topology keeps changing. The interpretation remains InheritedCopyTargets.
    private sealed class CapacityCopies(ImmutableDictionary<FormKey, ImmutableHashSet<FormKey>> map) : ILookup<FormKey, FormKey>
    {
        public static readonly CapacityCopies Empty = new(ImmutableDictionary<FormKey, ImmutableHashSet<FormKey>>.Empty);
        public IEnumerable<FormKey> this[FormKey key] => map.TryGetValue(key, out var values) ? values : [];
        public int Count => map.Count;
        public bool Contains(FormKey key) => map.ContainsKey(key);
        public CapacityCopies Add(FormKey original, FormKey copy) => new(map.SetItem(original,
            map.GetValueOrDefault(original, ImmutableHashSet<FormKey>.Empty).Add(copy)));
        public CapacityCopies Remove(FormKey original, FormKey copy)
        {
            var values = map[original].Remove(copy);
            return new(values.Count == 0 ? map.Remove(original) : map.SetItem(original, values));
        }
        private sealed class Group(FormKey key, IEnumerable<FormKey> values) : IGrouping<FormKey, FormKey>
        {
            public FormKey Key => key;
            public IEnumerator<FormKey> GetEnumerator() => values.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
        public IEnumerator<IGrouping<FormKey, FormKey>> GetEnumerator()
            => map.Select(p => (IGrouping<FormKey, FormKey>)new Group(p.Key, p.Value)).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
