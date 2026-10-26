using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // One original control (Global and/or fixed Chance None) reproduced on an owned
    // single-entry list above the counterpart position. The Global is the original record.
    private sealed record MirrorCopiedControl(FormKey List, FormKey Global, double ChanceNone);
    // Original receiver-side gates and actor-level window that the enchanted
    // destination path does not already impose. Empty means nothing is copied.
    private sealed record MirrorGateCopy(MirrorCopiedControl[] Controls, (int Minimum, int Maximum)[] Window,
        (int Minimum, int Maximum)[]? DestinationWindow = null)
    {
        public static readonly MirrorGateCopy None = new([], []);
        public bool IsEmpty => Controls.Length == 0 && Window.Length == 0;
    }

    // A receiver/class assessment deliberately knows nothing about the payload's
    // own gates. Check each source/payload against the frozen original graph. Gates
    // inside the payload are reproduced by its projection; receiver-side gates and
    // windows missing from the enchanted destination path are copied onto owned
    // lists above the wrapper (MirrorGateCopy) instead of being refused.
    private sealed record MirrorGateResult(string? Problem, MirrorGateCopy Copy);
    // The result reads the source only through its gate paths in the payload tree, so
    // sources with the same walk share it.
    private static MirrorGateResult MirrorOriginalGate(MirrorSite site, FormKey source, SpecializedRoute route, Catalog catalog)
    {
        using var timing = PhaseTiming.Count("mirror.original-gate-lookup");
        var identity = MirrorGateIdentity.Of(site, MirrorSourcePaths(site, source, SpecializedGraph(catalog)), route);
        if (catalog.MirrorOriginalGates.TryGetValue(identity, out var cached)) return cached;
        identity = identity.Snapshot();
        var problem = MirrorOriginalGateProblemCore(site, source, route, catalog, identity.Route, out var copy);
        return catalog.MirrorOriginalGates[identity] = new(problem, problem is null ? copy : MirrorGateCopy.None);
    }
    private const string OriginalGateRefusal = "enchanted destination is not under the source's availability gate";
    private static string? MirrorOriginalGateProblemCore(MirrorSite site, FormKey source, SpecializedRoute route, Catalog catalog,
        MirrorRouteIdentity identity, out MirrorGateCopy copy)
    {
        using var timing = PhaseTiming.Count("mirror.original-gates");
        copy = MirrorGateCopy.None;
        const string refusal = OriginalGateRefusal;
        var graph = SpecializedGraph(catalog);
        if (graph.HasAncestorCycle(site.Receiver)) { return refusal + "; cyclic original receiver ancestry"; }
        // SpecialLoot receivers and ancestors follow the ordinary rules below (KnownSelectionFlags).
        bool UnsupportedLevels(FormKey key) => MirrorUnsupportedLevels(graph, key);
        bool UnsupportedMetadata(FormKey key) => MirrorUnsupportedMetadata(graph, catalog, key);
        // Mutagen exposes a signed short; negative values encode native unsigned
        // levels above the supported static range, not early actor-level bands.
        if (route.Destination.Level < 0 || UnsupportedLevels(route.Destination.List)
            || route.MaterialPath?.Any(UnsupportedLevels) == true)
            return refusal + "; original selection level exceeds the supported static window range";
        if (UnsupportedMetadata(route.Destination.List) || route.MaterialPath?.Any(k => UnsupportedMetadata(k)) == true)
            return refusal + "; original selection flags are outside the supported static window contract";
        bool Gate(FormKey key) => MirrorGate(graph, key);
        var sourceWalk = MirrorSourcePaths(site, source, graph);
        var sourcePaths = sourceWalk.Paths;
        if (sourceWalk.Incomplete || sourcePaths.Count == 0) { return refusal + "; original source gate paths are unresolved or exceed the bounded witness"; }
        bool ContainsInOrder(IReadOnlyList<FormKey> destination, IReadOnlyList<FormKey> receiverGates,
            IReadOnlyList<FormKey> payloadGates, int skipPayload)
        {
            var receiver = 0; var payload = skipPayload;
            foreach (var key in destination)
                if (receiver < receiverGates.Count)
                { if (key == receiverGates[receiver]) receiver++; }
                else if (payload < payloadGates.Count && key == payloadGates[payload]) payload++;
            return receiver == receiverGates.Count && payload >= payloadGates.Count;
        }
        // R's ancestry is independent of the source and route; its walk shares the
        // source walk's step bound, as when both ran in one pass.
        var ancestry = MirrorReceiverAncestry(site.Receiver, catalog);
        var ancestors = ancestry.Paths;
        if (ancestry.Incomplete || sourceWalk.Work + ancestry.Work > 8192 || ancestors.Count == 0)
        { return refusal + "; original receiver ancestry is unresolved or exceeds supported static placement"; }
        // Checking all later actor levels is conservative when another destination
        // tier may close this placement. Never waive a gate using an alternative
        // that is suppressed by its own original highest-tier source window.
        var skipReceiverGate = Gate(site.Receiver) ? 1 : 0;
        bool MatchesWindows(TypedLootUse endpoint, FormKey[] destination, int minimum, int maximum)
        {
            var viable = false;
            var supporting = ancestry.Supporting(endpoint);
            var bands = sourceWalk.LevelBoundaries
                .Concat(supporting.SelectMany(p => new[] { p.Minimum, p.Maximum + 1 })).Append(minimum)
                .Where(level => level >= minimum && level <= maximum).Distinct().Order();
            foreach (var level in bands)
            {
                var paths = sourcePaths.Where(p => p.Minimum <= level && p.Maximum >= level).ToArray();
                // Empty payload positions stay empty in the projection. An
                // unavailable ancestor does not: reject every uncovered band.
                if (paths.Length == 0) continue;
                viable = true;
                var liveAncestors = supporting.Where(a => a.Minimum <= level && a.Maximum >= level).ToArray();
                if (liveAncestors.Length == 0) { return false; }
                if (!liveAncestors.Any(a => paths.Any(p =>
                    ContainsInOrder(destination, a.Gates, p.Gates, skipReceiverGate))))
                {
                    return false;
                }
            }
            return viable;
        }
        // The destination's consumer paths depend only on the route.
        var destinationWalk = MirrorDestinationPaths(route, catalog, identity);
        if (destinationWalk.Bad || !destinationWalk.Matched) return refusal;
        var reached = destinationWalk.Reached;
        // A fixed Chance None 100% without a Global never opens. A source whose every
        // path, or a destination whose every consumer path, passes one has nothing to follow.
        bool Closed(FormKey key) => graph.Lists[key].Global.IsNull && graph.Lists[key].ChanceNone.Value >= 1;
        if (sourcePaths.All(p => p.Gates.Any(Closed)) || ancestors.All(a => a.Gates.Any(Closed)))
            return refusal + "; fixed Chance None 100% leaves the source unavailable";
        if (reached.All(r => r.Gates.Any(Closed)))
            return refusal + "; fixed Chance None 100% leaves the enchanted destination unavailable";
        // Keep the proven destination bands even when no source controls need copying.
        // Static script counterparts must overlap these bands to be distributable.
        var destinationWindow = destinationWalk.Window;
        copy = new MirrorGateCopy([], [], destinationWindow);
        var strict = true;
        foreach (var endpoint in reached)
            if (!MatchesWindows(endpoint.Endpoint, endpoint.Gates, endpoint.Minimum, endpoint.Maximum))
            { strict = false; break; }
        if (strict) return null;

        // Each source has passed its own guards above. What the receiver must
        // additionally impose now depends only on these two frozen witnesses.
        if (!graph.MirrorReceiverCopies.TryGetValue((ancestry, destinationWalk), out var prepared))
        {
            using var copyTiming = PhaseTiming.Measure("mirror.receiver-copy-proof");
            prepared = MissingReceiverGateCopy(ancestry, destinationWalk, graph);
            graph.MirrorReceiverCopies.Add((ancestry, destinationWalk), prepared);
        }
        copy = prepared.Copy;
        return prepared.Problem;
    }

    private static MirrorGateResult MissingReceiverGateCopy(MirrorAncestry ancestry, MirrorDestinationWalk destination,
        SpecializedEvidence graph)
    {
        const string refusal = OriginalGateRefusal;
        var reached = destination.Reached;
        // Copy what the enchanted path lacks instead of refusing. Gates and windows
        // inside the payload are already reproduced by its projection. Receiver-side
        // alternatives are combined conservatively: every gate of every supporting
        // alternative is required (AND), and the window is the intersection over all
        // reached consumers of the union of their supporting alternatives' windows.
        // A counterpart therefore appears only where and when the source can.
        var window = new List<(int Minimum, int Maximum)> { (1, 65535) };
        var required = new HashSet<FormKey>();
        foreach (var endpoint in reached)
        {
            var supporting = ancestry.Supporting(endpoint.Endpoint);
            if (supporting.Length == 0)
            {
                return new(refusal + "; enchanted destination reaches a consumer the source never reaches", MirrorGateCopy.None);
            }
            window = IntersectBands(window, MergeBands(supporting.Select(a => (a.Minimum, a.Maximum))));
            foreach (var gate in supporting.SelectMany(a => a.Gates)) required.Add(gate);
        }
        if (IntersectBands(window, destination.Window.ToList()).Count == 0)
            return new(refusal + "; no common original actor-level window across the enchanted destination's consumers", MirrorGateCopy.None);
        var controls = new List<MirrorCopiedControl>();
        foreach (var gate in required.Where(g => !reached.All(r => r.Gates.Contains(g))).Order())
        {
            var list = graph.Lists[gate];
            if (((int)list.Flags & 7) is 4 or 5)
            {
                return new(refusal + "; a missing original UseAll selector cannot be copied", MirrorGateCopy.None);
            }
            controls.Add(new(gate, list.Global.IsNull ? FormKey.Null : list.Global.FormKey, list.ChanceNone.Value));
        }
        if (window.Any(band => band.Minimum > short.MaxValue))
            return new(refusal + "; original actor-level window starts beyond the supported signed level range", MirrorGateCopy.None);
        var copied = window is [(1, var top)] && top >= short.MaxValue ? []
            : window.Select(band => (band.Minimum, band.Maximum >= short.MaxValue ? 65535 : band.Maximum)).ToArray();
        return new(null, new MirrorGateCopy(controls.ToArray(), copied, destination.Window));
    }

    private sealed record MirrorAncestry(List<(TypedLootUse Use, FormKey[] Gates, int Minimum, int Maximum)> Paths, int Work, bool Incomplete)
    {
        private readonly Dictionary<TypedLootUse, (TypedLootUse Use, FormKey[] Gates, int Minimum, int Maximum)[]> supporting = [];
        public (TypedLootUse Use, FormKey[] Gates, int Minimum, int Maximum)[] Supporting(TypedLootUse endpoint)
            => supporting.TryGetValue(endpoint, out var known) ? known
                : supporting[endpoint] = Paths.Where(p => p.Use == endpoint || ExactActorDeathUse(endpoint, p.Use)).ToArray();
    }
    private sealed record MirrorSourceWalk(List<(FormKey[] Gates, int Minimum, int Maximum)> Paths, int Work, bool Incomplete)
    {
        public int[] LevelBoundaries { get; } = Paths.SelectMany(p => new[] { p.Minimum, p.Maximum + 1 }).Distinct().ToArray();
        // Equal for walks with the same paths, work and completeness, whatever the source.
        public int Id { get; init; }
        public static string Content(List<(FormKey[] Gates, int Minimum, int Maximum)> paths, int work, bool incomplete)
            => $"{work}|{incomplete}|" + string.Join(";", paths.Select(p => $"{string.Join(">", p.Gates)}:{p.Minimum}-{p.Maximum}"));
    }
    private sealed record MirrorDestinationWalk(List<(TypedLootUse Endpoint, FormKey[] Gates, int Minimum, int Maximum)> Reached, bool Bad, bool Matched)
    {
        public (int Minimum, int Maximum)[] Window { get; } = MergeBands(Reached.Select(r => (r.Minimum, r.Maximum))).ToArray();
    }

    // Every destination for the same source insertion uses these payload paths.
    // Retain the original traversal cost as well as its verdict so the combined
    // source/receiver work bound remains identical on cache hits.
    private static MirrorSourceWalk MirrorSourcePaths(MirrorSite site, FormKey source, SpecializedEvidence graph)
    {
        var identity = (source, site.Receiver, site.Payload);
        if (graph.MirrorSources.TryGetValue(identity, out var known)) return known;
        var paths = new List<(FormKey[] Gates, int Minimum, int Maximum)>();
        var work = 0; var incomplete = false;
        void Visit(FormKey key, List<FormKey> gates, int depth, int minimum, int maximum)
        {
            if (++work > 8192 || depth > 16 || paths.Count > 512) { incomplete = true; return; }
            if (minimum > maximum) return;
            if (key == source) { paths.Add((gates.ToArray(), minimum, maximum)); return; }
            if (!graph.Lists.TryGetValue(key, out var list)) return;
            if (MirrorUnsupportedLevels(graph, key)) { incomplete = true; return; }
            var nextGates = MirrorGate(graph, key) ? gates.Append(key).ToList() : gates;
            var giveAll = ((int)list.Flags & 7) is 4 or 5;
            var highestOnly = !giveAll && ((int)list.Flags & 1) == 0;
            var rows = LeveledEntryRows(list);
            foreach (var (level, reference, _, _, _) in rows)
            {
                var start = giveAll ? minimum : Math.Max(minimum, level);
                var end = maximum;
                if (highestOnly)
                {
                    // The next higher entry level bounds this entry's window.
                    var next = 65535;
                    foreach (var other in rows) if (other.Level > level) next = Math.Min(next, other.Level - 1);
                    end = Math.Min(maximum, next);
                }
                Visit(reference, nextGates, depth + 1, start, end);
            }
        }
        Visit(site.Payload, MirrorGate(graph, site.Receiver) ? [site.Receiver] : [], 0, 1, 65535);
        var content = MirrorSourceWalk.Content(paths, work, incomplete);
        if (!graph.MirrorWalkIds.TryGetValue(content, out var id)) graph.MirrorWalkIds[content] = id = graph.MirrorWalkIds.Count;
        return graph.MirrorSources[identity] = new(paths, work, incomplete) { Id = id };
    }

    private static (bool UnsupportedLevel, bool UnsupportedEntry) MirrorEntryFacts(SpecializedEvidence graph, FormKey key)
    {
        if (graph.MirrorEntryMetadata.TryGetValue(key, out var known)) return known;
        return graph.MirrorEntryMetadata[key] = graph.Lists.TryGetValue(key, out var list)
            ? ((list.Entries ?? []).Any(e => e.Data is { Level: < 0 }),
                (list.Entries ?? []).Any(e => e.Data is not { Count: > 0 } || e.ExtraData is not null))
            : (false, false);
    }
    private static bool MirrorUnsupportedLevels(SpecializedEvidence graph, FormKey key) => MirrorEntryFacts(graph, key).UnsupportedLevel;
    // premiseReceiver: the observed receiver, whose own flags follow the observer contract.
    private static bool MirrorUnsupportedMetadata(SpecializedEvidence graph, Catalog catalog, FormKey key, FormKey premiseReceiver = default)
        => graph.Lists.TryGetValue(key, out var list)
            && (MirrorUnsupportedLevels(graph, key) || (!KnownSelectionFlags(list)
                && !(!premiseReceiver.IsNull && key == premiseReceiver && MirrorReceiverFlagsSupported(key, catalog)))
                || MirrorEntryFacts(graph, key).UnsupportedEntry);
    private static bool MirrorGate(SpecializedEvidence graph, FormKey key) => graph.Lists.TryGetValue(key, out var list)
        && (!list.Global.IsNull || list.ChanceNone.Value > 0 || ((int)list.Flags & 7) is 4 or 5);

    // Extend the original gate/window assertion through R's ordinary parents. No
    // observed-path aliases or generated availability proofs are involved; authored
    // ancestors are static placement assumptions. The walk depends only on R and the
    // frozen graph, so each receiver is walked once per graph. R's own gate heads each
    // payload path, so the gates collected here start at R's parents' order reversed.
    private static MirrorAncestry MirrorReceiverAncestry(FormKey receiver, Catalog catalog)
    {
        var graph = SpecializedGraph(catalog);
        if (graph.MirrorAncestries.TryGetValue(receiver, out var known)) return known;
        var ancestors = new List<(TypedLootUse Use, FormKey[] Gates, int Minimum, int Maximum)>();
        var work = 0; var incomplete = false;
        void AncestorPaths(FormKey key, List<FormKey> reversedGates, HashSet<FormKey> active, int depth, int minimum, int maximum)
        {
            if (minimum > maximum) return;
            if (++work > 8192 || depth > 16 || ancestors.Count > 512 || !active.Add(key)) { incomplete = true; return; }
            if (!graph.Lists.TryGetValue(key, out var list) || list.IsDeleted || MirrorUnsupportedMetadata(graph, catalog, key, receiver)
                || (key != receiver && (catalog.RuntimeControlledLists.ContainsKey(key)
                    || catalog.ScriptUnlocks.ContainsKey(key) || catalog.UnsafeAvailability.ContainsKey(key))))
            { active.Remove(key); return; }
            var next = MirrorGate(graph, key) ? reversedGates.Append(key).ToList() : reversedGates;
            var gates = next.AsEnumerable().Reverse().ToArray();
            foreach (var use in graph.Uses.GetValueOrDefault(key, []).Where(u => u.Known))
                ancestors.Add((use, gates, minimum, maximum));
            foreach (var parent in graph.Parents.GetValueOrDefault(key, []).Distinct())
            {
                var owner = graph.Lists[parent.List];
                // An independent supported source path can license this exact
                // consumer; unsupported alternatives supply no evidence.
                if (MirrorUnsupportedMetadata(graph, catalog, parent.List)) { continue; }
                if (LeveledEntryRows(owner).Any(e => e.Reference == key && e.HasExtraData)
                    || parent.Count <= 0) { continue; }
                var start = Math.Max(minimum, SelectionEntryMinimum(owner, parent.Level));
                var end = HasHighestEligibleCutoff(owner) ? Math.Min(maximum, SelectionEntryMaximum(owner, parent.Level)) : maximum;
                AncestorPaths(parent.List, next, active, depth + 1, start, end);
            }
            active.Remove(key);
        }
        AncestorPaths(receiver, [], [], 0, 1, 65535);
        return graph.MirrorAncestries[receiver] = new(ancestors, work, incomplete);
    }

    // The enchanted destination's consumer paths depend only on the route, so each
    // route shape is walked once per graph.
    private static MirrorDestinationWalk MirrorDestinationPaths(SpecializedRoute route, Catalog catalog, MirrorRouteIdentity identity)
    {
        var graph = SpecializedGraph(catalog);
        var isolation = route.IsolationPath ?? [];
        if (graph.MirrorDestinations.TryGetValue(identity, out var known)) return known;
        var matchedEndpoint = false; var bad = false; var work = 0;
        var reached = new List<(TypedLootUse Endpoint, FormKey[] Gates, int Minimum, int Maximum)>();
        void DestinationPaths(FormKey key, List<FormKey> reversedGates, HashSet<FormKey> active, int depth, int minimum, int maximum)
        {
            if (minimum > maximum) return;
            if (bad) return;
            if (++work > 8192 || depth > 64 || !active.Add(key)) { bad = true; return; }
            if (!graph.Lists.ContainsKey(key) || MirrorUnsupportedMetadata(graph, catalog, key))
            { bad = true; return; }
            var nextGates = MirrorGate(graph, key) ? reversedGates.Append(key).ToList() : reversedGates;
            var pathIndex = -1;
            for (var i = 0; i < isolation.Count - 1; i++) if (isolation[i] == key) { pathIndex = i; break; }
            // Interior copies have no original direct consumers. Only the final
            // incoming boundary and its original ancestors consume this copy.
            var recipientBoundary = route.RecipientIsolation is not null && isolation.Count > 0 && key == isolation[^1];
            FormKey[]? gates = null;
            foreach (var endpoint in pathIndex >= 0 ? [] : graph.Uses.GetValueOrDefault(key, []).Where(u => u.Known
                && (!recipientBoundary || u.Consumer == route.RecipientIsolation!.Consumer && u.Role == route.RecipientIsolation.Role)))
            {
                matchedEndpoint = true;
                reached.Add((endpoint, gates ??= nextGates.AsEnumerable().Reverse().ToArray(), minimum, maximum));
            }
            var parents = recipientBoundary ? [] : graph.Parents.GetValueOrDefault(key, []).Distinct();
            if (pathIndex >= 0) parents = parents.Where(p => p.List == isolation[pathIndex + 1]);
            foreach (var parent in parents)
            {
                var owner = graph.Lists[parent.List];
                if (MirrorUnsupportedMetadata(graph, catalog, parent.List)) { bad = true; continue; }
                if (LeveledEntryRows(owner).Any(e => e.Reference == key && e.HasExtraData)
                    || parent.Count <= 0) { bad = true; continue; }
                var start = Math.Max(minimum, SelectionEntryMinimum(owner, parent.Level));
                var end = HasHighestEligibleCutoff(owner) ? Math.Min(maximum, SelectionEntryMaximum(owner, parent.Level)) : maximum;
                DestinationPaths(parent.List, nextGates, active, depth + 1, start, end);
            }
            active.Remove(key);
        }
        var point = route.MaterialPath is { Count: > 1 } material ? material[^1] : route.Destination.List;
        var terminal = graph.Lists[route.Destination.List];
        var routeMinimum = Math.Max(1, (int)route.Destination.Level);
        var routeMaximum = HasHighestEligibleCutoff(terminal) ? SelectionEntryMaximum(terminal, route.Destination.Level) : 65535;
        DestinationPaths(point, [], [], 0, routeMinimum, routeMaximum);
        return graph.MirrorDestinations[identity] = new(reached, bad, matchedEndpoint);
    }

    private static List<(int Minimum, int Maximum)> MergeBands(IEnumerable<(int Minimum, int Maximum)> bands)
    {
        var merged = new List<(int Minimum, int Maximum)>();
        foreach (var band in bands.Where(b => b.Minimum <= b.Maximum).OrderBy(b => b.Minimum).ThenBy(b => b.Maximum))
            if (merged.Count > 0 && band.Minimum <= merged[^1].Maximum + 1)
                merged[^1] = (merged[^1].Minimum, Math.Max(merged[^1].Maximum, band.Maximum));
            else merged.Add(band);
        return merged;
    }
    private static List<(int Minimum, int Maximum)> IntersectBands(List<(int Minimum, int Maximum)> left, List<(int Minimum, int Maximum)> right)
        => MergeBands(left.SelectMany(a => right.Select(b => (Math.Max(a.Minimum, b.Minimum), Math.Min(a.Maximum, b.Maximum)))));
}
