using System.Globalization;
using System.IO;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed class EnchantedBranchProfile
    {
        public bool Valid { get; set; } = true;
        public bool HasEnchanted { get; set; }
        public bool HasUnenchanted { get; set; }
        public int Leaves { get; set; }
        public HashSet<string> Signatures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Subtypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Effects { get; } = new(StringComparer.Ordinal);
        public HashSet<FormKey> Bases { get; } = [];
        public bool EnchantedOnly => Valid && HasEnchanted && !HasUnenchanted;

        public void Merge(EnchantedBranchProfile other)
        {
            Valid &= other.Valid;
            HasEnchanted |= other.HasEnchanted;
            HasUnenchanted |= other.HasUnenchanted;
            Leaves += other.Leaves;
            Signatures.UnionWith(other.Signatures);
            Materials.UnionWith(other.Materials);
            Subtypes.UnionWith(other.Subtypes);
            Effects.UnionWith(other.Effects);
            Bases.UnionWith(other.Bases);
        }
    }

    private sealed record PoolRoute(ParentEntry Parent, short ItemLevel, FormKey SiblingOf,
        bool DirectItemPlacement = false);
    private sealed record RankedTemplateRoute(EnchantedTemplate Template, PoolRoute Route, int Rank, ModKey Provider)
    {
        public ParentEntry Parent => Route.Parent;
    }
    private sealed record BroadRoute(EnchantedTemplate Template, FormKey Parent, short TierLevel,
        FormKey SiblingOf, int BranchDepth, int Weight, int Rank, ModKey Provider, int Distance, bool ExactSubtype);

    private static FormKey BaseIdentity(IMajorRecordGetter item, Catalog catalog)
    {
        var seen = new HashSet<FormKey>();
        while (TryEnchantment(item, out _))
        {
            if (!seen.Add(item.FormKey) || !TryBase(item, out var key)) return FormKey.Null;
            item = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key)!;
            if (item is null) return FormKey.Null;
        }
        // An unenchanted item's own identity matters, even if it inherits stats.
        return item.FormKey;
    }

    private static void EnsureSourceIndex(Catalog catalog)
    {
        if (catalog.SourceEquipment is not null) return;
        using var timing = PhaseTiming.Measure("source-index");
        catalog.SourceEquipment = catalog.Weapons.Values.Cast<IMajorRecordGetter>().Concat(catalog.Armors.Values)
            .Where(x => !x.IsDeleted).GroupBy(x => BaseIdentity(x, catalog)).Where(g => !g.Key.IsNull)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.FormKey).ToArray());
        catalog.DistributedBases = catalog.SourceEquipment.Where(g => g.Value.Any(i => catalog.DistributedItems.Contains(i.FormKey)))
            .Select(g => g.Key).ToHashSet();
    }

    private static string Charge(IMajorRecordGetter item) => item switch
    {
        IWeaponGetter w => w.EnchantmentAmount?.ToString(CultureInfo.InvariantCulture) ?? "null",
        IArmorGetter a => a.EnchantmentAmount?.ToString(CultureInfo.InvariantCulture) ?? "null",
        _ => "null"
    };

    private static string EffectIdentity(FormKey key, Catalog catalog)
        => SharedMemo(catalog.EffectIdentities, key, () => UncachedEffectIdentity(key, catalog));

    private static string UncachedEffectIdentity(FormKey key, Catalog catalog)
    {
        if (!catalog.ObjectEffects.TryGetValue(key, out var effect) || effect.IsDeleted
            || effect.Effects.Count == 0 || effect.Effects.Any(e => e.Data is null || e.BaseEffect.IsNull || e.Conditions.Count > 0))
            return "exact:" + key;
        // Equivalence is proven only for unconditional effects using identical MGEFs.
        // Different MGEFs (possibly scripted) and conditions are never guessed from names.
        var identity = string.Join("|", effect.EnchantmentCost, effect.Flags, effect.CastType, effect.EnchantmentAmount,
            effect.TargetType, effect.EnchantType, effect.ChargeTime.ToString("R", CultureInfo.InvariantCulture),
            effect.BaseEnchantment.FormKey, effect.WornRestrictions.FormKey, effect.ENITDataTypeState,
            string.Join(";", effect.Effects.Select(e => $"{e.BaseEffect.FormKey}:{e.Data!.Magnitude.ToString("R", CultureInfo.InvariantCulture)}:{e.Data.Area}:{e.Data.Duration}")));
        return identity;
    }

    // HashSet.Overlaps walks its argument. Coverage sets grow with every placement,
    // so walk whichever set is smaller; the answer is symmetric.
    private static bool SetsOverlap<T>(HashSet<T> a, HashSet<T> b) => a.Count <= b.Count ? b.Overlaps(a) : a.Overlaps(b);

    private const int ParallelContextProofs = 4;

    // Bounded parallel searches whose workers touch only thread-safe frozen-graph
    // memo tables. Lazily created shared state is materialised first.
    private static void ParallelContextSearch(int count, Action<int> search, Catalog catalog)
    {
        _ = catalog.ConsumerRecords;
        using var timing = PhaseTiming.Measure("review.context-parallel");
        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount)) }, i =>
            {
                using var detached = PhaseTiming.Detach();
                search(i);
            });
        }
        catch (AggregateException error) when (error.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
        }
    }

    private static string VariantIdentity(IMajorRecordGetter item, FormKey enchantment, Catalog catalog)
        => Facts(item, catalog).Identity(enchantment);

    private static bool ReusableEquipment(IMajorRecordGetter item) => !item.IsDeleted && item switch
    {
        IWeaponGetter w => !w.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable) && w.VirtualMachineAdapter is null,
        IArmorGetter a => !a.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable) && a.VirtualMachineAdapter is null,
        _ => false
    };

    private static bool OrdinaryEquipment(IMajorRecordGetter item, IReadOnlyDictionary<FormKey, string> names)
    {
        return ReusableEquipment(item)
            && !Keywords(item).Select(k => names.GetValueOrDefault(k.FormKey, ""))
                .Any(n => n.Contains("Artifact", StringComparison.OrdinalIgnoreCase) || n.Contains("DisallowEnchanting", StringComparison.OrdinalIgnoreCase));
    }

    private static EnchantedTemplate[] TemplatesForExactSource(IMajorRecordGetter source,
        IEnumerable<EnchantedTemplate> templates, Catalog catalog)
    {
        var subtype = DistributionSubtype(source, catalog);
        var sourceValue = CachedValue(source, catalog);
        var jewelrySource = CachedShape(source, catalog) is { } shape && IsJewelryType(shape.Type);
        var selectionKey = (sourceValue, subtype, jewelrySource);
        Dictionary<(int Value, string? Subtype, bool Jewelry), EnchantedTemplate[]>? selections = null;
        if (templates is List<EnchantedTemplate> stable)
        {
            if (!catalog.ExactTemplateSelections.TryGetValue(stable, out selections)
                && (ReferenceEquals(stable, catalog.Templates) || catalog.TemplatesByClass.Values.Any(x => ReferenceEquals(x, stable))))
                catalog.ExactTemplateSelections[stable] = selections = [];
            if (selections is not null && selections.TryGetValue(selectionKey, out var cached)) return cached;
        }
        EnchantedTemplate[] Finish(EnchantedTemplate[] result)
        {
            if (selections is not null) selections[selectionKey] = result;
            return result;
        }
        var available = templates.ToArray();
        var ranked = available.Select(t => (Template: t, Charge: CachedCharge(t.Item, catalog),
            Subtype: DistributionSubtype(t.BaseItem, catalog),
            Distance: Math.Abs(sourceValue - CachedValue(t.BaseItem, catalog))));
        // Jewelry tiers are spread across different metal/gem bases. Choosing one
        // base for an entire origin silently drops effects and tiers on other bases.
        if (jewelrySource)
        {
            var jewelryResult = new List<EnchantedTemplate>();
            foreach (var group in ranked.Where(t => t.Subtype == subtype).GroupBy(t => (t.Template.Enchantment, t.Charge)))
            {
                EnchantedTemplate? best = null;
                (int MissingParents, int Distance, FormKey Item) bestRank = default;
                foreach (var candidate in group)
                {
                    var t = candidate.Template;
                    var rank = (t.Parents.Count > 0 ? 0 : 1, candidate.Distance, t.Item.FormKey);
                    if (best is null || rank.CompareTo(bestRank) < 0) { best = t; bestRank = rank; }
                }
                jewelryResult.Add(best!);
            }
            return Finish(jewelryResult.ToArray());
        }
        // Retain the chosen origin's tier/charge coverage. A source-owned enchanted
        // item may prove an exact subtype but supply only one of its enchantments.
        var result = new List<EnchantedTemplate>();
        var seen = new HashSet<FormKey>();
        // Each seed can only select its own enchantment/charge bucket. ToLookup
        // retains candidate order, including ties and distinct record copies.
        var candidatesByEffect = ranked.ToLookup(t => (t.Template.Enchantment, t.Charge));
        foreach (var seed in TemplatesForSource(source, available, catalog))
        {
            var charge = CachedCharge(seed.Item, catalog);
            EnchantedTemplate? best = null;
            (int Subtype, int Distance, FormKey Base, FormKey Item) bestRank = default;
            foreach (var candidate in candidatesByEffect[(seed.Enchantment, charge)])
            {
                var t = candidate.Template;
                if (t.Parents.Count == 0 && t.Item.FormKey != seed.Item.FormKey) continue;
                var rank = (subtype is not null && candidate.Subtype == subtype ? 0 : 1,
                    candidate.Distance, t.BaseItem.FormKey, t.Item.FormKey);
                // Strictly less retains LINQ's first-wins behavior on full ties.
                if (best is null || rank.CompareTo(bestRank) < 0) { best = t; bestRank = rank; }
            }
            if (best is null) throw new InvalidOperationException("Sequence contains no elements");
            if (seen.Add(best.Item.FormKey)) result.Add(best);
        }
        return Finish(result.ToArray());
    }

    private static string SubtypeGroup(string? subtype)
    {
        var parts = subtype?.Split('|');
        return parts is { Length: >= 2 } ? $"{parts[0]}|{parts[1]}" : subtype ?? "";
    }

    private static EnchantedTemplate[] TemplatesForBroadSource(IMajorRecordGetter source,
        IEnumerable<EnchantedTemplate> templates, Catalog catalog)
    {
        return templates.GroupBy(x => x.Enchantment.ModKey)
            .SelectMany(origin =>
            {
                var archetype = ChooseArchetype(source, origin, catalog, broad: true);
                return origin.Where(x => x.BaseItem.FormKey == archetype.FormKey);
            })
            .DistinctBy(x => (x.Item.FormKey, x.Enchantment))
            .OrderBy(x => PluginName(x.Enchantment.ModKey, catalog), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Item.FormKey)
            .ToArray();
    }

    private static PlanBundle BuildSourcePlans(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies,
        Catalog catalog, List<CandidateItem> candidates, Action<string>? progress)
    {
        using var timing = PhaseTiming.Measure("plans.total");
        InitializeAvailability(settings, catalog);
        EnsureSourceIndex(catalog);
        var profile = settings.CurrentProfile;
        EnsureSpecializedChoices(candidates, catalog, profile);
        foreach (var c in candidates) c.Selected = IsSelected(c, policies, profile);
        SetPatchedSelection(catalog, candidates);
        DescribeScriptDistribution(candidates, catalog);
        var plans = catalog.BeginPlans();
        var pools = new Dictionary<(FormKey Source, FormKey Parent, short Gate, FormKey SiblingOf, string Availability), FormKey>();
        // A pool is keyed by its original target list and shared by that list's private
        // copies; each copy (or the original) still receives its own attachment.
        var attachedPools = new HashSet<(FormKey Pool, FormKey Parent, short Level)>();
        var broadPools = new Dictionary<(FormKey Source, FormKey Parent, short Gate, FormKey SiblingOf, int Depth, int Weight, string Proof), FormKey>();
        var broadFamilyNodes = new HashSet<FormKey>();
        var ancestors = new Dictionary<FormKey, HashSet<FormKey>>();
        var uniqueContexts = new Dictionary<FormKey, bool>();
        var repeatedAt = new Dictionary<FormKey, FormKey>();
        var routedContexts = new Dictionary<(ParentEntry Seed, bool AllowDirectItems), ParentEntry[]>();
        var branchProfiles = new Dictionary<FormKey, EnchantedBranchProfile>();
        var profiling = new HashSet<FormKey>();
        var templateRoutes = new Dictionary<(ParentEntry Seed, string Class, string Subtype, bool AllowDirectItems), PoolRoute[]>();
        var broadRouteCache = new Dictionary<(FormKey Item, string Group, string Type), BroadRoute[]>();
        var leafDepths = new Dictionary<FormKey, int?>();
        var acyclic = new Dictionary<FormKey, bool>();
        var active = new HashSet<FormKey>();
        // Cache hits replay their rejection evidence into the current variant scope.
        var diagnosticIndex = new Dictionary<(string, string, FormKey, string), int>();
        var routeEvidence = new Dictionary<string, int[]>();
        var trace = new HashSet<int>();
        var stage = "source";
        void Reject(string code, FormKey key, string detail = "")
        {
            var identity = (stage, code, key, detail);
            if (!diagnosticIndex.TryGetValue(identity, out var id))
            {
                id = catalog.RouteDiagnostics.Count + 1;
                diagnosticIndex.Add(identity, id);
                catalog.RouteDiagnostics.Add(new(id, stage, code, key.ToString(), catalog.Lists.GetValueOrDefault(key)?.EditorID ?? "", detail));
            }
            trace.Add(id);
        }
        (HashSet<int> Trace, string Stage) BeginTrace(string name)
        {
            var previous = (trace, stage); trace = []; stage = name; return previous;
        }
        void EndTrace(string key, (HashSet<int> Trace, string Stage) previous)
        {
            routeEvidence[key] = trace.Order().ToArray();
            previous.Trace.UnionWith(trace); trace = previous.Trace; stage = previous.Stage;
        }
        var witnessCache = new Dictionary<(FormKey, bool), string>();
        string BranchWitness(FormKey key, bool ordinary, HashSet<FormKey>? seen = null)
        {
            if (witnessCache.TryGetValue((key, ordinary), out var cached)) return cached;
            seen ??= [];
            if (!seen.Add(key)) return $"Cycle at {key}";
            string result;
            if (catalog.Lists.TryGetValue(key, out var l))
            {
                if (l.IsDeleted || l.Entries is not { Count: > 0 }) result = $"Empty/deleted list {key} {l.EditorID}";
                else
                {
                    var bad = l.Entries.FirstOrDefault(e => e.Data is null || (ordinary
                        ? BranchProfile(e.Data.Reference.FormKey).HasUnenchanted : !BranchProfile(e.Data.Reference.FormKey).Valid));
                    result = bad?.Data is { } d ? BranchWitness(d.Reference.FormKey, ordinary, seen) : $"Malformed entry in {key} {l.EditorID}";
                }
            }
            else
            {
                IMajorRecordGetter? item = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
                if (item is null) result = $"Unresolved/non-equipment leaf {key}";
                else if (!TryEnchantment(item, out _)) result = $"Unenchanted leaf {key} {item.EditorID}";
                else
                {
                    var baseKey = BaseIdentity(item, catalog);
                    var baseItem = catalog.Weapons.GetValueOrDefault(baseKey) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(baseKey);
                    result = $"Leaf {key} {item.EditorID}; base={baseKey}; class={(baseItem is null ? null : CachedClass(baseItem, catalog))}; subtype={(baseItem is null ? null : DistributionSubtype(baseItem, catalog))}";
                }
            }
            return witnessCache[(key, ordinary)] = result;
        }
        bool BranchAllowed(FormKey key, string? signature = null, string? group = null)
        {
            var b = BranchProfile(key);
            if (!b.EnchantedOnly)
            {
                if (!b.Valid) Reject("InvalidBranchLeaf", key, BranchWitness(key, false));
                if (b.HasUnenchanted) Reject("UnenchantedDescendant", key, BranchWitness(key, true));
                if (!b.HasEnchanted) Reject("NoRecognizedEnchantedDescendant", key);
                return false;
            }
            if (signature is not null && !b.Signatures.SetEquals([signature]))
            { Reject("EquipmentSignatureMismatch", key, $"Expected {signature}; found {string.Join(",", b.Signatures.Order())}"); return false; }
            if (group is not null && b.Subtypes.Any(s => SubtypeGroup(s) != group))
            { Reject("EquipmentGroupMismatch", key, $"Expected {group}; found {string.Join(",", b.Subtypes.Order())}"); return false; }
            return true;
        }
        bool CanLift(FormKey key, bool highestTier)
        {
            var l = catalog.Lists[key];
            if (!l.Global.IsNull) { Reject("GlobalBlocksLift", key, l.Global.FormKey.ToString()); return false; }
            if (l.ChanceNone != new Percent(0)) { Reject("ChanceNoneBlocksLift", key, l.ChanceNone.ToString()); return false; }
            if (((int)l.Flags & ~3) != 0) { Reject("FlagsBlockLift", key, l.Flags.ToString()); return false; }
            if (highestTier && ((int)l.Flags & 1) == 0 && l.Entries!.Select(e => e.Data!.Level).Distinct().Count() > 1)
            { Reject("HighestTierCutoffBlocksLift", key); return false; }
            if (catalog.DirectParents.GetValueOrDefault(key, []).Count == 0) Reject("NoHigherParent", key);
            return true;
        }

        bool Acyclic(FormKey key)
        {
            if (acyclic.TryGetValue(key, out var result)) return result;
            if (!catalog.Lists.TryGetValue(key, out var list)) return true;
            if (!active.Add(key)) return false;
            result = (list.Entries ?? []).All(e => e.Data is { } d && Acyclic(d.Reference.FormKey));
            active.Remove(key);
            return acyclic[key] = result;
        }
        HashSet<FormKey> Ancestors(FormKey key)
        {
            if (ancestors.TryGetValue(key, out var cached)) return cached;
            var found = new HashSet<FormKey>(); var todo = new Stack<FormKey>(); todo.Push(key);
            while (todo.TryPop(out var current))
            {
                if (!found.Add(current)) continue;
                foreach (var p in catalog.DirectParents.GetValueOrDefault(current, [])) todo.Push(p.List);
            }
            return ancestors[key] = found;
        }
        bool SafeContext(ParentEntry p)
        {
            if (p.Count != 1) { Reject("ParentEntryCount", p.List, p.Count.ToString()); return false; }
            if (!catalog.Lists.TryGetValue(p.List, out var l)) { Reject("MissingList", p.List); return false; }
            if (l.IsDeleted) { Reject("DeletedList", p.List); return false; }
            if (((int)l.Flags & 4) != 0) { Reject("UseAllFlag", p.List, l.Flags.ToString()); return false; }
            if (catalog.RuntimeControlledLists.TryGetValue(p.List, out var runtimeControl))
            { Reject("RuntimeControlledDestination", p.List, runtimeControl); return false; }
            if (!Ancestors(p.List).All(Acyclic)) { Reject("InvalidOrCyclicAncestorTree", p.List); return false; }
            if (l.Entries is not { Count: > 0 }) { Reject("EmptyList", p.List); return false; }
            if (l.Entries.Any(e => e.Data is null || e.ExtraData is not null))
            { Reject("MalformedOrExtraDataEntry", p.List); return false; }
            return true;
        }

        EnchantedBranchProfile BranchProfile(FormKey key)
        {
            if (branchProfiles.TryGetValue(key, out var cached)) return cached;
            if (!profiling.Add(key)) return new() { Valid = false };
            var result = new EnchantedBranchProfile();
            if (catalog.Lists.TryGetValue(key, out var list))
            {
                if (list.IsDeleted || list.Entries is not { Count: > 0 }) result.Valid = false;
                else foreach (var entry in list.Entries)
                {
                    if (entry.Data is null) { result.Valid = false; continue; }
                    result.Merge(BranchProfile(entry.Data.Reference.FormKey));
                }
            }
            else
            {
                IMajorRecordGetter? item = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
                result.Leaves = 1;
                if (item is null) result.Valid = false;
                else if (!TryEnchantment(item, out var enchantment)) result.HasUnenchanted = true;
                else
                {
                    // Destination validation needs the record's equipment shape, not a
                    // generation-capable base identity. A malformed or unresolved base
                    // link can therefore contribute no duplicate identity while its own
                    // weapon/armor type and slots still prove the branch shape.
                    var baseKey = BaseIdentity(item, catalog);
                    var baseItem = baseKey.IsNull ? null
                        : catalog.Weapons.GetValueOrDefault(baseKey) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(baseKey);
                    var shapeItem = baseItem ?? item;
                    var shape = CachedShape(shapeItem, catalog);
                    var cls = CachedClass(shapeItem, catalog);
                    var subtype = DistributionSubtype(shapeItem, catalog);
                    if (shape is null || subtype is null) result.Valid = false;
                    else
                    {
                        result.HasEnchanted = true;
                        if (!baseKey.IsNull) result.Bases.Add(baseKey);
                        result.Signatures.Add(shape.Signature);
                        if (cls is not null) result.Materials.Add(cls.Material);
                        result.Subtypes.Add(subtype);
                        result.Effects.Add(EffectIdentity(enchantment, catalog));
                    }
                }
            }
            profiling.Remove(key);
            return branchProfiles[key] = result;
        }

        bool CompatibleEnchantedBranch(FormKey key, EquipmentClass cls, string subtype, bool allowBroad)
        {
            var branch = BranchProfile(key);
            if (!BranchAllowed(key)) return false;
            if (branch.Signatures.Count != 1 || branch.Materials.Count != 1
                || !branch.Signatures.Contains(cls.Signature) || !branch.Materials.Contains(cls.Material))
            { Reject("SourceRouteClassMismatch", key, $"Expected {cls.Signature}/{cls.Material}; found {string.Join(",", branch.Signatures.Order())}/{string.Join(",", branch.Materials.Order())}"); return false; }
            var allowed = branch.Subtypes.SetEquals([subtype]) || allowBroad && branch.Subtypes.Contains(subtype)
                && branch.Subtypes.Count > 1 && branch.Subtypes.All(x => SubtypeGroup(x) == SubtypeGroup(subtype));
            if (!allowed) Reject("SourceRouteSubtypeMismatch", key, $"Expected {subtype}; found {string.Join(",", branch.Subtypes.Order())}");
            return allowed;
        }

        static string SubtypeGroup(string subtype)
        {
            var parts = subtype.Split('|');
            // A ring, amulet, and circlet are separate equipment families. Do not
            // let the broad ARMO grouping make one slot evidence for another.
            if (parts.Length >= 3 && parts[0].Equals("ARMO", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("Jewelry", StringComparison.OrdinalIgnoreCase))
                return $"{parts[0]}|{parts[1]}|{parts[2]}";
            return parts.Length >= 2 ? $"{parts[0]}|{parts[1]}" : subtype;
        }

        bool UniqueContext(FormKey key)
        {
            if (uniqueContexts.TryGetValue(key, out var cached)) { if (!cached) Reject("RepeatedAncestorContext", key, $"Reconverges at {repeatedAt[key]} {catalog.Lists.GetValueOrDefault(repeatedAt[key])?.EditorID}"); return cached; }
            var seen = new HashSet<FormKey> { key }; var queue = new Queue<FormKey>(); queue.Enqueue(key);
            while (queue.TryDequeue(out var current))
                foreach (var p in catalog.DirectParents.GetValueOrDefault(current, []))
                {
                    if (!seen.Add(p.List)) { repeatedAt[key] = p.List; Reject("RepeatedAncestorContext", key, $"Reconverges at {p.List} {catalog.Lists.GetValueOrDefault(p.List)?.EditorID}"); return uniqueContexts[key] = false; }
                    queue.Enqueue(p.List);
                }
            return uniqueContexts[key] = true;
        }
        bool SharedSelectionPoint(FormKey key, bool allowDirectItems)
        {
            var list = catalog.Lists[key];
            var children = list.Entries!.Select(e => e.Data!.Reference.FormKey).ToArray();
            var listChildren = children.Count(catalog.Lists.ContainsKey);
            if (listChildren == children.Length) return true;
            if (listChildren == 0 && list.Entries!.All(e => e.Data!.Count == 1)) return true;
            Reject(listChildren == 0 ? "SharedTerminalNeedsAvailabilityWrapper" : "SharedSelectorHasMixedDepth", key,
                $"List children={listChildren}; item children={children.Length - listChildren}; direct items allowed={allowDirectItems}");
            return false;
        }

        ParentEntry[] SourceRoutes(ParentEntry seed, bool allowDirectItems)
        {
            var cacheKey = (seed, allowDirectItems);
            var evidenceKey = $"source-route|{cacheKey}";
            if (routedContexts.TryGetValue(cacheKey, out var cached)) { trace.UnionWith(routeEvidence[evidenceKey]); return cached; }
            var previousTrace = BeginTrace("source-route");
            var found = new List<ParentEntry>(); var seen = new HashSet<FormKey>();
            var queue = new PriorityQueue<ParentEntry, short>(); queue.Enqueue(seed, seed.Level);
            while (queue.TryDequeue(out var current, out _))
            {
                if (!seen.Add(current.List) || !SafeContext(current) || !BranchAllowed(current.List)) continue;
                if (UniqueContext(current.List) || SharedSelectionPoint(current.List, allowDirectItems))
                { found.Add(current); continue; }
                var list = catalog.Lists[current.List];
                // Lift only through transparent source-owned routing. Never bypass
                // chance/global/UseAll/SpecialLoot controls or a highest-tier cutoff.
                if (!CanLift(current.List, true)) continue;
                foreach (var parent in catalog.DirectParents.GetValueOrDefault(current.List, []).OrderBy(p => p.Level).ThenBy(p => p.List))
                    if (parent.Count == 1)
                    {
                        var next = parent with { Level = Math.Max(current.Level, parent.Level) };
                        queue.Enqueue(next, next.Level);
                    }
                    else Reject("ParentEntryCount", parent.List, parent.Count.ToString());
            }
            EndTrace(evidenceKey, previousTrace);
            return routedContexts[cacheKey] = found.DistinctBy(p => p.List).ToArray();
        }

        bool TryRouteInsideHighestTier(ParentEntry child, ParentEntry parent, EquipmentClass cls,
            string? subtype, bool allowDirectItems, out PoolRoute? nested)
        {
            nested = null;
            var parentList = catalog.Lists[parent.List];
            var levels = parentList.Entries!.Select(e => e.Data!.Level).Distinct().Order().ToArray();
            var effectiveLevel = Math.Max(child.Level, parent.Level);
            // Calculate-from-all-levels selectors do not let a new higher entry take
            // over the list. A one-tier highest selector also has no boundary to cross.
            if (((int)parentList.Flags & 1) != 0 || levels.Length <= 1 || effectiveLevel == parent.Level)
                return false;

            var branch = BranchProfile(child.List);
            var parentBranch = BranchProfile(parent.List);
            if (subtype is null || !branch.EnchantedOnly || !branch.Materials.SetEquals([cls.Material])
                || !parentBranch.EnchantedOnly || !parentBranch.Signatures.SetEquals([cls.Signature])
                || parentBranch.Subtypes.Any(s => SubtypeGroup(s) != SubtypeGroup(subtype)))
            {
                Reject("HighestTierMaterialBoundaryUnknown", parent.List,
                    $"Cannot place level {child.Level} inside child {catalog.Lists[child.List].EditorID}; expected {cls.Material}/{(subtype is null ? "unknown subtype" : SubtypeGroup(subtype))}");
                return true;
            }

            var maximum = SelectionEntryMaximum(parentList, parent.Level);
            if (effectiveLevel > maximum)
            {
                Reject("TemplateOutsideMaterialTierWindow", parent.List,
                    $"{catalog.Lists[child.List].EditorID} is active at levels {parent.Level}-{maximum}, but the template requires level {effectiveLevel}");
                return true;
            }

            var childList = catalog.Lists[child.List];
            var children = childList.Entries!.Select(e => e.Data!.Reference.FormKey).ToArray();
            var listChildren = children.Count(catalog.Lists.ContainsKey);
            if (listChildren == 0 && allowDirectItems && childList.Entries!.All(e => e.Data!.Count == 1))
                nested = new(new(child.List, child.Level, 1), child.Level, FormKey.Null, DirectItemPlacement: true);
            else if (listChildren == children.Length)
                nested = new(new(child.List, child.Level, 1), child.Level, FormKey.Null);
            else
                Reject(listChildren == 0 ? "HighestTierFlatMaterialNeedsAvailabilityWrapper" : "HighestTierMaterialMixedDepth",
                    child.List, $"Cannot preserve the material tier without mixing direct equipment and sublists; direct items allowed={allowDirectItems}");
            return true;
        }

        IEnumerable<PoolRoute> PoolRoutes(ParentEntry route, bool allowDirectItems,
            EquipmentClass cls, string? subtype)
        {
            var list = catalog.Lists[route.List];
            var children = list.Entries!.Select(e => e.Data!.Reference.FormKey).ToArray();
            var listChildren = children.Count(catalog.Lists.ContainsKey);
            if (!UniqueContext(route.List))
            {
                // The existing selector is already shared by several paths. Modify it
                // once at its current depth rather than adding the same variant to
                // several parents that later reconverge.
                if (listChildren == children.Length)
                {
                    yield return new(route, route.Level, FormKey.Null);
                    yield break;
                }
                if (listChildren == 0 && allowDirectItems && list.Entries!.All(e => e.Data!.Count == 1))
                {
                    yield return new(route, route.Level, FormKey.Null, DirectItemPlacement: true);
                    yield break;
                }
                if (listChildren == 0 && list.Entries!.All(e => e.Data!.Count == 1))
                {
                    // A controlled family cannot be inserted as a bare item because a
                    // global or quest gate belongs to the family. Attach one generated
                    // wrapper at the same selector. The wrapper is deterministic at its
                    // outer edge, so the selector still chooses one equipment family.
                    yield return new(route, route.Level, FormKey.Null);
                    yield break;
                }
                Reject(listChildren == 0 ? "SharedTerminalNeedsAvailabilityWrapper" : "SharedSelectorHasMixedDepth",
                    route.List, $"List children={listChildren}; item children={children.Length - listChildren}; direct items allowed={allowDirectItems}");
                yield break;
            }
            // A terminal equipment/tier list is a choice belonging to its parent.
            // Mirror that choice beside it, retaining internal tiers and flags.
            if (listChildren == children.Length)
            {
                yield return new(route, route.Level, FormKey.Null);
                yield break;
            }
            if (listChildren > 0)
            {
                Reject("SelectorHasMixedDepth", route.List,
                    $"List children={listChildren}; item children={children.Length - listChildren}");
                yield break;
            }
            if (!CanLift(route.List, false)) yield break;
            if (list.Entries!.Any(e => e.Data!.Count != 1)) { Reject("TerminalEntryCount", route.List); yield break; }
            // Inside a Give All bundle a mirrored sibling would be one more
            // co-awarded entry of the bundle. Attach the wrapper inside this choice instead,
            // so the bundle's slot still awards one equipment family (UseAll contract C1).
            // The bundle keeps its own chance or global (the gate of every path through it).
            // A highest-eligible choice stays refused below: its tier maximum is checked only for a
            // mirrored sibling, and a source beyond that tier could never be selected inside it.
            if (!HasHighestEligibleCutoff(list) && catalog.DirectParents.GetValueOrDefault(route.List, []).Any(p =>
                    catalog.Lists.TryGetValue(p.List, out var owner) && SlotBundle(owner)))
            {
                yield return new(route, route.Level, FormKey.Null);
                yield break;
            }
            foreach (var parent in catalog.DirectParents.GetValueOrDefault(route.List, [])
                .OrderBy(p => p.List).ThenBy(p => p.Level))
            {
                if (!SafeContext(parent) || !UniqueContext(parent.List)
                    || !BranchAllowed(parent.List)) continue;
                // Repeated branch entries express weights; do not collapse them.
                if (catalog.Lists[parent.List].Entries!.Count(e => e.Data!.Reference.FormKey == route.List) != 1)
                { Reject("RepeatedWeightedChild", parent.List, route.List.ToString()); continue; }
                if (TryRouteInsideHighestTier(route, parent, cls, subtype, allowDirectItems, out var nested))
                {
                    if (nested is not null) yield return nested;
                    continue;
                }
                yield return new(parent, route.Level, route.List);
            }
        }

        PoolRoute[] TemplateRoutes(ParentEntry seed, EquipmentClass cls, string subtype, bool allowDirectItems)
        {
            var cacheKey = (seed, ClassKey(cls), subtype, allowDirectItems);
            var evidenceKey = $"template-route|{cacheKey}";
            if (templateRoutes.TryGetValue(cacheKey, out var cached)) { trace.UnionWith(routeEvidence[evidenceKey]); return cached; }
            var previousTrace = BeginTrace("template-route");
            var found = new List<PoolRoute>(); var seen = new HashSet<FormKey>();
            var queue = new PriorityQueue<ParentEntry, short>(); queue.Enqueue(seed, seed.Level);
            while (queue.TryDequeue(out var current, out _))
            {
                if (!seen.Add(current.List) || !SafeContext(current)) continue;
                var branch = BranchProfile(current.List);
                if (!BranchAllowed(current.List, cls.Signature, SubtypeGroup(subtype))) continue;
                var materialCompatible = branch.Materials.Contains(cls.Material);
                var subtypeCompatible = branch.Subtypes.SetEquals([subtype]) || branch.Subtypes.Count > 1;
                if (!materialCompatible) Reject("MaterialMismatchAtCandidate", current.List, $"Expected {cls.Material}; found {string.Join(",", branch.Materials.Order())}");
                if (!subtypeCompatible) Reject("ExactSubtypeMismatchAtCandidate", current.List, $"Expected {subtype}; found {string.Join(",", branch.Subtypes.Order())}");
                if (materialCompatible && subtypeCompatible)
                {
                    if (catalog.DirectParents.GetValueOrDefault(current.List, []).Count == 0
                        && branch.Subtypes.SetEquals([subtype]))
                    {
                        if (!LootConsumerKeys(catalog).Contains(current.List) || !IsGeneralRole(current.List, catalog))
                        {
                            Reject("UnverifiedParentlessEquipmentRoute", current.List,
                                "Parentless provider list lacks a verified general-loot consumer context; ownership or a quest-only consumer does not prove a compatible destination.");
                            continue;
                        }
                        if (catalog.Lists[current.List].Entries!.All(e => !catalog.Lists.ContainsKey(e.Data!.Reference.FormKey)
                                && e.Data.Count == 1))
                        {
                            if (!allowDirectItems && !IsJewelryType(cls.Type))
                            {
                                Reject("ParentlessSourceNeedsAvailabilityWrapper", current.List,
                                    "Controlled weapon/armor source cannot be added as a direct peer in a flat parentless root");
                                continue;
                            }
                            found.Add(new(current, current.Level, FormKey.Null, DirectItemPlacement: allowDirectItems));
                            continue;
                        }
                    }
                    // A direct template entry in a mixed jewelry selector can be
                    // matched in place, with a controlled source-family wrapper when
                    // required. This retains the selector's existing entries and
                    // the surrounding chance, global, level and consumer boundaries.
                    if (IsJewelryType(cls.Type) && current.List == seed.List
                        && branch.Subtypes.SetEquals([subtype])
                        && catalog.Lists[current.List].Entries!.All(e => e.Data is { Count: 1 })
                        && catalog.Lists[current.List].Entries!.Any(e => !catalog.Lists.ContainsKey(e.Data!.Reference.FormKey))
                        && catalog.Lists[current.List].Entries!.Any(e => catalog.Lists.ContainsKey(e.Data!.Reference.FormKey)))
                    {
                        found.Add(new(current, current.Level, FormKey.Null, DirectItemPlacement: allowDirectItems));
                        continue;
                    }
                    var routes = PoolRoutes(current, allowDirectItems, cls, subtype).ToArray();
                    if (routes.Length > 0)
                    {
                        found.AddRange(routes);
                        continue;
                    }
                }
                var list = catalog.Lists[current.List];
                if (!CanLift(current.List, true)) continue;
                foreach (var parent in catalog.DirectParents.GetValueOrDefault(current.List, []).OrderBy(p => p.Level).ThenBy(p => p.List))
                    if (parent.Count == 1)
                    {
                        if (!SafeContext(parent)) continue;
                        if (TryRouteInsideHighestTier(current, parent, cls, subtype, allowDirectItems, out var nested))
                        {
                            if (nested is not null) found.Add(nested);
                            continue;
                        }
                        var next = parent with { Level = Math.Max(current.Level, parent.Level) };
                        queue.Enqueue(next, next.Level);
                    }
                    else Reject("ParentEntryCount", parent.List, parent.Count.ToString());
            }
            EndTrace(evidenceKey, previousTrace);
            return templateRoutes[cacheKey] = found.Distinct().ToArray();
        }

        bool SafeBroadTarget(FormKey key)
        {
            if (!catalog.Lists.TryGetValue(key, out var list) || list.IsDeleted || catalog.QuestLists.Contains(key)
                || !list.Global.IsNull || list.ChanceNone != new Percent(0) || ((int)list.Flags & ~3) != 0
                || list.Entries is not { Count: > 0 })
            { Reject("BroadTargetControlledOrInvalid", key, list is null ? "Missing" : $"Flags={list.Flags}; global={list.Global.FormKey}; chance={list.ChanceNone}"); return false; }
            if (!list.Entries.All(e => e.Data is { Count: 1 } d && e.ExtraData is null && catalog.Lists.ContainsKey(d.Reference.FormKey)))
            { Reject("BroadTargetNotAllSingleCountSublists", key); return false; }
            return true;
        }

        int? UniformLeafDepth(FormKey key, HashSet<FormKey>? path = null)
        {
            if (!catalog.Lists.TryGetValue(key, out var list)) return 0;
            if (leafDepths.TryGetValue(key, out var cached)) return cached;
            path ??= [];
            if (!path.Add(key) || list.Entries is not { Count: > 0 }) return null;
            var depths = list.Entries.Select(e => e.Data is null ? (int?)null : UniformLeafDepth(e.Data.Reference.FormKey, path))
                .Distinct().ToArray();
            path.Remove(key);
            return leafDepths[key] = depths.Length == 1 && depths[0] is { } depth ? depth + 1 : null;
        }

        string[] providerOrder = profile.EnchantmentProviderPlugins.ToArray();
        var providerRanks = providerOrder.Select((name, rank) => (name, rank))
            .ToDictionary(x => x.name, x => x.rank, StringComparer.OrdinalIgnoreCase);
        int Rank(ModKey key) => providerRanks.GetValueOrDefault(PluginName(key, catalog), providerRanks.Count + 1000);
        (int Rank, ModKey Provider) RouteRank(EnchantedTemplate template, ParentEntry seed, ParentEntry route)
        {
            var evidence = new[]
            {
                template.Enchantment.ModKey,
                catalog.WinningPlugins.GetValueOrDefault(template.Item.FormKey, template.Item.FormKey.ModKey),
                catalog.WinningPlugins.GetValueOrDefault(seed.List, seed.List.ModKey),
                catalog.WinningPlugins.GetValueOrDefault(route.List, route.List.ModKey)
            };
            var provider = evidence.OrderBy(Rank).ThenBy(x => PluginName(x, catalog), StringComparer.OrdinalIgnoreCase).First();
            return (Rank(provider), provider);
        }
        int TemplateRank(EnchantedTemplate template) => new[]
        {
            template.Enchantment.ModKey,
            catalog.WinningPlugins.GetValueOrDefault(template.Item.FormKey, template.Item.FormKey.ModKey)
        }.Concat(template.Parents.Select(p => catalog.WinningPlugins.GetValueOrDefault(p.List, p.List.ModKey))).Min(Rank);

        BroadRoute[] BroadRoutes(EnchantedTemplate template, EquipmentShape shape)
        {
            var cacheKey = (template.Item.FormKey, shape.Group, shape.Type);
            var evidenceKey = $"broad-route|{cacheKey}";
            if (broadRouteCache.TryGetValue(cacheKey, out var cached)) { trace.UnionWith(routeEvidence[evidenceKey]); return cached; }
            var previousTrace = BeginTrace("broad-route");
            var found = new List<BroadRoute>();
            foreach (var seed in template.Parents)
            {
                var queue = new Queue<(ParentEntry Entry, int Distance)>();
                queue.Enqueue((seed, 0));
                var seen = new HashSet<FormKey>();
                while (queue.TryDequeue(out var state))
                {
                    var current = state.Entry;
                    if (!seen.Add(current.List) || !SafeContext(current)) continue;
                    var branch = BranchProfile(current.List);
                    if (!BranchAllowed(current.List, shape.Signature, shape.Group)) continue;
                    var childKeys = catalog.Lists[current.List].Entries?.Select(e => e.Data!.Reference.FormKey).ToArray() ?? [];
                    var distinctChildKeys = childKeys.Distinct().ToArray();
                    var childProfiles = distinctChildKeys.Select(BranchProfile).ToArray();
                    var singleMaterial = childProfiles.Where(p => p.EnchantedOnly && p.Materials.Count == 1).ToArray();
                    var singleSubtype = childProfiles.Where(p => p.EnchantedOnly && p.Subtypes.Count == 1).ToArray();
                    // Repeated child references are selector weights. Profile each child
                    // once, then copy the modal sibling weight to the generated family so
                    // existing relative weights remain intact.
                    var structuralMaterialSelector = childProfiles.Length > 1
                        && singleMaterial.Length * 2 >= childProfiles.Length
                        && singleMaterial.SelectMany(p => p.Materials).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
                    var structuralSubtypeSelector = childProfiles.Length > 1
                        && singleSubtype.Length * 2 >= childProfiles.Length
                        && singleSubtype.SelectMany(p => p.Subtypes).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
                    // A subtype selector inside a single, different material family is
                    // not a valid broad destination. Keep walking upward so the new
                    // material can become a sibling at a true material selector. A
                    // generic/mixed-material subtype selector, or one already matching
                    // the source material, remains usable.
                    var fixedMaterial = branch.Materials.Count == 1 ? branch.Materials.Single() : null;
                    var fixedMaterialMatchesSource = fixedMaterial is null
                        || shape.Materials.Contains(fixedMaterial, StringComparer.OrdinalIgnoreCase);
                    var usableSubtypeSelector = structuralSubtypeSelector && fixedMaterialMatchesSource;
                    if (structuralSubtypeSelector && !fixedMaterialMatchesSource)
                        Reject("BroadSubtypeSelectorFixedMaterialMismatch", current.List,
                            $"Source materials={string.Join(",", shape.Materials)}; selector material={fixedMaterial}");
                    if (!structuralMaterialSelector && !structuralSubtypeSelector)
                        Reject("NotMaterialOrSubtypeSelector", current.List, $"Entries={childKeys.Length}; distinctChildren={childProfiles.Length}; cleanMaterialChildren={singleMaterial.Length}; cleanSubtypeChildren={singleSubtype.Length}");
                    if ((structuralMaterialSelector || usableSubtypeSelector)
                        && SafeBroadTarget(current.List))
                    {
                        var siblings = childKeys.GroupBy(k => k)
                            .Select(g => (Key: g.Key, Weight: g.Count(), Depth: UniformLeafDepth(g.Key)))
                            .Where(x => x.Depth is > 0).ToArray();
                        var mode = siblings.GroupBy(x => x.Depth!.Value).OrderByDescending(g => g.Sum(x => x.Weight))
                            .ThenBy(g => g.Key).FirstOrDefault();
                        if (mode is null) Reject("NoUniformNonzeroDepthSibling", current.List);
                        if (mode is not null)
                        {
                            var modalWeight = mode.GroupBy(x => x.Weight).OrderByDescending(g => g.Count())
                                .ThenBy(g => g.Key).First().Key;
                            var sibling = mode.Where(x => x.Weight == modalWeight).OrderBy(x => x.Key).First();
                            var ranked = RouteRank(template, seed, new(current.List, current.Level, 1));
                            var exactSubtype = branch.Subtypes.SetEquals([IsJewelryType(shape.Type)
                                ? shape.Group : $"{shape.Group}|{shape.Type.ToLowerInvariant()}"]);
                            found.Add(new(template, current.List, current.Level, sibling.Key, mode.Key, modalWeight, ranked.Rank,
                                ranked.Provider, state.Distance, exactSubtype));
                        }
                    }
                    var list = catalog.Lists[current.List];
                    if (!CanLift(current.List, false)) continue;
                    foreach (var parent in catalog.DirectParents.GetValueOrDefault(current.List, []))
                        if (parent.Count == 1) queue.Enqueue((parent with { Level = Math.Max(current.Level, parent.Level) }, state.Distance + 1));
                        else Reject("ParentEntryCount", parent.List, parent.Count.ToString());
                }
            }
            EndTrace(evidenceKey, previousTrace);
            return broadRouteCache[cacheKey] = found.OrderBy(x => x.Rank).ThenByDescending(x => x.ExactSubtype).ThenByDescending(x => x.Distance)
                .ThenBy(x => x.Parent).ThenBy(x => x.TierLevel)
                .DistinctBy(x => (x.Parent, x.TierLevel, x.SiblingOf, x.Weight)).ToArray();
        }

        BroadRoute[] PreferredBroadRoutes(IEnumerable<BroadRoute> candidates)
        {
            var routes = candidates.GroupBy(x => x.Parent).Select(group => group
                .OrderBy(x => x.Rank)
                .ThenByDescending(x => x.ExactSubtype)
                .ThenByDescending(x => x.Distance)
                .ThenBy(x => x.TierLevel)
                .ThenBy(x => x.SiblingOf).First()).ToArray();
            var candidateParents = routes.Select(x => x.Parent).ToHashSet();
            // Try preferred providers and the highest compatible selectors first.
            // Existing recipient coverage suppresses redundant lower routes while
            // independent authorized contexts can still receive their own route.
            return routes.OrderBy(x => x.Rank)
                .ThenByDescending(x => x.ExactSubtype)
                .ThenBy(x => Ancestors(x.Parent).Count(k => k != x.Parent && candidateParents.Contains(k)))
                .ThenByDescending(x => Ancestors(x.Parent).Count)
                .ThenByDescending(x => x.Distance)
                .ThenBy(x => x.Parent)
                .ThenBy(x => x.TierLevel).ToArray();
        }

        FormKey EnsureBroadPool(IMajorRecordGetter source, EquipmentShape shape, FormKey parent, MaterialSourceProof proof,
            FormKey sibling, int depth, int weight)
        {
            var gate = proof.Level;
            var cacheKey = (source.FormKey, parent, gate, sibling, depth, weight, proof.ControlIdentity);
            if (broadPools.TryGetValue(cacheKey, out var existing)) return existing;
            FormKey previous = parent;
            FormKey deepest = FormKey.Null;
            var prototype = sibling;
            for (var index = 0; index < depth; index++)
            {
                var node = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                var prototypeList = catalog.Lists[prototype];
                var generated = new LeveledItem(node, SkyrimRelease.SkyrimSE)
                {
                    EditorID = $"Broad_{DisplayName(source)}_{gate}_{index + 1}",
                    Flags = prototypeList.Flags,
                    ChanceNone = new Percent(0)
                };
                catalog.AddList(node, generated);
                catalog.Fallbacks.Add(node, new($"broad|{source.FormKey}|{parent}|{gate}|{sibling}|{index + 1}/{depth}", shape.ToString(), generated));
                broadFamilyNodes.Add(node);
                var copies = index == 0 ? Math.Max(1, weight) : 1;
                for (var copy = 0; copy < copies; copy++)
                    if (index == 0)
                        AttachOtherMaterialBranch(source.FormKey, new(new(previous, gate, 1), node, proof.Availability, proof.Unlock), catalog, share: true);
                    else catalog.FallbackEdges.Add(new(new(previous, 1, 1), node));
                previous = deepest = node;
                if (index + 1 < depth)
                    prototype = prototypeList.Entries!.Select(e => e.Data!.Reference.FormKey)
                        .Where(catalog.Lists.ContainsKey).OrderBy(x => x).First();
            }
            broadPools[cacheKey] = deepest;
            return deepest;
        }

        catalog.DistributionNotes.Add($"Enabled origin and leveled-list priority (top first): {string.Join(" → ", providerOrder)}. The first provider with a valid route wins independently for each equivalent enchantment tier.");
        catalog.DistributionNotes.Add("Only enchanted-only branches can receive generated pools. Lists containing unenchanted equipment are never destinations.");
        catalog.DistributionNotes.Add("Template routes from the preferred overhaul are primary. Existing enchanted source-item routes are used only when no selected provider supplies a safe route.");
        catalog.DistributionNotes.Add("Terminal equipment lists remain intact. Generated pools attach at the same selection depth; controlled shared terminals receive a family wrapper that carries their source global or verified quest trigger.");
        catalog.DistributionNotes.Add("A specialized template route may rise through transparent enchanted-only ancestors. Intentional same-handedness subtype groups are accepted only when they already contain the requested subtype.");
        catalog.DistributionNotes.Add("Equivalent templates may supply routing evidence without replacing the chosen generation archetype. After exact routes are exhausted, enabled broad fallback may use a proven same-shape selector.");
        catalog.DistributionNotes.Add("Repeated category children are treated as weights. Generated broad families copy the modal sibling multiplicity so existing siblings retain their relative odds.");
        catalog.DistributionNotes.Add("Distribution follows the selected mode and proven recipients, availability conditions and level windows, without base-game, DLC or new-land partitions.");
        catalog.DistributionNotes.Add("Lists referenced directly by quests are excluded. A general enchanted branch may still feed a quest through an ancestor; the report identifies every modified list for review.");
        catalog.DistributionNotes.Add("Enchantment equivalence ignores names but requires identical unconditional effect records, parameters, behavior fields and item charge. Conditions or different MGEFs remain distinct and require review.");
        if (profile.BroadUnmatchedDistribution)
            catalog.DistributionNotes.Add("Broader unmatched fallback is enabled. Compatible templates require proven source availability and depth-matched enchanted branches. Automatic placement retains source recipient contexts through private copies; explicit General enchanted loot permits broader contexts. Adding a branch changes that category's selection share.");

        using var baselineTiming = PhaseTiming.Measure("plans.baseline");
        var bestAvailableEnchanted = new HashSet<FormKey>();
        var bestAvailableSourceFailures = new Dictionary<FormKey, string>();
        OperationProgress.Stage("Planning enchanted equipment…", GenerationStage.Planning);
        var sourceCount = candidates.Count(c => c.Selected && c.Eligible);
        var sourcesDone = 0;
        foreach (var candidate in candidates.Where(c => c.Selected && c.Eligible).OrderBy(c => c.Plugin, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.FormKey))
        {
            OperationProgress.Record("Planning equipment", ++sourcesDone, sourceCount, candidate.Plugin, candidate.Name);
            if (candidate.SpecializedMode == SpecializedLootMode.ScriptDistribution) continue;
            if (!HasBaseLootRoute(FormKey.Factory(candidate.FormKey), catalog))
            {
                candidate.Selected = false;
                candidate.Reason = "No base-item leveled-list route; enchanted-only distribution is not permitted.";
                catalog.DistributionNotes.Add($"{candidate.Name} [{candidate.FormKey}]: {candidate.Reason}");
                continue;
            }
            if (candidate.RequiresLootReview)
            {
                if (candidate.SpecializedMode == SpecializedLootMode.Disabled)
                {
                    candidate.Reason = "Specialized loot review: distribution disabled";
                    catalog.DistributionNotes.Add($"{candidate.Name} [{candidate.FormKey}]: {candidate.Reason}. {candidate.LootReviewReason}");
                    continue;
                }
                if (candidate.SpecializedMode == SpecializedLootMode.SpecializedEnchantedLists)
                {
                    // Plan isolated specialized edits after baseline graph discovery.
                    continue;
                }
                if (candidate.SpecializedMode == SpecializedLootMode.SpecializedLists)
                {
                    BuildSpecializedPlans(candidate, catalog, profile, plans);
                    continue;
                }
                if (candidate.SpecializedMode == SpecializedLootMode.MerchantFallback)
                {
                    BuildMerchantFallbackPlans(candidate, catalog, profile, plans);
                    continue;
                }
                if (candidate.SpecializedMode == SpecializedLootMode.BestAvailable)
                {
                    var fallbackKey = FormKey.Factory(candidate.FormKey);
                    if ((!catalog.BestAvailableFamilies.TryGetValue(fallbackKey, out var cdfFamily) || cdfFamily == SpecializedLootMode.SpecializedEnchantedLists)
                        && DirectCdfRoutes(fallbackKey, catalog).Length > 0)
                    {
                        catalog.BestAvailableFamilies[fallbackKey] = SpecializedLootMode.SpecializedEnchantedLists;
                        bestAvailableEnchanted.Add(fallbackKey);
                        continue;
                    }
                    var family = catalog.BestAvailableFamilies.TryGetValue(fallbackKey, out var minimumFamily)
                        ? minimumFamily : SpecializedLootMode.SpecializedEnchantedLists;
                    var assessment = AssessSpecializedRoutes(fallbackKey, catalog, profile.AllowOtherMaterialRoutes);
                    if (family == SpecializedLootMode.SpecializedEnchantedLists && assessment.Routes.Count > 0
                        && !SpecializedFamilyIntrinsicallyFull(fallbackKey, assessment, catalog, plans))
                    {
                        catalog.BestAvailableFamilies[fallbackKey] = SpecializedLootMode.SpecializedEnchantedLists;
                        bestAvailableEnchanted.Add(fallbackKey);
                        continue;
                    }
                    if (family == SpecializedLootMode.SpecializedEnchantedLists && assessment.Routes.Count > 0)
                        catalog.DistributionNotes.Add($"{candidate.Name} [{fallbackKey}]: specialized enchanted routes have no remaining authored flat-entry capacity; trying the next Best available family.");
                    if (family != SpecializedLootMode.GeneralLists)
                    {
                        catalog.BestAvailableFamilies[fallbackKey] = SpecializedLootMode.SpecializedLists;
                        var sourceStart = plans.Count;
                        BuildSpecializedPlans(candidate, catalog, profile, plans);
                        if (plans.Skip(sourceStart).Any(p => p.Source.FormKey == fallbackKey && p.Parents.Count > 0)
                            || catalog.AuthoredCounterpartCoverage.Contains(fallbackKey))
                        {
                            candidate.Reason = "Best available — specialized source lists: " + candidate.Reason;
                            continue;
                        }
                        // Source planning allocates only for accepted parents.
                        bestAvailableSourceFailures[fallbackKey] = candidate.Reason;
                        if (plans.Count > sourceStart) plans.RemoveRange(sourceStart, plans.Count - sourceStart);
                    }
                    catalog.BestAvailableFamilies[fallbackKey] = SpecializedLootMode.GeneralLists;
                }
            }
            trace = []; stage = "source-context";
            var key = FormKey.Factory(candidate.FormKey);
            if (catalog.UnsafeAvailability.TryGetValue(key, out var unsafeReason))
            {
                candidate.Reason = "Availability protected: " + unsafeReason;
                catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: {candidate.Reason}");
                continue;
            }
            var generalMixing = candidate.RequiresLootReview
                && candidate.SpecializedMode is SpecializedLootMode.GeneralLists or SpecializedLootMode.BestAvailable;
            MaterialSourceProof[]? placementProofs = null;
            MaterialSourceProof[] PlacementProofs() => placementProofs ??= generalMixing
                ? RelocatedSourceProofs(key, catalog, catalog.DistributionNotes)
                    .Where(p => p.Unlock is null || OrdinaryScriptWindowProblem(key, p.Unlock, catalog) is null).ToArray()
                : MaterialSourceProofs(key, catalog, []).ToArray();
            var controls = SourceGlobalControls(key, catalog);
            var unlocks = catalog.ScriptUnlocks.GetValueOrDefault(key, []);
            if (generalMixing) unlocks = unlocks.Where(u => PlacementProofs().Any(p => p.Unlock == u)).ToList();
            MaterialAvailabilityPath? runtimeAvailability = null;
            short runtimeMinimum = 1;
            var windowProblem = unlocks.Select(unlock => OrdinaryScriptWindowProblem(key, unlock, catalog)).FirstOrDefault(problem => problem is not null);
            if (!generalMixing && windowProblem is null && unlocks.Count > 0)
            {
                // General placement leaves the original receiver ancestry. A
                // complete source proof must establish its inherited restrictions.
                var runtimeProofs = MaterialSourceProofs(key, catalog, catalog.DistributionNotes);
                if (unlocks.Any(unlock => !runtimeProofs.Any(proof => proof.Unlock == unlock)))
                    windowProblem = "no complete runtime source availability path can be preserved outside its original receiver";
                else
                {
                    var paths = runtimeProofs.Where(proof => proof.Unlock is not null).Select(proof => proof.Availability).DistinctBy(path => path.Identity).ToArray();
                    if (paths.Length != 1) windowProblem = "alternative runtime source-control paths require route-specific review";
                    else
                    {
                        runtimeAvailability = paths[0];
                        runtimeMinimum = runtimeProofs.Where(proof => proof.Unlock is not null).Max(proof => proof.Level);
                    }
                }
            }
            if (windowProblem is not null)
            {
                candidate.Reason = "Availability protected: " + windowProblem;
                catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: {candidate.Reason}");
                continue;
            }
            var triggers = unlocks.GroupBy(u => (u.Quest,u.Stage,u.AtStart)).Select(g=>g.OrderByDescending(u=>u.Level).First()).ToArray();
            if (!generalMixing && triggers.Length > 1)
            {
                candidate.Reason = "Availability protected: multiple quest insertion triggers need route-specific review";
                continue;
            }
            IMajorRecordGetter source = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter)catalog.Armors[key];
            var cls = CachedClass(source, catalog);
            var shape = CachedShape(source, catalog);
            var exactAvailable = cls is null ? null : catalog.TemplatesByClass.GetValueOrDefault(ClassKey(cls));
            var broad = profile.BroadUnmatchedDistribution && shape is not null
                && (cls is null || !IsOwnBaseMaterial(cls.Material))
                && (cls is null || exactAvailable is null || exactAvailable.Count == 0);

            TypedLootUse[] PlacementUses() => ParentlessSourceUses(key, catalog);
            // Exact ordinary routes use the existing provenance/consumer proofs.
            // Broad routes use the same proof authority, including compound controls and quest unlocks.
            var ordinarySourceProofs = !broad && controls.Length > 0 && triggers.Length == 0
                ? PlacementProofs().Where(p => p.Unlock is null).ToArray() : null;
            var needsSourceProof = controls.Length > 1 || controls.Length == 1
                && !controls.SequenceEqual(GlobalControlsFrom(key, [key], catalog))
                || ordinarySourceProofs?.Any(p => p.Availability.IsCompound) == true;
            var sourceProofs = generalMixing ? PlacementProofs() : needsSourceProof ? ordinarySourceProofs : null;
            if (sourceProofs is { Length: 0 } || !broad && controls.Length > 1 && sourceProofs is null)
            {
                candidate.Reason = generalMixing ? "Availability protected: no complete source availability/window path can be preserved"
                    : "Availability protected: alternative or nested global controls require a proven common route";
                catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: {candidate.Reason}: {string.Join(", ", controls)}");
                continue;
            }
            var sourceUses = sourceProofs is null ? null : PlacementUses();
            var sourceMatcher = sourceProofs is null ? null : new MaterialSourceMatcher(sourceUses!, sourceProofs);
            var sourceGlobal = controls.Length == 1 ? controls[0] : default;
            var firstEdge = catalog.FallbackEdges.Count;
            var broadAvailabilityEdges = new HashSet<FallbackEdge>();
            var allowDirectSharedItems = controls.Length == 0 && triggers.Length == 0;
            var unrestricted = broad ? catalog.Templates.Where(t => ShapeMatches(shape!, t.Class)).ToList() : exactAvailable;
            // Exact-or-broad is decided before the author's list narrows the templates.
            var available = SourceTemplates(key, unrestricted, catalog);
            if (available is null || available.Count == 0)
            {
                candidate.Reason = unrestricted is { Count: > 0 } ? AuthorListLeavesNoTemplate : "No compatible enchantment templates";
                continue;
            }
            // Items classified by enchanted access are confined to their access destinations. An explicit
            // "General lists" review choice keeps the baseline rules.
            var accessRoots = candidate.RequiresLootReview ? null : catalog.AccessDestinations.GetValueOrDefault(key);
            var confineBroad = !candidate.RequiresLootReview;
            if (accessRoots is not null)
            {
                sourceProofs ??= PlacementProofs();
                sourceUses = PlacementUses();
                sourceMatcher = new MaterialSourceMatcher(sourceUses, sourceProofs);
            }
            Func<FormKey, (MaterialSourceProof? Proof, FormKey[] Path, LootRecipientEdge? Recipient)[]> CreateContextResolver(
                MaterialSourceProof[] proofs, Func<TypedLootUse, MaterialSourceProof, bool>? include = null)
            {
                var uses = PlacementUses();
                var matcher = new MaterialSourceMatcher(uses, proofs);
                var consumers = uses.Select(u => (u.Consumer, u.Role)).ToHashSet();
                var contexts = new Dictionary<FormKey, (MaterialSourceProof? Proof, FormKey[] Path, LootRecipientEdge? Recipient)[]>();
                SourceAuthorisationIndex? index = null;
                return Resolve;
                (MaterialSourceProof? Proof, FormKey[] Path, LootRecipientEdge? Recipient)[] Resolve(FormKey target)
                {
                    if (contexts.TryGetValue(target, out var cached)) return cached;
                    // Every target of this source shares the index's fits and match verdicts.
                    index ??= new SourceAuthorisationIndex(proofs.Length);
                    var results = new (List<FormKey[]> Paths, List<LootRecipientContext> Recipients, List<string> Evidence)[proofs.Length];
                    // Each proof's search reads only the frozen graph, this source's
                    // matcher and its own index slot, so proofs can run in parallel.
                    void Search(int proofIndex)
                    {
                        var proof = proofs[proofIndex];
                        bool Matches(TypedLootUse use) => matcher.Matches(use, proof) && (include?.Invoke(use, proof) ?? true);
                        bool RequiresProof(TypedLootUse use) => consumers.Contains((use.Consumer, use.Role));
                        // The matcher already caches each use's requirement; a per-proof verdict
                        // table cost more in inserts and resizes than the lookups it saved.
                        bool Indexed(TypedLootUse use) => Matches(use);
                        var paths = SpecializedContextPaths(target, proof.Uses, false, catalog, Indexed, RequiresProof,
                            index.BoundaryFits(proofIndex));
                        var evidence = new List<string>();
                        var recipients = LootRecipientContexts(target, Indexed, catalog, paths, evidence);
                        if (CompareAuthorisation)
                        {
                            var searchedPaths = SpecializedContextPaths(target, proof.Uses, false, catalog, Matches, RequiresProof);
                            var searchedEvidence = new List<string>();
                            var searchedRecipients = LootRecipientContexts(target, Matches, catalog, searchedPaths, searchedEvidence);
                            RequireSameSourceContexts(key, target, proof.Identity, paths, recipients, evidence,
                                searchedPaths, searchedRecipients, searchedEvidence);
                        }
                        results[proofIndex] = (paths, recipients, evidence);
                    }
                    if (CompareAuthorisation || proofs.Length < ParallelContextProofs)
                        for (var i = 0; i < proofs.Length; i++) Search(i);
                    else ParallelContextSearch(proofs.Length, Search, catalog);
                    // Report in proof order, exactly as a sequential search would.
                    // The same recipient lines recur for every source and variant; report each once.
                    foreach (var line in results.SelectMany(r => r.Evidence))
                        if (catalog.ReportedRecipientNotes.Add(line)) catalog.DistributionNotes.Add(line);
                    return contexts[target] = results.SelectMany((r, i) =>
                        r.Paths.Select(path => ((MaterialSourceProof?)proofs[i], path, (LootRecipientEdge?)null))
                            .Concat(r.Recipients.Select(c => ((MaterialSourceProof?)proofs[i], c.Path, (LootRecipientEdge?)c.Edge)))).ToArray();
                }
            }
            var exactContexts = sourceProofs is null || generalMixing ? null : CreateContextResolver(sourceProofs);
            Func<FormKey, (MaterialSourceProof? Proof, FormKey[] Path, LootRecipientEdge? Recipient)[]>? broadContextResolver = null;
            (MaterialSourceProof? Proof, FormKey[] Path, LootRecipientEdge? Recipient)[] SourceContexts(FormKey target)
                => generalMixing ? PlacementProofs().Select(p => ((MaterialSourceProof?)p, new[] { target }, (LootRecipientEdge?)null)).ToArray()
                    : exactContexts?.Invoke(target) ?? [(null, [target], null)];
            (short Minimum, int Maximum) PlacementWindow(MaterialSourceProof proof, short templateMinimum, FormKey sibling)
                => (Math.Max(templateMinimum, proof.Level), Math.Min(proof.Availability.MaximumLevel,
                    sibling.IsNull ? ushort.MaxValue : SelectionEntryMaximum(catalog.Lists[sibling], templateMinimum)));
            var destinationProofs = new Dictionary<FormKey, MaterialSourceProof[]>();
            progress?.Invoke($"{candidate.Plugin}: {candidate.Name}");
            var inventory = catalog.SourceEquipment!.GetValueOrDefault(key, []);
            var existing = inventory.Where(i => TryEnchantment(i, out _) && OrdinaryEquipment(i, catalog.KeywordNames))
                .GroupBy(i => { TryEnchantment(i, out var e); return VariantIdentity(i, e, catalog); })
                .ToDictionary(g => g.Key, g => g.OrderByDescending(i => catalog.DistributedItems.Contains(i.FormKey)).ThenBy(i => i.FormKey).ToArray());
            var subtype = DistributionSubtype(source, catalog);
            var enchantedContexts = cls is null || subtype is null ? [] : inventory.Where(i => OrdinaryEquipment(i, catalog.KeywordNames) && TryEnchantment(i, out _))
                .SelectMany(i => catalog.DirectParents.GetValueOrDefault(i.FormKey, []).Select(p => (Item: i, Parent: p)))
                .Where(x => SafeContext(x.Parent) && CompatibleEnchantedBranch(x.Parent.List, cls, subtype, true))
                .SelectMany(x => SourceRoutes(x.Parent, allowDirectSharedItems).Where(p => CompatibleEnchantedBranch(p.List, cls, subtype, true)).Select(p => (x.Item, Parent: p))).ToArray();
            var sourceTemplates = (broad ? TemplatesForBroadSource(source, available, catalog) : TemplatesForExactSource(source, available, catalog))
                .GroupBy(t => VariantIdentity(t.Item, t.Enchantment, catalog)).ToArray();
            var sourceEvidence = trace.ToArray();
            var starting = plans.Count;
            List<MaterialSourceProof>? parentlessProofs = null;
            TypedLootUse[]? parentlessUses = null; MaterialSourceMatcher? parentlessMatcher = null;
            // The answer depends on the target, its flags, the tier level and the
            // candidate's fixed proofs/uses: memoised per candidate.
            Dictionary<(FormKey Target, short Level, LeveledItem.Flag Flags), (short? Level, string? Problem)>? parentlessLevels = null;
            var parentlessConsumers = new Dictionary<FormKey, string?>();
            // Template groups of one source repeat the same direct-level questions;
            // the answer depends only on these inputs and this source's fixed proofs.
            var directLevels = new Dictionary<(FormKey Target, FormKey Endpoint, FormKey Consumer, string? Role, string Proof, short Level), (short? Level, string? Problem)>();
            // Its destination/proof half does not read the target or tier level.
            var directFloors = new Dictionary<(FormKey Endpoint, FormKey Consumer, string? Role, string Proof), (short? Floor, string? Problem)>();
            Dictionary<FormKey, (short? Floor, string? Problem)>? parentlessFloors = null;
            var templatesByVariant = IndexTemplatesByVariant(available, catalog);
            foreach (var templateGroup in sourceTemplates)
            {
                trace = new(sourceEvidence); stage = "variant";
                var equivalentTemplates = templateGroup.ToArray();
                var routeTemplates = templatesByVariant[templateGroup.Key]
                    .OrderBy(TemplateRank).ThenBy(t => t.Item.FormKey).ToArray();
                var generationTemplate = equivalentTemplates.OrderBy(TemplateRank)
                    .ThenBy(t => PluginName(t.Enchantment.ModKey, catalog), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Item.FormKey).First();
                foreach (var t in equivalentTemplates.Where(t => t.Parents.Count == 0))
                    Reject("TemplateHasNoParents", t.Item.FormKey, t.Item.EditorID ?? "");
                var template = generationTemplate;
                var identity = VariantIdentity(template.Item, template.Enchantment, catalog);
                var matching = existing.GetValueOrDefault(identity, []);
                var reused = matching.FirstOrDefault();
                // Existing equivalents anywhere in a common ancestor suppress another
                // path into that tree. Independent merchant/container trees remain valid.
                var occupied = new HashSet<FormKey>();
                foreach (var old in matching)
                    foreach (var parent in catalog.DirectParents.GetValueOrDefault(old.FormKey, [])) occupied.UnionWith(CdfCoveredAncestors(old.FormKey, Ancestors(parent.List), catalog));
                // Effective recipient coverage retains the consumer role; death-only stock
                // cannot satisfy a living inventory or outfit. Compute it once, on first use.
                HashSet<(FormKey Consumer, string Role)>? recipientCoverage = null;
                bool RecipientCovered(LootRecipientEdge? recipient) => recipient is not null
                    && (recipientCoverage ??= CdfLootRecipientCoverage(matching.Select(i => i.FormKey), catalog)).Contains((recipient.Consumer, recipient.Role));
                var plannedRecipientContexts = new Dictionary<(FormKey Consumer, string Role, string Controls), List<(int Minimum, int Maximum)>>();
                bool ContextCovered(FormKey consumer, string role, MaterialSourceProof proof, int minimum, int maximum)
                    => plannedRecipientContexts.TryGetValue((consumer, role, proof.ControlIdentity), out var windows)
                        && windows.Any(w => w.Minimum <= minimum && w.Maximum >= maximum);
                IEnumerable<(FormKey Consumer, string Role)> ContextRecipients(FormKey[] path, LootRecipientEdge? recipient)
                    => recipient is not null ? [(recipient.Consumer, recipient.Role)]
                        : SpecializedGraph(catalog).Endpoints(path[^1]).Select(u => (u.Consumer, u.Role)).Distinct();
                void CoverRecipients(FormKey[] path, LootRecipientEdge? recipient, MaterialSourceProof proof, int minimum, int maximum)
                {
                    // A source proof does not describe destination gates or ancestor windows.
                    // Reuse native availability proofs, and reserve only a positively proven
                    // unconditional all-levels single-node route. Highest-eligible edges
                    // and other isolation paths remain independent.
                    if (path.Length != 1 || ((int)catalog.Lists[path[0]].Flags & 1) == 0) return;
                    if (!destinationProofs.TryGetValue(path[0], out var native))
                        destinationProofs[path[0]] = native = MaterialSourceProofs(path[0], catalog, [])
                            .Where(p => p.Unlock is null && p.Availability.Controls.Length == 0).ToArray();
                    foreach (var (consumer, role) in ContextRecipients(path, recipient))
                    {
                        var starts = native.Where(p => p.Uses.Any(u => u.Consumer == consumer && u.Role == role)).Select(p => p.Level).ToArray();
                        if (starts.Length == 0) continue;
                        var effectiveMinimum = Math.Max(minimum, starts.Min());
                        var context = (consumer, role, proof.ControlIdentity);
                        if (!plannedRecipientContexts.TryGetValue(context, out var windows)) plannedRecipientContexts[context] = windows = [];
                        if (!ContextCovered(consumer, role, proof, effectiveMinimum, maximum)) windows.Add((effectiveMinimum, maximum));
                    }
                }
                (MaterialSourceProof? Proof, FormKey[] Path, LootRecipientEdge? Recipient)[] UncoveredSourceContexts(PoolRoute route)
                    => !generalMixing && sourceProofs is not null && plannedRecipientContexts.Count > 0
                        // A fresh resolver keeps its fits/index cache scoped to this
                        // coverage snapshot; partial overlaps use the existing isolation search.
                        ? CreateContextResolver(sourceProofs, (u, p) =>
                        {
                            var window = PlacementWindow(p, route.ItemLevel, route.SiblingOf);
                            return !ContextCovered(u.Consumer, u.Role, p, window.Minimum, window.Maximum);
                        })(route.Parent.List)
                        : SourceContexts(route.Parent.List);
                var broadContextCache = new Dictionary<BroadRoute, (MaterialSourceProof Proof, FormKey[] Path, LootRecipientEdge? Recipient)[]>();
                var broadCoverage = new Dictionary<string, HashSet<FormKey>>();
                var parentTemplates = new Dictionary<FormKey, EnchantedTemplate>();
                (MaterialSourceProof Proof, FormKey[] Path, LootRecipientEdge? Recipient)[] BroadContexts(BroadRoute route)
                    => broadContextCache.TryGetValue(route, out var cached) ? cached : broadContextCache[route] = AuthorizeBroad(route);
                (MaterialSourceProof Proof, FormKey[] Path, LootRecipientEdge? Recipient)[] AuthorizeBroad(BroadRoute route, bool uncoveredOnly = false)
                {
                    if (confineBroad && accessRoots is not null && !Ancestors(route.Parent).Overlaps(accessRoots))
                    {
                        Reject("OutsideEnchantedAccess", route.Parent, "No carrier group of the source item receives this enchanted list");
                        return [];
                    }
                    var contexts = confineBroad
                        ? uncoveredOnly
                            ? CreateContextResolver(PlacementProofs(), (u, p) =>
                            {
                                var window = PlacementWindow(p, route.TierLevel, route.SiblingOf);
                                return !ContextCovered(u.Consumer, u.Role, p, window.Minimum, window.Maximum);
                            })(route.Parent)
                            : (broadContextResolver ??= CreateContextResolver(PlacementProofs()))(route.Parent)
                        : PlacementProofs().Select(p => ((MaterialSourceProof?)p, new[] { route.Parent }, (LootRecipientEdge?)null)).ToArray();
                    var accepted = new List<(MaterialSourceProof, FormKey[], LootRecipientEdge?)>();
                    foreach (var (proof, path, recipient) in contexts)
                    {
                        if (proof is null) continue;
                        var (level, maximum) = PlacementWindow(proof, route.TierLevel, route.SiblingOf);
                        if (level > maximum)
                        {
                            Reject("SourceOutsideTemplateTierWindow", route.SiblingOf,
                                $"Source/template minimum {level} is beyond the permitted maximum {maximum}");
                            continue;
                        }
                        accepted.Add((proof, path, recipient));
                    }
                    if (accepted.Count == 0)
                        Reject("SourceAvailabilityContextUnproved", route.Parent, "No source proof preserves this destination's controls, window and consumers");
                    return accepted.ToArray();
                }
                BroadRoute[] AuthorizedBroadRoutes(IEnumerable<BroadRoute> routes)
                {
                    var preferred = PreferredBroadRoutes(routes.Where(r => AuthorAllowsBroad(r) && BroadContexts(r).Length > 0));
                    // Explicit general mixing chooses its preferred compatible stock;
                    // automatic routes retain independent source recipient contexts.
                    return confineBroad ? preferred : preferred.Take(1).ToArray();
                }
                bool AuthorAllowsBroad(BroadRoute route)
                {
                    if (AuthorAllowsList(key, route.Parent, catalog)) return true;
                    Reject("OutsideAuthorDestinations", route.Parent, OutsideAuthorDestinations);
                    return false;
                }
                bool AttachBroad(BroadRoute route, List<ParentEntry> destinations)
                {
                    var attached = false;
                    var contexts = confineBroad && plannedRecipientContexts.Count > 0
                        ? AuthorizeBroad(route, uncoveredOnly: true) : BroadContexts(route);
                    foreach (var (proof, path, recipient) in contexts.OrderBy(c => c.Proof.Level))
                    {
                        var window = PlacementWindow(proof, route.TierLevel, route.SiblingOf);
                        if (catalog.CdfInventory?.Removals.Any == true && !CdfOutputAvailable(key, route.Template,
                            SpecializedGraph(catalog).Endpoints(path[^1]).Where(u => recipient is null
                                || u.Consumer == recipient.Consumer && u.Role == recipient.Role), catalog, identity)) continue;
                        var context = recipient is null ? Ancestors(path[^1]) : [];
                        if (SetsOverlap(context, occupied) || RecipientCovered(recipient)
                            || broadCoverage.TryGetValue(proof.ControlIdentity, out var covered) && SetsOverlap(context, covered)
                            || recipient is not null && ContextCovered(recipient.Consumer, recipient.Role, proof, window.Minimum, window.Maximum))
                        { Reject("EquivalentAlreadyInAncestorTree", route.Parent); continue; }
                        var target = recipient is not null ? LootRecipientCopy(path, recipient, candidate.Category, catalog)
                            : path.Length > 1 ? IsolatedCopy(path, candidate.Category, catalog) : route.Parent;
                        var edgeStart = catalog.FallbackEdges.Count;
                        var pool = EnsureBroadPool(source, shape!, target, proof, route.SiblingOf, route.BranchDepth, route.Weight);
                        broadAvailabilityEdges.UnionWith(catalog.FallbackEdges.Skip(edgeStart));
                        destinations.Add(new(pool, Math.Max(route.TierLevel, proof.Level), 1));
                        parentTemplates.TryAdd(pool, route.Template);
                        // Reserve only successful placement, separately for independent controls/unlocks.
                        if (!broadCoverage.TryGetValue(proof.ControlIdentity, out var placed)) broadCoverage[proof.ControlIdentity] = placed = [];
                        placed.UnionWith(context);
                        CoverRecipients(path, recipient, proof, window.Minimum, window.Maximum);
                        attached = true;
                    }
                    return attached;
                }
                if (broad)
                {
                    var routes = routeTemplates.SelectMany(t => BroadRoutes(t, shape!)).Where(AuthorAllowsBroad).ToArray();
                    var preferred = AuthorizedBroadRoutes(routes);
                    var broadTemplate = generationTemplate;
                    var broadIdentity = VariantIdentity(broadTemplate.Item, broadTemplate.Enchantment, catalog);
                    var broadMatching = existing.GetValueOrDefault(broadIdentity, []);
                    var broadReused = broadMatching.FirstOrDefault();
                    var broadTargets = new List<ParentEntry>();
                    var usedBroadRoutes = new List<BroadRoute>();
                    foreach (var route in preferred)
                        if (AttachBroad(route, broadTargets)) usedBroadRoutes.Add(route);
                    var broadReason = preferred.Length == 0
                        ? "No safe generalized enchanted parent with a coherent child depth"
                        : broadTargets.Count == 0 ? "Equivalent variant already reachable in the distribution tree"
                        : $"Broad unmatched routes via {string.Join(", ", usedBroadRoutes.Select(x => x.Provider.FileName.String).Distinct(StringComparer.OrdinalIgnoreCase))}; source availability and recipient contexts proved";
                    plans.Add(new(candidate, source, broadTemplate, broadTargets, broadReused, broadReason, broadTargets.Count == 0 ? trace.Order().ToArray() : [], ParentTemplates: parentTemplates));
                    continue;
                }
                var exactClass = cls!;
                var rankedRoutes = new List<RankedTemplateRoute>();
                if (subtype is not null)
                    foreach (var routeTemplate in routeTemplates)
                        foreach (var seed in routeTemplate.Parents)
                            foreach (var route in TemplateRoutes(seed, exactClass, subtype, allowDirectSharedItems))
                            {
                                var checkedRoute = route;
                                if (accessRoots is null && catalog.DirectParents.GetValueOrDefault(route.Parent.List, []).Count == 0)
                                {
                                    if (!parentlessConsumers.TryGetValue(route.Parent.List, out var problem))
                                        parentlessConsumers[route.Parent.List] = problem = ParentlessConsumerProblem(key, route.Parent.List, catalog);
                                    if (problem is not null) { Reject("ParentlessConsumerConflict", route.Parent.List, problem); continue; }
                                    if (route.DirectItemPlacement && !generalMixing && !IsJewelryType(exactClass.Type))
                                    {
                                        parentlessProofs ??= MaterialSourceProofs(key, catalog, []);
                                        // The source's uses and matcher are fixed with its proofs; build them once per candidate.
                                        parentlessUses ??= ParentlessSourceUses(key, catalog);
                                        parentlessMatcher ??= new MaterialSourceMatcher(parentlessUses, parentlessProofs);
                                        parentlessLevels ??= [];
                                        var levelKey = (route.Parent.List, route.ItemLevel, catalog.Lists[route.Parent.List].Flags);
                                        if (!parentlessLevels.TryGetValue(levelKey, out var gate))
                                        {
                                            parentlessFloors ??= [];
                                            if (!parentlessFloors.TryGetValue(route.Parent.List, out var floor))
                                                parentlessFloors[route.Parent.List] = floor = ParentlessDirectFloor(parentlessProofs,
                                                    SpecializedGraph(catalog).Endpoints(route.Parent.List), parentlessUses, parentlessMatcher);
                                            parentlessLevels[levelKey] = gate = ParentlessDirectLevelAt(floor, route.Parent.List, route.ItemLevel, catalog);
                                        }
                                        if (gate.Level is not { } level) { Reject("ParentlessSourceAvailability", route.Parent.List, gate.Problem!); continue; }
                                        checkedRoute = route with { ItemLevel = level, Parent = route.Parent with { Level = level } };
                                    }
                                }
                                var ranked = RouteRank(routeTemplate, seed, checkedRoute.Parent);
                                rankedRoutes.Add(new(routeTemplate, checkedRoute, ranked.Rank, ranked.Provider));
                            }
                var compatibleRoutes = rankedRoutes.ToArray();
                // Enchanted access: only the enchanted lists the source item's own carriers already receive.
                if (accessRoots is not null)
                {
                    var outsideAccess = compatibleRoutes.Where(x => !Ancestors(x.Route.Parent.List).Overlaps(accessRoots)).ToArray();
                    foreach (var route in outsideAccess.DistinctBy(x => x.Route.Parent.List))
                        Reject("OutsideEnchantedAccess", route.Route.Parent.List, "No carrier group of the source item receives this enchanted list");
                    compatibleRoutes = compatibleRoutes.Except(outsideAccess).ToArray();
                }
                // Mod-author destinations narrow accepted routes before any copy is made.
                if (HasAuthorDestinations(key, catalog))
                {
                    var outsideAuthor = compatibleRoutes.Where(x => !AuthorAllowsList(key, x.Route.Parent.List, catalog)).ToArray();
                    foreach (var route in outsideAuthor.DistinctBy(x => x.Route.Parent.List))
                        Reject("OutsideAuthorDestinations", route.Route.Parent.List, OutsideAuthorDestinations);
                    compatibleRoutes = compatibleRoutes.Except(outsideAuthor).ToArray();
                }
                // Provider priority is tried before coverage, rather than discarding
                // lower-ranked routes that may be needed by an independent context.
                var preferredRoutes = compatibleRoutes.OrderBy(x => x.Rank)
                    .ThenBy(x => x.Parent.List).ThenBy(x => x.Parent.Level)
                    .DistinctBy(x => (x.Route, x.Template.Item.FormKey)).ToArray();
                if (generalMixing && preferredRoutes.Length > 0)
                {
                    var bestRank = preferredRoutes[0].Rank;
                    preferredRoutes = preferredRoutes.Where(x => x.Rank == bestRank).ToArray();
                }
                var routeRanks = preferredRoutes.GroupBy(x => x.Route).ToDictionary(g => g.Key, g => g.Min(x => x.Rank));
                var targets = new List<ParentEntry>();
                var sourceRouteWithheld = false;
                var sourceSeeds = enchantedContexts.Where(x => source is IWeaponGetter
                        ? CachedCharge(x.Item, catalog) == CachedCharge(template.Item, catalog)
                        : TryEnchantment(x.Item, out var e) && EffectIdentity(e, catalog) == EffectIdentity(template.Enchantment, catalog)).ToArray();
                var options = preferredRoutes.Select(x => x.Route).Distinct().ToList();
                var provingTemplates = preferredRoutes.GroupBy(x => x.Route)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.Template).DistinctBy(t => t.Item.FormKey).ToList());
                var usedProvider = preferredRoutes.FirstOrDefault()?.Provider;
                if (options.Count == 0)
                {
                    var lowerBound = equivalentTemplates.SelectMany(t => t.Parents).Select(p => p.Level).DefaultIfEmpty((short)1).Min();
                    foreach (var seed in sourceSeeds)
                    {
                        TryEnchantment(seed.Item, out var contextEffect);
                        // This fallback is proved by an existing enchanted source item.
                        // Its effect need not equal the generation effect for weapons.
                        var witness = new EnchantedTemplate { Item = seed.Item, BaseItem = source,
                            Class = CachedClass(seed.Item, catalog) ?? exactClass, Enchantment = contextEffect,
                            EnchantmentName = catalog.EnchantmentNames.GetValueOrDefault(contextEffect, contextEffect.ToString()), Parents = [seed.Parent] };
                        foreach (var route in PoolRoutes(seed.Parent with { Level = Math.Max(seed.Parent.Level, lowerBound) }, allowDirectSharedItems, exactClass, subtype))
                        {
                            if (!options.Contains(route)) options.Add(route);
                            if (!provingTemplates.TryGetValue(route, out var witnesses)) provingTemplates[route] = witnesses = [];
                            if (!witnesses.Any(t => t.Item.FormKey == witness.Item.FormKey)) witnesses.Add(witness);
                        }
                    }
                    foreach (var route in options.Where(r => !AuthorAllowsList(key, r.Parent.List, catalog)).DistinctBy(r => r.Parent.List).ToArray())
                        Reject("OutsideAuthorDestinations", route.Parent.List, OutsideAuthorDestinations);
                    options.RemoveAll(r => !AuthorAllowsList(key, r.Parent.List, catalog));
                }
                var usedBroadFallback = false;
                if (options.Count == 0 && profile.BroadUnmatchedDistribution && shape is not null
                    && !IsOwnBaseMaterial(exactClass.Material))
                {
                    var routes = routeTemplates.SelectMany(t => BroadRoutes(t, shape)).ToArray();
                    foreach (var route in AuthorizedBroadRoutes(routes))
                        if (AttachBroad(route, targets))
                        {
                            usedProvider = route.Provider;
                            usedBroadFallback = true;
                        }
                }
                if (subtype is not null)
                {
                    foreach (var route in options.OrderBy(p => routeRanks.GetValueOrDefault(p, int.MaxValue))
                                 .ThenByDescending(p => Ancestors(p.Parent.List).Count).ThenBy(p => p.Parent.List).ThenBy(p => p.Parent.Level).DistinctBy(p => p.Parent.List))
                    {
                        var target = route.Parent;
                        var witnesses = provingTemplates.GetValueOrDefault(route, [template]);
                        var parentless = catalog.DirectParents.GetValueOrDefault(target.List, []).Count == 0;
                        if (parentless && !parentlessConsumers.ContainsKey(target.List))
                            parentlessConsumers[target.List] = ParentlessConsumerProblem(key, target.List, catalog);
                        if (accessRoots is null && parentless && parentlessConsumers[target.List] is { } conflict)
                        { Reject("ParentlessConsumerConflict", target.List, conflict); continue; }
                        if (parentless && route.DirectItemPlacement)
                        {
                            var copies = catalog.SpecializedCloneOriginals.ToLookup(p => p.Value, p => p.Key);
                            var affected = InheritedCopyTargets(target.List, copies, catalog).Prepend(target.List).ToHashSet();
                            if (EffectiveStaticEntries.Reserve(catalog, plans).Reservations.Any(r => affected.Contains(r.List) && r.ProposedEntries >= 255)
                                || affected.Any(t => (catalog.Lists[t].Entries?.Count ?? 0) >= 255))
                            { Reject("ParentlessDirectCapacity", target.List, "No flat peer position remains without introducing overflow sublists"); continue; }
                        }
                        var contexts = UncoveredSourceContexts(route);
                        if (contexts.Length == 0)
                        {
                            sourceRouteWithheld = true;
                            Reject("SourceAvailabilityContextUnproved", target.List, "No source proof preserves this destination's controls and consumers");
                        }
                        foreach (var (proof, path, recipient) in contexts)
                        {
                            var recipients = catalog.CdfInventory?.Removals.Any == true
                                ? SpecializedGraph(catalog).Endpoints(path[^1]).Where(u => recipient is null
                                    || u.Consumer == recipient.Consumer && u.Role == recipient.Role)
                                : Enumerable.Empty<TypedLootUse>();
                            var provingTemplate = catalog.CdfInventory?.Removals.Any != true ? witnesses[0]
                                : witnesses.FirstOrDefault(w => CdfOutputAvailable(key, w, recipients, catalog, identity, reportRefusal: false));
                            if (provingTemplate is null)
                            {
                                CdfOutputAvailable(key, witnesses[0], recipients, catalog, identity);
                                sourceRouteWithheld = true; Reject("CdfRemoval", target.List, "Source/counterpart survival is unproved after native removals"); continue;
                            }
                            var (itemLevel, maximum) = proof is null ? (Math.Max(route.ItemLevel, (short)1), (int)ushort.MaxValue)
                                : PlacementWindow(proof, route.ItemLevel, route.SiblingOf);
                            if (proof is not null && itemLevel > maximum)
                            {
                                sourceRouteWithheld = true;
                                var windowRefusal = $"source and enchantment level windows do not overlap (minimum {itemLevel}, maximum {maximum})";
                                Reject("SourceOutsideTemplateTierWindow", route.SiblingOf, windowRefusal);
                                catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: {windowRefusal}");
                                continue;
                            }
                            if (generalMixing && proof is not null && route.DirectItemPlacement
                                && (proof.Availability.Controls.Length > 0 || proof.Unlock is not null))
                            { sourceRouteWithheld = true; Reject("DirectSourceAvailabilityUnproved", target.List, "A direct equipment position cannot carry the required source controls/window"); continue; }
                            if (proof is not null && route.DirectItemPlacement && !generalMixing)
                            {
                                var directKey = (target.List, path[^1], recipient?.Consumer ?? FormKey.Null, recipient?.Role, proof.Identity, route.ItemLevel);
                                if (!directLevels.TryGetValue(directKey, out var direct))
                                {
                                    var floorKey = (path[^1], recipient?.Consumer ?? FormKey.Null, recipient?.Role, proof.Identity);
                                    if (!directFloors.TryGetValue(floorKey, out var floor))
                                    {
                                        var destinations = recipient is null ? SpecializedGraph(catalog).Endpoints(path[^1])
                                            : SpecializedGraph(catalog).Uses.GetValueOrDefault(path[^1], []).Where(u =>
                                                u.Consumer == recipient.Consumer && u.Role == recipient.Role).ToArray();
                                        // As ParentlessDirectLevel: absent source uses or matcher are built for this proof.
                                        var floorUses = sourceUses ?? ParentlessSourceUses(key, catalog);
                                        directFloors[floorKey] = floor = ParentlessDirectFloor([proof], destinations, floorUses,
                                            sourceMatcher ?? new MaterialSourceMatcher(floorUses, [proof]));
                                    }
                                    directLevels[directKey] = direct = ParentlessDirectLevelAt(floor, target.List, route.ItemLevel, catalog);
                                }
                                if (direct.Level is not { } directLevel)
                                { sourceRouteWithheld = true; Reject("DirectSourceAvailabilityUnproved", target.List, direct.Problem!); continue; }
                                itemLevel = directLevel;
                            }
                            var context = recipient is null ? Ancestors(path[^1]) : [];
                            if (SetsOverlap(context, occupied) || RecipientCovered(recipient)
                                || generalMixing && proof is not null && broadCoverage.TryGetValue(proof.ControlIdentity, out var covered) && SetsOverlap(context, covered)
                                || recipient is not null && proof is not null && ContextCovered(recipient.Consumer, recipient.Role, proof, itemLevel, maximum)) { Reject("EquivalentAlreadyInAncestorTree", target.List); continue; }
                            if (generalMixing && proof is not null)
                            {
                                if (!broadCoverage.TryGetValue(proof.ControlIdentity, out var placed)) broadCoverage[proof.ControlIdentity] = placed = [];
                                placed.UnionWith(context);
                            }
                            else occupied.UnionWith(context);
                            if (proof is not null) CoverRecipients(path, recipient, proof, Math.Max(itemLevel, target.Level), maximum);
                            target = route.Parent with { List = recipient is not null ? LootRecipientCopy(path, recipient, candidate.Category, catalog)
                                : path.Length > 1 ? IsolatedCopy(path, candidate.Category, catalog) : route.Parent.List };
                            if (route.DirectItemPlacement)
                            {
                                targets.Add(new(target.List, itemLevel, 1));
                                parentTemplates.TryAdd(target.List, provingTemplate);
                                if (parentless) catalog.ShapePreservingDirectPlacements.Add((key, identity, target.List));
                                continue;
                            }
                            // On an all-levels target the source minimum can sit on this target's own
                            // edge: the window max(edge, entry) is unchanged, and proofs that differ
                            // only by minimum level share one pool. A highest-eligible target keeps the
                            // minimum on the variant entry, where raising an edge would change siblings.
                            var levelOnEdge = proof is not null && ((int)catalog.Lists[target.List].Flags & 1) != 0;
                            var entryLevel = levelOnEdge ? (short)Math.Max(route.ItemLevel, (short)1) : itemLevel;
                            var edgeLevel = proof is null ? target.Level
                                : (short)Math.Max(Math.Max(target.Level, proof.Unlock?.Level ?? target.Level), levelOnEdge ? proof.Level : target.Level);
                            var poolIdentity = (key, route.Parent.List, target.Level, route.SiblingOf,
                                proof is null ? "" : levelOnEdge ? "minimum-on-edge|" + proof.ControlIdentity : proof.Identity);
                            if (!pools.TryGetValue(poolIdentity, out var pool))
                            {
                                pool = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                                var original = new LeveledItem(pool, SkyrimRelease.SkyrimSE)
                                { EditorID = $"Variants_{DisplayName(source)}_{target.Level}",
                                    Flags = route.SiblingOf.IsNull ? (LeveledItem.Flag)3 : catalog.Lists[route.SiblingOf].Flags,
                                    ChanceNone = new Percent(0) };
                                catalog.AddList(pool, original);
                                catalog.Fallbacks.Add(pool, new($"source-pool|{key}|{route.Parent.List}|{target.Level}|sibling={route.SiblingOf}|availability={poolIdentity.Item5}", exactClass.ToString(), original));
                                pools.Add(poolIdentity, pool);
                            }
                            if (attachedPools.Add((pool, target.List, edgeLevel)))
                            {
                                if (proof is null) catalog.FallbackEdges.Add(new(target, pool));
                                else AttachOtherMaterialBranch(key, new(target with { Level = edgeLevel }, pool, proof.Availability, proof.Unlock), catalog, share: true);
                            }
                            var poolEntry = new ParentEntry(pool, entryLevel, 1);
                            if (!targets.Contains(poolEntry)) targets.Add(poolEntry);
                            parentTemplates.TryAdd(pool, provingTemplate);
                        }
                    }
                }
                var reason = subtype is null
                    ? "Unknown or conflicting subtype"
                    : targets.Count == 0 ? options.Count == 0 ? "No safe route in a selected distribution provider or existing enchanted source structure"
                        : sourceRouteWithheld ? "No proven source availability route for the destination's consumers"
                        : "Equivalent variant already reachable in the distribution tree"
                    : usedBroadFallback && usedProvider is { } broadProvider ? $"Broad fallback after exact-route exhaustion via {broadProvider.FileName.String}"
                    : usedProvider is { } provider ? $"Preferred distribution provider: {provider.FileName.String}"
                    : "Existing enchanted source-item route fallback";
                if (subtype is null) Reject("UnknownSourceSubtype", key);
                plans.Add(new(candidate, source, template, targets, reused, reason, targets.Count == 0 ? trace.Order().ToArray() : [], ParentTemplates: parentTemplates));
            }
            if (sourceProofs is null)
            {
                var runtimeUnlock = triggers.SingleOrDefault();
                if (runtimeUnlock is not null && runtimeAvailability is not null)
                    runtimeUnlock = runtimeUnlock with { Level = Math.Max(runtimeUnlock.Level, runtimeMinimum) };
                ProtectSourceEdges(key, sourceGlobal, runtimeUnlock, firstEdge, catalog, broadAvailabilityEdges, runtimeAvailability);
            }
            BuildOrdinarySpecialLootPlans(candidate, catalog, profile, plans);
            var own = plans.Skip(starting).ToArray();
            candidate.Reason = own.Length == 0 ? "No compatible enchantments" : own.All(p => p.Parents.Count == 0 && p.Reused is null)
                ? "No variants written: no proven enchanted route, unknown subtype, or protected context"
                : $"{own.Count(p => p.Reused is null && p.Parents.Count > 0)} new; {own.Count(p => p.Reused is not null)} reused; {own.Count(p => p.Parents.Count > 0)} placements planned";
            if (HasAuthorDestinations(key, catalog) && !own.Any(p => p.Parents.Count > 0))
                candidate.Reason += " (the mod author's destination list for this item excludes the other accepted routes)";
            if (candidate.RequiresLootReview && candidate.SpecializedMode == SpecializedLootMode.BestAvailable)
                candidate.Reason = own.Any(p => p.Parents.Count > 0)
                    ? "Best available — general enchanted loot: " + candidate.Reason
                    : "Best available exhausted: specialized enchanted lists had no accepted route; source lists: "
                      + bestAvailableSourceFailures.GetValueOrDefault(key, "no supported source route")
                      + "; general enchanted loot: " + candidate.Reason;
        }
        baselineTiming?.Dispose();
        if (candidates.Any(c => c.Selected && c.Eligible && c.RequiresLootReview
                && (c.SpecializedMode == SpecializedLootMode.GeneralLists
                    || c.SpecializedMode == SpecializedLootMode.BestAvailable
                    && c.Reason.StartsWith("Best available — general enchanted loot", StringComparison.Ordinal))))
            catalog.DistributionNotes.Add("EXPERT general-loot route in use: General enchanted loot ignores where each base item appears. Its variants can reach consumers that never receive the ordinary item. Best available uses this route only after specialized enchanted and source-list routes fail.");
        foreach (var candidate in candidates.Where(c => c.Selected && c.Eligible && c.RequiresLootReview
                     && (c.SpecializedMode == SpecializedLootMode.SpecializedEnchantedLists
                         || bestAvailableEnchanted.Contains(FormKey.Factory(c.FormKey)))))
            BuildSpecializedEnchantedPlans(candidate, catalog, profile, plans);
        // Finish ordinary context copies and selector edges for every item before
        // expansion moves any of those contents into a preserved material family.
        if (profile.AllowOtherMaterialRoutes)
            foreach (var candidate in candidates.Where(c => c.Selected && c.Eligible && c.RequiresLootReview
                         && (c.SpecializedMode == SpecializedLootMode.SpecializedEnchantedLists
                             || bestAvailableEnchanted.Contains(FormKey.Factory(c.FormKey)))))
                BuildSpecializedMaterialPlans(candidate, catalog, profile, plans);
        foreach (var candidate in candidates.Where(c => bestAvailableEnchanted.Contains(FormKey.Factory(c.FormKey))))
            candidate.Reason = "Best available — specialized enchanted lists: " + candidate.Reason;
        OperationProgress.Stage("Finalizing distribution plans…", GenerationStage.FinalizingPlans);
        FinalizeCountedMerchantPlans(catalog, profile, plans);
        BuildScriptDistributionPlans(candidates, catalog, profile, plans);
        BuildCdfDirectPlans(candidates, catalog, profile, plans);
        // The source floor can precede every selected enchantment tier. Defer all
        // owned source-family levels using the same policy as material expansion.
        if (pools.Count > 0 || broadFamilyNodes.Count > 0)
            DeferGeneratedFamilyMinima(pools.Values.Concat(broadFamilyNodes), plans.SelectMany(p => p.Parents), catalog, "Source distribution");
        // Once a flat category has been split into material families, existing
        // same-material additions belong inside the preserved family. Normalize
        // before preview/counting and writing so all three see identical targets.
        var materialCopies = catalog.SpecializedCloneOriginals.ToLookup(x => x.Value, x => x.Key);
        for (var i = 0; i < plans.Count; i++)
            plans[i] = plans[i] with { Parents = plans[i].Parents.Select(parent =>
                parent with { List = MaterialContents(parent.List, catalog) }).ToArray(),
                // Proving templates follow their parents into the preserved family.
                ParentTemplates = plans[i].ParentTemplates?.GroupBy(p => MaterialContents(p.Key, catalog))
                    .ToDictionary(g => g.Key, g => g.First().Value) };
        var directPlacements = catalog.ShapePreservingDirectPlacements.Select(p =>
            (p.Source, p.Variant, Target: MaterialContents(p.Target, catalog))).ToArray();
        catalog.ShapePreservingDirectPlacements.Clear();
        catalog.ShapePreservingDirectPlacements.UnionWith(directPlacements);
        // Materialize inherited runtime destinations once, after all context copies exist.
        // Keep the original trigger and level; each target gets its own controller.
        var runtimeMirrors = catalog.RuntimePlacements.SelectMany(insertion =>
            InheritedCopyTargets(insertion.Target.List, materialCopies, catalog)
                .Select(target => insertion with { Target = insertion.Target with { List = target } })).ToArray();
        catalog.RuntimePlacements.AddRange(runtimeMirrors);
        FinalizeCdfOutputPlans(plans, catalog);
        return new(candidates, plans, plans.Select(p => p.Source.FormKey).Distinct().Count());
    }

}
