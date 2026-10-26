using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    [Flags]
    private enum MerchantEquipmentKind { None = 0, Weapon = 1, Armor = 2, Jewelry = 4 }
    private sealed record MerchantVendor(FormKey Container, HashSet<FormKey> FilterKeywords, bool ExcludeListed,
        MerchantEquipmentKind StockKinds);
    private sealed record MerchantAvailability(MaterialAvailabilityPath Path, short Level, string? Problem = null);

    private static MerchantEquipmentKind MerchantKind(IMajorRecordGetter item, Catalog catalog)
    {
        if (item.IsDeleted) return MerchantEquipmentKind.None;
        if (item is IWeaponGetter weapon)
            return weapon.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable) ? MerchantEquipmentKind.None : MerchantEquipmentKind.Weapon;
        if (item is not IArmorGetter armor || armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable)) return MerchantEquipmentKind.None;
        if (CachedShape(item, catalog) is { Type: "Ring" or "Amulet" or "Circlet" }) return MerchantEquipmentKind.Jewelry;
        return EquipmentType(armor, []).Type is not null && (armor.BodyTemplate?.ArmorType is ArmorType.LightArmor or ArmorType.HeavyArmor)
            ? MerchantEquipmentKind.Armor : MerchantEquipmentKind.None;
    }

    private static MerchantAvailability MerchantAvailabilityFor(FormKey source, Catalog catalog)
    {
        if (catalog.MerchantAvailabilityCache.TryGetValue(source, out var cached)) return cached;
        MerchantAvailability Block(string problem) => catalog.MerchantAvailabilityCache[source] = new(new(), 1, problem);
        if (catalog.UnsafeAvailability.TryGetValue(source, out var unsafeReason)) return Block("unsupported source availability: " + unsafeReason);
        if (catalog.ScriptUnlocks.GetValueOrDefault(source, []).Count > 0) return Block("quest-unlocked sources cannot be transferred to merchant stock");
        var globals = SourceGlobalControls(source, catalog);

        // Moving stock to new consumers must not invent an AND/OR relationship
        // between independent source routes. Accept one complete proven path;
        // preserve its ordered, provenance-aware globals and chance rolls.
        var evidence = new List<string>();
        var proofs = RelocatedSourceProofs(source, catalog, evidence);
        // A complete unconditional alternative independently authorizes stock;
        // controls belonging only to other routes must not veto or gate it.
        var unconditional = proofs.Where(p => p.Unlock is null && p.Availability.Controls.Length == 0).ToArray();
        if (unconditional.Length > 0)
            return catalog.MerchantAvailabilityCache[source] = new(new(), unconditional.Min(p => p.Level));
        var paths = proofs.Select(p => p.Availability).DistinctBy(p => p.Identity).ToArray();
        if (proofs.Any(p => p.Unlock is not null) || paths.Length != 1)
            return Block(paths.Length > 1 ? "alternative source-control paths require review" : "no complete source-control path can be reproduced");
        var path = paths[0];
        if (globals.Any(g => !path.Controls.Any(c => c.Kind == MaterialAvailabilityKind.Global && c.Global == g)))
            return Block("a source global is absent from the proven merchant availability path");
        return catalog.MerchantAvailabilityCache[source] = new(path, proofs.Max(p => p.Level));
    }
    // Last-resort distribution deliberately uses only load-order record evidence:
    // a vendor faction, its buy/sell form list, its placed merchant chest, and an
    // inclusive/exclusive keyword filter. Inverted filters additionally require
    // original stock of the item's equipment kind. No plugin or EditorID allow-list
    // participates in the choice.
    private static FormKey[] MerchantContainersFor(IMajorRecordGetter source, Catalog catalog)
    {
        lock (catalog.MerchantEligibilityCache)
            if (catalog.MerchantEligibilityCache.TryGetValue(source.FormKey, out var cached)) return cached;
        var sourceKeywords = Keywords(source).Select(k => k.FormKey).ToHashSet();
        var sourceKind = MerchantKind(source, catalog);
        var vendorIndex = SharedOnce(catalog, () => catalog.MerchantVendorIndex, value => catalog.MerchantVendorIndex = value, () =>
        {
            using var timing = PhaseTiming.Measure("merchant.vendor-index");
            var formLists = catalog.Mods.SelectMany(m => m.FormLists).GroupBy(x => x.FormKey)
                .ToDictionary(g => g.Key, g => g.Last());
            // Resolve only the vendor factions' referenced REFRs. A typed link
            // cache preserves winning overrides without decoding all major records.
            using var placed = catalog.Mods.ToImmutableLinkCache();
            var factions = catalog.Mods.SelectMany(m => m.Factions).GroupBy(x => x.FormKey).Select(g => g.Last());
            var listKeywords = new Dictionary<FormKey, HashSet<FormKey>?>();
            // Stock evidence is original input CONT/LVLI topology. Build it once,
            // before planning any merchant pools; generated variants must never
            // make a previously unsuitable merchant eligible for another source.
            var stockLists = new Dictionary<FormKey, ILeveledItemGetter>(catalog.Lists);
            var stockKinds = new Dictionary<FormKey, MerchantEquipmentKind>();
            MerchantEquipmentKind StockKinds(IContainerGetter container)
            {
                if (stockKinds.TryGetValue(container.FormKey, out var known)) return known;
                var result = MerchantEquipmentKind.None;
                var seen = new HashSet<FormKey>();
                var todo = new Stack<FormKey>((container.Items ?? []).Where(e => e.Item.Count > 0).Select(e => e.Item.Item.FormKey));
                while (todo.TryPop(out var key))
                {
                    if (!seen.Add(key)) continue;
                    var equipment = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
                    if (equipment is not null) result |= MerchantKind(equipment, catalog);
                    else if (stockLists.TryGetValue(key, out var list) && !list.IsDeleted
                        && (!list.Global.IsNull || list.ChanceNone.Value < 1))
                        foreach (var entry in list.Entries ?? [])
                            if (entry.Data is { Count: > 0 } data) todo.Push(data.Reference.FormKey);
                }
                return stockKinds[container.FormKey] = result;
            }
            HashSet<FormKey>? VendorKeywords(FormKey key, HashSet<FormKey>? active = null)
            {
                if (listKeywords.TryGetValue(key, out var known)) return known;
                active ??= [];
                if (!active.Add(key) || !formLists.TryGetValue(key, out var list) || list.IsDeleted) return null;
                var keywords = new HashSet<FormKey>();
                foreach (var item in list.Items ?? [])
                {
                    if (formLists.ContainsKey(item.FormKey))
                    {
                        var nested = VendorKeywords(item.FormKey, active);
                        if (nested is null) { active.Remove(key); return listKeywords[key] = null; }
                        keywords.UnionWith(nested);
                    }
                    else keywords.Add(item.FormKey);
                }
                active.Remove(key);
                return listKeywords[key] = keywords;
            }

            var vendors = new List<MerchantVendor>();
            foreach (var faction in factions)
            {
                if (faction.IsDeleted || !faction.Flags.HasFlag(Faction.FactionFlag.Vendor)
                    || faction.VendorBuySellList.IsNull || faction.MerchantContainer.IsNull
                    || !placed.TryResolve<IPlacedObjectGetter>(faction.MerchantContainer.FormKey, out var reference)
                    || reference.IsDeleted || reference.Base.IsNull)
                    continue;
                var containerKey = reference.Base.FormKey;
                if (catalog.ConsumerRecords.Containers.TryGetValue(containerKey, out var container) && !container.IsDeleted
                    && VendorKeywords(faction.VendorBuySellList.FormKey) is { } keywords)
                    vendors.Add(new(containerKey, keywords, faction.VendorValues?.NotSellBuy == true,
                        faction.VendorValues?.NotSellBuy == true ? StockKinds(container) : MerchantEquipmentKind.None));
            }
            return vendors;
        });
        return SharedMemo(catalog.MerchantEligibilityCache, source.FormKey, () => vendorIndex
            .Where(x => x.FilterKeywords.Overlaps(sourceKeywords) != x.ExcludeListed)
            .Where(x => !x.ExcludeListed || (sourceKind != MerchantEquipmentKind.None && (x.StockKinds & sourceKind) != 0))
            .Select(x => x.Container).Distinct().Order().ToArray());
    }

    private static void BuildMerchantFallbackPlans(CandidateItem candidate, Catalog catalog,
        ProfileSettings profile, PlanningAdditions<VariantPlan> plans)
    {
        using var timing = PhaseTiming.Measure("plans.merchant-fallback");
        var key = FormKey.Factory(candidate.FormKey);
        void Skip(string reason)
        {
            candidate.Reason = "Merchant fallback: " + reason;
            catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: {candidate.Reason}");
        }

        var availability = MerchantAvailabilityFor(key, catalog);
        if (availability.Problem is { } problem) { Skip(problem); return; }

        IMajorRecordGetter source = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter)catalog.Armors[key];
        var containers = MerchantContainersFor(source, catalog);
        if (HasAuthorDestinations(key, catalog))
        {
            containers = containers.Where(container => AuthorAllowsPlace(key, container, catalog)).ToArray();
            if (!containers.Any()) { Skip("no vendor chest is a destination the mod author lists for this item"); return; }
        }
        // A stock root lifting a swapped small inventory to 64 entries would stop LLOS's swap.
        if (catalog.LlosLists is { HasContainerEdits: true } llos)
            containers = containers.Where(container => catalog.MerchantContainerRoots.ContainsKey(container)
                || llos.ContainerAppendProblem(catalog.ConsumerRecords.Containers.GetValueOrDefault(container)?.Items,
                    catalog.Mods.SelectMany(m => m.Containers).LastOrDefault(c => c.FormKey == container)?.Items) is null).ToArray();
        if (containers.Length == 0)
        { Skip("no active vendor has a resolvable chest and an accepting buy/sell filter; inverted-filter vendors also require existing stock of this equipment kind"); return; }

        var cls = CachedClass(source, catalog);
        var shape = CachedShape(source, catalog);
        var templates = cls is null ? null : catalog.TemplatesByClass.GetValueOrDefault(ClassKey(cls));
        var broad = templates is not { Count: > 0 } && shape is not null;
        if (broad) templates = catalog.Templates.Where(t => ShapeMatches(shape!, t.Class)).ToList();
        var unrestricted = templates;
        templates = SourceTemplates(source.FormKey, templates, catalog);
        if (templates is not { Count: > 0 }) { Skip(unrestricted is { Count: > 0 } ? AuthorListLeavesNoTemplate : "no compatible enchantment templates"); return; }

        var selected = (broad ? TemplatesForBroadSource(source, templates, catalog)
                : TemplatesForExactSource(source, templates, catalog))
            .GroupBy(t => VariantIdentity(t.Item, t.Enchantment, catalog)).ToArray();
        if (selected.Length == 0) { Skip("no enabled enchantment has a compatible ordinary template"); return; }
        var selectedTiers = selected.Select(group => (Group: group, Tier: Math.Max(availability.Level,
            group.SelectMany(t => t.Parents).Select(p => p.Level).DefaultIfEmpty((short)1).Min())))
            .Where(choice => choice.Tier <= availability.Path.MaximumLevel).ToArray();
        if (selectedTiers.Length == 0) { Skip("source and enchantment level windows do not overlap"); return; }
        var firstTier = selectedTiers.Min(choice => choice.Tier);

        if (!catalog.MerchantSourcePools.TryGetValue(key, out var sourcePool))
        {
            sourcePool = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
            var list = new LeveledItem(sourcePool, SkyrimRelease.SkyrimSE)
            { EditorID = $"Merchant_{DisplayName(source)}", Flags = (LeveledItem.Flag)3, ChanceNone = new Percent(0) };
            catalog.AddList(sourcePool, list);
            catalog.Fallbacks.Add(sourcePool, new($"merchant-source|{key}", candidate.Category, list));
            catalog.MerchantSourcePools[key] = sourcePool;
        }

        foreach (var container in containers)
        {
            if (!catalog.MerchantContainerRoots.TryGetValue(container, out var root))
            {
                root = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                var list = new LeveledItem(root, SkyrimRelease.SkyrimSE)
                { EditorID = $"MerchantStock_{container}", Flags = (LeveledItem.Flag)3, ChanceNone = new Percent(0.5) };
                catalog.AddList(root, list);
                catalog.Fallbacks.Add(root, new($"merchant-root|{container}", "Merchant stock", list));
                catalog.MerchantContainerRoots[container] = root;
            }
            if (catalog.MerchantAttachments.Add((container, key)))
                AttachOtherMaterialBranch(key, new(new(root, firstTier, 1), sourcePool, availability.Path, null), catalog, share: true);
        }

        var inventory = catalog.SourceEquipment!.GetValueOrDefault(key, []);
        var count = 0;
        foreach (var (group, tier) in selectedTiers)
        {
            var template = group.OrderBy(t => profile.EnchantmentProviderPlugins.FindIndex(p =>
                    p.Equals(t.Enchantment.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(t => t.Item.FormKey).First();
            var existing = inventory.FirstOrDefault(i => OrdinaryEquipment(i, catalog.KeywordNames)
                && TryEnchantment(i, out var enchantment)
                && VariantIdentity(i, enchantment, catalog) == group.Key);
            plans.Add(new(candidate, source, template, [new(sourcePool, tier, 1)], existing,
                $"Merchant fallback: occasional stock at {containers.Length} compatible merchant chest(s); inclusive/exclusive buy/sell filter retained; inverted vendors have existing stock of this equipment kind; proven source controls retained"));
            count++;
        }
        Skip($"{count} semantic variant(s) assigned to {containers.Length} compatible merchant chest(s) through one 50% occasional-stock entry per chest");
    }
}
