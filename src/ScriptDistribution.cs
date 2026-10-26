using System.IO;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

// "Follow script distribution": a source script's literal LeveledItem.AddForm calls
// become virtual receiver edges that the ordinary specialized and material planners
// place, under the same consumer, gate and window rules as static sources. Calls in
// one block always run together; each block gets one owned Global. XEP_ScriptGate
// opens it (Chance None 0) only while the block's last insertion is present in its
// receiver, and closes it (100) otherwise, so counterparts follow the script's
// current result without observing every row.
public static partial class PatcherEngine
{
    private static readonly ModKey ScriptGatePlanningMod = ModKey.FromFileName("__XEP_ScriptGatePlanning.esp");

    private sealed record ScriptSite(string Instance, string Function, FormKey Receiver, FormKey Payload,
        short Level, short Count, int Position, string Block, bool InLoop, string Evidence, string ReceiverProperty, string PayloadProperty)
    {
        public MirrorSite Mirror => new(Receiver, Payload, Level, Count, Evidence);
    }
    // Members run together with Representative; its presence in Representative.Receiver
    // proves the whole block ran after the receivers' last reset. The gate refreshes on
    // the owning function's completion event and on every subscribed resetter's event.
    private sealed record ScriptGate(string Block, FormKey Placeholder, ScriptSite Representative, ScriptSite[] Members, string[] CompletionEvents, string[] Warnings);
    private sealed record ScriptReset(string Instance, string Function, FormKey Receiver, int Position, string Method, FormKey[] Owners);
    private sealed record ScriptSiteVerdict(string? Problem, string[] Warnings, string[] Events);

    // One pass owns the accepted gates and the frozen evidence graph. A new gate
    // selection or graph replaces the index; a different source has its own chains.
    private sealed class ScriptChainIndex
    {
        public readonly ScriptGate[] Gates;
        public readonly SpecializedEvidence Graph;
        public readonly (ScriptGate Gate, ScriptSite Site)[] Members;
        public readonly ILookup<FormKey, (ScriptGate Gate, ScriptSite Site)> ByReceiver;
        public readonly Dictionary<FormKey, (ScriptGate Gate, ScriptSite Site)[][]> Chains = [];
        public ScriptChainIndex(ScriptGate[] gates, SpecializedEvidence graph)
        {
            Gates = gates; Graph = graph;
            Members = gates.SelectMany(g => g.Members.Select(m => (Gate: g, Site: m))).ToArray();
            ByReceiver = Members.ToLookup(m => m.Site.Receiver);
        }
    }

    // Every literal insertion site, independent of the selection. Gates copy the
    // collections they extend.
    private sealed record ScriptSiteScan(ScriptSite[] Sites, ScriptReset[] Reverts, HashSet<FormKey> UnresolvedTargets,
        HashSet<string> UntrustedInstances, Dictionary<FormKey, HashSet<FormKey>> Reached, int Instances);

    // The selected source items decide which mods are patched (ADR 0008): their own mods
    // and the source mods of insertions that hand any of them out. The gates' combined
    // evidence depends only on that mod set, so a selection with the same mods reuses them.
    private static void SetPatchedItems(Catalog catalog, IEnumerable<FormKey> items)
    {
        var selected = items.ToHashSet();
        if (catalog.PatchedItems is { } old && old.SetEquals(selected)) return;
        var mods = PatchedModsFor(selected, catalog);
        catalog.PatchedItems = selected;
        if (catalog.PatchedMods is { } oldMods && oldMods.SetEquals(mods)) return;
        catalog.PatchedMods = mods; catalog.ScriptGates = null; catalog.ScriptChains = null; catalog.ScriptGateRefusals.Clear(); catalog.RuntimeSliceBindings.Clear();
        catalog.AdoptedGateReceivers = null;
        // Gates built for the previous mod set left their receiver effects.
        if (catalog.GateFreeReceivers is { } free)
        {
            catalog.ScriptReceiverLevels.Clear();
            foreach (var (receiver, levels) in free.Levels) catalog.ScriptReceiverLevels[receiver] = new(levels);
            catalog.ScriptUnresolvedReceivers.Clear(); catalog.ScriptUnresolvedReceivers.UnionWith(free.Unresolved);
        }
    }

    private static HashSet<ModKey> PatchedModsFor(HashSet<FormKey> items, Catalog catalog)
    {
        var scan = ScriptSites(catalog);
        var patched = items.Select(k => k.ModKey).ToHashSet();
        foreach (var site in scan.Sites)
            if (ScriptPayloadReach(site.Payload, scan, catalog).Overlaps(items)) patched.Add(SliceSourceMod(catalog.RuntimeScriptInstances[site.Instance]));
        return patched;
    }

    // Scan evidence for items without ordinary enchanted access: eligible items that a literal
    // insertion hands out are reviewed as if patched, with the saved Follow script items, so
    // their recommendation uses the same accepted chains the planner would (ADR 0008/0009).
    private static Func<FormKey, (ScriptGate Gate, ScriptSite Site)[][]> ScriptRecommendationChains(List<CandidateItem> candidates,
        Catalog catalog, ProfileSettings profile, Func<FormKey, bool> ordinaryAccess)
    {
        using var timing = PhaseTiming.Measure("review.script-recommendation");
        var handedOut = ScriptDistributionSources(catalog);
        var reviewed = new HashSet<FormKey>();
        foreach (var candidate in candidates)
        {
            candidate.HasScriptDistributionSource = false;
            if (!candidate.Eligible) continue;
            var key = FormKey.Factory(candidate.FormKey);
            candidate.HasScriptDistributionSource = handedOut.Contains(key);
            if (candidate.HasScriptDistributionSource && !profile.SpecializedLootOverrides.ContainsKey(candidate.FormKey) && !ordinaryAccess(key))
                reviewed.Add(key);
        }
        // Selection is decided after review; saved Follow script items stand in for it. The set
        // is settled even with nothing to review, so gates adopted for another set are dropped.
        SetPatchedItems(catalog, reviewed.Concat(candidates.Where(c => c.Eligible
            && profile.SpecializedLootOverrides.GetValueOrDefault(c.FormKey) == SpecializedLootMode.ScriptDistribution).Select(c => FormKey.Factory(c.FormKey))));
        if (reviewed.Count == 0) return _ => [];
        var gates = ScriptDistributionGates(catalog);
        return key => reviewed.Contains(key) ? ScriptSiteChains(key, gates, catalog) : [];
    }

    // Conservative discovery only; accepted gates and placements are decided later.
    private static HashSet<FormKey> ScriptDistributionSources(Catalog catalog)
    {
        var scan = ScriptSites(catalog);
        return scan.Sites.SelectMany(s => ScriptPayloadReach(s.Payload, scan, catalog)).ToHashSet();
    }
    // Records a payload hands out through its authored entries, itself included.
    private static HashSet<FormKey> ScriptPayloadReach(FormKey payload, ScriptSiteScan scan, Catalog catalog)
    {
        if (scan.Reached.TryGetValue(payload, out var known)) return known;
        var graph = SpecializedGraph(catalog);
        var seen = new HashSet<FormKey>(); var pending = new Queue<(FormKey Key, int Depth)>(); pending.Enqueue((payload, 0));
        while (pending.TryDequeue(out var current))
            if (current.Depth <= 16 && seen.Count < 8192 && seen.Add(current.Key) && graph.Lists.TryGetValue(current.Key, out var list))
                foreach (var entry in list.Entries ?? []) if (entry.Data is { } data) pending.Enqueue((data.Reference.FormKey, current.Depth + 1));
        return scan.Reached[payload] = seen;
    }

    // The final selection decides which mods are patched: those analysed in script
    // distribution, i.e. selected items that follow it (ADR 0008).
    private static void SetPatchedSelection(Catalog catalog, IEnumerable<CandidateItem> candidates) =>
        SetPatchedItems(catalog, candidates.Where(c => c.Selected && c.Eligible && c.SpecializedMode == SpecializedLootMode.ScriptDistribution)
            .Select(c => FormKey.Factory(c.FormKey)));

    // Script instances only accumulate while a catalog is analysed, so their count keys the scan.
    private static ScriptSiteScan ScriptSites(Catalog catalog)
    {
        if (catalog.ScriptSites is { } scanned && scanned.Instances == catalog.RuntimeScriptInstances.Count) return scanned;
        using var timing = PhaseTiming.Measure("script.sites");
        var sites = new List<ScriptSite>();
        var reverts = new List<ScriptReset>();
        var unresolvedTargets = new HashSet<FormKey>();
        var untrustedInstances = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in catalog.RuntimeScriptInstances.Values.OrderBy(i => i.Identity, StringComparer.Ordinal))
        {
            if (instance.Text is null) continue;
            if (instance.BindingMetadataProblem is not null) { untrustedInstances.Add(instance.Identity); continue; }
            foreach (var function in instance.Functions)
            {
                var stack = new List<(int Id, bool Loop)> { (0, false) };
                var nextBlock = 0; var offset = 0;
                foreach (var raw in function.Body.Split('\n'))
                {
                    var position = function.BodyStart + offset; offset += raw.Length + 1;
                    var line = raw.Split(';')[0].Trim();
                    if (line.Length == 0) continue;
                    var keyword = Regex.Match(line, @"(?i)^(if|elseif|else|endif|while|endwhile)\b");
                    if (keyword.Success)
                    {
                        switch (keyword.Groups[1].Value.ToLowerInvariant())
                        {
                            case "if": stack.Add((++nextBlock, false)); break;
                            case "while": stack.Add((++nextBlock, true)); break;
                            case "elseif": case "else": if (stack.Count > 1) stack[^1] = (++nextBlock, false); break;
                            default: if (stack.Count > 1) stack.RemoveAt(stack.Count - 1); break;
                        }
                        continue;
                    }
                    var call = DirectMutation.Match(line);
                    if (!call.Success) continue;
                    var method = call.Groups["m"].Value;
                    var bound = instance.Objects.TryGetValue(call.Groups["recv"].Value, out var r);
                    // A bound non-list receiver (for example a FormList) changes no leveled list,
                    // and a declared property the plugin never fills is None: the call does nothing.
                    if (bound && !catalog.Lists.ContainsKey(r)) continue;
                    if (!bound && DeclaredProperty(instance.Text, call.Groups["recv"].Value)) continue;
                    var receiver = bound ? r : (FormKey?)null;
                    if (!method.Equals("AddForm", StringComparison.OrdinalIgnoreCase))
                    {
                        // Changes through other handles are slice-flow effects (below).
                        if (receiver is { } reverted && IsRuntimeListReset(method))
                            reverts.Add(new(instance.Identity, function.Name, reverted, position, method, [instance.Quest.FormKey]));
                        continue;
                    }
                    var args = SplitArguments(call.Groups["args"].Value);
                    var payloadName = args.Length > 0 ? Regex.Replace(args[0], @"(?i)\s+as\s+form\s*$", "").Trim() : "";
                    var resolved = receiver is not null && args.Length == 3 && instance.Objects.TryGetValue(payloadName, out var payload)
                        && short.TryParse(args[1].Trim(), out var level) && level > 0 && short.TryParse(args[2].Trim(), out var count) && count > 0;
                    if (!resolved && receiver is not null && args.Length == 3 && !instance.Objects.ContainsKey(payloadName)
                        && DeclaredProperty(instance.Text, payloadName)) continue; // AddForm(None) does nothing.
                    if (!resolved) { if (receiver is { } target) unresolvedTargets.Add(target); continue; }
                    instance.Objects.TryGetValue(payloadName, out payload);
                    sites.Add(new(instance.Identity, function.Name, receiver!.Value, payload, short.Parse(args[1].Trim()), short.Parse(args[2].Trim()), position,
                        $"{instance.Identity}|{function.Name}|{string.Join('>', stack.Select(s => s.Id))}", stack.Any(s => s.Loop),
                        $"script-site {instance.Label}.{function.Name}@{position}: {line}; {instance.Evidence}", call.Groups["recv"].Value, payloadName));
                }
            }
        }
        return catalog.ScriptSites = new(sites.ToArray(), reverts.ToArray(), unresolvedTargets, untrustedInstances, [], catalog.RuntimeScriptInstances.Count);
    }

    // Lists the script changes at runtime never receive static counterparts.
    private static HashSet<FormKey> ScriptProtectedLists(Catalog catalog)
    {
        var lists = ScriptDistributionGates(catalog).SelectMany(g => g.Members).SelectMany(s => new[] { s.Receiver, s.Payload }).ToHashSet();
        lists.UnionWith(catalog.ScriptAddTargets);
        return lists;
    }

    private static ScriptGate[] ScriptDistributionGates(Catalog catalog)
    {
        if (catalog.ScriptGates is { } built)
        {
            if (catalog.AdoptedGateReceivers is { } gated)
            {
                foreach (var (receiver, levels) in gated.Levels) catalog.ScriptReceiverLevels[receiver] = new(levels);
                catalog.ScriptUnresolvedReceivers.UnionWith(gated.Unresolved);
                catalog.AdoptedGateReceivers = null;
            }
            return built;
        }
        using var timing = PhaseTiming.Measure("script.gates");
        var scan = ScriptSites(catalog);
        var sites = scan.Sites; var reverts = scan.Reverts.ToList(); var untrustedInstances = scan.UntrustedInstances;
        var unresolvedTargets = new HashSet<FormKey>(scan.UnresolvedTargets);
        // Without a selection (PatchedMods unset) each source stands alone.
        // ADR 0009: each mod's own slice flow, before any verdict. Insertions and resets
        // through other handles join the scanned ones.
        var analysed = (catalog.PatchedMods ?? sites.Select(s => SliceSourceMod(catalog.RuntimeScriptInstances[s.Instance]))).Distinct()
            .OrderBy(m => m.FileName.String, StringComparer.OrdinalIgnoreCase).ToArray();
        var flowMetadata = new HashSet<FormKey>();
        EnsureSliceFlows(analysed, catalog);
        foreach (var (effect, body) in analysed.Select(m => EnsureSliceFlow(m, catalog))
            .SelectMany(state => state.Results.Values.SelectMany(r => r.Effects).Distinct().Select(e => (e, state.Bodies[e.Body]))))
        {
            if (effect.Method.Equals("AddForm", StringComparison.OrdinalIgnoreCase)) unresolvedTargets.UnionWith(effect.Lists);
            else if (IsRuntimeListReset(effect.Method))
                foreach (var list in effect.Lists) reverts.Add(new(body.Instance?.Identity ?? body.Identity, effect.Function, list, effect.Position, effect.Method, body.Owners));
            else flowMetadata.UnionWith(effect.Lists);
        }
        var pairCounts = sites.GroupBy(s => (s.Receiver, s.Payload)).ToDictionary(g => g.Key, g => g.Count());
        // Every level a script can add to a receiver, for highest-eligible cutoffs.
        foreach (var site in sites)
        {
            if (!catalog.ScriptReceiverLevels.TryGetValue(site.Receiver, out var levels)) catalog.ScriptReceiverLevels[site.Receiver] = levels = [];
            levels.Add(site.Level);
        }
        catalog.ScriptUnresolvedReceivers.UnionWith(unresolvedTargets);
        var payloadTrees = new Dictionary<(FormKey Receiver, FormKey Payload), (FormKey[] Lists, string? Problem)>();
        var resetTargets = reverts.Select(r => r.Receiver).ToHashSet();
        var completions = new Dictionary<(string Instance, string Function), ScriptCompletion>();
        ScriptCompletion Completion(string instance, string function)
        {
            if (!completions.TryGetValue((instance, function), out var completion))
                completions[(instance, function)] = completion = ScriptCompletionEvent(SliceInstance(instance, catalog)!, function, catalog);
            return completion;
        }
        // Slice proofs (ADR 0009) of one binding, including a receiver's holder walk.
        var bindings = new Dictionary<(string, string, bool), RuntimePropertyBinding>();
        RuntimePropertyBinding SliceBindingFor(ScriptInstance instance, string property, bool receiver)
        {
            if (bindings.TryGetValue((instance.Identity, property.ToLowerInvariant(), receiver), out var old)) return old;
            var binding = catalog.RuntimeSliceBindingResolver!(instance, property, receiver);
            if (binding.Problem is null && receiver && instance.Objects.TryGetValue(property, out var list) && catalog.Lists.ContainsKey(list))
            {
                var scope = new RuntimeSliceScope(SliceSourceMod(instance));
                binding = RuntimeHolderFrameProblem([], catalog, list, out _, scope) is { } holder ? new(holder, true)
                    : binding with { Warnings = binding.Warnings.Concat(scope.Warnings).Distinct().ToArray() };
            }
            return bindings[(instance.Identity, property.ToLowerInvariant(), receiver)] = binding;
        }
        // ADR 0008/0009: an unproved operation that can reach this list refuses inside
        // the source mod and warns outside it. Binding taints are re-proved in the slice.
        string? SliceListProblem(FormKey list, ModKey source, List<string> warnings)
        {
            if (SliceFlowProblem([list], source, catalog, warnings) is { } flow) return flow;
            // Other mods' scripts are outside the proof; their changes to the list come
            // from their own analyses when they are patched (above and the resetter rule).
            foreach (var taint in catalog.RuntimeListTaints.GetValueOrDefault(list, []).Where(t => !t.Flow))
            {
                var instance = catalog.RuntimeScriptInstances[taint.Instance];
                if (!InSliceScope(instance.Quest.FormKey, source, catalog)) continue;
                var reason = taint.Reason;
                if (taint.Property is { } property)
                {
                    var binding = SliceBindingFor(instance, property, taint.Receiver);
                    if (binding.Problem is null) { warnings.AddRange(binding.Warnings); continue; }
                    reason = binding.Problem;
                }
                return $"{instance.Label}: {reason}";
            }
            return null;
        }
        // Scripts touching the same list: the source mod and every patched mod, each from
        // its own analysis (ADR 0008). A signalling function subscribes the gate, so any
        // number of signals refresh it; a signal-less reset is refused inside the source
        // mod and warned in another patched mod. Other mods' resets are outside the proof.
        string? OtherChangesProblem(FormKey list, ScriptSite site, Func<string, string, bool> own, ModKey source, List<string> warnings, List<string> events)
        {
            foreach (var reset in reverts.Where(x => x.Receiver == list && !own(x.Instance, x.Function)))
            {
                // An observed removal keeps its load-order-wide refusal.
                if (reset.Method.Equals("RemoveAddedForm", StringComparison.OrdinalIgnoreCase)) return "entries are removed by another function or script";
                if (!reset.Owners.Any(o => SliceCombined(o, source, catalog))) continue;
                // Only quest scripts can carry an S1 completion signal.
                var signal = SliceInstance(reset.Instance, catalog) is not null ? Completion(reset.Instance, reset.Function) : null;
                if (signal?.Event is { } name) { events.Add(name); warnings.AddRange(signal.Warnings); continue; }
                var label = SliceInstance(reset.Instance, catalog) is { } other ? other.Label : reset.Instance;
                if (reset.Owners.Any(o => InSliceScope(o, source, catalog))) return "receiver can be reset by another function or script";
                warnings.Add($"{label}.{reset.Function} in another patched mod resets {list} without a completion signal; affected gates resync at load and initialization after it runs.");
            }
            return null;
        }
        // E4: a payload-tree list whose entries the scripts change is mirrored with
        // per-entry gates when every change is a reset or a readable literal insertion
        // by a function the gate can follow. Its own insertions are gated script sites.
        string? BagProblem(FormKey bag, ScriptSite site, ModKey source, List<string> warnings, List<string> events)
        {
            if (unresolvedTargets.Contains(bag)) return "a script-changed payload list has an unresolved insertion";
            // Insertions by mods that are not patched are outside the proof (ADR 0008).
            var inserts = sites.Where(s => s.Receiver == bag && SliceCombined(catalog.RuntimeScriptInstances[s.Instance].Quest.FormKey, source, catalog)).ToArray();
            if (inserts.Any(s => s.InLoop)) return "a script-changed payload list has an insertion inside a loop";
            if (inserts.Any(s => untrustedInstances.Contains(s.Instance))) return "a script-changed payload list is changed by a script with unresolved list mutations";
            if (OtherChangesProblem(bag, site, (_, _) => false, source, warnings, events) is { } reset) return "a script-changed payload list: " + reset;
            foreach (var (instance, function) in inserts.Select(s => (s.Instance, s.Function)).Distinct())
            {
                var signal = Completion(instance, function);
                if (signal.Event is { } name) { events.Add(name); warnings.AddRange(signal.Warnings); continue; }
                var other = catalog.RuntimeScriptInstances[instance];
                if (InSliceScope(other.Quest.FormKey, source, catalog)) return $"a script-changed payload list is filled by {other.Label}.{function} without a completion signal";
                warnings.Add($"{other.Label}.{function} in another patched mod fills {bag} without a completion signal; affected gates resync at load and initialization after it runs.");
            }
            return null;
        }
        // Reuse the bounded payload traversal, including shared descendants.
        (FormKey[] Lists, string? Problem) PayloadTree(ScriptSite site)
        {
            if (!payloadTrees.TryGetValue((site.Receiver, site.Payload), out var tree))
            {
                var lists = MirrorPayloadTree(site.Mirror, catalog, out var problem);
                payloadTrees[(site.Receiver, site.Payload)] = tree = (lists, problem);
            }
            return tree;
        }
        // E4 bags: payload-tree lists whose entries a script changes.
        HashSet<FormKey>? bagLists = null;
        HashSet<FormKey> BagLists() => bagLists ??= sites.SelectMany(s => PayloadTree(s).Lists)
            .Where(l => catalog.ScriptAddTargets.Contains(l) || resetTargets.Contains(l)).ToHashSet();
        var verdicts = new Dictionary<ScriptSite, ScriptSiteVerdict>();
        ScriptSiteVerdict MemberVerdict(ScriptSite site)
        {
            if (verdicts.TryGetValue(site, out var old)) return old;
            var warnings = new List<string>(); var events = new List<string>();
            ScriptSiteVerdict Verdict(string? problem) => verdicts[site] = new(problem, warnings.Distinct().ToArray(), events.Distinct(StringComparer.Ordinal).ToArray());
            if (untrustedInstances.Contains(site.Instance)) return Verdict("its script has unresolved list mutations");
            if (site.InLoop) return Verdict("insertion inside a loop");
            // The receiver's entries bound the insertion's window and witness the block; LLOS may change them at startup.
            if (LlosWithheld(site.Receiver, catalog)) return Verdict(WithheldReceiver(site.Receiver));
            var instance = catalog.RuntimeScriptInstances[site.Instance];
            var source = SliceSourceMod(instance);
            foreach (var (property, receiver) in new[] { (site.ReceiverProperty, true), (site.PayloadProperty, false) })
            {
                var binding = SliceBindingFor(instance, property, receiver);
                if (binding.Problem is not null) return Verdict($"{(receiver ? "receiver" : "payload")} binding is unproved: {binding.Problem}");
                warnings.AddRange(binding.Warnings);
            }
            if (SliceListProblem(site.Receiver, source, warnings) is { } taint) return Verdict("receiver has an unproved runtime mutation or binding: " + taint);
            if (catalog.RuntimeMetadataMutations.Contains(site.Receiver) || flowMetadata.Contains(site.Receiver)) return Verdict("receiver metadata is changed at runtime");
            if (OtherChangesProblem(site.Receiver, site, (i, f) => i == site.Instance && f == site.Function, source, warnings, events) is { } other)
                return Verdict(other);
            // E4: a bag's item gate refreshes on every accepted signal of a function that
            // changes the bag, fillers included, like the receiver gate whose tree holds it.
            if (BagLists().Contains(site.Receiver) && BagProblem(site.Receiver, site, source, warnings, events) is { } itemProblem)
                return Verdict(itemProblem);
            // Static controls are valid only while every list in that tree is stable or an E4 bag.
            var tree = PayloadTree(site);
            if (tree.Problem is { } payloadProblem) return Verdict(payloadProblem);
            foreach (var list in tree.Lists)
            {
                if (catalog.RuntimeMetadataMutations.Contains(list) || flowMetadata.Contains(list)) return Verdict("payload list or descendant metadata is changed at runtime");
                if (SliceListProblem(list, source, warnings) is { } listTaint) return Verdict("payload list or descendant has an unproved runtime mutation: " + listTaint);
                if ((catalog.ScriptAddTargets.Contains(list) || resetTargets.Contains(list)) && BagProblem(list, site, source, warnings, events) is { } bag)
                    return Verdict(bag);
            }
            return Verdict(null);
        }
        string? RepresentativeProblem(ScriptSite site)
        {
            if (pairCounts[(site.Receiver, site.Payload)] > 1) return "the same payload is inserted into this receiver elsewhere";
            if (unresolvedTargets.Contains(site.Receiver)) return "receiver has an unresolved insertion";
            if ((catalog.Lists[site.Receiver].Entries ?? []).Any(e => e.Data?.Reference.FormKey == site.Payload))
                return "receiver already contains the payload statically";
            return null;
        }
        var gates = new List<ScriptGate>();
        // A later reset in the same function can empty one receiver independently of the
        // block. Those insertions form a group per receiver whose representative lives in
        // that receiver, so its presence also proves the reset did not run afterwards.
        int? NextReset(ScriptSite site) => reverts.Where(x => x.Receiver == site.Receiver && x.Instance == site.Instance
            && x.Function == site.Function && x.Position > site.Position).Select(x => (int?)x.Position).Min();
        // A representative after a reset cannot witness insertions erased by that reset.
        // Split each receiver's members at every reset, including repeated resets in one block.
        foreach (var block in sites.GroupBy(s => s.Block + (NextReset(s) is { } reset ? "|reset:" + s.Receiver + "@" + reset : "")).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = block.OrderBy(s => s.Position).ToArray();
            var owner = ordered[0];
            // Only patched source mods' blocks can hand out a selected item (ADR 0008).
            if (catalog.PatchedMods is { } patchedMods && !patchedMods.Contains(SliceSourceMod(catalog.RuntimeScriptInstances[owner.Instance]))) continue;
            var completion = Completion(owner.Instance, owner.Function);
            if (completion.Problem is { } completionProblem)
            { catalog.ScriptGateRefusals.Add($"{block.Key}: {completionProblem}"); continue; }
            var representative = ordered.Reverse().FirstOrDefault(s => MemberVerdict(s).Problem is null && RepresentativeProblem(s) is null);
            if (representative is null)
            {
                catalog.ScriptGateRefusals.Add($"{block.Key}: no insertion in this block can prove the block ran ({string.Join("; ",
                    ordered.Select(s => MemberVerdict(s).Problem ?? RepresentativeProblem(s)).Distinct())})");
                continue;
            }
            var members = new List<ScriptSite>();
            foreach (var site in ordered.Where(s => s.Position <= representative.Position))
                if (MemberVerdict(site).Problem is { } problem) catalog.ScriptGateRefusals.Add($"{site.Evidence}: {problem}");
                else members.Add(site);
            foreach (var site in ordered.Where(s => s.Position > representative.Position))
                catalog.ScriptGateRefusals.Add($"{site.Evidence}: follows the block's last provable insertion");
            var events = members.SelectMany(m => MemberVerdict(m).Events).Prepend(completion.Event!).Distinct(StringComparer.Ordinal).ToArray();
            var warnings = completion.Warnings.Concat(members.SelectMany(m => MemberVerdict(m).Warnings)).Distinct().ToArray();
            gates.Add(new(block.Key, new FormKey(ScriptGatePlanningMod, (uint)(gates.Count + 1)), representative, members.ToArray(), events, warnings));
        }
        return catalog.ScriptGates = gates.ToArray();
    }

    private static bool DeclaredProperty(string text, string name) =>
        Regex.IsMatch(text, $@"(?im)^\s*\w+(?:\[\])?\s+Property\s+{Regex.Escape(name)}\b");

    // A serial proof can reproduce payload paths sharing the same controls, with
    // the union of their windows. Differing alternative controls need a structural
    // projection; combining them into one AND can close a productive source.
    private static (MaterialAvailabilityControl[] Controls, List<(int Minimum, int Maximum)> Window, string? Problem) PayloadPathControls(
        FormKey payload, FormKey source, Catalog catalog, bool requireStaticPayload = false)
    {
        var graph = SpecializedGraph(catalog);
        var paths = new List<(FormKey[] Lists, int Minimum, int Maximum)>(); var work = 0; string? problem = null;
        void Visit(FormKey key, List<FormKey> lists, int depth, int minimum, int maximum)
        {
            if (problem is not null || minimum > maximum) return;
            if (++work > 8192 || depth > 16 || paths.Count > 512) { problem = "payload paths exceed the bounded search"; return; }
            if (key == source) { paths.Add((lists.ToArray(), minimum, maximum)); return; }
            if (!graph.Lists.TryGetValue(key, out var list)) return;
            if ((list.Entries ?? []).Any(e => e.Data is { Level: < 0 })) { problem = "payload level outside the signed static range"; return; }
            var next = lists.Append(key).ToList();
            var highestOnly = HasHighestEligibleCutoff(list);
            if (highestOnly && catalog.ScriptUnresolvedReceivers.Contains(key) && graph.Up(source).Contains(key))
            { problem = "highest-eligible payload path has an insertion with unknown level"; return; }
            foreach (var entry in list.Entries ?? [])
                if (entry.Data is { } data)
                {
                    var start = Math.Max(minimum, SelectionEntryMinimum(list, data.Level));
                    // E4: a script-changed list's possible insertions also cut off lower entries.
                    var end = highestOnly ? Math.Min(maximum, (list.Entries ?? []).Where(e => e.Data is { } d && d.Level > data.Level)
                        .Select(e => (int)e.Data!.Level - 1).Concat(catalog.ScriptReceiverLevels.GetValueOrDefault(key, [])
                            .Where(l => l > data.Level).Select(l => (int)l - 1)).DefaultIfEmpty(65535).Min()) : maximum;
                    Visit(data.Reference.FormKey, next, depth + 1, start, end);
                }
        }
        Visit(payload, [], 0, 1, 65535);
        if (problem is null && paths.Count == 0) problem = "payload does not reach the source";
        // Mode 8 proves runtime bags separately. Ordinary quest insertion has no
        // such premise and must not treat mutable payload metadata as static.
        if (problem is null && requireStaticPayload && paths.SelectMany(path => path.Lists).Any(catalog.RuntimeControlledLists.ContainsKey))
            problem = "ordinary script payload path is runtime-mutated or unverifiable";
        if (problem is not null) return ([], [], problem);
        MaterialAvailabilityPath? shared = null;
        foreach (var path in paths)
        {
            var controls = new List<MaterialAvailabilityControl>();
            foreach (var list in path.Lists.Distinct().Order().Select(key => graph.Lists[key]))
                if (!list.Global.IsNull) controls.Add(new(MaterialAvailabilityKind.Global, list.FormKey, list.Global.FormKey, 0));
                else if (list.ChanceNone.Value > 0) controls.Add(new(MaterialAvailabilityKind.FixedChance, list.FormKey, default, list.ChanceNone.Value));
            var availability = new MaterialAvailabilityPath(controls);
            if (shared is not null && shared.Identity != availability.Identity)
                return ([], [], "alternative payload paths have different availability controls; cannot reproduce them as one serial gate chain");
            shared ??= availability;
        }
        return (shared!.Controls, MergeBands(paths.Select(p => (p.Minimum, p.Maximum))), null);
    }

    // Member sites whose payload reaches the source, directly or through script-changed
    // lists (E4 bags) whose own insertions are gated members. Bounded like payload graphs.
    private static (ScriptGate Gate, ScriptSite Site)[][] ScriptSiteChains(FormKey source, ScriptGate[] gates, Catalog catalog)
    {
        using var timing = PhaseTiming.Measure("script.site-chains");
        var graph = SpecializedGraph(catalog);
        if (catalog.ScriptChains is not { } index || !ReferenceEquals(index.Gates, gates) || !ReferenceEquals(index.Graph, graph))
        {
            using var indexTiming = PhaseTiming.Measure("script.site-chain-index");
            catalog.ScriptChains = index = new ScriptChainIndex(gates, graph);
        }
        if (index.Chains.TryGetValue(source, out var cached)) return cached;
        using var searchTiming = PhaseTiming.Measure("script.site-chain-search");
        var result = new List<(ScriptGate, ScriptSite)[]>();
        void Extend(List<(ScriptGate Gate, ScriptSite Site)> chain)
        {
            var last = chain[^1].Site;
            if (MirrorPayloadContains(last.Mirror, source, catalog)) result.Add(chain.ToArray());
            if (chain.Count >= 8 || result.Count >= 512) return;
            var tree = MirrorPayloadTree(last.Mirror, catalog, out var problem);
            if (problem is not null) return;
            foreach (var next in tree.SelectMany(bag => index.ByReceiver[bag]))
                if (!chain.Any(c => c.Site.Receiver == next.Site.Receiver))
                { chain.Add(next); Extend(chain); chain.RemoveAt(chain.Count - 1); }
        }
        foreach (var member in index.Members) Extend([member]);
        return index.Chains[source] = result.ToArray();
    }

    private static void BuildScriptDistributionPlans(List<CandidateItem> candidates, Catalog catalog, ProfileSettings profile, PlanningAdditions<VariantPlan> plans)
    {
        var selected = candidates.Where(c => c.Selected && c.Eligible && c.SpecializedMode == SpecializedLootMode.ScriptDistribution)
            .OrderBy(c => c.FormKey, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) return;
        using var timing = PhaseTiming.Measure("plans.script-distribution");
        SetPatchedSelection(catalog, candidates);
        var graph = SpecializedGraph(catalog);
        var gates = ScriptDistributionGates(catalog);
        foreach (var warning in gates.SelectMany(g => g.Warnings).Distinct())
            if (!catalog.DistributionNotes.Contains(warning)) catalog.DistributionNotes.Add(warning);
        var protectedInputs = ScriptProtectedLists(catalog);
        var payloadControls = new Dictionary<(FormKey, FormKey), (MaterialAvailabilityControl[] Controls, List<(int Minimum, int Maximum)> Window, string? Problem)>();
        // A route's outcome (protected-input refusal, or original-gate result) depends on the
        // receiver's route set, the chain and the source's gate walk, not on the source itself
        // (see the ticket's input split). Sources sharing all three read one computation.
        var routeOutcomes = new Dictionary<(object Entry, MirrorSite Site, string Label, int Walk), (string? Refusal, MirrorGateResult? Gate, int Shape)[]>();
        // Material routes of one proof-free shape, across chains and sources; a source's
        // proof completes the shape (MaterialRouteComparer.WithoutProof).
        var shapeIds = new Dictionary<SpecializedRoute, int>(MaterialRouteComparer.WithoutProof);
        int ShapeId(SpecializedRoute route) => shapeIds.TryGetValue(route, out var id) ? id : shapeIds[route] = shapeIds.Count;
        foreach (var candidate in selected)
        {
            using var candidateTiming = PhaseTiming.Measure("plans.script-candidate");
            var key = FormKey.Factory(candidate.FormKey);
            IMajorRecordGetter source = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter)catalog.Armors[key];
            var refusals = new List<string>();
            // Material shapes in first-appearance order, members in route order: what
            // grouping these routes after attaching their proofs produced.
            var shapes = new List<(SpecializedRoute First, List<(EnchantedTemplate Template, ParentEntry Parent)> Entries)>();
            var shapeIndex = new Dictionary<(int Shape, int Proof), int>();
            var proofIds = new Dictionary<MaterialSourceProof, int>(ReferenceEqualityComparer.Instance);
            var proofIdentities = new Dictionary<string, int>(StringComparer.Ordinal);
            int ProofId(MaterialSourceProof proof)
            {
                if (proofIds.TryGetValue(proof, out var id)) return id;
                if (!proofIdentities.TryGetValue(proof.Identity, out id)) proofIdentities[proof.Identity] = id = proofIdentities.Count;
                return proofIds[proof] = id;
            }
            var directRoutes = new List<SpecializedRoute>();
            var sites = ScriptSiteChains(key, gates, catalog);
            foreach (var chain in sites)
            {
                var (gate, site) = chain[0];
                var label = string.Join(" > ", chain.Select(c => $"{c.Site.Receiver}/{c.Site.Payload}"));
                if (chain.Select(c => MirrorShapeProblem(c.Site.Mirror, catalog)).FirstOrDefault(p => p is not null) is { } shape) { refusals.Add($"{label}: {shape}"); continue; }
                if (chain.FirstOrDefault(c => c.Site.Count != 1).Site is { } counted) { refusals.Add($"{label}: insertion count {counted.Count} is not supported"); continue; }
                // A highest-eligible receiver (or bag) picks an insertion only below the next
                // higher level any entry can have. Absent higher entries only widen the real window.
                // SpecialLoot receivers follow the same ordinary rules (KnownSelectionFlags).
                var receiverWindow = new List<(int Minimum, int Maximum)> { (1, 65535) };
                string? windowProblem = null;
                foreach (var (_, member) in chain)
                {
                    var band = (Minimum: (int)SelectionEntryMinimum(graph.Lists[member.Receiver], member.Level), Maximum: 65535);
                    if (HasHighestEligibleCutoff(graph.Lists[member.Receiver]))
                    {
                        if (catalog.ScriptUnresolvedReceivers.Contains(member.Receiver)) { windowProblem = "highest-eligible receiver has an insertion with unknown level"; break; }
                        var higher = (graph.Lists[member.Receiver].Entries ?? []).Where(e => e.Data is not null).Select(e => (int)unchecked((ushort)e.Data!.Level))
                            .Concat(catalog.ScriptReceiverLevels.GetValueOrDefault(member.Receiver, []).Select(l => (int)l)).Where(l => l > member.Level);
                        band.Maximum = Math.Min(65535, higher.DefaultIfEmpty(65536).Min() - 1);
                    }
                    receiverWindow = IntersectBands(receiverWindow, [band]);
                }
                if (windowProblem is not null) { refusals.Add($"{label}: {windowProblem}"); continue; }
                // Controls on the static path to each bag, the bag's own control, then the
                // last payload's path to the source. Each bag entry adds its own item gate.
                var payloadPath = new List<MaterialAvailabilityControl>(); var payloadWindow = new List<(int Minimum, int Maximum)> { (1, 65535) };
                string? payloadProblem = null;
                for (var k = 0; k < chain.Length && payloadProblem is null; k++)
                {
                    var target = k + 1 < chain.Length ? chain[k + 1].Site.Receiver : key;
                    if (!payloadControls.TryGetValue((chain[k].Site.Payload, target), out var segment))
                        payloadControls[(chain[k].Site.Payload, target)] = segment = PayloadPathControls(chain[k].Site.Payload, target, catalog);
                    payloadProblem = segment.Problem;
                    payloadPath.AddRange(segment.Controls); payloadWindow = IntersectBands(payloadWindow, segment.Window);
                    if (k + 1 < chain.Length && graph.Lists[target] is var bag)
                        if (!bag.Global.IsNull) payloadPath.Add(new(MaterialAvailabilityKind.Global, target, bag.Global.FormKey, 0));
                        else if (bag.ChanceNone.Value > 0) payloadPath.Add(new(MaterialAvailabilityKind.FixedChance, target, default, bag.ChanceNone.Value));
                }
                if (payloadProblem is not null) { refusals.Add($"{label}: {payloadProblem}"); continue; }
                // Receiver-side gates and destination windows depend only on the outer
                // receiver; the gate witness walks the last payload's static paths.
                var mirrorSite = chain.Length == 1 ? site.Mirror : new MirrorSite(site.Receiver, chain[^1].Site.Payload, site.Level, 1, site.Evidence);
                var uses = graph.Endpoints(site.Receiver).Where(u => u.Known).Distinct().ToArray();
                var assessment = AssessSpecializedRoutes(key, catalog, profile.AllowOtherMaterialRoutes, site.Receiver);
                refusals.AddRange(assessment.Evidence.Where(e => e.StartsWith("Recipient ", StringComparison.Ordinal) && e.Contains(" withheld:"))
                    .Select(e => $"{label}: {e}"));
                if (assessment.Routes.Count == 0) refusals.Add($"{label}: {string.Join(" ", assessment.Evidence)}");
                // Template variants often share a gate result. This chain owns
                // fixed payload/receiver windows, placeholders and recipient uses,
                // so their proof value can be prepared once without caching policy
                // across sources, chains or planning passes.
                var prepared = new Dictionary<MirrorGateResult, (MaterialSourceProof? Proof, string? Problem)>(ReferenceEqualityComparer.Instance);
                (MaterialSourceProof? Proof, string? Problem) Prepare(MirrorGateResult result)
                {
                    if (prepared.TryGetValue(result, out var prior)) return prior;
                    using var proofTiming = PhaseTiming.Measure("plans.script-route-proof");
                    var value = BuildProof(result.Copy);
                    prepared.Add(result, value);
                    return value;
                }
                (MaterialSourceProof? Proof, string? Problem) BuildProof(MirrorGateCopy copy)
                {
                    var window = IntersectBands(IntersectBands(copy.Window.Length == 0 ? [(1, 65535)] : copy.Window.ToList(), payloadWindow), receiverWindow);
                    window = IntersectBands(window, (copy.DestinationWindow ?? []).ToList());
                    if (window.Count == 0) return (null, "no common actor-level window");
                    if (window is not [(var minimum, var maximum)])
                        return (null, $"actor-level window with gaps {string.Join(',', window.Select(w => $"{w.Minimum}-{w.Maximum}"))} is not supported");
                    // Nested gates are a logical AND: the bag is in the receiver and the entry in the bag.
                    var controls = chain.Select(c => new MaterialAvailabilityControl(MaterialAvailabilityKind.Global, c.Site.Receiver, c.Gate.Placeholder, 0)).ToList();
                    controls.AddRange(copy.Controls.Select(c => !c.Global.IsNull
                        ? new MaterialAvailabilityControl(MaterialAvailabilityKind.Global, c.List, c.Global, 0)
                        : new MaterialAvailabilityControl(MaterialAvailabilityKind.FixedChance, c.List, default, c.ChanceNone)));
                    controls.AddRange(payloadPath);
                    if (maximum < 65535) controls.Add(new(MaterialAvailabilityKind.ActorLevelWindow, site.Receiver, default, 0, maximum));
                    return (new MaterialSourceProof(new MaterialAvailabilityPath(controls.DistinctBy(c => c.Identity)), null, (short)minimum, uses), null);
                }
                var candidateRoutes = assessment.Routes.Where(r => r.MerchantIsolation is null && r.CountedMerchantSource is null).ToArray();
                (string? Refusal, MirrorGateResult? Gate, int Shape) Outcome(SpecializedRoute route)
                {
                    using var routeTiming = PhaseTiming.Count("plans.script-route");
                    var routeLists = (route.MaterialPath ?? []).Concat(route.IsolationPath ?? []).Append(route.Destination.List);
                    if (routeLists.Any(protectedInputs.Contains)) return ($"{site.Receiver}: counterpart route touches a list the script changes", null, -1);
                    var gateResult = MirrorOriginalGate(mirrorSite, key, route, catalog);
                    if (gateResult.Problem is { } gateProblem) return ($"{label}: {gateProblem}", null, -1);
                    return (null, gateResult, route.MaterialPath is { Count: > 1 } ? ShapeId(route) : -1);
                }
                var outcomeKey = assessment.MirrorEntry is { } entry ? (entry, mirrorSite, label, MirrorSourcePaths(mirrorSite, key, graph).Id) : default;
                if (assessment.MirrorEntry is null || !routeOutcomes.TryGetValue(outcomeKey, out var outcomes))
                {
                    outcomes = candidateRoutes.Select(Outcome).ToArray();
                    if (assessment.MirrorEntry is not null) routeOutcomes.Add(outcomeKey, outcomes);
                }
                for (var index = 0; index < candidateRoutes.Length; index++)
                {
                    var route = candidateRoutes[index];
                    var (refused, gateResult, routeShape) = outcomes[index];
                    if (refused is not null) { refusals.Add(refused); continue; }
                    var (proof, problem) = Prepare(gateResult!);
                    if (problem is not null) { refusals.Add($"{label}: {problem}"); continue; }
                    if (routeShape < 0) { directRoutes.Add(route with { SourceProof = proof }); continue; }
                    if (!shapeIndex.TryGetValue((routeShape, ProofId(proof!)), out var member))
                    {
                        member = shapes.Count; shapeIndex.Add((routeShape, ProofId(proof!)), member);
                        shapes.Add((route with { SourceProof = proof }, []));
                    }
                    shapes[member].Entries.Add((route.Template, route.Destination));
                }
            }
            var before = plans.Count;
            if (shapes.Count > 0)
                MaterializeSpecializedExpansion(candidate, catalog, profile, plans,
                    shapes.Select(s => new MaterialRouteShape(s.First, s.Entries.ToArray())).ToArray(), observedSource: true);
            var direct = AttachSpecializedAvailabilityRoutes(candidate, source, catalog, profile, plans, directRoutes, observedSource: true);
            var planned = plans.Skip(before).Count(p => p.Source.FormKey == key);
            candidate.Reason = sites.Length == 0
                ? "Follow script distribution: no accepted script insertion block hands out this item"
                : planned > 0 || direct > 0
                    ? $"Follow script distribution: {sites.Length} insertion(s); counterparts gated by the script's current result"
                    : "Follow script distribution: no counterpart route under the source's gates and consumers";
            catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: {candidate.Reason}." + (refusals.Count > 0 ? " Refused: " + string.Join(" | ", refusals.Distinct().Take(8)) : ""));
            foreach (var refusal in refusals.Distinct()) catalog.ScriptGateRefusals.Add($"{key}: {refusal}");
        }
    }

    // Same-material counterparts for script-gated or statically proven sources: one gated
    // family list per destination entry, so the existing peer branch keeps its shape and the
    // counterpart carries the source's controls. New attachments have independent source
    // proofs: covering a destination under one block's gate does not cover it under another's.
    private sealed partial class Catalog
    {
        public Dictionary<string, FormKey> AvailabilityFamilies { get; } = new(StringComparer.Ordinal);
        public HashSet<(FormKey Family, FormKey Target, short Level, short Count)> AttachedAvailabilityFamilies { get; } = [];
    }

    // A bundle slot sits under the bundle's own gate and under every gate all of the recipient's paths to the written bundle
    // cross; its counterpart already inherits those, and a source path through the same lists carries them too. Such a
    // control is common to both and must not be rolled a second time by the attachment (UseAll contracts C2 and C4).
    //  1. Only a control its owning list itself carries (its own Global, or its own Chance None) can be shared. A script
    //     placeholder, a payload-path control or any other synthetic control is never shared.
    //  2. A shareable control is shared when its list is the destination bundle, or lies on, or dominates, the position the
    //     counterpart is written at: every path from the recipient consumers reaches it through that list. A private copy
    //     is written under the top of its isolation chain, so the walk starts there, not at the original bundle.
    //  3. A shareable control on an ancestor of the bundle that is not proved shared would be rolled twice inside the slot,
    //     so the slot route is refused, unless the source is reachable from that list without crossing the bundle (the
    //     list gates the source on its own different path). Controls of lists that are not ancestors of the bundle stay
    //     source gates and keep their wrapper. Returns null when the route is refused.
    private static SpecializedRoute? WithoutSharedSlotGates(SpecializedRoute route, FormKey source, Catalog catalog, bool observedSource)
    {
        if (route.Slot.IsNull || route.SourceProof is not { } proof) return route;
        var graph = SpecializedGraph(catalog);
        var bundle = route.Destination.List;
        var position = route.IsolationPath is { Count: > 1 } chain ? chain[^1] : bundle;
        HashSet<FormKey>? recipients = null;
        HashSet<FormKey> Recipients() => recipients ??= route.RecipientIsolation is { } edge ? [edge.Consumer]
            : graph.Endpoints(position).Select(u => u.Consumer).ToHashSet();
        bool Carries(MaterialAvailabilityControl c) => graph.Lists.TryGetValue(c.List, out var owner)
            && (c.Kind == MaterialAvailabilityKind.Global ? !owner.Global.IsNull && owner.Global.FormKey == c.Global
                : c.Kind == MaterialAvailabilityKind.FixedChance && owner.Global.IsNull && owner.ChanceNone.Value != 0 && owner.ChanceNone.Value == c.Chance);
        bool Dominates(FormKey control)
        {
            var seen = new HashSet<FormKey>(); var todo = new Stack<FormKey>(); todo.Push(position);
            while (todo.TryPop(out var key))
            {
                if (key == control || !seen.Add(key)) continue;
                if (graph.Uses.GetValueOrDefault(key, []).Any(u => Recipients().Contains(u.Consumer))) return false;
                foreach (var parent in graph.Parents.GetValueOrDefault(key, [])) todo.Push(parent.List);
            }
            return Recipients().Count > 0;
        }
        bool SourceBeside(FormKey control)
        {
            var seen = new HashSet<FormKey>(); var todo = new Stack<FormKey>(); todo.Push(control);
            while (todo.TryPop(out var key))
            {
                if (key == source) return true;
                if (key == bundle || !seen.Add(key) || !graph.Lists.TryGetValue(key, out var list)) continue;
                foreach (var entry in LeveledEntryRows(list)) todo.Push(entry.Reference);
            }
            return false;
        }
        var kept = new List<MaterialAvailabilityControl>();
        foreach (var c in proof.Availability.Controls)
        {
            if (!Carries(c) || c.List != bundle && !graph.Up(bundle).Contains(c.List)) { kept.Add(c); continue; }
            if (c.List == bundle || route.IsolationPath?.Contains(c.List) == true || Dominates(c.List)) continue;
            if (SourceBeside(c.List)) { kept.Add(c); continue; }
            var reason = $"gate on {graph.Lists[c.List].EditorID} [{c.List}] above the bundle is shared only on some recipient paths; the slot would roll it twice";
            catalog.DistributionNotes.Add($"{source}: bundle slot {route.Slot} of {graph.Lists[bundle].EditorID} [{bundle}] refused: {reason}.");
            if (observedSource) catalog.ScriptGateRefusals.Add($"{source}: bundle slot of {bundle}: {reason}");
            return null;
        }
        if (kept.Count == proof.Availability.Controls.Length) return route;
        return route with { SourceProof = new MaterialSourceProof(new MaterialAvailabilityPath(kept, proof.Availability.Structure), proof.Unlock, proof.Level, proof.Uses) };
    }

    private static int AttachSpecializedAvailabilityRoutes(CandidateItem candidate, IMajorRecordGetter source, Catalog catalog, ProfileSettings profile,
        PlanningAdditions<VariantPlan> plans, List<SpecializedRoute> routes, bool observedSource)
    {
        using var timing = PhaseTiming.Measure("plans.availability-routes");
        var key = source.FormKey; var attached = 0;
        if (routes.Count == 0) return 0;
        var budget = DirectPeerBudget.ForPlanning(catalog);
        var inventory = catalog.SourceEquipment!.GetValueOrDefault(key, []);
        int Rank(SpecializedRoute r) { var i = profile.EnchantmentProviderPlugins.FindIndex(p => p.Equals(r.Template.Enchantment.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }
        foreach (var group in routes.GroupBy(r => VariantIdentity(r.Template.Item, r.Template.Enchantment, catalog)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(Rank).ThenBy(r => r.Template.Item.FormKey).Select(r => WithoutSharedSlotGates(r, key, catalog, observedSource)).OfType<SpecializedRoute>().ToArray();
            if (ordered.Length == 0) continue;
            var existing = inventory.Where(i => OrdinaryEquipment(i, catalog.KeywordNames) && TryEnchantment(i, out var e) && VariantIdentity(i, e, catalog) == group.Key).ToArray();
            var occupied = existing.SelectMany(i => CdfCoveredAncestors(i.FormKey, PlannedLootAncestors(i.FormKey, catalog), catalog)).Select(k => (k, "")).ToHashSet();
            var authoredOccupied = occupied.ToHashSet();
            if (ordered.Any(r => r.RecipientIsolation is not null))
                authoredOccupied.UnionWith(CdfLootRecipientCoverage(existing.Select(i => i.FormKey), catalog));
            if (!observedSource)
                occupied.UnionWith(plans.Where(p => p.Source.FormKey == key && VariantIdentity(p.Template.Item, p.Template.Enchantment, catalog) == group.Key)
                    .SelectMany(p => p.Parents).SelectMany(p => PlannedLootAncestors(p.List, catalog)).Select(k => (k, "")));
            if (ordered.Any(r => r.RecipientIsolation is not null))
                occupied.UnionWith(CdfLootRecipientCoverage(existing.Select(i => i.FormKey).Concat(!observedSource
                    ? plans.Where(p => p.Source.FormKey == key && VariantIdentity(p.Template.Item, p.Template.Enchantment, catalog) == group.Key)
                        .SelectMany(p => p.Parents).Select(p => p.List) : []), catalog));
            var parents = new List<ParentEntry>();
            var parentTemplates = new Dictionary<FormKey, EnchantedTemplate>();
            // Static routes only: loot ancestors already covered under each control set, keyed by
            // MaterialAvailabilityPath.Identity. Like script gates, source conditions are independent:
            // a boundary covered under one set of controls is not covered under a different set.
            // `occupied` above holds what is already reachable regardless of controls.
            var coveredByControls = new Dictionary<string, HashSet<(FormKey, string)>>();
            // Within one control set, the lowest source minimum goes first so a higher-minimum
            // proof reaching the same boundary cannot replace it (OrderBy is stable).
            foreach (var route in ordered.OrderBy(r => r.SourceProof!.Level)
                .DistinctBy(r => (r.Destination, Isolation: SpecializedIsolationIdentity(r), r.SourceProof!.Identity)))
            {
                if (!CdfRouteAvailable(key, route, catalog)) continue;
                var boundary = route.IsolationPath is { Count: > 1 } isolation ? isolation[^1] : route.Destination.List;
                // PlannedLootAncestors captures the current graph before any
                // attachment below. Project that snapshot without a second set.
                IEnumerable<(FormKey, string)> boundaryAncestors = route.RecipientIsolation is { } recipient
                    ? [(recipient.Consumer, recipient.Role)] : PlannedLootAncestors(boundary, catalog).Select(k => (k, ""));
                if (occupied.Overlaps(boundaryAncestors))
                {
                    if (!observedSource && authoredOccupied.Overlaps(boundaryAncestors)) catalog.AuthoredCounterpartCoverage.Add(key);
                    continue;
                }
                var controlsKey = route.SourceProof!.Availability.Identity;
                if (!observedSource && coveredByControls.TryGetValue(controlsKey, out var alreadyCovered) && alreadyCovered.Overlaps(boundaryAncestors)) continue;
                // A fresh copy inherits this prototype's effective additions. Check
                // before allocating isolation or reserving this proof's coverage.
                // A bundle slot's attachment joins its generated slot choice, which no script reverts.
                var slots = route.SourceProof!.Unlock is not null && route.Slot.IsNull
                    && catalog.VerifiedRevertLists.ContainsKey(SpecializedFlatTarget(route, catalog)) ? 2 : 1;
                if (!SpecializedCanAdd(route, budget, slots, catalog, out var projectedTarget, out var positions))
                {
                    catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: controlled specialized terminal {projectedTarget} has no remaining flat-entry capacity.");
                    if (observedSource) catalog.ScriptGateRefusals.Add($"{key}: {projectedTarget}: counterpart wrapper exceeds the terminal's flat-entry capacity reservation");
                    continue;
                }
                // A bundle slot's family is attached inside its slot choice, never as a bundle entry.
                var target = SpecializedPlacementTarget(route, candidate.Category, catalog);
                // As for ordinary source pools: one family per original destination, attached to
                // each (possibly private) target. On a calculate-from-all-levels target the source
                // minimum sits on the attachment edge, so proofs differing only in minimum share the
                // family; the window max(edge, entry) is unchanged. Highest-eligible targets keep the
                // minimum on the variant entry, where raising an edge would change siblings.
                var levelOnEdge = ((int)catalog.Lists[target].Flags & 1) != 0;
                var edgeLevel = levelOnEdge ? Math.Max(route.Destination.Level, route.SourceProof!.Level) : route.Destination.Level;
                var familyIdentity = $"{(observedSource ? "script-family" : "availability-family")}|{key}|{route.Destination.List}|{route.Destination.Level}|"
                    + (route.Slot.IsNull ? "" : $"slot:{route.Slot}|")
                    + $"{(levelOnEdge ? "minimum-on-edge|" + route.SourceProof!.ControlIdentity : route.SourceProof!.Identity)}|{group.Key}";
                if (!catalog.AvailabilityFamilies.TryGetValue(familyIdentity, out var family) || !catalog.Fallbacks.ContainsKey(family))
                {
                    family = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                    var list = new LeveledItem(family, SkyrimRelease.SkyrimSE)
                    { EditorID = $"Variants_{DisplayName(source)}_{(observedSource ? "script" : "availability")}", Flags = (LeveledItem.Flag)3, ChanceNone = new Percent(0), Entries = new() };
                    catalog.AddList(family, list);
                    catalog.Fallbacks.Add(family, new(familyIdentity, candidate.Category, list));
                    catalog.AvailabilityFamilies[familyIdentity] = family;
                }
                if (catalog.AttachedAvailabilityFamilies.Add((family, target, edgeLevel, route.Destination.Count)))
                {
                    AttachOtherMaterialBranch(key, new(new(target, edgeLevel, route.Destination.Count), family, route.SourceProof.Availability, route.SourceProof.Unlock), catalog, share: true);
                    if (route.SourceProof.Unlock is null) budget.Commit(target);
                }
                parents.Add(new(family, levelOnEdge ? (short)1 : Math.Max((short)1, route.SourceProof.Level), 1));
                parentTemplates.TryAdd(family, route.Template);
                if (!observedSource)
                {
                    if (!coveredByControls.TryGetValue(controlsKey, out var covered)) coveredByControls[controlsKey] = covered = [];
                    covered.UnionWith(boundaryAncestors);
                }
            }
            if (parents.Count == 0) continue;
            plans.Add(new(candidate, source, ordered[0].Template, parents.Distinct().ToArray(), existing.FirstOrDefault(),
                observedSource ? "Follow script distribution: same-material counterpart behind the source script's block gate and original controls"
                    : "Evidence-supported same-material counterpart behind the source's ordered availability controls", ParentTemplates: parentTemplates));
            attached++;
        }
        return attached;
    }

    private static string DescribeScriptGate(ScriptGate gate, FormKey global, Catalog catalog)
    {
        var members = gate.Members.Select(m =>
        {
            var payload = catalog.Lists.TryGetValue(m.Payload, out var list)
                ? $"{m.Payload} (raw flags {(int)list.Flags})" : $"{m.Payload} (direct equipment)";
            return $"{payload} in {m.Receiver} (raw flags {(int)catalog.Lists[m.Receiver].Flags}), level {m.Level}, count {m.Count}";
        });
        return $"{gate.Block}: {global} open while {gate.Representative.Payload} is in {gate.Representative.Receiver}; {gate.Members.Length} insertion(s); completion event(s) {string.Join(", ", gate.CompletionEvents)}; refresh at initialization and load; conditional availability, live gate state is not evaluated offline; insertions: {string.Join(" | ", members)}"
            + (gate.Warnings.Length == 0 ? "" : "; warnings: " + string.Join(" | ", gate.Warnings));
    }

    // Owned condition Globals actually referenced by planned lists, in stable order.
    private static ScriptGate[] UsedScriptGates(Catalog catalog)
    {
        if (catalog.ScriptGates is not { Length: > 0 } gates) return [];
        var used = catalog.Fallbacks.Values.Select(f => f.Original.Global.FormKey).Where(k => k.ModKey == ScriptGatePlanningMod).ToHashSet();
        return gates.Where(g => used.Contains(g.Placeholder)).ToArray();
    }
    private static int ScriptGateRecordCount(Catalog catalog) { var used = UsedScriptGates(catalog).Length; return used == 0 ? 0 : used + 4; }

    private static void WriteScriptGates(Catalog catalog, SkyrimMod[] patches, HashSet<uint>[] usedIds, HashSet<string> editorIds,
        IReadOnlyDictionary<FormKey, FormKey> globals)
    {
        var gates = UsedScriptGates(catalog);
        if (gates.Length == 0) return;
        var index = patches.Length - 1;
        if (usedIds[index].Count + 4 > OutputLayout.For(patches[index]).IdCount) throw new InvalidDataException("Script distribution controls exceed the final output plugin's record capacity.");
        FormList Collection(string name, IEnumerable<FormKey> items)
        {
            var list = new FormList(StableKey(patches[index], "script-gate-v1|" + name, usedIds[index]), SkyrimRelease.SkyrimSE)
            { EditorID = EditorId("ScriptGate" + name, editorIds) };
            foreach (var item in items) list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(item));
            patches[index].FormLists.Add(list);
            return list;
        }
        var receivers = Collection("Receivers", gates.Select(g => g.Representative.Receiver));
        var payloads = Collection("Payloads", gates.Select(g => g.Representative.Payload));
        var globalList = Collection("Globals", gates.Select(g => globals[g.Placeholder]));
        var quest = new Quest(StableKey(patches[index], "script-gate-v1|quest", usedIds[index]), SkyrimRelease.SkyrimSE)
        { EditorID = EditorId("ScriptGateController", editorIds), Flags = Quest.Flag.StartGameEnabled, VirtualMachineAdapter = new QuestAdapter() };
        var script = new ScriptEntry { Name = "XEP_ScriptGate" };
        void Link(string name, FormKey value) => script.Properties.Add(new ScriptObjectProperty { Name = name, Flags = ScriptProperty.Flag.Edited,
            Object = new FormLink<ISkyrimMajorRecordGetter>(value) });
        Link("Receivers", receivers.FormKey); Link("Payloads", payloads.FormKey); Link("Gates", globalList.FormKey);
        script.Properties.Add(new ScriptIntProperty { Name = "ChecksPerUpdate", Flags = ScriptProperty.Flag.Edited, Data = 16 });
        script.Properties.Add(new ScriptStringListProperty { Name = "CompletionEvents", Flags = ScriptProperty.Flag.Edited,
            Data = new(gates.SelectMany(g => g.CompletionEvents).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) });
        quest.VirtualMachineAdapter.Scripts.Add(script);
        var playerAlias = new QuestAlias { ID = 0, Name = "ScriptGatePlayer", Type = QuestAlias.TypeEnum.Reference };
        playerAlias.ForcedReference.SetTo(PlayerReference);
        quest.Aliases.Add(playerAlias);
        quest.VirtualMachineAdapter.Aliases.Add(new QuestFragmentAlias { Property = new ScriptObjectProperty {
            Object = new FormLink<ISkyrimMajorRecordGetter>(quest.FormKey), Alias = 0 },
            Scripts = new() { new ScriptEntry { Name = "XEP_ScriptGatePlayerAlias" } } });
        patches[index].Quests.Add(quest);
    }
}
