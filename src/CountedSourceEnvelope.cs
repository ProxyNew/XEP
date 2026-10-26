using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // These are exact static inventory paths, not the aggregated consumers of
    // MaterialSourceProofs. Entry positions distinguish repeated weighted edges.
    private sealed record SourceRouteStep(FormKey List, int EntryIndex, FormKey Child,
        short Level, short Count, ILeveledItemGetter Original);
    private sealed record MerchantSourceRoute(FormKey Source, FormKey Container, int InventoryIndex,
        IContainerEntryGetter SourceInventoryEntry, IReadOnlyList<SourceRouteStep> Steps,
        MaterialSourceProof Proof, string Identity);

    private sealed record CountedSourceEnvelope(FormKey Source, FormKey KernelRoot,
        IReadOnlyDictionary<FormKey, ILeveledItemGetter> Kernel,
        IReadOnlySet<FormKey> OtherItems,
        IReadOnlyList<SourceRouteStep> OuterSteps, short MinimumLevel, string Identity)
    {
        public string ProjectionScope { get; init; } = "whole-inventory-entry-source-marginal-v2";
        public string[] OmittedSourceBranches { get; init; } = [];
        public FormKey[] OmittedRuntimeTargets { get; init; } = [];
        public string[] RuntimeDisjointEvidence { get; init; } = [];
        public string SamplingIdentity { get; init; } = "";
    }

    // Default false is intentional. Ordinary proofs and merchant fallback must
    // not accept structure until a destination planner can actually realize it.
    private static List<MerchantSourceRoute> MerchantSourceRoutes(FormKey source, Catalog catalog,
        ICollection<string> evidence, bool allowStructured = false)
    {
        using var timing = PhaseTiming.Measure("review.merchant-source-routes");
        var graph = SpecializedGraph(catalog);
        using var inventoryTiming = PhaseTiming.Measure("review.merchant-source-inventory");
        // Winning consumer records are frozen for the operation; generated inventory
        // rewrites must not become new source evidence for a later item.
        var inventories = SharedOnce(graph, () => graph.MerchantSourceInventories, value => graph.MerchantSourceInventories = value,
            () => catalog.ConsumerRecords.Containers.Values.Where(c => !c.IsDeleted)
            .SelectMany(c => (c.Items ?? []).Select((e, i) => (Container: c.FormKey, Index: i, Entry: e)))
            .Where(e => !e.Entry.Item.Item.IsNull)
            .GroupBy(e => e.Entry.Item.Item.FormKey).ToDictionary(g => g.Key, g => g.ToArray()));
        inventoryTiming?.Dispose();
        var found = new Dictionary<string, MerchantSourceRoute>(StringComparer.Ordinal);
        var frozen = new Dictionary<FormKey, ILeveledItemGetter>();
        // Membership is a root/source/graph fact, independent of the exact path,
        // inventory position and structural permission. Only this synchronous
        // source search shares complete verdicts; later calls get a fresh cache.
        var sourceMembership = new Dictionary<FormKey, bool>();
        // A selector kernel depends only on (source, kernel root): every container and path
        // of this search that reaches the same root shares one build.
        var kernels = new Dictionary<FormKey, CountedKernel>();
        // Sibling proofs revisit the same binary-overlay lists many times in
        // this synchronous search. Reuse immutable entry facts, never proof verdicts.
        var sourceEntries = new Dictionary<ILeveledItemGetter, (bool Valid, FormKey Child)[]>(ReferenceEqualityComparer.Instance);
        var todo = new Queue<(FormKey Key, SourceRouteStep[] Steps)>();
        todo.Enqueue((source, []));
        var examined = 0;
        while (todo.TryDequeue(out var route))
        {
            if (++examined > 8192)
            {
                evidence.Add("Exact merchant source search exceeded 8192 paths; no partial source proof is used.");
                return [];
            }
            if (inventories.TryGetValue(route.Key, out var positions))
                foreach (var position in positions)
                {
                    string Prefix() => $"Exact merchant source route withheld for {position.Container}, inventory entry {position.Index}";
                    // Container ownership/counts are preserved in the descriptor,
                    // but not yet interpreted as source availability/cardinality.
                    if (position.Entry.Item.Count != 1 || position.Entry.Data is not null)
                    {
                        evidence.Add($"{Prefix()}: source CONT count != 1 or ownership extra-data requires an additional inventory-boundary proof.");
                        continue;
                    }
                    var problem = TryMerchantSourcePath(source, route.Steps, position.Container, position.Index,
                        catalog, allowStructured, sourceMembership, kernels, sourceEntries, out var availability, out var minimum);
                    if (allowStructured && problem?.Contains("source-bearing SpecialLoot", StringComparison.Ordinal) == true)
                    {
                        var wholeProblem = problem;
                        problem = TryMerchantSourcePath(source, route.Steps, position.Container, position.Index,
                            catalog, allowStructured, sourceMembership, kernels, sourceEntries, out availability, out minimum, selectedBranch: true);
                        if (problem is null && availability?.Structure is { } branch)
                            evidence.Add($"Whole inventory-entry source marginal withheld for {position.Container}, entry {position.Index}: {wholeProblem}. Supported {branch.ProjectionScope} retains only its proved authored branch; omitted source-bearing siblings: {string.Join("; ", branch.OmittedSourceBranches)}. Runtime targets confined to omitted siblings: {string.Join(", ", branch.OmittedRuntimeTargets)}; their changing membership is outside this projection. Retained kernel, outer entries and collapsed competitors remain protected from mutation. Total merchant source quantities and original/added stock correlation are not asserted unchanged.");
                    }
                    if (problem is not null) { evidence.Add($"{Prefix()}: {problem}."); continue; }
                    var use = new TypedLootUse(position.Container, "Container inventory",
                        $"container:{position.Container}", $"container:{position.Container}", true);
                    var proof = new MaterialSourceProof(availability!, null, minimum, [use]);
                    var identity = $"{source}|container:{position.Container}|entry:{position.Index}|"
                        + (availability!.Structure?.Identity ?? SourceStepsIdentity(route.Steps));
                    found.TryAdd(identity, new(source, position.Container, position.Index,
                        position.Entry.DeepCopy(), Array.AsReadOnly(route.Steps), proof, identity));
                }
            if (route.Steps.Length >= 16)
            {
                if (graph.Parents.ContainsKey(route.Key)) evidence.Add($"Exact merchant source route withheld above {route.Key}: exceeds 16 LVLI edges.");
                continue;
            }
            foreach (var parentKey in graph.Parents.GetValueOrDefault(route.Key, []).Select(p => p.List).Distinct().Order(FormKeyOrder.Instance))
            {
                if (route.Steps.Any(s => s.List == parentKey))
                { evidence.Add($"Exact merchant source route withheld at {parentKey}: cyclic static path."); continue; }
                var parent = graph.Lists[parentKey];
                if (!frozen.TryGetValue(parentKey, out var snapshot))
                {
                    using var freezeTiming = PhaseTiming.Measure("review.merchant-source-freeze");
                    frozen[parentKey] = snapshot = parent.DeepCopy();
                }
                foreach (var row in LeveledEntryRows(parent))
                {
                    if (row.Reference != route.Key) continue;
                    if (row.Count <= 0 || row.HasExtraData)
                    { evidence.Add($"Exact merchant source route withheld at {parentKey}, entry {row.Index}: non-positive count or entry extra-data."); continue; }
                    todo.Enqueue((parentKey, [.. route.Steps, new(parentKey, row.Index, route.Key,
                        row.Level, row.Count, snapshot)]));
                }
            }
        }
        return found.Values.OrderBy(r => r.Container, FormKeyOrder.Instance).ThenBy(r => r.InventoryIndex)
            .ThenBy(r => r.Identity, StringComparer.Ordinal).ToList();
    }

    private static string? TryMerchantSourcePath(FormKey source, SourceRouteStep[] steps,
        FormKey container, int inventoryIndex, Catalog catalog, bool allowStructured,
        Dictionary<FormKey, bool> sourceMembership, Dictionary<FormKey, CountedKernel> kernels,
        Dictionary<ILeveledItemGetter, (bool Valid, FormKey Child)[]> sourceEntries,
        out MaterialAvailabilityPath? availability, out short minimum, bool selectedBranch = false)
    {
        using var timing = PhaseTiming.Count("review.merchant-source-path");
        bool ContainsSource(FormKey root) => sourceMembership.TryGetValue(root, out var contains) ? contains
            : sourceMembership[root] = SourceKernelContains(root, source, catalog);
        availability = null; minimum = 1;
        var controls = new List<MaterialAvailabilityControl>();
        var firstStructure = -1;
        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var problem = StaticSourceNodeProblem(step.Original, catalog);
            if (problem is not null) return $"{step.List}: {problem}";
            var list = step.Original;
            if (!list.Global.IsNull)
                controls.Add(new(MaterialAvailabilityKind.Global, step.List, list.Global.FormKey, 0));
            else if (list.ChanceNone.Value != 0)
                controls.Add(new(MaterialAvailabilityKind.FixedChance, step.List, default, list.ChanceNone.Value));
            minimum = Math.Max(minimum, SelectionEntryMinimum(list, step.Level));
            if (firstStructure < 0 && (step.Count != 1
                || GivesAllEntries(list) && (!list.Global.IsNull || list.ChanceNone.Value != 0))) firstStructure = i;
        }
        if (firstStructure < 0)
        {
            foreach (var step in steps)
                if (HasHighestEligibleCutoff(step.Original)
                    && (step.Original.Entries ?? []).Any(e => e.Data is { } d && d.Level > step.Level))
                    return $"{step.List}: finite source level window requires structural replication";
            availability = new(controls);
            return null;
        }
        if (!allowStructured) return "counted or controlled Give All source requires an opt-in structural bundle attachment";

        // Find a bundle boundary above all affected inner selector operations.
        // This supports counts inside a selector as well as counts of it. The
        // complete inner graph retains finite windows and weighted competitors.
        while (firstStructure < steps.Length
            && steps.Skip(firstStructure).Any(s => !GivesAllEntries(s.Original))) firstStructure++;
        if (firstStructure == steps.Length) return "counted source has no supported outer Give All bundle boundary";

        // If an enclosing bundle reaches the source through several children,
        // all of those awards belong to one marginal. Lift the complete kernel
        // through the last such bundle, instead of independently projecting its
        // reconvergent paths and either losing or duplicating source quantities.
        if (!selectedBranch)
            for (var i = firstStructure; i < steps.Length; i++)
                if ((steps[i].Original.Entries ?? []).Select((e, index) => (Entry: e, Index: index))
                    .Any(e => e.Index != steps[i].EntryIndex && e.Entry.Data is { } d
                        && ContainsSource(d.Reference.FormKey))) firstStructure = i + 1;

        // Clone the COMPLETE selector kernel. Non-source equipment outcomes map
        // to empty leaves, retaining their weights/cutoffs. That is an explicit
        // marginal projection, usable ONLY beside preserved stock in a Give All
        // bundle: an empty source roll must never compete with destination stock.
        using var kernelTiming = PhaseTiming.Measure("review.merchant-source-kernel");
        var kernelRoot = firstStructure == steps.Length ? steps[^1].List : steps[firstStructure].Child;
        var kernel = new Dictionary<FormKey, ILeveledItemGetter>();
        var otherItems = new HashSet<FormKey>();
        var visiting = new HashSet<FormKey>();
        var minima = new Dictionary<FormKey, short>();
        var nonSourceNodes = new HashSet<FormKey>();
        var omittedNodes = new HashSet<FormKey>();
        var omittedRuntimeTargets = new HashSet<FormKey>();
        var runtimeDisjointEvidence = new HashSet<string>(StringComparer.Ordinal);
        (bool Valid, FormKey Child)[] Entries(ILeveledItemGetter list)
        {
            if (!sourceEntries.TryGetValue(list, out var entries))
                sourceEntries[list] = entries = (list.Entries ?? []).Select(entry =>
                    entry.Data is { Count: > 0 } data && entry.ExtraData is null
                        ? (true, data.Reference.FormKey) : (false, default(FormKey))).ToArray();
            return entries;
        }
        bool StaticItem(FormKey key)
        {
            return StaticSourceItems(catalog).Contains(key);
        }
        string? NonSourceBranch(FormKey key, HashSet<FormKey> active, int depth, bool allowSelectedSource = false)
        {
            if (key == source) return allowSelectedSource ? null : "non-source branch reaches selected source";
            if (!catalog.Lists.TryGetValue(key, out var list)) return StaticItem(key) ? null : $"unresolved or non-item outcome {key}";
            // An omitted Give All sibling has a different proof contract from
            // a retained empty competitor. Never reuse its relaxed verdict.
            var completed = allowSelectedSource ? omittedNodes : nonSourceNodes;
            if (completed.Contains(key)) return null;
            if (depth > 16 || completed.Count + active.Count >= 8192) return "non-source branch exceeds bounded traversal";
            if (!active.Add(key)) return "cyclic non-source branch";
            if (!allowSelectedSource && SourceListHasRuntimeMutation(key, catalog))
            {
                var runtimeProblem = RuntimeDisjointSourceBranch(key, new HashSet<FormKey> { source }, catalog, out var runtimeEvidence);
                active.Remove(key);
                if (runtimeProblem is not null) return $"non-source kernel {key}: runtime-mutated or unresolved source lifecycle; source-disjoint proof withheld: {runtimeProblem}";
                runtimeDisjointEvidence.Add(runtimeEvidence); completed.Add(key); return null;
            }
            // SpecialLoot's level formula is immaterial to a subtree that can
            // never return this source. Still prove every reference and reject
            // dynamic additions, unknown bits, cycles or invalid entry data.
            if (allowSelectedSource && SourceListHasRuntimeMutation(key, catalog)) omittedRuntimeTargets.Add(key);
            var problem = StaticSourceNodeProblem(list, catalog, allowFixedNone: true, allowSpecialLoot: true,
                omittedGiveAllSibling: allowSelectedSource);
            if (problem is null)
                foreach (var entry in Entries(list))
                {
                    if (!entry.Valid)
                    { problem = "invalid count, missing entry data or entry extra-data"; break; }
                    problem = NonSourceBranch(entry.Child, active, depth + 1, allowSelectedSource);
                    if (problem is not null) break;
                }
            active.Remove(key);
            if (problem is null) completed.Add(key);
            return problem is null ? null : $"{(allowSelectedSource ? "omitted static sibling" : "non-source kernel")} {key}: {problem}";
        }
        if (selectedBranch)
            foreach (var step in steps.Skip(firstStructure))
            foreach (var entry in (step.Original.Entries ?? []).Select((e, index) => (Entry: e, Index: index)))
                if (entry.Index != step.EntryIndex && entry.Entry.Data is { } d && ContainsSource(d.Reference.FormKey))
                {
                    // Every retained outer node is an immutable Give All list.
                    // Output/membership below this omitted edge cannot change
                    // the selected child's count, gate or selection. Validate
                    // its static links, but do not certify its lifecycle. Shared
                    // nodes reached by the retained kernel are checked strictly
                    // again, including empty competitors.
                    var problem = NonSourceBranch(d.Reference.FormKey, [], 0, allowSelectedSource: true);
                    if (problem is not null) return problem;
                }
        string? kernelProblem = null;
        short Kernel(FormKey key, int depth)
        {
            if (key == source) return 1;
            if (catalog.Weapons.TryGetValue(key, out var weapon) && !weapon.IsDeleted
                || catalog.Armors.TryGetValue(key, out var armor) && !armor.IsDeleted)
            { otherItems.Add(key); return short.MaxValue; }
            if (!catalog.Lists.ContainsKey(key))
            {
                // Ammo, ingredients and other verified static inventory items
                // can be non-source outcomes too. Their positions/quantities
                // remain in the kernel and emit no additional source family.
                if (StaticItem(key)) { otherItems.Add(key); return short.MaxValue; }
            }
            else if (!ContainsSource(key))
            {
                var branchProblem = NonSourceBranch(key, [], depth);
                if (branchProblem is not null) { kernelProblem = branchProblem; return 1; }
                otherItems.Add(key); return short.MaxValue;
            }
            if (minima.TryGetValue(key, out var cached)) return cached;
            if (depth > 16 || kernel.Count >= 1024) { kernelProblem = "selector kernel exceeds bounded traversal"; return 1; }
            if (!visiting.Add(key)) { kernelProblem = "cyclic selector kernel"; return 1; }
            if (!catalog.Lists.TryGetValue(key, out var list))
            { kernelProblem = $"selector kernel contains unresolved or non-item outcome {key}; marginal projection cannot interpret it"; visiting.Remove(key); return 1; }
            var problem = StaticSourceNodeProblem(list, catalog, allowFixedNone: true);
            if (problem is not null) { kernelProblem = $"kernel {key}: {problem}"; visiting.Remove(key); return 1; }
            kernel[key] = list.DeepCopy();
            var min = short.MaxValue;
            foreach (var entry in list.Entries ?? [])
            {
                if (entry.Data is not { Count: > 0 } data || entry.ExtraData is not null)
                { kernelProblem = $"kernel {key}: invalid count, missing entry data or entry extra-data"; break; }
                var childMin = Kernel(data.Reference.FormKey, depth + 1);
                min = Math.Min(min, Math.Max(childMin, SelectionEntryMinimum(list, data.Level)));
                if (kernelProblem is not null) break;
            }
            visiting.Remove(key);
            return minima[key] = min;
        }
        if (!kernels.TryGetValue(kernelRoot, out var shared))
        {
            var kernelMinimum = Kernel(kernelRoot, 0);
            var kernelBuildProblem = kernelProblem;
            if (kernelBuildProblem is null)
                kernelBuildProblem = SourceKernelMinimum(kernelRoot, source, kernel, out kernelMinimum);
            kernels[kernelRoot] = shared = new CountedKernel(kernel, otherItems, kernelMinimum, kernelBuildProblem,
                runtimeDisjointEvidence.Order(StringComparer.Ordinal).ToArray(),
                string.Join('|', kernel.OrderBy(k => k.Key).Select(k => SourceNodeIdentity(k.Value))));
        }
        minimum = shared.Minimum;
        if (shared.Problem is not null) return shared.Problem;
        kernel = shared.Kernel; otherItems = shared.OtherItems;
        var outer = steps.Skip(firstStructure).ToArray();
        foreach (var step in outer)
        {
            if (!GivesAllEntries(step.Original)) return $"{step.List}: counted envelope crosses a non-Give-All outer selector";
        }
        var scope = selectedBranch ? "selected-source-branch-marginal-v1" : "whole-inventory-entry-source-marginal-v2";
        var omitted = selectedBranch ? outer.SelectMany(s => (s.Original.Entries ?? []).Select((e, i) => (Entry: e, Index: i))
            .Where(e => e.Index != s.EntryIndex && e.Entry.Data is { } d && ContainsSource(d.Reference.FormKey))
            .Select(e => $"{s.List} entry {e.Index}: {e.Entry.Data!.Reference.FormKey}, count {e.Entry.Data.Count}, level {e.Entry.Data.Level}"))
            .ToArray() : [];
        var structuralText = $"{scope}|{source}|container:{container}|entry:{inventoryIndex}|kernel:{kernelRoot}|"
            + shared.NodeIdentities
            + "|outer:" + SourceStepsIdentity(outer)
            + "|runtime-disjoint:" + string.Join('|', shared.RuntimeDisjointEvidence);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(structuralText)));
        var sampling = $"{scope}|container:{container}|entry:{inventoryIndex}|kernel:{kernelRoot}|outer:{SourceStepsIdentity(outer)}";
        var envelope = new CountedSourceEnvelope(source, kernelRoot, kernel, otherItems, Array.AsReadOnly(outer), minimum, identity)
        { ProjectionScope = scope, OmittedSourceBranches = omitted, OmittedRuntimeTargets = omittedRuntimeTargets.Order().ToArray(),
            RuntimeDisjointEvidence = shared.RuntimeDisjointEvidence, SamplingIdentity = sampling };
        foreach (var runtimeEvidence in envelope.RuntimeDisjointEvidence)
            ReviewNote(catalog, $"Source {source}, exact merchant {container} entry {inventoryIndex}: {runtimeEvidence}.");
        // Include every kernel control, not only the one chosen traversal path.
        // These are provenance. The materializer retains the actual branch rolls.
        var provenance = controls.Concat(kernel.Values.SelectMany(list => !list.Global.IsNull
            ? new[] { new MaterialAvailabilityControl(MaterialAvailabilityKind.Global, list.FormKey, list.Global.FormKey, 0) }
            : list.ChanceNone.Value != 0
                ? [new MaterialAvailabilityControl(MaterialAvailabilityKind.FixedChance, list.FormKey, default, list.ChanceNone.Value)]
                : Array.Empty<MaterialAvailabilityControl>())).Distinct().OrderBy(c => c.List).ToArray();
        availability = new(provenance, envelope);
        return null;
    }

    // One built selector kernel: read-only lists and verdicts shared by envelopes.
    private sealed record CountedKernel(Dictionary<FormKey, ILeveledItemGetter> Kernel, HashSet<FormKey> OtherItems,
        short Minimum, string? Problem, string[] RuntimeDisjointEvidence, string NodeIdentities);

    private static bool SourceListHasRuntimeMutation(FormKey key, Catalog catalog)
        => catalog.RuntimeControlledLists.ContainsKey(key) || catalog.UnsafeAvailability.ContainsKey(key)
            || catalog.ScriptAddTargets.Contains(key) || catalog.ScriptUnlocks.ContainsKey(key)
            || catalog.UnresolvedScriptPayloads.Contains(key) || catalog.RuntimeAddPayloadProblems.ContainsKey(key)
            || UnclosedRuntimeHolderProblem([], catalog, key) is not null;

    private static string? StaticSourceNodeProblem(ILeveledItemGetter list, Catalog catalog, bool allowFixedNone = false,
        bool allowSpecialLoot = false, bool omittedGiveAllSibling = false)
    {
        using var timing = PhaseTiming.Count("review.merchant-source-node-safety");
        // Counted bundles reproduce the source sampler exactly, so a source-bearing
        // SpecialLoot node stays refused even though ordinary proofs read the flag.
        return list.IsDeleted ? "deleted source list"
            : !KnownSelectionFlags(list) ? "unknown selection flags"
            : !allowSpecialLoot && ((int)list.Flags & 8) != 0 ? "source-bearing SpecialLoot selection semantics are unverified"
            : !omittedGiveAllSibling && SourceListHasRuntimeMutation(list.FormKey, catalog)
                ? "runtime-mutated or unresolved source lifecycle"
            : !allowFixedNone && list.Global.IsNull && list.ChanceNone.Value >= 1 ? "fixed Chance None 100% has no source availability"
            : null;
    }

    // A nested source minimum can fall AFTER its enclosing highest-tier window.
    // Prove at least one reachable level, rather than recommending a dead family.
    // Dynamic globals may open later; fixed100 and empty competitors cannot.
    private static string? SourceKernelMinimum(FormKey root, FormKey source,
        IReadOnlyDictionary<FormKey, ILeveledItemGetter> kernel, out short minimum)
    {
        using var timing = PhaseTiming.Measure("review.merchant-source-kernel-minimum");
        minimum = 1;
        var levels = kernel.Values.SelectMany(l => l.Entries ?? [])
            .Select(e => Math.Max((short)1, e.Data!.Level)).Append((short)1).Distinct().Order();
        var memo = new Dictionary<(FormKey, short), bool>();
        var exhausted = false;
        bool Possible(FormKey key, short level)
        {
            if (key == source) return true;
            if (!kernel.TryGetValue(key, out var list)) return false;
            if (memo.TryGetValue((key, level), out var cached)) return cached;
            if (memo.Count >= 8192) { exhausted = true; return false; }
            if (list.Global.IsNull && list.ChanceNone.Value >= 1) return memo[(key, level)] = false;
            var all = GivesAllEntries(list);
            var eligible = (list.Entries ?? []).Where(e => all || e.Data!.Level <= level).ToArray();
            if (!all && HasHighestEligibleCutoff(list) && eligible.Length > 0)
            {
                var highest = eligible.Max(e => e.Data!.Level);
                eligible = eligible.Where(e => e.Data!.Level == highest).ToArray();
            }
            return memo[(key, level)] = eligible.Any(e => Possible(e.Data!.Reference.FormKey, level));
        }
        foreach (var level in levels)
        {
            var possible = Possible(root, level);
            if (exhausted) return "source kernel level proof exceeded 8192 states; no partial proof is used";
            if (possible) { minimum = level; return null; }
        }
        return "source kernel has no reachable source outcome at any level; nested minimum is outside every eligible window";
    }

    private static bool SourceKernelContains(FormKey root, FormKey source, Catalog catalog)
    {
        using var timing = PhaseTiming.Measure("review.merchant-source-kernel-membership");
        var todo = new Stack<FormKey>(); todo.Push(root);
        var seen = new HashSet<FormKey>();
        while (todo.TryPop(out var key))
        {
            if (key == source) return true;
            if (!seen.Add(key)) continue;
            if (seen.Count > 8192) return true; // uncertain siblings cannot be declared disjoint
            if (catalog.Lists.TryGetValue(key, out var list))
                foreach (var entry in list.Entries ?? []) if (entry.Data is { } d) todo.Push(d.Reference.FormKey);
        }
        return false;
    }

    private static string SourceNodeIdentity(ILeveledItemGetter list)
    {
        using var timing = PhaseTiming.Measure("review.merchant-source-node-identity");
        return $"{list.FormKey}|flags:{(int)list.Flags}|global:{list.Global.FormKey}|chance:{list.ChanceNone.Value.ToString("R", CultureInfo.InvariantCulture)}|"
            + string.Join(';', (list.Entries ?? []).Select((e, i) => $"{i}:{e.Data?.Reference.FormKey}:{e.Data?.Level}:{e.Data?.Count}:{e.ExtraData is not null}"));
    }
    private static string SourceStepsIdentity(IEnumerable<SourceRouteStep> steps)
        => string.Join('>', steps.Select(s => $"{s.EntryIndex}:{s.Child}:{SourceNodeIdentity(s.Original)}"));

    // Planning only; the ordinary writer must remap every planning key. This
    // returns the controlled *new* family branch, never overrides source stock.
    private static FormKey MaterializeCountedSourceEnvelope(FormKey source, FormKey family,
        CountedSourceEnvelope envelope, Catalog catalog)
    {
        if (source != envelope.Source) throw new InvalidOperationException("Counted source envelope used for a different item.");
        var copies = new Dictionary<FormKey, FormKey>();
        var category = $"Counted merchant source {source}";
        FormKey Add(string identity, ILeveledItemGetter? original = null, SourceRouteStep? projection = null)
        {
            var key = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
            var node = new LeveledItem(key, SkyrimRelease.SkyrimSE)
            {
                EditorID = original is null ? "CountedSourceFlavor" : "CountedSource_" + original.EditorID,
                Flags = original?.Flags ?? (LeveledItem.Flag)1,
                ChanceNone = original?.ChanceNone ?? default,
                Entries = original is null ? new() : new((projection is null
                    ? original.Entries ?? [] : [original.Entries![projection.EntryIndex]]).Select(e => e.DeepCopy()))
            };
            if (original is not null) node.Global.SetTo(original.Global.FormKey);
            catalog.AddList(key, node);
            catalog.Fallbacks.Add(key, new(identity, category, node));
            return key;
        }
        var flavor = Add($"counted-flavor|{envelope.Identity}|family:{family}");
        ((LeveledItem)catalog.Lists[flavor]).Entries!.Add(new()
        { Data = new() { Reference = new FormLink<IItemGetter>(family), Level = 1, Count = 1 } });
        MarkEntryReferencesChanged(flavor, catalog);
        // Explicit no-source result. Every original non-source item edge
        // retains its exact slot/level/count, including highest-tier cutoffs.
        var empty = envelope.OtherItems.Count == 0 ? default
            : Add($"counted-empty|{envelope.Identity}|family:{family}");
        FormKey Clone(FormKey original)
        {
            if (original == source) return flavor;
            if (envelope.OtherItems.Contains(original)) return empty;
            if (copies.TryGetValue(original, out var prior)) return prior;
            var copy = Add($"counted-kernel|{envelope.Identity}|family:{family}|node:{original}", envelope.Kernel[original]);
            copies[original] = copy;
            foreach (var entry in ((LeveledItem)catalog.Lists[copy]).Entries!)
                entry.Data!.Reference.SetTo(Clone(entry.Data.Reference.FormKey));
            MarkEntryReferencesChanged(copy, catalog);
            return copy;
        }
        var child = Clone(envelope.KernelRoot);
        foreach (var step in envelope.OuterSteps)
        {
            var wrapper = Add($"counted-outer|{envelope.Identity}|family:{family}|{step.List}:{step.EntryIndex}", step.Original, step);
            ((LeveledItem)catalog.Lists[wrapper]).Entries![0].Data!.Reference.SetTo(child);
            MarkEntryReferencesChanged(wrapper, catalog);
            child = wrapper;
        }
        return child;
    }
}
