using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record JointCountedSourceEnvelope(
        IReadOnlySet<FormKey> Sources, FormKey KernelRoot,
        IReadOnlyDictionary<FormKey, ILeveledItemGetter> Kernel,
        IReadOnlySet<FormKey> OtherItems, IReadOnlyList<SourceRouteStep> OuterSteps,
        IReadOnlyDictionary<FormKey, short> MinimumLevels,
        string SamplingIdentity, string ProjectionScope, string Identity,
        IReadOnlyList<string> OmittedSourceBranches)
    { public string[] RuntimeDisjointEvidence { get; init; } = []; }

    // A source-specific kernel may have collapsed the OTHER selected source's
    // subtree. Rebuild once from author evidence for the UNION, never splice the
    // independently projected lists or independently re-roll their shared gates.
    private static string? TryJointCountedSourceEnvelope(IReadOnlyList<CountedSourceEnvelope> envelopes,
        Catalog catalog, out JointCountedSourceEnvelope? proof)
    {
        proof = null;
        if (envelopes.Count == 0 || envelopes.Any(e => e is null)) return "joint source proof requires non-empty envelopes";
        if (envelopes.Count > 1024) return "joint source proof exceeds 1024 envelopes";
        var first = envelopes[0];
        if (string.IsNullOrEmpty(first.SamplingIdentity) || string.IsNullOrEmpty(first.ProjectionScope))
            return "joint source proof lacks an authored sampling identity or projection scope";
        var outerIdentity = SourceStepsIdentity(first.OuterSteps);
        if (first.OuterSteps.Count > 16) return "joint source outer chain exceeds 16 edges";
        if (envelopes.Any(e => e.SamplingIdentity != first.SamplingIdentity || e.KernelRoot != first.KernelRoot
            || e.ProjectionScope != first.ProjectionScope || SourceStepsIdentity(e.OuterSteps) != outerIdentity
            || e.OuterSteps.Count != first.OuterSteps.Count
            || e.OuterSteps.Zip(first.OuterSteps).Any(p => p.First.List != p.Second.List
                || p.First.EntryIndex != p.Second.EntryIndex || p.First.Child != p.Second.Child
                || p.First.Level != p.Second.Level || p.First.Count != p.Second.Count)))
            return "joint source envelopes do not share the exact authored event, kernel, outer steps and projection scope";
        var sources = envelopes.Select(e => e.Source).ToHashSet();
        if (sources.Any(k => k.IsNull || !(catalog.Weapons.TryGetValue(k, out var w) && !w.IsDeleted
                || catalog.Armors.TryGetValue(k, out var a) && !a.IsDeleted)))
            return "joint source union contains unresolved or deleted equipment";
        var author = SpecializedGraph(catalog).Lists;
        // Cross-check every supplied frozen source snapshot. SamplingIdentity
        // describes the occurrence, not every inner node's current contents.
        foreach (var envelope in envelopes)
            foreach (var node in envelope.Kernel)
                if (!author.TryGetValue(node.Key, out var current) || SourceNodeIdentity(current) != SourceNodeIdentity(node.Value))
                    return $"joint source author graph changed at frozen kernel {node.Key}";

        var globals = catalog.Mods.SelectMany(m => m.Globals).GroupBy(g => g.FormKey)
            .Where(g => !g.Last().IsDeleted).Select(g => g.Key).ToHashSet();
        var staticItems = catalog.StaticSourceItems ??= catalog.Mods.SelectMany(m => m.EnumerateMajorRecords<IItemGetter>())
            .GroupBy(i => i.FormKey).Where(g => !g.Last().IsDeleted).Select(g => g.Key).ToHashSet();
        var outer = new List<SourceRouteStep>();
        var expectedChild = first.KernelRoot;
        var outerSeen = new HashSet<FormKey>();
        foreach (var step in first.OuterSteps)
        {
            if (step.List != step.Original.FormKey || step.Child != expectedChild || !outerSeen.Add(step.List)
                || !author.TryGetValue(step.List, out var current)
                || SourceNodeIdentity(current) != SourceNodeIdentity(step.Original))
                return $"joint source outer occurrence changed or is cyclic at {step.List}";
            var problem = StaticSourceNodeProblem(step.Original, catalog, allowFixedNone: true);
            if (problem is not null || !GivesAllEntries(step.Original))
                return $"joint source outer {step.List}: {problem ?? "not a supported Give All boundary"}";
            if (!step.Original.Global.IsNull && !globals.Contains(step.Original.Global.FormKey))
                return $"joint source outer {step.List}: unresolved chance global";
            if (step.EntryIndex < 0 || step.Original.Entries is null || step.EntryIndex >= step.Original.Entries.Count
                || step.Original.Entries[step.EntryIndex].ExtraData is not null
                || step.Original.Entries[step.EntryIndex].Data is not { Count: > 0 } data
                || data.Reference.FormKey != step.Child || data.Count != step.Count || data.Level != step.Level || data.Level < 0)
                return $"joint source outer {step.List}: invalid exact entry, count, level or extra-data";
            outer.Add(step with { Original = step.Original.DeepCopy() });
            expectedChild = step.List;
        }

        var kernel = new Dictionary<FormKey, ILeveledItemGetter>();
        var other = new HashSet<FormKey>();
        var frozen = new Dictionary<FormKey, ILeveledItemGetter>();
        var complete = new Dictionary<FormKey, bool>();
        var active = new HashSet<FormKey>();
        var seenSources = new HashSet<FormKey>();
        var runtimeDisjointEvidence = new HashSet<string>(StringComparer.Ordinal);
        string? failure = null;
        bool Visit(FormKey key, int depth)
        {
            if (sources.Contains(key)) { seenSources.Add(key); return true; }
            if (complete.TryGetValue(key, out var prior)) return prior;
            if (depth > 16 || complete.Count + active.Count >= 8192)
            { failure = "joint kernel exceeds 16 edges or 8192 states; no partial proof is used"; return false; }
            if (!author.TryGetValue(key, out var original))
            {
                if (key.IsNull || !staticItems.Contains(key))
                { failure = $"joint kernel contains unresolved or non-item reference {key}"; return false; }
                other.Add(key); return complete[key] = false;
            }
            if (!active.Add(key)) { failure = $"joint kernel contains cycle at {key}"; return false; }
            var snapshot = original.DeepCopy(); frozen[key] = snapshot;
            if ((snapshot.Entries?.Count ?? 0) > 255)
            { failure = $"joint kernel {key} exceeds native 255-entry capacity"; return false; }
            if (!snapshot.Global.IsNull && !globals.Contains(snapshot.Global.FormKey))
            { failure = $"joint kernel {key} contains unresolved chance global"; return false; }
            var bearing = false;
            foreach (var entry in snapshot.Entries ?? [])
            {
                if (entry.Data is not { Count: > 0 } d || d.Level < 0 || entry.ExtraData is not null)
                { failure = $"joint kernel {key} contains invalid count, level, missing data or entry extra-data"; return false; }
                bearing |= Visit(d.Reference.FormKey, depth + 1);
                if (failure is not null) return false;
            }
            var nodeProblem = StaticSourceNodeProblem(snapshot, catalog, allowFixedNone: true, allowSpecialLoot: !bearing);
            if (nodeProblem is not null)
            {
                if (bearing || !SourceListHasRuntimeMutation(key, catalog))
                { failure = $"joint kernel {key}: {nodeProblem}"; return false; }
                var runtimeProblem = RuntimeDisjointSourceBranch(key, sources, catalog, out var runtimeEvidence);
                if (runtimeProblem is not null)
                { failure = $"joint kernel {key}: {nodeProblem}; source-union disjoint proof withheld: {runtimeProblem}"; return false; }
                runtimeDisjointEvidence.Add(runtimeEvidence);
            }
            active.Remove(key);
            if (bearing)
            {
                if (outerSeen.Contains(key)) { failure = $"joint kernel reconverges into outer occurrence {key}"; return false; }
                if (kernel.Count >= 1024) { failure = "joint union-bearing kernel exceeds 1024 nodes"; return false; }
                kernel[key] = snapshot;
            }
            else other.Add(key); // a validated disjoint subtree emits no new family
            return complete[key] = bearing;
        }
        var hasSource = Visit(first.KernelRoot, 0);
        if (failure is not null) return failure;
        if (!hasSource || !sources.SetEquals(seenSources)) return "joint selected component does not reach every selected source";
        var minima = new Dictionary<FormKey, short>();
        foreach (var source in sources.Order())
        {
            var problem = SourceKernelMinimum(first.KernelRoot, source, kernel, out var minimum);
            if (problem is not null) return $"joint source {source}: {problem}";
            minima[source] = minimum;
        }
        var signature = "joint-counted-union-v1|" + first.SamplingIdentity
            + "|sources:" + string.Join(';', sources.Order())
            + "|author:" + string.Join('|', frozen.OrderBy(p => p.Key).Select(p => SourceNodeIdentity(p.Value)))
            + "|runtime-disjoint:" + string.Join('|', runtimeDisjointEvidence.Order(StringComparer.Ordinal));
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        proof = new(sources, first.KernelRoot, new ReadOnlyDictionary<FormKey, ILeveledItemGetter>(kernel), other,
            outer.AsReadOnly(), new ReadOnlyDictionary<FormKey, short>(minima), first.SamplingIdentity, first.ProjectionScope,
            identity, Array.AsReadOnly(envelopes.SelectMany(e => e.OmittedSourceBranches).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
        { RuntimeDisjointEvidence = runtimeDisjointEvidence.Order(StringComparer.Ordinal).ToArray() };
        return null;
    }

    // Returns a planning root only. The caller reserves the authoring event and
    // attaches this root ONCE at a compatible destination. Calling it separately
    // for each source would recreate the joint-quantity bug.
    private static FormKey MaterializeJointCountedSourceEnvelope(IReadOnlyDictionary<FormKey, FormKey> families,
        JointCountedSourceEnvelope proof, Catalog catalog)
    {
        if (families.Count == 0 || families.Keys.Any(s => !proof.Sources.Contains(s)))
            throw new InvalidDataException("Joint family mapping is empty or includes a source outside the proved union.");
        foreach (var family in families.Values)
        {
            if (family.IsNull || family.ModKey != FallbackPlanningMod || !catalog.Fallbacks.ContainsKey(family)
                || !catalog.Lists.TryGetValue(family, out var list) || !KnownSelectionFlags(list) || GivesAllEntries(list)
                || !list.Global.IsNull || list.ChanceNone.Value != 0
                || (list.Entries ?? []).Any(e => e.Data is not { Count: 1 } || e.ExtraData is not null))
                throw new InvalidDataException("Joint family must be a registered planning choice returning one variant, with no additional chance/global gate.");
        }
        // Missing source mappings (fully covered or no eligible flavors) retain
        // their native positions as no-extra outcomes, never renormalize peers.
        var mapping = string.Join(';', families.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
        var prefix = $"joint-counted|{proof.Identity}|mapping:{mapping}";
        var category = "Joint counted merchant source";
        FormKey Add(string id, ILeveledItemGetter? original = null, SourceRouteStep? selected = null)
        {
            var key = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
            var node = new LeveledItem(key, SkyrimRelease.SkyrimSE)
            {
                EditorID = original is null ? "JointSourceFlavor" : "JointSource_" + original.EditorID,
                Flags = original?.Flags ?? (LeveledItem.Flag)1,
                ChanceNone = original?.ChanceNone ?? default,
                Entries = original is null ? new() : new((selected is null ? original.Entries ?? []
                    : [original.Entries![selected.EntryIndex]]).Select(e => e.DeepCopy()))
            };
            if (original is not null) node.Global.SetTo(original.Global.FormKey);
            catalog.AddList(key, node); catalog.Fallbacks.Add(key, new(id, category, node));
            return key;
        }
        var empty = Add(prefix + "|empty");
        var flavors = new Dictionary<FormKey, FormKey>();
        foreach (var (source, family) in families.OrderBy(p => p.Key))
        {
            var adapter = Add(prefix + "|flavor:" + source);
            ((LeveledItem)catalog.Lists[adapter]).Entries!.Add(new()
            { Data = new() { Reference = new FormLink<IItemGetter>(family), Level = 1, Count = 1 } });
            MarkEntryReferencesChanged(adapter, catalog);
            flavors[source] = adapter;
        }
        var copies = new Dictionary<FormKey, FormKey>();
        FormKey Clone(FormKey key)
        {
            if (proof.Sources.Contains(key)) return flavors.GetValueOrDefault(key, empty);
            if (proof.OtherItems.Contains(key)) return empty;
            if (copies.TryGetValue(key, out var prior)) return prior;
            var copy = Add(prefix + "|kernel:" + key, proof.Kernel[key]); copies[key] = copy;
            foreach (var entry in ((LeveledItem)catalog.Lists[copy]).Entries!) entry.Data!.Reference.SetTo(Clone(entry.Data.Reference.FormKey));
            MarkEntryReferencesChanged(copy, catalog);
            return copy;
        }
        var child = Clone(proof.KernelRoot);
        foreach (var step in proof.OuterSteps)
        {
            var wrapper = Add(prefix + $"|outer:{step.List}:{step.EntryIndex}", step.Original, step);
            ((LeveledItem)catalog.Lists[wrapper]).Entries![0].Data!.Reference.SetTo(child); child = wrapper;
            MarkEntryReferencesChanged(wrapper, catalog);
        }
        return child;
    }
}
