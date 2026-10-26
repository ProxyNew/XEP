using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record MaterialChance(FormKey List, double Value);
    // ActorLevelWindow: the source is selectable only up to Maximum (inclusive).
    private enum MaterialAvailabilityKind { Global, FixedChance, ActorLevelWindow }
    private sealed record MaterialAvailabilityControl(MaterialAvailabilityKind Kind, FormKey List, FormKey Global, double Chance, int Maximum = 0)
    {
        // Formatted once per instance; a table rather than a field keeps record equality unchanged.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MaterialAvailabilityControl, string> IdentityTexts = new();
        public string Identity => IdentityTexts.GetValue(this, static c => c.Kind switch
        {
            MaterialAvailabilityKind.Global => $"global:{c.List}:{c.Global}",
            MaterialAvailabilityKind.ActorLevelWindow => $"window:{c.List}:{c.Maximum}",
            _ => $"chance:{c.List}:{c.Chance.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        });
        public string Describe(SpecializedEvidence graph) => Kind switch
        {
            MaterialAvailabilityKind.Global => $"global {Global} imposed by {graph.Lists[List].EditorID} [{List}]",
            MaterialAvailabilityKind.ActorLevelWindow => $"actor level at most {Maximum} imposed by {graph.Lists[List].EditorID} [{List}]",
            _ => $"fixed Chance None {Chance:P0} imposed by {graph.Lists[List].EditorID} [{List}]",
        };
    }
    private sealed record MaterialAvailabilityPath
    {
        public MaterialAvailabilityControl[] Controls { get; }
        // Only the exact merchant-source planner may opt into this representation.
        // Controls remain provenance; the envelope materializes their rolls and
        // cardinality together, so a caller must not apply scalar wrappers too.
        public CountedSourceEnvelope? Structure { get; }
        public string Identity { get; }
        public bool IsCompound => Controls.Length > 1;
        public int MaximumLevel => Controls.Where(c => c.Kind == MaterialAvailabilityKind.ActorLevelWindow)
            .Select(c => c.Maximum).DefaultIfEmpty(ushort.MaxValue).Min();

        public MaterialAvailabilityPath(IEnumerable<MaterialAvailabilityControl>? controls = null)
            : this(controls, null)
        { }

        public MaterialAvailabilityPath(IEnumerable<MaterialAvailabilityControl>? controls, CountedSourceEnvelope? structure)
        {
            Controls = controls?.ToArray() ?? [];
            Structure = structure;
            var controlsIdentity = string.Join('>', Controls.Select(c => c.Identity));
            Identity = structure is null ? controlsIdentity : controlsIdentity + "|structure:" + structure.Identity;
        }

        public MaterialAvailabilityPath Add(MaterialAvailabilityControl control) => new(Controls.Append(control), Structure);
    }
    private sealed record MaterialSourceProof
    {
        public MaterialAvailabilityPath Availability { get; }
        public FormKey Global { get; }
        public ScriptUnlock? Unlock { get; }
        public short Level { get; }
        public TypedLootUse[] Uses { get; }
        public MaterialChance? Chance { get; }
        public string Identity { get; }
        // Identity without the source minimum level: the same controls and quest unlock.
        public string ControlIdentity { get; }

        public MaterialSourceProof(FormKey global, ScriptUnlock? unlock, short level, TypedLootUse[] uses, MaterialChance? chance = null)
            : this(LegacyPath(global, chance), unlock, level, uses, chance, legacyIdentity: true)
        { }

        public MaterialSourceProof(MaterialAvailabilityPath availability, ScriptUnlock? unlock, short level, TypedLootUse[] uses)
            : this(availability, unlock, level, uses, null, legacyIdentity: false)
        { }

        private MaterialSourceProof(MaterialAvailabilityPath availability, ScriptUnlock? unlock, short level, TypedLootUse[] uses,
            MaterialChance? legacyChance, bool legacyIdentity)
        {
            Availability = availability; Unlock = unlock; Level = level; Uses = uses;
            var globals = availability.Controls.Where(c => c.Kind == MaterialAvailabilityKind.Global).ToArray();
            var chances = availability.Controls.Where(c => c.Kind == MaterialAvailabilityKind.FixedChance).ToArray();
            Global = globals.Length == 1 ? globals[0].Global : default;
            Chance = chances.Length == 1 ? new(chances[0].List, chances[0].Chance) : null;
            // Keep the textual format for ordering and pool identities, but allocate
            // it once. Get-only properties prevent a with-update from making it stale.
            Identity = legacyIdentity
                ? $"{Global}|{Unlock?.Quest}|{Unlock?.Stage}|{Unlock?.AtStart}|{Unlock?.OriginalTarget}|{Unlock?.Payload}|{Level}|{legacyChance?.List}|{legacyChance?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : $"{availability.Identity}|quest:{Unlock?.Quest}:{Unlock?.Stage}:{Unlock?.AtStart}:{Unlock?.OriginalTarget}:{Unlock?.Payload}|level:{Level}";
            // A predicate quest can differ from the script owner whose Revert
            // lifecycle is proven. Preserve old identities when owner is unset.
            ControlIdentity = legacyIdentity
                ? $"{Global}|{Unlock?.Quest}|{Unlock?.Stage}|{Unlock?.AtStart}|{Unlock?.OriginalTarget}|{Unlock?.Payload}|{legacyChance?.List}|{legacyChance?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : $"{availability.Identity}|quest:{Unlock?.Quest}:{Unlock?.Stage}:{Unlock?.AtStart}:{Unlock?.OriginalTarget}:{Unlock?.Payload}";
            if (unlock is not null && !unlock.OwnerQuest.IsNull) { Identity += $"|owner:{unlock.OwnerQuest}"; ControlIdentity += $"|owner:{unlock.OwnerQuest}"; }
        }

        private static MaterialAvailabilityPath LegacyPath(FormKey global, MaterialChance? chance)
        {
            var controls = new List<MaterialAvailabilityControl>();
            if (!global.IsNull) controls.Add(new(MaterialAvailabilityKind.Global, default, global, 0));
            if (chance is not null) controls.Add(new(MaterialAvailabilityKind.FixedChance, chance.List, default, chance.Value));
            return new(controls);
        }
    }

    // Only the source paths supporting a consumer can authorize expansion there.
    // Unsupported side branches must not veto an independently proven path.
    private static List<MaterialSourceProof> MaterialSourceProofs(FormKey source, Catalog catalog, ICollection<string> evidence) =>
        MaterialSourceProofsCore(source, catalog, evidence);

    // Explicit relocation can carry static upper bounds through the existing availability wrapper.
    // Other callers retain their structural-replication policy; ordinary runtime bounds stay refused.
    private static List<MaterialSourceProof> RelocatedSourceProofs(FormKey source, Catalog catalog, ICollection<string> evidence) =>
        MaterialSourceProofsCore(source, catalog, evidence, preserveStaticWindows: true);

    private const string SourceSearchCapNotice = "Source availability search exceeded 8192 states; no partial source proof is used.";

    private static List<MaterialSourceProof> MaterialSourceProofsCore(FormKey source, Catalog catalog, ICollection<string> evidence,
        FormKey requiredAncestor = default, ParentEntry? approvedPlacement = null, bool replicatedStart = false, bool preserveStaticWindows = false)
    {
        var graph = SpecializedGraph(catalog);
        var found = new Dictionary<(string Availability, ScriptUnlock? Unlock, short Level), (MaterialAvailabilityPath Path, HashSet<TypedLootUse> Uses)>();
        var todo = new Queue<(FormKey Key, MaterialAvailabilityPath Availability, ScriptUnlock? Unlock, short Level, FormKey Payload, bool PassedAncestor)>();
        var unlocks = catalog.ScriptUnlocks.GetValueOrDefault(source, [])
            .GroupBy(u => (u.Quest, u.Stage, u.AtStart, u.OriginalTarget, u.Count, u.Payload))
            .Select(g => g.OrderByDescending(u => u.Level).First()).ToArray();
        // A verified insertion elsewhere does not replace ordinary distribution.
        // This seed follows only static parents; script edges are traversed by
        // the separate payload walks below, with their own controls and minima.
        todo.Enqueue((source, new(), null, 1, default, false));
        foreach (var unlock in unlocks)
        {
            if (unlock.Count != 1) { evidence.Add("Source route withheld: quest payload count requires selector replication."); continue; }
            // Prove the contents of an inserted payload before crossing its script
            // edge. Starting at the insertion target would lose payload globals.
            todo.Enqueue((source, new(), unlock, unlock.Level, unlock.Payload.IsNull ? source : unlock.Payload, false));
        }
        var seen = new HashSet<(FormKey, string, ScriptUnlock?, short, FormKey, bool)>();
        while (todo.TryDequeue(out var route))
        {
            var (key, availability, unlock, level, payload, passedAncestor) = route;
            passedAncestor |= requiredAncestor.IsNull || key == requiredAncestor;
            if (!seen.Add((key, availability.Identity, unlock, level, payload, passedAncestor))) continue;
            if (seen.Count > 8192)
            { evidence.Add(SourceSearchCapNotice); return []; }
            if (graph.Lists.TryGetValue(key, out var list))
            {
                // A fully validated replicated selector retains this node's
                // structure/controls itself. The proof still follows its authored
                // ancestors to the actual recipient and keeps their requirements.
                var copiedStart = replicatedStart && key == source;
                string? reason = list.IsDeleted ? "deleted list"
                    : catalog.RuntimeControlledLists.ContainsKey(key) ? "runtime-mutated or unverifiable list"
                    : list.Global.IsNull && list.ChanceNone.Value == 1 ? "fixed Chance None 100% supplies no availability on this path"
                    : !copiedStart && !KnownSelectionFlags(list) ? "unsupported selection flags require structural replication"
                    : graph.HasAncestorCycle(key) ? "cyclic source ancestry"
                    : null;
                if (reason is not null)
                {
                    var encountered = !list.Global.IsNull
                        ? new MaterialAvailabilityControl(MaterialAvailabilityKind.Global, key, list.Global.FormKey, 0)
                        : list.ChanceNone.Value != 0
                            ? new MaterialAvailabilityControl(MaterialAvailabilityKind.FixedChance, key, default, list.ChanceNone.Value)
                            : null;
                    var path = encountered is null ? availability : availability.Add(encountered);
                    evidence.Add($"Source route withheld at {list.EditorID} [{key}]: {reason}. Ordered path: {DescribeAvailability(path, graph)}.");
                    continue;
                }
                if (!copiedStart && !list.Global.IsNull)
                    availability = availability.Add(new(MaterialAvailabilityKind.Global, key, list.Global.FormKey, 0));
                else if (!copiedStart && list.ChanceNone.Value != 0)
                    availability = availability.Add(new(MaterialAvailabilityKind.FixedChance, key, default, list.ChanceNone.Value));
                var uses = graph.Uses.GetValueOrDefault(key, []).Where(u => u.Known && CdfSurvives(source, u, catalog)).ToArray();
                if (uses.Length > 0 && payload.IsNull && passedAncestor)
                {
                    var identity = (availability.Identity, unlock, level);
                    if (!found.TryGetValue(identity, out var entry))
                        found[identity] = entry = (availability, []);
                    entry.Uses.UnionWith(uses);
                }
            }
            if (key == source && payload.IsNull && passedAncestor && !graph.Lists.ContainsKey(key))
            {
                var directUses = graph.Uses.GetValueOrDefault(key, []).Where(u => u.Known
                    && u.Role.StartsWith("CDF inventory ", StringComparison.Ordinal) && CdfSurvives(source, u, catalog)).ToArray();
                if (directUses.Length > 0)
                {
                    var identity = (availability.Identity, unlock, level);
                    if (!found.TryGetValue(identity, out var entry)) found[identity] = entry = (availability, []);
                    entry.Uses.UnionWith(directUses);
                }
            }
            if (!payload.IsNull && key == payload)
            {
                todo.Enqueue((unlock!.OriginalTarget, availability, unlock, level, default, passedAncestor));
                continue;
            }
            foreach (var parent in graph.Parents.GetValueOrDefault(key, []).Distinct())
            {
                if (!payload.IsNull && !graph.Up(parent.List).Contains(payload)) continue;
                var owner = graph.Lists[parent.List];
                if (parent.Count != 1 || LeveledEntryRows(owner).Any(e => e.Reference == key && e.HasExtraData))
                { evidence.Add($"Source route withheld at {owner.EditorID} [{parent.List}]: count or entry conditions require replication."); continue; }
                var nextAvailability = availability;
                if (HasHighestEligibleCutoff(owner))
                {
                    var maximum = SelectionEntryMaximum(owner, parent.Level, catalog.ScriptReceiverLevels.GetValueOrDefault(parent.List, []));
                    if (catalog.ScriptUnresolvedReceivers.Contains(parent.List)
                        || maximum < ushort.MaxValue && (!preserveStaticWindows || unlock is not null))
                    { evidence.Add($"Source route withheld at {owner.EditorID} [{parent.List}]: finite level window requires replication."); continue; }
                    if (maximum < ushort.MaxValue)
                        nextAvailability = availability.Add(new(MaterialAvailabilityKind.ActorLevelWindow, parent.List, default, 0, maximum));
                }
                var minimum = Math.Max(level, SelectionEntryMinimum(owner, parent.Level));
                if (minimum > nextAvailability.MaximumLevel)
                { evidence.Add($"Source route withheld at {owner.EditorID} [{parent.List}]: source level window is empty."); continue; }
                todo.Enqueue((parent.List, nextAvailability, unlock, minimum, payload, passedAncestor));
            }
        }
        // A shared destination may serve consumers whose source starts at different
        // levels. The latest required minimum safely supports all of them; otherwise
        // separate equal-level groups could falsely make every boundary unprovable.
        var proofs = found.GroupBy(p => (p.Key.Availability, p.Key.Unlock)).SelectMany(group =>
            group.Select(p => p.Key.Level).Distinct().Select(level => new MaterialSourceProof(group.First().Value.Path, group.Key.Unlock, level,
                group.Where(p => p.Key.Level <= level).SelectMany(p => p.Value.Uses).Distinct().ToArray())))
            .OrderBy(p => p.Unlock is not null).ThenBy(p => p.Availability.Controls.Length > 0).ThenBy(p => p.Level).ThenBy(p => p.Identity, StringComparer.Ordinal).ToList();
        foreach (var path in proofs.Select(p => p.Availability).Where(p => p.IsCompound).DistinctBy(p => p.Identity))
            evidence.Add($"Compound availability path retained as ordered nested controls: {DescribeAvailability(path, graph)}.");
        foreach (var chance in proofs.SelectMany(p => p.Availability.Controls).Where(c => c.Kind == MaterialAvailabilityKind.FixedChance).Distinct())
            evidence.Add($"Source fixed Chance None {chance.Chance:P0} imposed by {graph.Lists[chance.List].EditorID} [{chance.List}] must be retained on this route.");
        if (proofs.Select(p => (p.Availability.Identity, p.Unlock)).Distinct().Count() > 1)
            evidence.Add("Source availability protected: different source controls require independently proven consumer routes; ambiguous shared contexts are withheld.");
        return proofs;
    }

    private static string DescribeAvailability(MaterialAvailabilityPath path, SpecializedEvidence graph) => path.Controls.Length == 0
        ? "unconditional"
        : string.Join(" -> ", path.Controls.Select(c => c.Describe(graph)));

    // One instance per assessment: uses, proofs and their control records stay fixed.
    // A destination requirement is independent of the particular proof being tested.
    private sealed class MaterialSourceMatcher
    {
        private sealed record Requirement(string Availability, ScriptUnlock? Unlock, short Level);
        private readonly ILookup<(FormKey Consumer, string Role), TypedLootUse> exactUses;
        private readonly ILookup<FormKey, TypedLootUse> livingNpcUses;
        private readonly ILookup<(string Role, string Context), TypedLootUse> contextUses;
        private readonly Dictionary<TypedLootUse, Requirement?> sourceRequirements = [];
        // Filled from parallel context workers; each requirement is pure.
        private readonly ConcurrentDictionary<TypedLootUse, Requirement?> destinationRequirements = new();

        public MaterialSourceMatcher(TypedLootUse[] all, IReadOnlyList<MaterialSourceProof> proofs)
        {
            exactUses = all.ToLookup(u => (u.Consumer, u.Role));
            livingNpcUses = all.Where(u => u.Known && IsLivingNpcRole(u.Role)).ToLookup(u => u.Consumer);
            contextUses = all.Where(u => u.Known).ToLookup(u => (u.Role, u.Context));
            var choices = new Dictionary<TypedLootUse, List<MaterialSourceProof>>();
            foreach (var proof in proofs)
                foreach (var use in proof.Uses.Distinct())
                {
                    if (!choices.TryGetValue(use, out var rows)) choices[use] = rows = [];
                    rows.Add(proof);
                }
            foreach (var (use, rows) in choices)
            {
                var unconditional = rows.Where(p => p.Availability.Controls.Length == 0 && p.Unlock is null).ToArray();
                var eligible = unconditional.Length > 0 ? (IReadOnlyList<MaterialSourceProof>)unconditional : rows;
                var first = eligible[0];
                sourceRequirements[use] = eligible.Any(p => p.Availability.Identity != first.Availability.Identity || p.Unlock != first.Unlock)
                    ? null : new(first.Availability.Identity, first.Unlock, eligible.Min(p => p.Level));
            }
        }

        public bool Matches(TypedLootUse destination, MaterialSourceProof proof)
        {
            var requirement = destinationRequirements.TryGetValue(destination, out var known) ? known
                : destinationRequirements.GetOrAdd(destination, BuildRequirement(destination));
            return requirement is not null && proof.Availability.Identity == requirement.Availability && proof.Unlock == requirement.Unlock
                && proof.Level >= requirement.Level;
        }

        private Requirement? BuildRequirement(TypedLootUse destination)
        {
            if (!destination.Known) return null;
            var exact = (destination.Consumer, destination.Role);
            var hasExact = exactUses.Contains(exact);
            // Preserve exact-consumer precedence, including unresolved blockers.
            // Other contexts still use the shared recipient predicate; a matching
            // NPC context alone never establishes source equipment entitlement.
            var relevant = hasExact ? exactUses[exact]
                : destination.EstablishedRole.Length > 0 && exactUses.Contains((destination.Consumer, destination.EstablishedRole))
                    ? exactUses[(destination.Consumer, destination.EstablishedRole)]
                : destination.Role is "NPC death loot" or SpidDeathRole ? livingNpcUses[destination.Consumer]
                : NativeContainerRole(destination.Role)
                    ? contextUses[("Container inventory", destination.Family)]
                : contextUses[(destination.Role, destination.Context)]
                    .Where(source => MatchesSingleSpecializedUse(destination, source, false));
            Requirement? combined = null;
            foreach (var use in relevant)
            {
                if (!sourceRequirements.TryGetValue(use, out var required) || required is null) return null;
                if (combined is null) combined = required;
                else
                {
                    // All relevant consumers must admit the SAME proof control.
                    // Their separate minimum levels reduce to the largest minimum.
                    if (combined.Availability != required.Availability || combined.Unlock != required.Unlock) return null;
                    if (required.Level > combined.Level) combined = combined with { Level = required.Level };
                }
            }
            return combined; // Null also caches absence of supporting consumers.
        }
    }
}
