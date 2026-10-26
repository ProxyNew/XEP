using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // FormKey.CompareTo compares the two "Name.ext" plugin strings ordinal-ignore-case
    // and then the ID, allocating both strings on every comparison. This compares the
    // same characters in place: the same order, no allocation.
    private sealed class FormKeyOrder : IComparer<FormKey>
    {
        public static readonly FormKeyOrder Instance = new();
        public int Compare(FormKey x, FormKey y)
        {
            bool xNull = x.ModKey.IsNull, yNull = y.ModKey.IsNull;
            if (xNull && yNull) return x.ID.CompareTo(y.ID);
            if (xNull) return -1;
            if (yNull) return 1;
            var compared = CompareModKeys(x.ModKey, y.ModKey);
            return compared != 0 ? compared : x.ID.CompareTo(y.ID);
        }
        private static int CompareModKeys(ModKey a, ModKey b)
        {
            Span<char> left = a.Name.Length <= 252 ? stackalloc char[a.Name.Length + 4] : new char[a.Name.Length + 4];
            Span<char> right = b.Name.Length <= 252 ? stackalloc char[b.Name.Length + 4] : new char[b.Name.Length + 4];
            Write(a, left); Write(b, right);
            return ((ReadOnlySpan<char>)left).CompareTo(right, StringComparison.OrdinalIgnoreCase);
        }
        private static void Write(ModKey key, Span<char> chars)
        {
            key.Name.AsSpan().CopyTo(chars);
            chars[key.Name.Length] = '.';
            key.Type.GetFileExtension().AsSpan().CopyTo(chars[(key.Name.Length + 1)..]);
        }
    }

    private static bool MatchesSpecializedUse(TypedLootUse destination, TypedLootUse[] sources, bool controlled)
    {
        foreach (var source in sources)
            if (MatchesSingleSpecializedUse(destination, source, controlled)) return true;
        return false;
    }

    // SPID death items: appended to the shared base at each death, so every actor of that base
    // may carry them. Living equipment of that recipient licenses them; they license only
    // other SPID death items, never living inventory (the general death-never-licenses-living rule).
    private const string SpidDeathRole = "SPID death inventory";
    // Living equipment can license the same NPC base's death loot, never the reverse
    // and never another base's death reference. Both death roles share this decision.
    private static bool IsLivingNpcRole(string role) => role is "NPC inventory" or "NPC outfit";
    private static bool ExactActorDeathUse(TypedLootUse destination, TypedLootUse source)
        => (destination.Role == "NPC death loot" || destination.Role == SpidDeathRole) && IsLivingNpcRole(source.Role)
            && destination.Known && source.Known && destination.Consumer == source.Consumer;
    private static bool MatchesSingleSpecializedUse(TypedLootUse destination, TypedLootUse source, bool controlled)
        => ExactActorDeathUse(destination, source)
              || destination.Known && source.Known && destination.EstablishedRole.Length > 0
                  && source.Role == destination.EstablishedRole && source.Consumer == destination.Consumer
            // Physical base inventory is available inside its narrower CDF/CID
            // recipient predicate. The reverse direction would broaden a native
            // reference-specific source to every instance of the base.
            || destination.Known && source.Known && NativeContainerRole(destination.Role)
                && source.Role == "Container inventory" && destination.Family == "container:" + source.Consumer
            || (destination.Known
            ? source.Known && source.Role == destination.Role
                // Similar traits or a template link do not grant another actor's equipment.
                // Actual inventory inheritors already have their own resolved source uses.
                && (destination.Role == SpidDeathRole || destination.Role.StartsWith("NPC ", StringComparison.Ordinal)
                    ? source.Consumer == destination.Consumer : source.Context == destination.Context)
            : controlled && !source.Known && source.Consumer == destination.Consumer
                && source.Role == destination.Role && source.Context == destination.Context);

    // Shared destination-context and isolation rules for existing and missing material families.
    private static List<FormKey[]> SpecializedContextPaths(FormKey point, TypedLootUse[] sources, bool controlled, Catalog catalog,
        Func<TypedLootUse, bool>? sourceMatch = null, Func<TypedLootUse, bool>? requiresProof = null,
        Dictionary<FormKey, bool>? boundaryFits = null)
    {
        using var timing = PhaseTiming.Measure("review.context-paths");
        var graph = SpecializedGraph(catalog);
        bool Match(TypedLootUse u) => sourceMatch?.Invoke(u) ?? MatchesSpecializedUse(u, sources, controlled);
        bool Fits(FormKey k)
        {
            if (boundaryFits is not null && boundaryFits.TryGetValue(k, out var cached)) return cached;
            var uses = graph.Endpoints(k);
            // Resolved scope-only consumers do not veto a boundary (docs/CONSUMER-SCOPE-2.15.0.md);
            // unresolved consumers still must match.
            var fits = uses.Any(Match) && uses.Where(u => !(u.Known && u.ScopeOnly) || requiresProof?.Invoke(u) == true).All(Match);
            if (boundaryFits is not null) boundaryFits[k] = fits;
            return fits;
        }
        bool BoundaryEdgeFits(FormKey child, FormKey parent)
        {
            var native = catalog.NativeLists;
            if (native is null || !native.Physical.ContainsKey(parent) && !native.Problems.ContainsKey(parent)) return true;
            var list = graph.Lists[parent];
            // Only the boundary remains an original record. Its replaced links
            // will name a new list, never an existing captured native form.
            var proposed = (list.Entries ?? []).Select(e => new ProjectedStaticEntry(e,
                new(e.Data?.Reference.FormKey == child ? FormKey.Null : e.Data?.Reference.FormKey ?? FormKey.Null))).ToImmutableArray();
            return EffectiveStaticEntries.ExistingWritingProblem(catalog, parent, proposed) is null;
        }
        return DisjointContextPathsCore(point, graph.ContextParents, Fits, parentsAreSortedDistinct: true,
            fitsIsMemoized: boundaryFits is not null, boundaryEdgeFits: BoundaryEdgeFits);
    }

    // Equivalent to shortest/lexical greedy selection over all valid paths, but
    // search again after reserving each boundary path instead of enumerating every
    // reconvergent path. Re-searching is necessary: an alternative lower path may
    // become the best remaining route after an earlier choice blocks shared nodes.
    // The breadth-first discovery records one predecessor per list, so only a
    // chosen path is materialized; discovery order and the chosen paths are the
    // same as enqueuing a path copy per list.
    private static List<FormKey[]> DisjointContextPaths(FormKey point,
        Func<FormKey, IEnumerable<FormKey>> parents, Func<FormKey, bool> fits, bool parentsAreSortedDistinct = false)
        => DisjointContextPathsCore(point, parents, fits, parentsAreSortedDistinct, fitsIsMemoized: false);

    private static List<FormKey[]> DisjointContextPathsCore(FormKey point,
        Func<FormKey, IEnumerable<FormKey>> parents, Func<FormKey, bool> fits, bool parentsAreSortedDistinct, bool fitsIsMemoized,
        Func<FormKey, FormKey, bool>? boundaryEdgeFits = null)
    {
        // Most searches end at the starting list; allocate search state only after.
        var pointFits = fits(point);
        if (pointFits) return [[point]];
        // A caller that memoizes its own verdicts needs no second table here.
        var fitCache = fitsIsMemoized ? null : new Dictionary<FormKey, bool> { [point] = pointFits };
        bool Fits(FormKey key) => fitCache is null ? fits(key)
            : fitCache.TryGetValue(key, out var value) ? value : fitCache[key] = fits(key);
        var parentCache = new Dictionary<FormKey, FormKey[]>();
        FormKey[] Parents(FormKey key) => parentCache.TryGetValue(key, out var value) ? value
            : parentCache[key] = parents(key).Distinct().OrderBy(k => k.ToString(), StringComparer.Ordinal).ToArray();
        var selected = new List<FormKey[]>(); var reserved = new HashSet<FormKey>();
        // The discovery tables are taken from the thread's pool for this search; a
        // re-entrant search (none is expected) simply allocates its own.
        var cameFrom = pooledCameFrom ?? new Dictionary<FormKey, FormKey>(); pooledCameFrom = null;
        var todo = pooledTodo ?? new Queue<FormKey>(); pooledTodo = null;
        try
        {
        while (true)
        {
            cameFrom.Clear(); cameFrom[point] = default;
            todo.Clear(); todo.Enqueue(point);
            FormKey? chosen = null;
            // Parent sets are arrays; index them rather than box an enumerator per node.
            bool Visit(FormKey node, FormKey parent)
            {
                if (reserved.Contains(parent)) return false;
                if (!cameFrom.TryAdd(parent, node))
                {
                    // A previous edge may have made this a forbidden boundary,
                    // while this reconverging edge can safely replace its child.
                    if (boundaryEdgeFits is null || !Fits(parent) || !boundaryEdgeFits(node, parent)) return false;
                    for (var cursor = node; ; cursor = cameFrom[cursor])
                    {
                        if (cursor == parent) return false;
                        if (cursor == point) break;
                    }
                    cameFrom[parent] = node; chosen = parent; return true;
                }
                if (Fits(parent) && (boundaryEdgeFits?.Invoke(node, parent) ?? true)) { chosen = parent; return true; }
                todo.Enqueue(parent);
                return false;
            }
            while (todo.TryDequeue(out var node))
            {
                var nodeParents = parentsAreSortedDistinct ? parents(node) : Parents(node);
                if (nodeParents is FormKey[] array)
                {
                    for (var i = 0; i < array.Length; i++) if (Visit(node, array[i])) break;
                }
                else foreach (var parent in nodeParents) if (Visit(node, parent)) break;
                if (chosen is not null) break;
            }
            if (chosen is null) break;
            var path = new List<FormKey>();
            for (var key = chosen.Value; ; key = cameFrom[key])
            {
                path.Add(key);
                if (key == point) break;
            }
            path.Reverse();
            var chosenPath = path.ToArray();
            selected.Add(chosenPath); reserved.UnionWith(chosenPath.Skip(1));
        }
        }
        finally { cameFrom.Clear(); todo.Clear(); pooledCameFrom = cameFrom; pooledTodo = todo; }
        return selected;
    }
    [ThreadStatic] private static Dictionary<FormKey, FormKey>? pooledCameFrom;
    [ThreadStatic] private static Queue<FormKey>? pooledTodo;

    private static string? SelectionStructureProblem(ILeveledItemGetter list, int supportedFlags)
    {
        if (list.IsDeleted) return "list is deleted";
        if (((int)list.Flags & ~supportedFlags) != 0) return $"unsupported selection flags ({(int)list.Flags})";
        if (list.Entries is not { Count: > 0 }) return "list has no entries";
        for (var i = 0; i < list.Entries.Count; i++)
        {
            var entry = list.Entries[i];
            if (entry.Data is null) return $"entry {i} has no item data";
            if (entry.Data.Count != 1) return $"entry {i} count {entry.Data.Count} is not 1";
            if (entry.ExtraData is not null) return $"entry {i} has extra data";
        }
        return null;
    }

    // Source-independent facts only. Availability has been initialized and remains
    // fixed for an AccessIndex's lifetime; generated lists are not graph evidence.
    private static string? MaterialBranchProblem(AccessIndex access, FormKey key, string kind, Catalog catalog)
    {
        var graph = SpecializedGraph(catalog);
        var list = graph.Lists[key];
        string? Evaluate()
        {
            if (SelectionStructureProblem(list, 11) is { } structureProblem) return structureProblem;
            if (catalog.RuntimeControlledLists.TryGetValue(key, out var runtime)) return $"material branch is runtime-controlled ({runtime})";
            if (catalog.UnsafeAvailability.TryGetValue(key, out var unsafeReason)) return $"unsupported material availability ({unsafeReason})";
            if (catalog.ScriptUnlocks.ContainsKey(key)) return "material branch requires a scripted availability attachment";
            if (graph.HasAncestorCycle(key)) return "cyclic material branch ancestry";
            foreach (var leaf in graph.Down(key))
            {
                var equipment = AccessEquipment(access, leaf, catalog);
                if (equipment is not { Enchanted: true }) return $"leaf [{leaf}] is not resolved enchanted equipment";
                if (equipment.Value.Kind != kind) return $"leaf [{leaf}] equipment kind {equipment.Value.Kind} differs from {kind}";
            }
            return null;
        }
        return SharedMemo(access.MaterialBranchSafety, (key, kind), Evaluate);
    }

    private static IReadOnlySet<(string Kind, string Material)> MaterialBranchMaterials(AccessIndex access,
        FormKey point, Catalog catalog)
    {
        return SharedMemo(access.MaterialBranchMaterials, point, () => (IReadOnlySet<(string Kind, string Material)>)SpecializedGraph(catalog).Down(point)
            .Select(k => AccessEquipment(access, k, catalog))
            .Where(e => e is { Enchanted: true, Material: not null })
            .Select(e => (Kind: e!.Value.Kind, Material: e.Value.Material!)).ToHashSet());
    }

    // Path is terminal -> material branch -> category selector. A repeated last node
    // means the context root itself must be wrapped to preserve its existing choices.
    private static List<FormKey[]> SpecializedMaterialPaths(FormKey source, FormKey terminal, EquipmentClass cls,
        Catalog catalog, SpecializedAssessment assessment, IReadOnlySet<FormKey>? observedRoots = null)
    {
        var graph = SpecializedGraph(catalog);
        List<FormKey[]> Stop(string problem)
        { assessment.Refuse("material path " + graph.PathLabel([terminal]), problem); return []; }
        var access = catalog.Access ?? (observedRoots is not null ? BuildAccessIndex(catalog) : null);
        if (access is null) return Stop("no enchanted access index is available");
        var roots = observedRoots?.ToHashSet();
        if (roots is null)
        {
            if (!catalog.AccessVerdicts.TryGetValue(source, out var verdict)) return Stop($"no enchanted access assessment for source [{source}]");
            roots = verdict.KindRoots.ToHashSet();
        }
        if (!roots.Overlaps(graph.Up(terminal))) return Stop("destination does not reach a source consumer's enchanted equipment root");
        var kind = SpecializedGroup(cls);
        var found = new List<FormKey[]>(); var todo = new Queue<FormKey[]>(); todo.Enqueue([terminal]);
        var examined = 0;
        while (todo.TryDequeue(out var path))
        {
            var point = path[^1];
            if (++examined > 8192)
            {
                assessment.Refuse("material path " + graph.PathLabel(path), "material-route search exceeded 8192 paths; expansion withheld for this terminal");
                return []; // Never emit a partial path search as complete route evidence.
            }
            var problem = MaterialBranchProblem(access, point, kind, catalog);
            if (problem is not null) { assessment.Refuse("material path " + graph.PathLabel(path), problem); continue; }
            var materials = MaterialBranchMaterials(access, point, catalog);
            if (materials.Contains((kind, cls.Material)))
            {
                assessment.Refuse("material path " + graph.PathLabel(path), $"same-material equipment exists ({cls.Material} {kind}); specialized planner decides here (static presence, regardless of level or availability controls)");
                continue;
            }
            if (path.Length > 1 && materials.Where(x => x.Kind == kind).Select(x => x.Material).Distinct().Count() > 1)
            { found.Add(path); continue; }
            if (roots.Contains(point))
            {
                // A single-material context pool is valid. Keep its entire original
                // family on one side if it already has several subtype/tier choices.
                if (path.Length > 1 && graph.Lists[point].Entries!.Count == 1
                    && graph.Lists[point].Entries![0].Data!.Reference.FormKey == path[^2]) found.Add(path);
                else found.Add(path.Append(point).ToArray());
                continue;
            }
            // Repeated entries encode weight, not additional topological routes.
            foreach (var parent in graph.Parents.GetValueOrDefault(point, []).Select(p => p.List).Distinct())
                if (!path.Contains(parent) && roots.Any(r => graph.Up(parent).Contains(r))) todo.Enqueue(path.Append(parent).ToArray());
        }
        return found;
    }
}
