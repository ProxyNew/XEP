using System.Collections.Concurrent;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private static List<FormKey[]> ExpansionContexts(FormKey point, int proof, string? destinationProblem,
        Dictionary<(FormKey Point, int Proof), List<FormKey[]>> cache, Func<List<FormKey[]>> resolve)
    {
        // A destination refusal is local; it cannot populate or bypass the shared context cache.
        if (destinationProblem is not null) return [];
        if (!cache.TryGetValue((point, proof), out var paths)) cache[(point, proof)] = paths = resolve();
        return paths;
    }
    private sealed record SpecializedRoute(EnchantedTemplate Template, ParentEntry Destination, FormKey PeerPool,
        FormKey ReplicatedSelector = default, IReadOnlyList<FormKey>? IsolationPath = null, IReadOnlyList<FormKey>? MaterialPath = null,
        MaterialSourceProof? SourceProof = null, MerchantInventoryEdge? MerchantIsolation = null, MerchantSourceRoute? CountedMerchantSource = null, LootRecipientEdge? RecipientIsolation = null)
    {
        // The direct bundle entry (an authored item of Destination.List, a Give All bundle)
        // whose slot choice receives the counterpart instead of the bundle itself (UseAllBundleSlots).
        public FormKey Slot { get; init; }
    }
    private sealed record SpecializedRefusal(string Attempt, string Problem)
    {
        public string Description => $"Refused {Attempt}: {Problem.TrimEnd('.')}.";
    }
    private sealed record SpecializedAssessment(List<SpecializedRoute> Routes, List<string> Evidence)
    {
        public string Stage { get; set; } = "CounterpartSearch";
        public List<SpecializedRefusal> Refusals { get; init; } = [];
        // The mirror counterpart cache entry this observed assessment was copied from or
        // stored as. Assessments with the same entry have equal routes in the same order.
        public object? MirrorEntry { get; set; }
        public void Refuse(string attempt, string problem)
        {
            var refusal = new SpecializedRefusal(attempt, problem);
            Refusals.Add(refusal); Evidence.Add(refusal.Description);
        }
    }
    private sealed record TypedLootUse(FormKey Consumer, string Role, string Context, string Family, bool Known, bool ScopeOnly = false)
    {
        public string EstablishedRole { get; init; } = "";
        // Uses key hot planning tables and their strings are long: hash once per
        // instance. A with-copy starts uncached; equality ignores the cache.
        // EstablishedRole joins equality but not the hash; it is almost always empty.
        private int hash;
        private TypedLootUse(TypedLootUse original)
        {
            Consumer = original.Consumer; Role = original.Role; Context = original.Context;
            Family = original.Family; Known = original.Known; ScopeOnly = original.ScopeOnly;
            EstablishedRole = original.EstablishedRole;
        }
        public bool Equals(TypedLootUse? other) => ReferenceEquals(this, other) || other is not null
            && Consumer == other.Consumer && Known == other.Known && ScopeOnly == other.ScopeOnly
            && Role == other.Role && Context == other.Context && Family == other.Family
            && EstablishedRole == other.EstablishedRole;
        public override int GetHashCode()
        {
            if (hash == 0) hash = HashCode.Combine(Consumer, Role, Context, Family, Known, ScopeOnly);
            return hash;
        }
    }
    private sealed record ControlledSelector(FormKey Root, FormKey[] Lists, FormKey[] Leaves);

    // Frozen evidence graph: planning new pools must never become evidence for another item.
    private sealed class SpecializedEvidence
    {
        public readonly Dictionary<FormKey, ILeveledItemGetter> Lists;
        public readonly Dictionary<FormKey, List<ParentEntry>> Parents;
        public readonly Dictionary<FormKey, List<TypedLootUse>> Uses = [];
        // Matcher-independent recipient searches over this frozen graph (see LootRecipientContexts).
        public ConcurrentDictionary<FormKey, RecipientWalk> RecipientWalks { get; } = new();
        public Dictionary<FormKey, IOutfitGetter> Outfits = [];
        public Dictionary<FormKey, INpcGetter> InventoryProviders = [];
        public Dictionary<FormKey, FormKey[]> InventoryDependents = [];
        public (string Identity, FormKey Reference, FormKey Consumer, string Role)[]? NpcRecipientReferences;
        public (FormKey Container, int Index, FormKey Item)[]? ContainerRecipientReferences;
        public LootRootIndex? LootRoots;
        public Dictionary<FormKey, (FormKey Container, int Index, IContainerEntryGetter Entry)[]>? MerchantSourceInventories;
        public readonly Dictionary<FormKey, SpecializedAssessment> Assessments = [];
        public readonly Dictionary<FormKey, SpecializedAssessment> ExpandedAssessments = [];
        // Memo tables over the frozen graph. Context lookups fill them from parallel
        // workers; every entry is a pure function of the frozen graph.
        public readonly ConcurrentDictionary<FormKey, HashSet<FormKey>> Ancestors = new();
        public readonly ConcurrentDictionary<FormKey, HashSet<FormKey>> Leaves = new();
        public readonly ConcurrentDictionary<FormKey, TypedLootUse[]> Consumers = new();
        // A root's distinct uses in consumer order, computed once over the frozen graph:
        // filtering them afterwards gives the same sequence as filtering first, because
        // equal uses share a verdict and the sort is stable.
        private readonly ConcurrentDictionary<FormKey, TypedLootUse[]> distinctSortedUses;
        public TypedLootUse[] DistinctSortedUses(FormKey root) => distinctSortedUses.TryGetValue(root, out var known) ? known
            : distinctSortedUses.GetOrAdd(root, Uses.GetValueOrDefault(root, []).Distinct().OrderBy(u => u.Consumer, FormKeyOrder.Instance).ToArray());
        public readonly ConcurrentDictionary<FormKey, bool> Cycles = new();
        // Bounded payload proofs depend only on this frozen graph and loaded
        // equipment. Pass copies start empty; no new cache is snapshot-retained.
        public readonly Dictionary<FormKey, (FormKey[] Nodes, string? Problem)> MirrorPayloadTrees = [];
        // Follow script gate-proof walks that depend only on this frozen graph:
        // receiver ancestry per receiver, destination consumer paths per route shape.
        public readonly Dictionary<FormKey, MirrorAncestry> MirrorAncestries = [];
        public readonly Dictionary<(FormKey Source, FormKey Receiver, FormKey Payload), MirrorSourceWalk> MirrorSources = [];
        public readonly Dictionary<string, int> MirrorWalkIds = new(StringComparer.Ordinal);
        public readonly Dictionary<MirrorRouteIdentity, MirrorDestinationWalk> MirrorDestinations = [];
        public readonly Dictionary<(MirrorAncestry, MirrorDestinationWalk), MirrorGateResult> MirrorReceiverCopies = [];
        public readonly Dictionary<FormKey, (bool UnsupportedLevel, bool UnsupportedEntry)> MirrorEntryMetadata = [];
        // Availability is initialized before this frozen graph is built. Generated
        // edges/copies must not enter this adjacency or become source evidence.
        private readonly HashSet<FormKey> contextBlocked;
        public SpecializedEvidence(Catalog catalog)
        {
            Lists = new(catalog.Lists);
            distinctSortedUses = new();
            Parents = catalog.DirectParents.ToDictionary(x => x.Key, x => x.Value.ToList());
            contextBlocked = catalog.RuntimeControlledLists.Keys.Concat(catalog.ScriptUnlocks.Keys)
                .Concat(catalog.UnsafeAvailability.Keys).ToHashSet();
            AllTemplates = BuildUsableTemplates(catalog.Armors, catalog.Weapons,
                catalog.ObjectEffects, catalog.KeywordNames, catalog.EnchantmentNames, catalog.DirectParents);
        }
        public SpecializedEvidence(SpecializedEvidence frozen)
        {
            Lists = frozen.Lists;
            Parents = frozen.Parents;
            Uses = frozen.Uses; Outfits = frozen.Outfits;
            distinctSortedUses = frozen.distinctSortedUses;
            InventoryProviders = frozen.InventoryProviders; InventoryDependents = frozen.InventoryDependents;
            contextBlocked = frozen.contextBlocked;
            AllTemplates = frozen.AllTemplates;
            templateIndex = frozen.templateIndex is null ? null : new(frozen.templateIndex);
            Variants = frozen.Variants;
            Assessments = frozen.Assessments.ToDictionary(x => x.Key,
                x => new SpecializedAssessment(x.Value.Routes.ToList(), x.Value.Evidence.ToList()) { Stage = x.Value.Stage, Refusals = x.Value.Refusals.ToList() });
            ExpandedAssessments = frozen.ExpandedAssessments.ToDictionary(x => x.Key,
                x => new SpecializedAssessment(x.Value.Routes.ToList(), x.Value.Evidence.ToList()) { Stage = x.Value.Stage, Refusals = x.Value.Refusals.ToList() });
            Ancestors = new(frozen.Ancestors.Select(x => KeyValuePair.Create(x.Key, new HashSet<FormKey>(x.Value))));
            Leaves = new(frozen.Leaves.Select(x => KeyValuePair.Create(x.Key, new HashSet<FormKey>(x.Value))));
            Consumers = new(frozen.Consumers);
            Cycles = new(frozen.Cycles);
            contextParents = new(frozen.contextParents);
            contextSortKeys = new(frozen.contextSortKeys);
            facts = frozen.facts;
        }
        private SpecializedGraphFacts? facts;
        private SpecializedGraphFacts Facts => LazyInitializer.EnsureInitialized(ref facts, () => new(
            Parents.ToDictionary(p => p.Key, p => p.Value.Select(e => e.List).ToArray()),
            Lists.ToDictionary(p => p.Key, p => (p.Value.Entries ?? []).Where(e => e.Data is not null)
                .Select(e => e.Data!.Reference.FormKey).ToArray())));

        public void PrepareFacts()
        {
            using var timing = PhaseTiming.Measure("specialized-graph.analysis");
            var inputs = Facts;
            // Template destinations and their ancestors are the structural roots
            // used by context and material proofs. Unrelated records stay lazy.
            var roots = new HashSet<FormKey>();
            var todo = new Queue<FormKey>(AllTemplates.SelectMany(t => t.Parents.Select(p => p.List)));
            while (todo.TryDequeue(out var key))
                if (roots.Add(key)) foreach (var parent in Parents.GetValueOrDefault(key, [])) todo.Enqueue(parent.List);
            var uses = Uses.ToDictionary(p => p.Key, p => p.Value.ToArray());
            var completed = inputs.Analyze(roots.ToArray(), uses, Math.Min(4, Math.Max(1, Environment.ProcessorCount)));
            foreach (var result in completed)
            {
                Ancestors[result.Key] = result.Ancestors; Leaves[result.Key] = result.Leaves;
                Cycles[result.Key] = result.Cycle; Consumers[result.Key] = result.Consumers;
            }
        }
        private readonly ConcurrentDictionary<(FormKey Key, bool CompleteEntry), FormKey[]> contextParents = new();
        private readonly ConcurrentDictionary<FormKey, string> contextSortKeys = new();
        public FormKey[] ContextParents(FormKey key) => ContextParents(key, completeEntry: false);
        // The cache hit allocates nothing: the computing path's closures live in their own method.
        public FormKey[] ContextParents(FormKey key, bool completeEntry)
            => contextParents.TryGetValue((key, completeEntry), out var cached) ? cached : ComputeContextParents(key, completeEntry);
        private FormKey[] ComputeContextParents(FormKey key, bool completeEntry)
        {
            string SortKey(FormKey k) => contextSortKeys.GetOrAdd(k, static x => x.ToString());
            return contextParents[(key, completeEntry)] = Parents.GetValueOrDefault(key, []).Select(e => e.List).Distinct()
                .Where(parentKey => Lists.TryGetValue(parentKey, out var parent) && !parent.IsDeleted
                    && !contextBlocked.Contains(parentKey) && !HasAncestorCycle(parentKey) && ((int)parent.Flags & ~15) == 0
                    && !(parent.Entries ?? []).Where(e => e.Data?.Reference.FormKey == key)
                        .Any(e => e.Data is not { Count: > 0 } || !completeEntry && (e.Data.Count != 1 || e.ExtraData is not null)))
                .OrderBy(SortKey, StringComparer.Ordinal).ToArray();
        }
        public readonly List<EnchantedTemplate> AllTemplates;
        private SpecializedTemplateIndex? templateIndex;
        // Semantic variant identities (effect identity and charge) interned per template.
        // Frozen catalog facts: pass copies share the table.
        public MaterialVariantTable Variants { get; private set; } = new();
        public SpecializedTemplateIndex TemplateIndex
            => Volatile.Read(ref templateIndex) ?? Interlocked.CompareExchange(ref templateIndex, new(AllTemplates), null) ?? templateIndex!;
        // Specialized destinations are parents of same material/group templates, so a
        // class without a SpecialLoot template parent can have no SpecialLoot route.
        private HashSet<(string Material, string Group)>? specialLootClasses;
        public bool HasSpecialLootTemplates(EquipmentClass cls) => (Volatile.Read(ref specialLootClasses)
            ?? Interlocked.CompareExchange(ref specialLootClasses, AllTemplates
                .Where(t => t.Parents.Any(p => ((int)Lists[p.List].Flags & 8) != 0))
                .Select(t => (t.Class.Material, SpecializedGroup(t.Class))).ToHashSet(), null) ?? specialLootClasses!)
            .Contains((cls.Material, SpecializedGroup(cls)));
        public HashSet<FormKey> Up(FormKey key)
            => Ancestors.TryGetValue(key, out var old) ? old : Ancestors.GetOrAdd(key, Facts.Up(key));
        public HashSet<FormKey> Down(FormKey key)
            => Leaves.TryGetValue(key, out var old) ? old : Leaves.GetOrAdd(key, Facts.Down(key));
        public TypedLootUse[] Endpoints(FormKey key) => Consumers.TryGetValue(key, out var old) ? old
            : Consumers.GetOrAdd(key, Up(key).SelectMany(k => Uses.GetValueOrDefault(k, [])).Distinct().ToArray());
        public string PathLabel(IEnumerable<FormKey> path) => string.Join(" > ", path.Select(k => $"{Lists.GetValueOrDefault(k)?.EditorID ?? "record"} [{k}]"));
        public TypedLootUse[] UncontrolledEndpoints(FormKey key, SpecializedAssessment assessment)
        {
            var seen = new HashSet<FormKey>(); var todo = new Stack<FormKey[]>(); todo.Push([key]);
            while (todo.TryPop(out var path))
            {
                var k = path[^1];
                if (seen.Add(k)) foreach (var p in Parents.GetValueOrDefault(k, []))
                {
                    string? problem = !Lists.TryGetValue(p.List, out var l) ? "unresolved source list"
                        : l.IsDeleted ? "source list is deleted"
                        : !l.Global.IsNull ? $"source global {l.Global.FormKey} controls availability"
                        : l.ChanceNone.Value != 0 ? $"source Chance None {l.ChanceNone} controls availability"
                        : !KnownSelectionFlags(l) ? $"unsupported source selection flags ({(int)l.Flags})"
                        : p.Count != 1 ? $"source entry count {p.Count} is not 1" : null;
                    var next = path.Append(p.List).ToArray();
                    if (problem is null) todo.Push(next);
                    else assessment.Refuse("source path " + PathLabel(next), problem);
                }
            }
            return seen.SelectMany(k => Uses.GetValueOrDefault(k, [])).Where(u => u.Known).Distinct().ToArray();
        }
        public bool HasAncestorCycle(FormKey key)
        {
            if (Cycles.TryGetValue(key, out var old)) return old;
            return Cycles.GetOrAdd(key, Facts.HasAncestorCycle(key));
        }
    }

    private static SpecializedEvidence SpecializedGraph(Catalog catalog)
    {
        if (!catalog.AvailabilityInitialized)
            throw new InvalidOperationException("Initialize source availability before building specialized evidence.");
        if (catalog.SpecializedEvidence is { } existing) return existing;
        using var timing = PhaseTiming.Measure("specialized-graph");
        var graph = new SpecializedEvidence(catalog);
        catalog.SpecializedEvidence = graph;
        var npcs = catalog.ConsumerRecords.Npcs.Values
            .Where(n => !n.IsDeleted).ToDictionary(n => n.FormKey);
        var leveled = catalog.Mods.SelectMany(m => m.LeveledNpcs).GroupBy(n => n.FormKey).Select(g => g.Last())
            .Where(n => !n.IsDeleted).ToDictionary(n => n.FormKey);
        var outfits = catalog.Mods.SelectMany(m => m.Outfits).GroupBy(n => n.FormKey).Select(g => g.Last())
            .Where(n => !n.IsDeleted).ToDictionary(n => n.FormKey);
        // LLOS protocol 450 swaps every outfit entry naming its target for a list it
        // generates at startup: consumers see that effective outfit.
        if (catalog.LlosLists is { } llos)
            foreach (var (key, outfit) in outfits.ToArray())
                if ((outfit.Items ?? []).Any(i => llos.OutfitSwap(i.FormKey) is not null || llos.OutfitWithheld(i.FormKey) is not null))
                {
                    var effective = outfit.DeepCopy();
                    for (var i = 0; i < effective.Items!.Count; i++)
                        if (llos.OutfitSwap(effective.Items[i].FormKey) is { } swapped) effective.Items[i] = new FormLink<IOutfitTargetGetter>(swapped);
                        // A withheld swap replaces the entry with an unmodelled list: the
                        // target authorizes nothing (its payloads are runtime-controlled).
                        else if (llos.OutfitWithheld(effective.Items[i].FormKey) is not null) effective.Items[i] = new FormLink<IOutfitTargetGetter>(FormKey.Null);
                    outfits[key] = effective;
                }
        graph.Outfits = outfits;
        var templateTargets = new HashSet<FormKey>();
        var templateQueue = new Queue<FormKey>(npcs.Values.Select(n => n.Template.FormKey).Where(k => !k.IsNull));
        while (templateQueue.TryDequeue(out var target))
            if (templateTargets.Add(target) && leveled.TryGetValue(target, out var templateList))
                foreach (var entry in templateList.Entries ?? []) if (entry.Data is { } data) templateQueue.Enqueue(data.Reference.FormKey);
        var questActors = catalog.Mods.SelectMany(m => m.Quests).GroupBy(q => q.FormKey).Select(g => g.Last())
            .Where(q => !q.IsDeleted).SelectMany(q => q.EnumerateFormLinks()).Select(l => l.FormKey).Where(npcs.ContainsKey).ToHashSet();
        void Add(IMajorRecordGetter consumer, string role, string context, string family, bool known, IEnumerable<FormKey> refs, bool scopeOnly = false)
        {
            foreach (var key in refs.Where(k => !k.IsNull).Distinct())
            {
                if (!graph.Uses.TryGetValue(key, out var rows)) graph.Uses[key] = rows = [];
                rows.Add(new(consumer.FormKey, role, context, family, known, scopeOnly));
            }
        }
        // Shared choice maps retain correlation between inventory, race and faction.
        var npcResolver = new NpcBranchResolver(npcs, leveled);
        SpidRecipients spid;
        using (PhaseTiming.Measure("spid.recipient-rules")) spid = new SpidRecipients(catalog.Spid, catalog, npcs, leveled, outfits.ToDictionary(x => x.Key, x => (IOutfitGetter)x.Value));
        catalog.SpidProtectedOutfits = spid.ProtectedOutfits;
        // SPID rows and each NPC's branch contexts, for the inheritors added after the loop.
        var spidRows = new List<(FormKey Npc, string Role, FormKey[] Payloads, bool Known)>();
        var branchContexts = new Dictionary<FormKey, List<(string Context, bool Known)>>();
        foreach (var npc in npcs.Values)
        {
            var branches = npcResolver.Branches(npc.FormKey);
            if (branches.All(b => b.Inventory is not null) && branches.Select(b => b.Inventory!.FormKey).Distinct().Count() == 1)
                graph.InventoryProviders[npc.FormKey] = branches[0].Inventory!;
            foreach (var b in branches)
            {
                var known = b.Inventory is not null && b.Traits is not null && b.Factions is not null;
                var factionKeys = b.Factions?.Factions.Select(f => f.Faction.FormKey).Order().ToArray() ?? [];
                // Unique actors and actors without a complete faction/race context cannot
                // authorize a progression inference from other actors.
                // A unique or quest-referenced actor can still intentionally inherit a
                // generic random inventory. Scope only locally supplied inventory to
                // that actor; inherited inventory keeps the provider template's
                // semantic context and scripts.
                var localInventory = b.Inventory?.FormKey == npc.FormKey;
                var unique = localInventory && (npcResolver.IsUnique(npc)
                    || questActors.Contains(npc.FormKey));
                var scripts = string.Join(",", (b.Inventory?.VirtualMachineAdapter?.Scripts.Select(s => s.Name.ToLowerInvariant()) ?? []).Distinct().Order());
                var context = known && !unique && !b.Traits!.Race.IsNull && factionKeys.Length > 0
                    ? $"{b.Traits.Race.FormKey}|{string.Join(',', factionKeys)}|scripts:{scripts}" : $"actor:{npc.FormKey}";
                if (spid.HasRules)
                {
                    if (!branchContexts.TryGetValue(npc.FormKey, out var contexts)) branchContexts[npc.FormKey] = contexts = [];
                    contexts.Add((context, known));
                }
                if (b.Inventory is { } inventory)
                {
                    // Branch resolution follows Inventory inheritance independently of
                    // traits/factions. Only that resolved provider owns this equipment.
                    var family = $"npc-template:{inventory.FormKey}";
                    Add(npc, "NPC inventory", context, family, known, (inventory.Items ?? []).Where(i => i.Item.Count > 0).Select(i => i.Item.Item.FormKey),
                        unique || !localInventory);
                    // A SPID replacement takes the actor's authored outfit away: certainly replaced,
                    // it neither consumes nor evidences anything there; possibly replaced, it is an
                    // unknown consumer (never evidence). Other actors sharing the outfit keep theirs.
                    var replacement = spid.ReplacesOutfits ? spid.OutfitReplacement(npc, b.Traits, b.Factions, b.Inventory) : default;
                    foreach (var (outfitKey, sleeping) in new[] { (inventory.DefaultOutfit.FormKey, false), (inventory.SleepingOutfit.FormKey, true) })
                    {
                        if (!outfits.TryGetValue(outfitKey, out var outfit)) continue;
                        if (sleeping ? replacement.SleepReplaced : replacement.DefaultReplaced) continue;
                        var undecided = sleeping ? replacement.SleepUndecided : replacement.DefaultUndecided;
                        Add(npc, "NPC outfit", context, family, known && !undecided, (outfit.Items ?? []).Select(i => i.FormKey));
                    }
                }
                else
                {
                    // Keep raw unresolved assignments as blockers, never permissions.
                    Add(npc, "NPC inventory", context, "", false, (npc.Items ?? []).Where(i => i.Item.Count > 0).Select(i => i.Item.Item.FormKey));
                }
                // SPID adds items (death items too: they accumulate on the shared base) to this
                // actor's base inventory and replaces its outfit. These consumers must always be
                // authorized: never scope-only, unknown whenever a filter fact is unresolved.
                // NPC x rule evaluation; timed only when a modelled rule exists (installed profiles
                // without equipment-reaching SPID rules pay nothing here).
                using var spidTiming = spid.HasRules ? PhaseTiming.Measure("spid.recipient-evaluation") : null;
                foreach (var (payload, isOutfit, sleep, death, certain) in spid.HasRules ? spid.Received(npc, b.Traits, b.Factions, b.Inventory).ToArray() : [])
                {
                    // SPID decides each base by its own filters, so template-family sharing must not
                    // let one actor's SPID payload authorize another's: the family is per actor.
                    var spidFamily = $"spid-actor:{npc.FormKey}";
                    if (!isOutfit)
                    {
                        var role = death ? SpidDeathRole : "NPC inventory";
                        Add(npc, role, context, spidFamily, known && certain, [payload]);
                        spidRows.Add((npc.FormKey, role, [payload], known && certain));
                    }
                    else if (outfits.TryGetValue(payload, out var replacement))
                    {
                        var items = (replacement.Items ?? []).Select(i => i.FormKey).ToArray();
                        Add(npc, "NPC outfit", context, spidFamily, known && certain, items);
                        // A SleepOutfit is set on the base: NPCs inheriting its inventory may wear it
                        // whatever their own filters say (not modelled: unknown consumers there).
                        if (sleep) spidRows.Add((npc.FormKey, "NPC outfit", items, false));
                    }
                }
            }
            // Death-item inheritance has a separate engine contract. Exact actor scope is
            // retained; it is never used for cross-actor equipment progression.
            Add(npc, "NPC death loot", $"actor:{npc.FormKey}", $"actor:{npc.FormKey}", npc.Template.IsNull && !templateTargets.Contains(npc.FormKey), [npc.DeathItem.FormKey]);
        }
        graph.InventoryDependents = npcResolver.InventoryDependencies.SelectMany(p => p.Value.Select(provider => (Provider: provider, Consumer: p.Key)))
            .GroupBy(p => p.Provider).ToDictionary(g => g.Key, g => g.Select(p => p.Consumer).Distinct().ToArray());
        // SPID adds items to a base NPC's container; NPCs inheriting their inventory from that
        // base carry them too, whatever their own filters say. Each inheritor is a consumer in
        // its own contexts and must be authorized like the base.
        foreach (var (provider, role, payloads, rowKnown) in spidRows)
            foreach (var dependent in graph.InventoryDependents.GetValueOrDefault(provider, []))
                if (dependent != provider && npcs.TryGetValue(dependent, out var inheritor))
                    foreach (var (context, contextKnown) in branchContexts.GetValueOrDefault(dependent, []))
                        Add(inheritor, role, context, $"spid-actor:{dependent}", rowKnown && contextKnown, payloads);
        foreach (var container in catalog.ConsumerRecords.Containers.Values.Where(c => !c.IsDeleted))
            Add(container, "Container inventory", $"container:{container.FormKey}", $"container:{container.FormKey}", true,
                (container.Items ?? []).Where(i => i.Item.Count > 0).Select(i => i.Item.Item.FormKey));
        foreach (var use in catalog.CdfInventory?.Uses ?? [])
        {
            if (!graph.Uses.TryGetValue(use.Payload, out var rows)) graph.Uses[use.Payload] = rows = [];
            rows.Add(CdfUse(use));
        }
        var cid = CidUses(catalog.Cid, catalog);
        foreach (var (use, payload) in cid.Uses.Zip(cid.Payloads))
        {
            if (!graph.Uses.TryGetValue(payload, out var rows)) graph.Uses[payload] = rows = [];
            rows.Add(use);
        }
        // An unbounded SPID/CID rule is an unknown consumer of its payload: it refuses
        // placement there and in everything that payload reaches, never grants it.
        foreach (var blocker in spid.Blockers.Concat(cid.Blockers))
        {
            if (!graph.Uses.TryGetValue(blocker.Payload, out var rows)) graph.Uses[blocker.Payload] = rows = [];
            rows.Add(new(blocker.Payload, blocker.Role, blocker.Reason, "", false));
        }
        foreach (var note in spid.Notes)
            catalog.DistributionNotes.AddDiagnostic(note, spid.Notes.Information.Contains(note));
        catalog.DistributionNotes.AddRange(cid.Notes);
        graph.PrepareFacts();
        return graph;
    }

    private static string SpecializedGroup(EquipmentClass c) => c.Signature == "ARMO"
        ? $"ARMO|{c.ArmorType}|{c.Type}" : c.Type switch
        {
            "Sword" or "WarAxe" or "Mace" => "WEAP|1H",
            "Greatsword" or "Battleaxe" or "Warhammer" => "WEAP|2H",
            _ => $"WEAP|{c.Type}"
        };

    private static bool IsOfficialDistributionRecord(FormKey key)
    {
        var name = key.ModKey.FileName.String;
        return name.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Update.esm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Dawnguard.esm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("HearthFires.esm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Dragonborn.esm", StringComparison.OrdinalIgnoreCase);
    }

    // The template list is replaced, not edited, when the selection changes; its item
    // set is built once per list (the count guards an in-place edit).
    private sealed record TemplateItemSet(int Count, HashSet<FormKey> Items);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<EnchantedTemplate>, TemplateItemSet> TemplateItemSets = new();
    private static IReadOnlySet<FormKey> TemplateItems(List<EnchantedTemplate> templates)
    {
        if (TemplateItemSets.TryGetValue(templates, out var known) && known.Count == templates.Count) return known.Items;
        var items = templates.Select(t => t.Item.FormKey).ToHashSet();
        TemplateItemSets.AddOrUpdate(templates, new(templates.Count, items));
        return items;
    }

    private sealed record PeerWindow(short Low, int High);
    // Ordinary peer paths are transparent unless a captured source proof supplies
    // each encountered control with its exact imposing-list provenance. The proof
    // still has to authorize the destination consumer and is attached at placement.
    private static List<PeerWindow> PeerWindows(FormKey root, FormKey item, SpecializedEvidence graph,
        short low = 1, int high = short.MaxValue, bool allowOuterBundles = false,
        MaterialAvailabilityPath? availability = null)
    {
        var result = new List<PeerWindow>();
        var windows = new HashSet<PeerWindow>();
        var active = new HashSet<FormKey>();
        // One path belongs to this traversal. Unwind it before a sibling visits
        // a shared descendant; deduplicate completed windows in first-seen order.
        void Visit(FormKey key, short minimum, int maximum)
        {
            if (key == item)
            {
                var window = new PeerWindow(minimum, maximum);
                if (windows.Add(window)) result.Add(window);
                return;
            }
            if (!active.Add(key)) return;
            try
            {
                if (!graph.Lists.TryGetValue(key, out var l) || l.IsDeleted
                    || !KnownSelectionFlags(l) || !allowOuterBundles && ((int)l.Flags & 4) != 0) return;
                if (!l.Global.IsNull)
                {
                    if (availability?.Controls.Any(c => c.Kind == MaterialAvailabilityKind.Global && c.List == key && c.Global == l.Global.FormKey) != true) return;
                }
                else if (l.ChanceNone.Value != 0
                    && availability?.Controls.Any(c => c.Kind == MaterialAvailabilityKind.FixedChance && c.List == key && c.Chance == l.ChanceNone.Value) != true) return;
                foreach (var d in LeveledEntryRows(l))
                {
                    if (d.Count != 1 || d.HasExtraData) continue;
                    var nextLow = Math.Max(minimum, SelectionEntryMinimum(l, d.Level));
                    var nextHigh = Math.Min(maximum, SelectionEntryMaximum(l, d.Level));
                    if (nextLow <= nextHigh && (d.Reference == item || graph.Down(d.Reference).Contains(item)))
                        Visit(d.Reference, nextLow, nextHigh);
                }
            }
            finally { active.Remove(key); }
        }
        Visit(root, low, high);
        return result;
    }

    private static ControlledSelector? ReplicableControlledSelector(FormKey item, EquipmentClass sourceClass,
        SpecializedEvidence graph, Catalog catalog, out string rejection)
    {
        rejection = "";
        foreach (var root in graph.Up(item).Where(graph.Lists.ContainsKey)
                     .Where(k => k.ModKey == item.ModKey && (((int)graph.Lists[k].Flags & 4) != 0))
                     .OrderBy(k => graph.Up(k).Count).ThenBy(k => graph.Down(k).Count).ThenBy(k => k))
        {
            var lists = new HashSet<FormKey>(); var leaves = new HashSet<FormKey>(); var active = new HashSet<FormKey>();
            var hasControlledBranch = false; string? problem = null;
            bool Visit(FormKey key)
            {
                if (!graph.Lists.TryGetValue(key, out var list))
                {
                    var equipment = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
                    var cls = equipment is null ? null : CachedClass(equipment, catalog);
                    if (equipment is null || cls is null || !OrdinaryEquipment(equipment, catalog.KeywordNames)
                        || ExclusionReason(equipment, catalog.KeywordNames).Length > 0
                        || cls.Material != sourceClass.Material || SpecializedGroup(cls) != SpecializedGroup(sourceClass)
                        || DistributionSubtype(equipment, catalog) != DistributionSubtype(
                            catalog.Weapons.GetValueOrDefault(item) ?? (IMajorRecordGetter)catalog.Armors[item], catalog))
                    { problem = $"selector leaf {key} is not compatible ordinary equipment"; return false; }
                    leaves.Add(key); return true;
                }
                if (!active.Add(key)) { problem = $"selector contains a cycle at {key}"; return false; }
                if (list.IsDeleted || key.ModKey != root.ModKey || catalog.RuntimeControlledLists.ContainsKey(key)
                    || catalog.ScriptUnlocks.ContainsKey(key) || catalog.UnsafeAvailability.ContainsKey(key)
                    || (list.Entries?.Count ?? 0) == 0 || ((int)list.Flags & ~15) != 0)
                { problem = $"selector list {key} has unsupported ownership, flags, availability or contents"; active.Remove(key); return false; }
                if (!list.Global.IsNull || list.ChanceNone.Value != 0) hasControlledBranch = true;
                lists.Add(key);
                var entryKinds = (list.Entries ?? []).Where(e => e.Data is not null)
                    .Select(e => graph.Lists.ContainsKey(e.Data!.Reference.FormKey)).Distinct().ToArray();
                if (entryKinds.Length > 1)
                { problem = $"selector list {key} mixes equipment and child lists"; active.Remove(key); return false; }
                foreach (var entry in list.Entries ?? [])
                    if (entry.Data is not { Count: > 0 } data || entry.ExtraData is not null || !Visit(data.Reference.FormKey))
                    { problem ??= $"selector list {key} has unsupported entry data"; active.Remove(key); return false; }
                active.Remove(key); return true;
            }
            if (Visit(root) && leaves.Contains(item) && leaves.Count > 1 && lists.Count > 1 && hasControlledBranch)
                return new(root, lists.Order().ToArray(), leaves.Order().ToArray());
            if (problem is not null) rejection = problem;
        }
        return null;
    }

    // A runtime receiver's consumers reach it under the receiver-side gates on their own
    // paths (MirrorReceiverAncestry, the original-gate authority). Consumers whose gates
    // differ are separate availability contexts, one context proof each, as static source
    // proofs separate differently gated source paths: the shared matcher then keeps a
    // destination in place only when every consumer it reaches shares one context, and
    // recipient isolation gives each context its own copy. The original-gate copy applies
    // each context's own gates, so one consumer's closed gate never hides another's
    // counterparts. Gates every consumer passes do not split; one context keeps the single
    // unconditional receiver proof. The controls only identify a context: routes keep the
    // receiver proof and the Script planner reproduces the gates (MirrorOriginalGate).
    // A consumer's gates are those of its least-gated alternative paths: as the static
    // matcher prefers a consumer's unconditional proof, a gated alternative whose level window
    // paths with a subset of its gates cover is optional and does not separate the consumer (merging
    // contexts only lets the original-gate check combine them, never widens availability).
    // Several incomparable alternatives keep their union, as the original-gate copy does.
    private static List<MaterialSourceProof> ObservedReceiverContexts(FormKey receiver, TypedLootUse[] uses, MaterialSourceProof receiverProof, Catalog catalog)
    {
        var ancestry = MirrorReceiverAncestry(receiver, catalog);
        if (ancestry.Incomplete || uses.Length < 2) return [receiverProof];
        HashSet<FormKey> Required(TypedLootUse use)
        {
            // An alternative is optional only where less-gated alternatives cover its whole
            // actor-level window (merged as the original-gate copy merges receiver windows).
            var alternatives = ancestry.Supporting(use).Select(a => (Gates: a.Gates.ToHashSet(), a.Minimum, a.Maximum)).ToList();
            bool Covered((HashSet<FormKey> Gates, int Minimum, int Maximum) a) => MergeBands(alternatives
                .Where(b => b.Gates.IsProperSubsetOf(a.Gates)).Select(b => (b.Minimum, b.Maximum)))
                .Any(band => band.Minimum <= a.Minimum && band.Maximum >= a.Maximum);
            return alternatives.Where(a => !Covered(a)).SelectMany(a => a.Gates).ToHashSet();
        }
        var gates = uses.Select(Required).ToArray();
        var common = new HashSet<FormKey>(gates[0]);
        foreach (var set in gates.Skip(1)) common.IntersectWith(set);
        var contexts = uses.Select((use, i) => (Use: use, Gates: gates[i].Except(common).Order().ToArray()))
            .GroupBy(x => string.Join(">", x.Gates), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        if (contexts.Length < 2) return [receiverProof];
        var graph = SpecializedGraph(catalog);
        MaterialAvailabilityControl Control(FormKey gate) => graph.Lists[gate] is var list && !list.Global.IsNull
            ? new(MaterialAvailabilityKind.Global, gate, list.Global.FormKey, 0)
            : new(MaterialAvailabilityKind.FixedChance, gate, default, list.ChanceNone.Value);
        return contexts.Select(c => new MaterialSourceProof(new MaterialAvailabilityPath(c.First().Gates.Select(Control)), null, 1,
            c.Select(x => x.Use).ToArray())).ToList();
    }

    private static SpecializedAssessment AssessSpecialized(FormKey key, Catalog catalog) => AssessSpecializedRoutes(key, catalog, false);

    private static SpecializedAssessment AssessSpecializedRoutes(FormKey key, Catalog catalog, bool expandMaterials, FormKey? observedReceiver = null)
    {
        using var timing = PhaseTiming.Measure(expandMaterials ? "review.material-expansion" : "review.specialized-routes");
        var graph = SpecializedGraph(catalog);
        var cache = expandMaterials ? graph.ExpandedAssessments : graph.Assessments;
        if (observedReceiver is null && cache.TryGetValue(key, out var cached)) return cached;
        var result = new SpecializedAssessment([], []); if (observedReceiver is null && !ReviewPrefetch.Active) cache[key] = result;
        ControlledSelector? selector = null;
        EquipmentClass? mirrorClass = null; string? mirrorSubtype = null; bool cacheObserved = false;
        SpecializedAssessment Finish()
        {
            bool Available(SpecializedRoute route)
            {
                if (route.ReplicatedSelector.IsNull) return CdfRouteAvailable(key, route, catalog);
                TypedLootUse[]? recipients = null;
                string? identity = null;
                return selector!.Leaves.All(leaf => !CdfRemovalsReach(leaf, route.Template, default, catalog)
                    || CdfOutputAvailable(leaf, route.Template, recipients ??= CdfRouteRecipients(route, catalog), catalog,
                        identity ??= VariantIdentity(route.Template.Item, route.Template.Enchantment, catalog)));
            }
            result.Routes.RemoveAll(route => !Available(route));
            if (HasAuthorDestinations(key, catalog))
            {
                var outside = result.Routes.Where(route => !AuthorAllowsRoute(key, route, observedReceiver, catalog)).ToArray();
                foreach (var route in outside.DistinctBy(r => r.Destination.List))
                    result.Refuse($"enchanted destination {catalog.Lists.GetValueOrDefault(route.Destination.List)?.EditorID} [{route.Destination.List}]", OutsideAuthorDestinations);
                result.Routes.RemoveAll(outside.Contains);
            }
            // XEP copies or wraps every list on an isolation or material path; one that
            // holds a list LLOS creates at startup cannot be written.
            if (catalog.LlosLists is { HasGenerated: true } llos
                && result.Routes.RemoveAll(route => llos.TouchesGenerated(catalog.Lists, (route.IsolationPath ?? []).Concat(route.MaterialPath ?? []))) > 0)
                result.Evidence.Add("Routes withheld: their isolation or material path holds a list LLOS creates at startup, which XEP cannot copy or wrap.");
            if (result.Routes.Count == 0 && result.Evidence.Count == 0)
                result.Evidence.Add($"[{result.Stage}] Counterpart assessment returned no routes and no other evidence.");
            var unique = result.Evidence.Distinct(StringComparer.Ordinal).ToArray();
            result.Evidence.Clear(); result.Evidence.AddRange(unique);
            if (cacheObserved) result.MirrorEntry = catalog.MirrorCounterparts!.Store(observedReceiver!.Value, mirrorClass!, mirrorSubtype!, expandMaterials, result);
            return result;
        }
        SpecializedAssessment Stop(string stage, string evidence)
        { result.Stage = stage; result.Refuse($"source [{key}] ({stage})", evidence); return Finish(); }
        IMajorRecordGetter? source = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
        if (source is null || ExclusionReason(source, catalog.KeywordNames).Length > 0) return Stop("SourceEligibility", "Ineligible equipment: existing source safety checks apply.");
        var cls = CachedClass(source, catalog);
        if (cls is null) return Stop("SourceClassification", "Unresolved source material/type/slot; no automatic specialized route.");
        var expandMissingMaterial = expandMaterials && !IsOwnBaseMaterial(cls.Material);
        if (expandMaterials && !expandMissingMaterial)
            result.Evidence.Add("Own-base material family permits only enchantment templates derived from that canonical base; missing-material expansion is disabled.");
        if (graph.HasAncestorCycle(key)) return Stop("SourceAncestry", "Cyclic source list ancestry requires review.");
        if (observedReceiver is { } cacheReceiver)
        {
            mirrorClass = cls; mirrorSubtype = DistributionSubtype(source, catalog);
            if (mirrorSubtype is null) return Stop("SourceSubtype", "Unknown or conflicting source subtype; mirror withheld.");
            catalog.MirrorCounterparts ??= new RuntimeMirrorCounterpartCache(catalog);
            // The counterpart cache is shared across sources. Author rules and CDF/CID
            // removals can narrow one source, so those sources neither read nor store it.
            var restricted = catalog.SourceEnchantmentAllow.ContainsKey(key) || HasAuthorDestinations(key, catalog)
                || catalog.CdfInventory?.Removals.Affects(key) == true;
            if (!restricted && catalog.MirrorCounterparts.TryGet(cacheReceiver, cls, mirrorSubtype, expandMaterials, out var counterpart)) return counterpart;
            cacheObserved = !restricted;
        }
        using var observedTiming = observedReceiver is not null ? PhaseTiming.Measure("mirror.counterpart-cache-miss") : null;
        if (observedReceiver is null) AssessCountedMerchantRoutes(key, cls, expandMaterials, catalog, result);
        var selectorRejection = "";
        selector = observedReceiver is null ? ReplicableControlledSelector(key, cls, graph, catalog, out selectorRejection) : null;
        var sourceAtom = observedReceiver ?? selector?.Root ?? key;
        var sourceUses = observedReceiver is { } receiver ? graph.Endpoints(receiver).Where(u => u.Known).Distinct().ToArray()
            : selector is null ? graph.UncontrolledEndpoints(key, result) : graph.Endpoints(selector.Root);
        bool MatchesSource(TypedLootUse destination) => observedReceiver is not null
            ? destination.Known && sourceUses.Any(s => s.Consumer == destination.Consumer && s.Role == destination.Role || ExactActorDeathUse(destination, s))
            : MatchesSpecializedUse(destination, sourceUses, selector is not null);
        if (observedReceiver is not null && sourceUses.Length == 0)
            return Stop("OriginalReceiverConsumers", "Runtime mirror withheld: no resolved original receiver consumer; virtual insertions cannot authorize orphan or unresolved contexts.");
        var receiverProof = observedReceiver is not null ? new MaterialSourceProof(new MaterialAvailabilityPath(), null, 1, sourceUses) : null;
        var sourceProofs = receiverProof is not null
            ? ObservedReceiverContexts(observedReceiver!.Value, sourceUses, receiverProof, catalog)
            : MaterialSourceProofs(key, catalog, result.Evidence);
        var proofUses = observedReceiver is not null ? sourceUses : graph.Endpoints(key).Concat(catalog.ScriptUnlocks.GetValueOrDefault(key, [])
            .SelectMany(u => graph.Endpoints(u.OriginalTarget))).Where(u => u.Known).Distinct().ToArray();
        var exactSourceConsumers = proofUses.Select(u => (u.Consumer, u.Role)).ToHashSet();
        var sourceMatcher = new MaterialSourceMatcher(proofUses, sourceProofs);
        // This cache belongs to one assessment: proof Uses, exact consumers and
        // the complete proof set are fixed here. Never key a cross-source cache
        // merely by proof.Identity, which does not encode those dependencies.
        var availabilityContexts = new Dictionary<(FormKey Point, int Proof), List<FormKey[]>>();
        // Recipient contexts depend only on the point, the proof and the context paths
        // above; repeated destinations through one point reuse them (evidence is deduplicated).
        var recipientContexts = new Dictionary<(FormKey Point, int Proof, bool Contexts), List<LootRecipientContext>>();
        // Merchant coverage uses only the roots of ordinary material isolation
        // paths. Accumulate them as routes are admitted instead of rescanning
        // and copying every route for each later material path to the same point.
        var materialContextRoots = new Dictionary<FormKey, HashSet<FormKey>>();
        // A boundary verdict depends on this source's complete consumer/proof set,
        // exact-consumer precedence and this particular availability proof. Share
        // it across starting points only within this assessment/proof. Material,
        // provider selection and frozen graph epoch are fixed by the enclosing
        // assessment; no cache survives a new source, catalog or review rescan.
        var availabilityBoundaries = new Dictionary<FormKey, bool>?[sourceProofs.Count];
        var requiresAvailabilityAttachment = observedReceiver is null && (sourceUses.Length == 0 || catalog.ScriptUnlocks.ContainsKey(key));
        if (sourceUses.Length == 0)
            sourceUses = sourceProofs.Where(p => expandMissingMaterial || p.Unlock is null).SelectMany(p => p.Uses).Distinct().ToArray();
        if (sourceUses.Length == 0) return Stop("SourceConsumers", graph.Endpoints(key).Length == 0
            ? "No resolved live source inventory/outfit/container path; orphan lists and runtime-only routes need review."
            : selectorRejection.Length > 0
                ? $"Source consumers exist, but controlled selection cannot be replicated safely: {selectorRejection}."
                : "Source consumers exist, but every supported path is controlled or unresolved. No complete controlled selector could be replicated.");
        if (observedReceiver is null && catalog.UnsafeAvailability.TryGetValue(key, out var unsafeReason)) return Stop("SourceAvailability", "Unsupported runtime availability: " + unsafeReason);
        if (observedReceiver is null && !expandMissingMaterial && catalog.ScriptUnlocks.ContainsKey(key)) return Stop("SourceAvailability", "Verified quest insertion requires the existing runtime planner; static specialized recommendation is withheld.");
        IReadOnlySet<FormKey> selectedItems = observedReceiver is not null ? catalog.MirrorCounterparts!.EnabledTemplates
            : TemplateItems(catalog.Templates);
        // Treat the insertion as a choice inside R. The live tuple's level/count
        // stay in the receiver projection; search and isolation reuse the frozen
        // ordinary graph, and admission checks its original ancestor windows.
        var ancestors = graph.Up(sourceAtom);
        var matching = graph.TemplateIndex.Matching(cls);
        var peers = new Dictionary<FormKey, FormKey>();
        var controlledPeers = new Dictionary<(FormKey Base, int Proof), FormKey[]>();
        var peerMatchers = new Dictionary<FormKey, MaterialSourceMatcher>();
        MaterialSourceMatcher PeerMatcher(FormKey pool)
        {
            if (!peerMatchers.TryGetValue(pool, out var matcher))
            {
                var poolEvidence = new List<string>();
                peerMatchers[pool] = matcher = new(proofUses, MaterialSourceProofsCore(key, catalog, poolEvidence, requiredAncestor: pool)
                    .Where(p => p.Unlock is null).ToArray());
                // A capped search yields no proofs, so this pool's peer route is silently lost; report that.
                // Other evidence lines repeat the main proof walk.
                if (poolEvidence.Contains(SourceSearchCapNotice) && !result.Evidence.Contains(SourceSearchCapNotice)) result.Evidence.Add(SourceSearchCapNotice);
            }
            return matcher;
        }
        var controlledProofs = observedReceiver is null && selector is null
            ? Enumerable.Range(0, sourceProofs.Count).Where(i => sourceProofs[i].Unlock is null
                && sourceProofs[i].Availability.Controls.Length > 0 && sourceProofs[i].Availability.Structure is null).ToArray() : [];
        // Source windows depend only on the pool and the availability proof, not on the base item.
        var sourceWindowCache = new Dictionary<(FormKey Pool, int Proof), List<PeerWindow>>();
        IEnumerable<FormKey> CompatiblePeerPools(FormKey baseKey, int proofIndex = -1)
        {
            var availability = proofIndex >= 0 ? sourceProofs[proofIndex].Availability : null;
            bool ReplacedPeer(FormKey pool) => sourceAtom == key && (catalog.NativeLists?.HasReplacementPeer(pool, key, baseKey) == true
                || catalog.LlosLists?.HasReplacementPeer(pool, key, baseKey) == true);
            foreach (var pool in ancestors.Where(graph.Lists.ContainsKey).Where(p => graph.Down(p).Contains(baseKey) || ReplacedPeer(p))
                .OrderBy(p => graph.Down(p).Count).ThenBy(p => p))
            {
                if (!sourceWindowCache.TryGetValue((pool, proofIndex), out var sourceWindows))
                    sourceWindowCache[(pool, proofIndex)] = sourceWindows = PeerWindows(pool, sourceAtom, graph, allowOuterBundles: selector is not null, availability: availability);
                // A captured in-place replacement retains its predecessor's slot.
                // Use the live source path through that exact list; all ordinary
                // window/control and later consumer/isolation checks still apply.
                var peerWindows = PeerWindows(pool, ReplacedPeer(pool) ? sourceAtom : baseKey, graph, availability: availability);
                if (peerWindows.Count > 0 && peerWindows.All(p => sourceWindows.Any(s => s.Low <= p.Low && s.High >= p.High))) yield return pool;
            }
        }
        foreach (var baseKey in matching.Select(t => t.BaseItem.FormKey).Distinct())
        {
            using var peerTiming = PhaseTiming.Measure("review.peer-search");
            // Observed choices already have a resolved class/subtype and their
            // own conditional projection. R supplies their ordinary selector;
            // the shared destination planner still checks the original consumer.
            if (observedReceiver is { } mirrorReceiver) { peers[baseKey] = mirrorReceiver; continue; }
            if (observedReceiver is null && baseKey == key)
            {
                peers[baseKey] = sourceAtom;
                foreach (var index in controlledProofs) controlledPeers[(baseKey, index)] = [sourceAtom];
                continue;
            }
            var ordinaryPool = CompatiblePeerPools(baseKey).FirstOrDefault();
            if (!ordinaryPool.IsNull) peers[baseKey] = ordinaryPool;
            else if (sourceUses.Length > 0 && sourceUses.All(use => use.Role.StartsWith("CDF inventory ", StringComparison.Ordinal)
                && graph.Uses.GetValueOrDefault(baseKey, []).Any(peer => MatchesSpecializedUse(use, [peer], false))))
                // Direct deterministic CDF additions supply the ordinary peer
                // without an LVLI selector. Every source context must prove it;
                // destination/window/isolation checks below remain unchanged.
                peers[baseKey] = sourceAtom;
            foreach (var index in controlledProofs)
            {
                var controlledPools = CompatiblePeerPools(baseKey, index).ToArray();
                if (controlledPools.Length > 0) controlledPeers[(baseKey, index)] = controlledPools;
            }
        }
        var controlledBases = controlledPeers.Keys.Select(p => p.Base).ToHashSet();
        // A fully validated selector is itself the ordinary availability archetype.
        // Heavy Armory can replace every vanilla peer in the winning ordinary branch,
        // so absence of a surviving base record must not erase the selector evidence.
        // Destination consumer checks below still require an enchanted-only list in
        // the same live actor/container contexts.
        if (selector is not null)
            foreach (var baseKey in matching.Select(t => t.BaseItem.FormKey).Distinct())
                peers.TryAdd(baseKey, sourceAtom);
        var considered = observedReceiver is not null ? graph.TemplateIndex.ObservedDestinations(cls, expandMissingMaterial)
            : SpecializedTemplateIndex.Destinations((expandMissingMaterial ? graph.TemplateIndex.Expandable(cls) : matching)
                .Where(t => peers.ContainsKey(t.BaseItem.FormKey) || controlledBases.Contains(t.BaseItem.FormKey) || expandMissingMaterial));
        if (considered.Length == 0)
        {
            return Stop("CompatibleEnchantedPeerSearch", "No ordinary peer with a compatible enchanted counterpart and equivalent level/availability path. Gated replacements, UseAll bundles and unrelated enchantments do not authorize distribution.");
        }
        IReadOnlySet<FormKey>? observedRoots = null;
        // Whether the equipment of one choice (a flat destination, or one bundle slot) admits
        // this source: ordinary enchanted equipment of its category, and its subtype unless the
        // choice already mixes categories.
        string? ChoiceEquipmentProblem(IEnumerable<ILeveledItemEntryGetter> entries)
        {
            using var equipmentTiming = PhaseTiming.Measure("review.destination-equipment");
            var equipment = entries.Select(e => e.Data is { } d
                ? catalog.Weapons.GetValueOrDefault(d.Reference.FormKey) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(d.Reference.FormKey) : null).ToArray();
            var classes = equipment.Select(e => e is null ? null : CachedClass(e, catalog)).ToArray();
            if (equipment.Any(e => e is null || !TryEnchantment(e, out _) || !OrdinaryEquipment(e, catalog.KeywordNames)))
                return "destination is not a flat, ordinary-equipment enchanted choice; a separate containing-category plan is required";
            if (classes.Any(c => c is null || SpecializedGroup(c) != SpecializedGroup(cls)))
                return "destination equipment category differs";
            if (DistributionSubtype(source, catalog) is not { } sourceSubtype) return "unknown or conflicting source subtype";
            var mixedCategory = classes.Select(c => c!.Type).Distinct().Count() > 1;
            if (!mixedCategory && !equipment.Any(e => DistributionSubtype(e!, catalog) == sourceSubtype))
                return "narrow terminal does not admit this subtype; no automatic insertion into a different weapon family";
            return null;
        }
        foreach (var group in considered)
        {
            using var destinationTiming = PhaseTiming.Measure("review.destination");
            var dest = graph.Lists[group.Key]; var label = $"{dest.EditorID} [{dest.FormKey}]";
            // A Give All bundle is a same-material destination only through its direct
            // enchanted slots: each slot is a one-entry choice of its authored item, entered at
            // level 1 since the bundle ignores entry levels. The checks below apply to that
            // virtual choice; each slot's equipment is judged on its own, since the bundle's
            // other slots are separate awards, never siblings in one choice. Other-material
            // expansion keeps refusing bundles (flag mask below).
            var bundle = ((int)dest.Flags & 4) != 0 && group.Any(x => x.Template.Class.Material == cls.Material);
            IEnumerable<(EnchantedTemplate Template, ParentEntry Parent)> members = group;
            if (bundle)
            {
                var slots = new List<(EnchantedTemplate Template, ParentEntry Parent)>();
                foreach (var slot in group.GroupBy(x => x.Template.Item.FormKey))
                    if ((BundleSlotProblem(dest, slot.Key)
                        ?? ChoiceEquipmentProblem((dest.Entries ?? []).Where(e => e.Data?.Reference.FormKey == slot.Key))) is { } slotProblem)
                        result.Refuse($"bundle slot {slot.First().Template.Item.EditorID} [{slot.Key}] of {label}", slotProblem);
                    // The bundle entry's count N stays on that entry (one roll multiplied by N, contract C5), so the choice's own
                    // entries are single awards: carrying N on the counterparts as well would give N x N items.
                    else slots.AddRange(slot.Select(x => (x.Template, x.Parent with { Level = SelectionEntryMinimum(dest, x.Parent.Level), Count = 1 })));
                if (slots.Count == 0) continue;
                members = slots;
            }
            var sameMaterial = members.Any(x => x.Template.Class.Material == cls.Material);
            var hasControlledPeer = members.Any(x => controlledBases.Contains(x.Template.BaseItem.FormKey));
            FormKey Slot(EnchantedTemplate template) => bundle ? template.Item.FormKey : default;
            if (sameMaterial && ((requiresAvailabilityAttachment && !hasControlledPeer)
                || !members.Any(x => peers.ContainsKey(x.Template.BaseItem.FormKey)) && !hasControlledPeer))
            {
                result.Refuse("enchanted destination " + label, requiresAvailabilityAttachment
                    ? "same-material equipment exists, but this route requires a source-availability attachment without a supported static proof and compatible ordinary peer. Quest insertion requires the existing runtime planner. Material expansion cannot bypass this refusal."
                    : "same-material equipment exists; no ordinary peer with a compatible enchanted counterpart. Material expansion cannot bypass this refusal.");
                continue;
            }
            if (!sameMaterial && !expandMissingMaterial) continue;
            if (!sameMaterial && sourceProofs.Count == 0)
            {
                result.Refuse("enchanted destination " + label, "no source-to-consumer route with supported availability; see source-route evidence");
                continue;
            }
            if (!sameMaterial && observedReceiver is { } observed && observedRoots is null)
            {
                var accessIndex = catalog.Access ?? BuildAccessIndex(catalog);
                observedRoots = AccessCarriers(accessIndex, observed, catalog).SelectMany(id => GroupAccess(accessIndex, accessIndex.Groups[id], catalog).Keys)
                    .Where(k => k.Kind == SpecializedGroup(cls)).Select(k => k.Root).ToHashSet();
            }
            var materialPaths = !sameMaterial ? SpecializedMaterialPaths(observedReceiver ?? key, dest.FormKey, cls, catalog, result, observedRoots) : [];
            if (!sameMaterial && materialPaths.Count == 0) continue;
            string? problem = null;
            var destUses = graph.Endpoints(dest.FormKey);
            if (!bundle && SelectionStructureProblem(dest, 11) is { } structureProblem)
                problem = structureProblem;
            else if (graph.HasAncestorCycle(dest.FormKey)) problem = "cyclic destination ancestry";
            // A bundle's admitted slots each passed this check above.
            else if (!bundle && ChoiceEquipmentProblem(dest.Entries ?? []) is { } equipmentProblem) problem = equipmentProblem;
            else if (destUses.Length == 0) problem = "no resolved live destination consumer";
            if (problem is null && catalog.RuntimeControlledLists.TryGetValue(dest.FormKey, out var runtimeControl))
                problem = $"destination list is directly runtime-controlled ({runtimeControl})";
            // XEP writes additions for a generated list to a sibling list beside it, which
            // a UseAll list would always give.
            if (problem is null && catalog.LlosLists?.IsGenerated(dest.FormKey) == true && dest.Flags.HasFlag(LeveledItem.Flag.UseAll))
                problem = "LLOS creates this destination list at startup with UseAll, so XEP cannot add beside it";
            if (problem is not null) { result.Refuse("enchanted destination " + label, problem); continue; }
            var parentlessProblem = graph.Parents.GetValueOrDefault(dest.FormKey, []).Count == 0
                ? ParentlessConsumerProblem(observedReceiver ?? key, dest.FormKey, catalog, sourceUses, selector is not null, observedReceiver is not null ? MatchesSource : null) : null;
            var enabled = members.Where(x => selectedItems.Contains(x.Template.Item.FormKey) && TemplateAllowed(key, x.Template, catalog)
                && (!sameMaterial || (x.Template.Class.Material == cls.Material && (peers.ContainsKey(x.Template.BaseItem.FormKey)
                    || controlledBases.Contains(x.Template.BaseItem.FormKey))))).ToArray();
            if (enabled.Length == 0)
            {
                if (members.Any(x => selectedItems.Contains(x.Template.Item.FormKey)) && !members.Any(x => TemplateAllowed(key, x.Template, catalog)))
                {
                    result.Refuse("enchanted destination " + label, AuthorListLeavesNoTemplate);
                    continue;
                }
                result.Refuse("enchanted destination " + label, $"Existing enchanted route, but its origins/enchantments are disabled: {string.Join(", ", members.Select(x => x.Template.Enchantment.ModKey.FileName.String).Distinct())}");
                continue;
            }
            if (!sameMaterial)
            {
                foreach (var materialPath in materialPaths.DistinctBy(p => string.Join('>', p)))
                {
                    var point = materialPath[^1];
                    var contextCount = 0;
                    for (var proofIndex = 0; proofIndex < sourceProofs.Count; proofIndex++)
                    {
                        var index = proofIndex;
                        var proof = sourceProofs[index];
                        // Observed contexts carry the receiver proof; their gates are applied by the Script planner.
                        var routeProof = receiverProof ?? proof;
                        var contexts = ExpansionContexts(point, index, parentlessProblem, availabilityContexts,
                            () => SpecializedContextPaths(point, proof.Uses, false, catalog,
                                d => (observedReceiver is null || MatchesSource(d)) && sourceMatcher!.Matches(d, proof), d => exactSourceConsumers.Contains((d.Consumer, d.Role)),
                                availabilityBoundaries[index] ??= []));
                        contextCount += contexts.Count;
                        foreach (var isolation in contexts)
                        {
                            if (observedReceiver is null)
                            {
                                if (!materialContextRoots.TryGetValue(point, out var roots)) materialContextRoots[point] = roots = [];
                                roots.Add(isolation[^1]);
                            }
                            foreach (var x in enabled)
                                result.Routes.Add(new(x.Template, x.Parent, default, IsolationPath: isolation, MaterialPath: materialPath, SourceProof: routeProof));
                        }
                        bool MatchesRecipient(TypedLootUse d) => (observedReceiver is null || MatchesSource(d)) && sourceMatcher.Matches(d, proof);
                        var recipientKey = (point, index, parentlessProblem is null);
                        if (!recipientContexts.TryGetValue(recipientKey, out var recipients))
                            recipientContexts[recipientKey] = recipients = LootRecipientContexts(point, MatchesRecipient, catalog, contexts, result.Evidence);
                        foreach (var recipient in recipients)
                        {
                            contextCount++;
                            foreach (var x in enabled)
                                result.Routes.Add(new(x.Template, x.Parent, default, IsolationPath: recipient.Path, MaterialPath: materialPath,
                                    SourceProof: routeProof, RecipientIsolation: recipient.Edge));
                        }
                    }
                    foreach (var merchant in observedReceiver is null
                        ? MerchantInventoryContexts(key, point, catalog, result.Evidence, materialContextRoots.GetValueOrDefault(point, [])) : [])
                    {
                        contextCount++;
                        foreach (var x in enabled)
                            result.Routes.Add(new(x.Template, x.Parent, default, IsolationPath: merchant.Path, MaterialPath: materialPath,
                                SourceProof: merchant.Proof, MerchantIsolation: merchant.Edge));
                    }
                    if (contextCount == 0) result.Refuse("material context path " + graph.PathLabel(materialPath),
                        "no matching consumer context or safe isolation boundary" + (parentlessProblem is null ? "" : $" ({parentlessProblem})"));
                    else result.Evidence.Add($"Supported missing-material destination {graph.Lists[point].EditorID}: {cls.Material}; {contextCount} compatible context route(s), with material/subtype/tier structure retained.");
                }
                continue;
            }
            for (var index = 0; index < sourceProofs.Count && hasControlledPeer; index++)
            {
                var proof = sourceProofs[index];
                var controlled = enabled.Where(x => controlledPeers.ContainsKey((x.Template.BaseItem.FormKey, index))).ToArray();
                if (controlled.Length == 0) continue;
                var contextCount = 0;
                foreach (var peer in controlled.SelectMany(x => controlledPeers[(x.Template.BaseItem.FormKey, index)]
                    .Select(pool => (Pool: pool, x.Template, x.Parent))).GroupBy(x => x.Pool))
                {
                    var matcher = PeerMatcher(peer.Key);
                    // The exact source path must pass through this ordinary peer
                    // selector before reaching the consumer. A peer elsewhere in
                    // the source's ancestry cannot authorize an unrelated route.
                    var contexts = parentlessProblem is not null ? [] : SpecializedContextPaths(dest.FormKey, proof.Uses, false, catalog,
                        d => sourceMatcher.Matches(d, proof) && matcher.Matches(d, proof),
                        d => exactSourceConsumers.Contains((d.Consumer, d.Role)));
                    contextCount += contexts.Count;
                    foreach (var path in contexts)
                        foreach (var x in peer)
                            result.Routes.Add(new(x.Template, x.Parent, peer.Key, IsolationPath: path, SourceProof: proof) { Slot = Slot(x.Template) });
                    bool MatchesRecipient(TypedLootUse d) => sourceMatcher.Matches(d, proof) && matcher.Matches(d, proof);
                    foreach (var recipient in LootRecipientContexts(dest.FormKey, MatchesRecipient, catalog, contexts, result.Evidence))
                    {
                        contextCount++;
                        foreach (var x in peer)
                            result.Routes.Add(new(x.Template, x.Parent, peer.Key, IsolationPath: recipient.Path, SourceProof: proof, RecipientIsolation: recipient.Edge) { Slot = Slot(x.Template) });
                    }
                }
                if (contextCount == 0) result.Refuse("same-material availability path " + label,
                    "no matching consumer context or safe isolation boundary for the source proof" + (parentlessProblem is null ? "" : $" ({parentlessProblem})"));
                else result.Evidence.Add($"Supported controlled same-material destination {label}: source minimum {proof.Level}; {DescribeAvailability(proof.Availability, graph)}; {contextCount} compatible isolated context route(s). Original peer windows and unrelated consumers remain protected.");
            }
            enabled = enabled.Where(x => peers.ContainsKey(x.Template.BaseItem.FormKey)).ToArray();
            if (requiresAvailabilityAttachment || enabled.Length == 0) continue;
            // An official ordinary selector establishes that the source may use the
            // peer's enchantment family. It does not authorize every consumer of a
            // shared enchanted terminal. Placement still has to remain inside a
            // matching source context below.
            var officialPeerSelector = observedReceiver is null && enabled.All(x =>
            {
                var peer = peers[x.Template.BaseItem.FormKey];
                if (!IsOfficialDistributionRecord(peer) || !graph.Lists.TryGetValue(peer, out var ordinary)) return false;
                var direct = (ordinary.Entries ?? []).Where(e => e.Data is not null).Select(e => e.Data!.Reference.FormKey).ToHashSet();
                return direct.Contains(sourceAtom) && direct.Contains(x.Template.BaseItem.FormKey);
            });
            var accepted = 0;
            var selectedPaths = new List<FormKey[]>();
            // Replication retains the selector's internal controls. Its authored
            // route to each recipient still has to prove outer controls/windows.
            var counterpartProofs = selector is null ? sourceProofs
                : MaterialSourceProofsCore(selector.Root, catalog, result.Evidence, replicatedStart: true);
            var counterpartMatcher = selector is null ? sourceMatcher : new MaterialSourceMatcher(sourceUses, counterpartProofs);
            foreach (var proof in counterpartProofs)
            {
                bool MatchesRecipient(TypedLootUse use) => MatchesSource(use) && counterpartMatcher.Matches(use, proof);
                // Every consumer of an observed receiver is an exact source consumer whose
                // context must match, scope-only ones included: the original-gate copy reads
                // every consumer a shared destination reaches (as the missing-material branch).
                var paths = SpecializedContextPaths(dest.FormKey, proof.Uses, selector is not null, catalog, MatchesRecipient,
                    receiverProof is null ? null : d => exactSourceConsumers.Contains((d.Consumer, d.Role)));
                selectedPaths.AddRange(paths);
                var recipients = LootRecipientContexts(dest.FormKey, MatchesRecipient, catalog, paths, result.Evidence);
                foreach (var (path, edge) in paths.Select(p => (Path: p, Edge: (LootRecipientEdge?)null))
                    .Concat(recipients.Select(r => (r.Path, (LootRecipientEdge?)r.Edge))))
                    foreach (var x in enabled)
                    {
                        var level = Math.Max(x.Parent.Level, proof.Level);
                        if (HasHighestEligibleCutoff(dest) && level > x.Parent.Level)
                        { result.Refuse("recipient path " + graph.PathLabel(path), $"source minimum {level} would change highest-eligible tier {x.Parent.Level}"); continue; }
                        var attachment = receiverProof is null && (proof.Availability.Controls.Length > 0 || proof.Unlock is not null) ? proof : null;
                        result.Routes.Add(new(x.Template, x.Parent with { Level = level }, peers[x.Template.BaseItem.FormKey],
                            selector?.Root ?? default, path, SourceProof: attachment, RecipientIsolation: edge) { Slot = Slot(x.Template) });
                        accepted++;
                    }
            }
            foreach (var merchant in observedReceiver is null
                ? MerchantInventoryContexts(key, dest.FormKey, catalog, result.Evidence, selectedPaths.Select(p => p[^1])) : [])
            {
                if (result.Routes.Any(r => r.RecipientIsolation?.Consumer == merchant.Edge.Container && r.Destination.List == dest.FormKey)) continue;
                foreach (var x in enabled)
                {
                    var level = Math.Max(x.Parent.Level, merchant.Proof.Level);
                    if (HasHighestEligibleCutoff(dest) && level > x.Parent.Level) continue;
                    result.Routes.Add(new(x.Template, x.Parent with { Level = level }, peers[x.Template.BaseItem.FormKey],
                        selector?.Root ?? default, merchant.Path, SourceProof: merchant.Proof, MerchantIsolation: merchant.Edge) { Slot = Slot(x.Template) });
                    accepted++;
                }
            }
            if (officialPeerSelector)
                result.Evidence.Add("Canonical ordinary peer evidence licenses the enchantment family; each written counterpart still requires its consumer and availability proof.");
            if (accepted == 0)
                result.Refuse("enchanted destination " + label, "no proved incoming LVLI boundary or writable private recipient reference; shared consumers remain protected");
            else
                result.Evidence.Add($"Supported {(bundle ? "Give All bundle slot(s) of " : "")}{label}: {accepted} proved counterpart route(s); original controls, source minima and unrelated consumers retained."
                    + (bundle ? " Each slot becomes a one-entry choice of its authored item; no bundle entry is added." : ""));
        }
        result.Stage = result.Routes.Count > 0 ? "CounterpartRoutesFound" : "CounterpartSearchExhausted";
        return Finish();
    }

    private static bool SpecializedRouteIsolated(SpecializedRoute route) =>
        route.RecipientIsolation is not null || route.MerchantIsolation is not null || route.IsolationPath is { Count: > 1 };

    // The flat list a route writes to: its existing context copy, or the original.
    // Before a copy exists the original stands in for it as the copy's prototype.
    private static FormKey SpecializedFlatTarget(SpecializedRoute route, Catalog catalog) =>
        !SpecializedRouteIsolated(route)
            ? route.Destination.List : catalog.SpecializedPathClones.GetValueOrDefault(SpecializedIsolationIdentity(route), route.Destination.List);

    private static bool SpecializedFreshCopy(SpecializedRoute route, Catalog catalog) =>
        SpecializedRouteIsolated(route) && !catalog.SpecializedPathClones.ContainsKey(SpecializedIsolationIdentity(route));

    private static bool SpecializedFlatCapacity(ILeveledItemGetter target, int additions, int multiplicity) =>
        target.Entries!.Count + additions + multiplicity <= 255;

    private static bool SpecializedFamilyIntrinsicallyFull(FormKey source, SpecializedAssessment assessment, Catalog catalog, PlanningAdditions<VariantPlan> plans)
    {
        if (assessment.Routes.Count == 0) return false;
        var graph = SpecializedGraph(catalog);
        var inventory = catalog.SourceEquipment!.GetValueOrDefault(source, []);
        foreach (var route in assessment.Routes)
        {
            // Deferred/composed routes and existing coverage are not proved failures.
            if (route.MaterialPath is not null || route.CountedMerchantSource is not null
                || !route.ReplicatedSelector.IsNull || route.SourceProof is not null || !route.Slot.IsNull) return false;
            var target = SpecializedFlatTarget(route, catalog);
            if (target != route.Destination.List || catalog.Fallbacks.ContainsKey(target)
                || !graph.Lists.TryGetValue(target, out var original) || !ReferenceEquals(original, catalog.Lists[target])
                || SpecializedFlatCapacity(original, 0, 1)) return false;
            var identity = VariantIdentity(route.Template.Item, route.Template.Enchantment, catalog);
            if (inventory.Any(i => OrdinaryEquipment(i, catalog.KeywordNames) && TryEnchantment(i, out var enchantment)
                    && VariantIdentity(i, enchantment, catalog) == identity)
                || plans.Any(p => p.Source.FormKey == source && p.Parents.Count > 0
                    && VariantIdentity(p.Template.Item, p.Template.Enchantment, catalog) == identity)) return false;
        }
        return true;
    }

    private static void BuildSpecializedEnchantedPlans(CandidateItem candidate, Catalog catalog, ProfileSettings profile, PlanningAdditions<VariantPlan> plans)
    {
        using var timing = PhaseTiming.Measure("plans.specialized");
        var key = FormKey.Factory(candidate.FormKey);
        MaterializeSpecializedEnchantedPlans(candidate, catalog, profile, plans,
            AssessSpecializedRoutes(key, catalog, profile.AllowOtherMaterialRoutes));
    }

    private static void BuildOrdinarySpecialLootPlans(CandidateItem candidate, Catalog catalog, ProfileSettings profile, PlanningAdditions<VariantPlan> plans)
    {
        using var timing = PhaseTiming.Measure("plans.special-loot");
        var graph = SpecializedGraph(catalog);
        var key = FormKey.Factory(candidate.FormKey);
        IMajorRecordGetter? source = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
        if (source is null || CachedClass(source, catalog) is not { } cls || !graph.HasSpecialLootTemplates(cls)) return;
        var assessment = AssessSpecializedRoutes(key, catalog, false);
        // Reuse the same flat-terminal authorization and materialization as specialized
        // routing. SpecialLoot never becomes transparent to the ordinary lifting planner.
        var routes = assessment.Routes.Where(r => ((int)graph.Lists[r.Destination.List].Flags & 8) != 0
            && r.ReplicatedSelector.IsNull && r.CountedMerchantSource is null).ToList();
        if (routes.Count == 0)
        {
            catalog.DistributionNotes.Add($"{candidate.Name}: no proved SpecialLoot attachment. " + string.Join(" ", assessment.Evidence));
            return;
        }
        var prior = IndexSourcePlans(plans, key, catalog);
        var start = plans.Count;
        MaterializeSpecializedEnchantedPlans(candidate, catalog, profile, plans, assessment with { Routes = routes });
        var additions = plans.Skip(start).ToArray();
        plans.RemoveRange(start, plans.Count - start);
        foreach (var addition in additions)
        {
            var identity = VariantIdentity(addition.Template.Item, addition.Template.Enchantment, catalog);
            if (prior.TryGetValue(identity, out var index))
                plans[index] = plans[index] with { Parents = plans[index].Parents.Concat(addition.Parents).Distinct().ToArray(),
                    PlacementReason = "Proved SpecialLoot attachment preserving the original selection boundary", DiagnosticIds = [],
                    ParentTemplates = MergeParentTemplates(plans[index], addition) };
            else { prior.Add(identity, plans.Count); plans.Add(addition); }
        }
    }

    private static void MaterializeSpecializedEnchantedPlans(CandidateItem candidate, Catalog catalog, ProfileSettings profile,
        PlanningAdditions<VariantPlan> plans, SpecializedAssessment assessment)
    {
        var key = FormKey.Factory(candidate.FormKey);
        IMajorRecordGetter source = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter)catalog.Armors[key];
        var inventory = catalog.SourceEquipment!.GetValueOrDefault(key, []);
        static FormKey IsolationBoundary(SpecializedRoute route) => route.IsolationPath is { Count: > 1 } path
            ? path[^1] : route.Destination.List;
        ParentEntry Destination(SpecializedRoute route) => route.Destination with
            { List = SpecializedPlacementTarget(route, candidate.Category, catalog) };
        FormKey ReplicateSelector(SpecializedRoute route, string variantIdentity)
        {
            var graph = catalog.SpecializedEvidence!;
            // Shared supporting leaves must emit one actual record. Contexts with
            // different erasures/proving peers need private selectors, so final
            // selection never has to split the recipients of one leaf parent.
            var constrained = catalog.CdfInventory?.Removals.Any == true && graph.Down(route.ReplicatedSelector)
                .Where(leaf => catalog.Weapons.ContainsKey(leaf) || catalog.Armors.ContainsKey(leaf))
                .Any(leaf => CdfRemovalsReach(leaf, route.Template, default, catalog)
                    || CdfEquivalentOutputs(leaf, variantIdentity, catalog).Any(item => catalog.CdfInventory.Removals.Affects(item.FormKey)));
            var context = constrained ? "|cdf:" + route.Template.Item.FormKey + "|" + string.Join(';', CdfRouteRecipients(route, catalog)
                .Select(use => use.Consumer + "|" + use.Role + "|" + use.Family).Order(StringComparer.Ordinal)) : "";
            var cacheKey = (route.ReplicatedSelector, variantIdentity + context);
            if (catalog.ReplicatedSelectors.TryGetValue(cacheKey, out var existingRoot)) return existingRoot;
            var leafParents = new Dictionary<FormKey, List<ParentEntry>>();
            var cloned = new Dictionary<FormKey, FormKey>();
            FormKey Clone(FormKey originalKey)
            {
                if (cloned.TryGetValue(originalKey, out var found)) return found;
                var original = graph.Lists[originalKey];
                var cloneKey = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                var clone = new LeveledItem(cloneKey, SkyrimRelease.SkyrimSE)
                {
                    EditorID = $"Selector_{original.EditorID}", Flags = original.Flags,
                    ChanceNone = original.ChanceNone, Entries = new Noggog.ExtendedList<LeveledItemEntry>()
                };
                clone.Global.SetTo(original.Global.FormKey);
                cloned[originalKey] = cloneKey;
                catalog.AddList(cloneKey, clone);
                catalog.Fallbacks.Add(cloneKey, new($"controlled-selector|{route.ReplicatedSelector}|{variantIdentity}|{originalKey}", candidate.Category, clone));
                foreach (var entry in original.Entries ?? [])
                {
                    var data = entry.Data!;
                    if (graph.Lists.ContainsKey(data.Reference.FormKey))
                        catalog.FallbackEdges.Add(new(new(cloneKey, data.Level, data.Count), Clone(data.Reference.FormKey)));
                    else
                    {
                        if (!leafParents.TryGetValue(data.Reference.FormKey, out var parents)) leafParents[data.Reference.FormKey] = parents = [];
                        parents.Add(new(cloneKey, data.Level, data.Count));
                    }
                }
                return cloneKey;
            }
            var root = Clone(route.ReplicatedSelector);
            foreach (var (leafKey, parents) in leafParents.OrderBy(x => x.Key))
            {
                IMajorRecordGetter leaf = catalog.Weapons.GetValueOrDefault(leafKey) ?? (IMajorRecordGetter)catalog.Armors[leafKey];
                var leafInventory = catalog.SourceEquipment!.GetValueOrDefault(leafKey, []);
                var reused = leafInventory.FirstOrDefault(i => OrdinaryEquipment(i, catalog.KeywordNames)
                    && TryEnchantment(i, out var enchantment) && VariantIdentity(i, enchantment, catalog) == variantIdentity);
                var leafClass = CachedClass(leaf, catalog)!;
                var leafCandidate = new CandidateItem
                {
                    Plugin = leafKey.ModKey.FileName.String, FormKey = leafKey.ToString(), Signature = leafClass.Signature,
                    EditorId = leaf.EditorID ?? "", Name = DisplayName(leaf), Category = leafClass.ToString(),
                    Eligible = true, AlreadyDistributed = catalog.DistributedItems.Contains(leafKey),
                    Reason = $"Supporting leaf in replicated selector {graph.Lists[route.ReplicatedSelector].EditorID}",
                    AvailableVariants = 1, Selected = true
                };
                plans.Add(new(leafCandidate, leaf, route.Template, parents, reused,
                    $"Replicated controlled selector {graph.Lists[route.ReplicatedSelector].EditorID}; original branch flags, globals, chance, levels, counts and multiplicity retained",
                    PreserveParentMultiplicity: true));
            }
            catalog.ReplicatedSelectors[cacheKey] = root;
            return root;
        }
        var start = plans.Count;
        var replicatedCoverage = false;
        var budget = DirectPeerBudget.ForPlanning(catalog);
        foreach (var group in assessment.Routes.Where(r => r.MaterialPath is null && r.CountedMerchantSource is null
                     && (r.SourceProof is null || r.MerchantIsolation is not null || !r.ReplicatedSelector.IsNull)).GroupBy(r => VariantIdentity(r.Template.Item, r.Template.Enchantment, catalog)))
        {
            int Rank(SpecializedRoute r) { var i = profile.EnchantmentProviderPlugins.FindIndex(p => p.Equals(r.Template.Enchantment.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }
            var ordered = group.OrderBy(Rank).ThenBy(r => r.Template.Item.FormKey).ToArray();
            var existing = inventory.Where(i => OrdinaryEquipment(i, catalog.KeywordNames) && TryEnchantment(i, out var e) && VariantIdentity(i, e, catalog) == group.Key).ToArray();
            var occupied = existing.SelectMany(i => CdfCoveredAncestors(i.FormKey, PlannedLootAncestors(i.FormKey, catalog), catalog)).Select(k => (k, "")).ToHashSet();
            var authoredOccupied = occupied.ToHashSet();
            if (ordered.Any(r => r.MerchantIsolation is not null || r.RecipientIsolation is not null))
                authoredOccupied.UnionWith(CdfLootRecipientCoverage(existing.Select(i => i.FormKey), catalog));
            occupied.UnionWith(plans.Where(p => p.Source.FormKey == key
                    && VariantIdentity(p.Template.Item, p.Template.Enchantment, catalog) == group.Key)
                .SelectMany(p => p.Parents).SelectMany(p => PlannedLootAncestors(p.List, catalog)).Select(k => (k, "")));
            if (ordered.Any(r => r.MerchantIsolation is not null || r.RecipientIsolation is not null))
                occupied.UnionWith(CdfLootRecipientCoverage(existing.Select(i => i.FormKey).Concat(plans
                    .Where(p => p.Source.FormKey == key && VariantIdentity(p.Template.Item, p.Template.Enchantment, catalog) == group.Key)
                    .SelectMany(p => p.Parents).Select(p => p.List)), catalog));
            var parents = new List<ParentEntry>();
            var parentTemplates = new Dictionary<FormKey, EnchantedTemplate>();
            foreach (var route in ordered.DistinctBy(r => (r.Destination, Isolation: SpecializedIsolationIdentity(r))))
            {
                if (!CdfRouteAvailable(key, route, catalog)) continue;
                var multiplicity = route.ReplicatedSelector.IsNull ? 1 : Math.Max(1,
                    (catalog.SpecializedEvidence!.Lists[route.Destination.List].Entries ?? []).Count(e =>
                        e.Data?.Reference.FormKey == route.Template.Item.FormKey
                        && e.Data.Level == route.Destination.Level && e.Data.Count == route.Destination.Count));
                var ancestry = route.RecipientIsolation is { } recipient ? new HashSet<(FormKey, string)> { (recipient.Consumer, recipient.Role) } : route.MerchantIsolation is { } merchant ? new HashSet<(FormKey, string)> { (merchant.Container, "Container inventory") }
                    : PlannedLootAncestors(IsolationBoundary(route), catalog).Select(k => (k, "")).ToHashSet();
                if (SetsOverlap(ancestry, occupied))
                {
                    if (SetsOverlap(ancestry, authoredOccupied)) catalog.AuthoredCounterpartCoverage.Add(key);
                    continue;
                }
                // Direct entries retain this terminal's existing level formula, including
                // SpecialLoot. Do not split/restructure a flagged terminal on overflow:
                // an addition to an original is also written to its context copies.
                if (!SpecializedCanAdd(route, budget, 1, catalog, out var capacityTarget, out var positions)
                    || positions + multiplicity > 255)
                {
                    catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: specialized terminal {(route.Slot.IsNull ? route.Destination.List : capacityTarget)} has no remaining flat-entry capacity; no selection semantics were rewritten.");
                    continue;
                }
                occupied.UnionWith(ancestry);
                if (route.ReplicatedSelector.IsNull)
                {
                    var parent = Destination(route);
                    parents.Add(parent);
                    parentTemplates.TryAdd(parent.List, route.Template);
                    budget.Commit(parent.List);
                }
                else
                {
                    var destination = Destination(route);
                    var root = ReplicateSelector(route, group.Key);
                    if (!catalog.PlannedReplicatedSelectors.TryGetValue(key, out var selectors))
                        catalog.PlannedReplicatedSelectors[key] = selectors = [];
                    selectors.Add(root);
                    replicatedCoverage = true;
                    var present = catalog.FallbackEdgeCount(new(destination, root));
                    for (var copy = present; copy < multiplicity; copy++)
                    {
                        if (route.SourceProof is { } proof)
                            AttachOtherMaterialBranch(key, new(destination, root, proof.Availability, proof.Unlock), catalog, preserveChildControls: true);
                        else catalog.FallbackEdges.Add(new(destination, root));
                        // A runtime insertion is counted live by the budget.
                        if (route.SourceProof?.Unlock is null) budget.Commit(destination.List);
                    }
                }
            }
            if (parents.Count > 0) plans.Add(new(candidate, source, ordered[0].Template, parents, existing.FirstOrDefault(),
                "Evidence-supported specialized enchanted selection; original ordinary/enchanted branch weights and controls retained", ParentTemplates: parentTemplates));
        }
        AttachSpecializedAvailabilityRoutes(candidate, source, catalog, profile, plans,
            assessment.Routes.Where(r => r.MaterialPath is null && r.SourceProof is not null
                && r.MerchantIsolation is null && r.CountedMerchantSource is null && r.ReplicatedSelector.IsNull).ToList(), observedSource: false);
        candidate.Reason = plans.Count == start && !replicatedCoverage
            ? "No specialized enchanted additions: no supported enabled route, or equivalent variants already reachable"
            : replicatedCoverage && plans.Count == start
                ? "Covered by an already planned replicated selector; ordinary source lists unchanged"
                : $"{plans.Count - start} specialized enchanted variants planned; ordinary source lists unchanged";
        // Without plans, the assessment's "Supported ..." lines describe routes a later attachment check refused.
        catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: {candidate.Reason}. " + (plans.Count == start && !replicatedCoverage ? "Route assessment before attachment checks: " : "")
            + string.Join(" ", assessment.Evidence));
        BuildCountedMerchantPlans(candidate, catalog, profile, plans, assessment.Routes.Where(r => r.CountedMerchantSource is not null).ToArray());
    }

    private static void BuildSpecializedMaterialPlans(CandidateItem candidate, Catalog catalog, ProfileSettings profile, PlanningAdditions<VariantPlan> plans)
    {
        using var timing = PhaseTiming.Measure("plans.expansion");
        var key = FormKey.Factory(candidate.FormKey);
        var assessment = AssessSpecializedRoutes(key, catalog, true);
        if (profile.AllowOtherMaterialRoutes)
        {
            var normalReason = candidate.Reason;
            MaterializeSpecializedExpansion(candidate, catalog, profile, plans, MaterialRouteShapes(assessment.Routes.Where(r => r.MaterialPath is not null)));
            candidate.Reason = $"Specialized enchanted routes: {normalReason}; material expansion: {candidate.Reason}";
            catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: {candidate.Reason}");
        }
    }

    // Copies an isolation path [list, ..., boundary] below its boundary, so the copy of path[0] serves only
    // the consumers above the boundary. Shared by specialized enchanted routes and other-material lists.
    private static FormKey IsolatedCopy(IReadOnlyList<FormKey> path, string category, Catalog catalog)
    {
        var identity = string.Join('>', path);
        if (catalog.SpecializedPathClones.TryGetValue(identity, out var cloneKey)) return cloneKey;
        var graph = catalog.SpecializedEvidence!;
        // Clone from the boundary downwards and share every node clone whose
        // complete upward path is identical. Routes that diverge below a shared
        // node therefore reuse the same linked parent clone instead of building
        // a second copy that the boundary can no longer reference.
        FormKey parentKey = path[^1];
        LeveledItem? parentClone = null;
        for (var i = path.Count - 2; i >= 0; i--)
        {
            var originalKey = path[i];
            var nodeIdentity = "node|" + string.Join('>', path.Skip(i));
            if (!catalog.SpecializedPathClones.TryGetValue(nodeIdentity, out var nodeClone))
            {
                var original = graph.Lists[originalKey];
                nodeClone = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                var clone = new LeveledItem(nodeClone, SkyrimRelease.SkyrimSE)
                {
                    EditorID = $"Specialized_{original.EditorID}", Flags = original.Flags, ChanceNone = original.ChanceNone,
                    Entries = new Noggog.ExtendedList<LeveledItemEntry>((original.Entries ?? []).Select(e => e.DeepCopy()))
                };
                clone.Global.SetTo(original.Global.FormKey);
                catalog.AddList(nodeClone, clone);
                catalog.Fallbacks.Add(nodeClone, new($"specialized-context|{nodeIdentity}", category, clone));
                catalog.SpecializedPathClones[nodeIdentity] = nodeClone;
                catalog.IsolatedCopyScopes[nodeClone] = new(path[^1]);
                catalog.CopyTopology.RegisterClone(nodeClone, originalKey);
                CoordinateMaterialCopy(originalKey, nodeClone, category, catalog);
                CoordinateBundleSlotCopy(originalKey, nodeClone, category, catalog);
                // Replace the parent's reference to the original node with the clone,
                // retaining every repeated entry's level and count.
                LeveledItem parent;
                if (parentClone is not null) parent = parentClone;
                else
                {
                    var contents = MaterialContents(parentKey, catalog);
                    parent = (catalog.SpecializedRewrites.GetValueOrDefault(contents) ?? catalog.Lists[contents]).DeepCopy();
                    catalog.SetList(parent.FormKey, parent);
                    catalog.SpecializedRewrites[parent.FormKey] = parent;
                }
                var replaced = (parent.Entries ?? []).Where(e => e.Data?.Reference.FormKey == originalKey).ToArray();
                if (replaced.Length == 0)
                    throw new System.IO.InvalidDataException($"Context-isolated clone of {original.EditorID} [{originalKey}] has no parent entry to replace in {parent.EditorID} [{parent.FormKey}].");
                foreach (var entry in replaced)
                {
                    catalog.FallbackEdges.Add(new(new(parent.FormKey, entry.Data!.Level, entry.Data.Count), nodeClone));
                    parent.Entries!.Remove(entry);
                }
                MarkEntryReferencesChanged(parent.FormKey, catalog);
            }
            parentKey = nodeClone;
            parentClone = (LeveledItem)catalog.Lists[MaterialContents(nodeClone, catalog)];
        }
        cloneKey = parentKey;
        catalog.SpecializedPathClones[identity] = cloneKey;
        return cloneKey;
    }
}
