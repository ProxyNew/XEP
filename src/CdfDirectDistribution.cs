using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record CdfDirectRoute(CdfListUse Source, EnchantedTemplate Template, short Level);
    private sealed record CdfOutputRoot(FormKey Root, CdfListUse Source);
    private sealed partial class Catalog
    {
        public Dictionary<FormKey, CdfDirectRoute[]> CdfDirectRoutes { get; } = [];
        public List<CdfOutputRoot> CdfOutputRoots { get; } = [];
    }

    private static TypedLootUse CdfUse(CdfListUse use)
    {
        var role = "CDF inventory " + use.Predicate;
        return new(use.Consumer, role, role + ":" + use.Consumer, "container:" + use.Base, true)
        {
            // A possible draw/replacement falls back to its established predicate; an
            // established location-conditional use falls back to the same recipient
            // without location predicates. Each is a broader context at this recipient.
            EstablishedRole = use.EstablishedPredicate.Length > 0 ? "CDF inventory " + use.EstablishedPredicate
                : use.BroaderPredicate.Length > 0 ? "CDF inventory " + use.BroaderPredicate : ""
        };
    }

    // Native direct effects provide a level-one source path. Admit counterparts
    // through the same equipment/template and source-context authorities as lists;
    // only the destination representation and output lowering differ.
    private static CdfDirectRoute[] DirectCdfRoutes(FormKey key, Catalog catalog)
    {
        var native = catalog.CdfInventory;
        if (native is null || !native.DirectByPayload.Contains(key)) return [];
        if (catalog.CdfDirectRoutes.TryGetValue(key, out var cached)) return cached;
        using var timing = PhaseTiming.Measure("cdf.direct-routes");
        IMajorRecordGetter? source = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
        if (native is null || source is null || !native.DirectByPayload.Contains(key)
            || ExclusionReason(source, catalog.KeywordNames).Length != 0 || catalog.UnsafeAvailability.ContainsKey(key)
            || CachedClass(source, catalog) is not { } cls || DistributionSubtype(source, catalog) is null)
            return catalog.CdfDirectRoutes[key] = [];
        var proofs = MaterialSourceProofs(key, catalog, catalog.DistributionNotes);
        var allUses = SpecializedGraph(catalog).Endpoints(key);
        var matcher = new MaterialSourceMatcher(allUses, proofs);
        var routes = new List<CdfDirectRoute>();
        var graph = SpecializedGraph(catalog);
        var counterpartProofs = new Dictionary<FormKey, (List<MaterialSourceProof> Proofs, MaterialSourceMatcher Matcher)>();
        var minima = new Dictionary<(FormKey, TypedLootUse), short?>();
        short? Minimum(FormKey item, TypedLootUse destination)
        {
            if (!CdfSurvives(item, destination, catalog)) return null;
            if (minima.TryGetValue((item, destination), out var minimum)) return minimum;
            if (catalog.UnsafeAvailability.ContainsKey(item)) return null;
            if (!counterpartProofs.TryGetValue(item, out var evidence))
            {
                var paths = MaterialSourceProofs(item, catalog, catalog.DistributionNotes);
                var physical = graph.Uses.GetValueOrDefault(item, []).Where(use => use.Known && use.Role == "Container inventory").ToArray();
                if (physical.Length > 0) paths.Add(new(new MaterialAvailabilityPath(), null, 1, physical));
                counterpartProofs[item] = evidence = (paths, new MaterialSourceMatcher(graph.Endpoints(item), paths));
            }
            return minima[(item, destination)] = evidence.Proofs.Where(p => p.Unlock is null && p.Availability.Controls.Length == 0 && evidence.Matcher.Matches(destination, p))
                .Select(p => (short?)p.Level).Min();
        }
        foreach (var use in native.DirectByPayload[key])
        {
            // A separate native rule cannot observe which alternative CDF drew.
            // Keep the possible use in shared analysis, but never turn it into
            // an independently fired companion. Established singleton draws work.
            if (use.EstablishedPredicate.Length > 0) continue;
            if (!AuthorAllowsPlace(key, use.Consumer, catalog)) continue;
            var destination = CdfUse(use);
            if (!CdfSurvives(key, destination, catalog)) continue;
            if (!proofs.Any(p => p.Unlock is null && p.Availability.Controls.Length == 0 && p.Level <= 1 && matcher.Matches(destination, p))) continue;
            var available = catalog.TemplatesByClass.GetValueOrDefault(ClassKey(cls), []).Where(t => TemplateAllowed(key, t, catalog) && Minimum(t.Item.FormKey, destination) is not null
                && (t.BaseItem.FormKey == key || Minimum(t.BaseItem.FormKey, destination) is not null)).ToArray();
            foreach (var template in TemplatesForExactSource(source, available, catalog))
            {
                // No implicit cross-subtype/material expansion at a direct native
                // destination; existing exact-source selection still owns ranking.
                if (DistributionSubtype(template.BaseItem, catalog) != DistributionSubtype(source, catalog)) continue;
                routes.Add(new(use, template, Math.Max(Minimum(template.Item.FormKey, destination)!.Value,
                    template.BaseItem.FormKey == key ? (short)1 : Minimum(template.BaseItem.FormKey, destination)!.Value)));
            }
        }
        if (routes.Count == 0) catalog.DistributionNotes.Add($"CDF direct {key}: no compatible enabled counterpart has a proved recipient and availability window; unsupported gates or windows are not discarded.");
        return catalog.CdfDirectRoutes[key] = routes.ToArray();
    }

    private static void BuildCdfDirectPlans(List<CandidateItem> candidates, Catalog catalog, ProfileSettings profile, PlanningAdditions<VariantPlan> plans)
    {
        if (catalog.CdfInventory?.DirectByPayload.Count is not > 0) return;
        using var timing = PhaseTiming.Measure("cdf.direct-planning");
        foreach (var candidate in candidates.Where(c => c.Selected && c.Eligible && c.SpecializedMode is
            SpecializedLootMode.SpecializedEnchantedLists or SpecializedLootMode.ScriptDistribution or SpecializedLootMode.BestAvailable))
        {
            var key = FormKey.Factory(candidate.FormKey);
            if (candidate.SpecializedMode == SpecializedLootMode.BestAvailable && catalog.BestAvailableFamilies.TryGetValue(key, out var family)
                && family != SpecializedLootMode.SpecializedEnchantedLists) continue;
            if (candidate.SpecializedMode == SpecializedLootMode.ScriptDistribution && catalog.CdfInventory!.DirectByPayload.Contains(key))
            {
                // Script mode owns the source's ordinary routes. Only lists that CDF alone
                // delivers, at the exact contexts where CDF adds this source directly, and
                // that no script changes at runtime, serve the CDF context; everything else
                // stays with Script planning.
                var assessment = AssessSpecializedRoutes(key, catalog, profile.AllowOtherMaterialRoutes);
                var directContexts = catalog.CdfInventory.DirectByPayload[key].Select(CdfUse).ToArray();
                var scriptLists = ScriptProtectedLists(catalog);
                var cdfOnly = assessment.Routes.Where(route => CdfRouteRecipients(route, catalog) is { Length: > 0 } recipients
                    && recipients.All(use => use.Role.StartsWith("CDF inventory ", StringComparison.Ordinal)
                        && directContexts.Any(direct => MatchesSpecializedUse(use, [direct], false)))
                    && !(route.MaterialPath ?? []).Concat(route.IsolationPath ?? []).Append(route.Destination.List).Any(scriptLists.Contains)).ToList();
                if (cdfOnly.Count > 0) MaterializeSpecializedEnchantedPlans(candidate, catalog, profile, plans, assessment with { Routes = cdfOnly });
            }
            var routes = DirectCdfRoutes(key, catalog);
            if (routes.Length == 0) continue;
            IMajorRecordGetter source = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter)catalog.Armors[key];
            var inventory = catalog.SourceEquipment!.GetValueOrDefault(key, []);
            // Recipients of one authored change that admit the same variant set share one
            // generated list and one native rule, so fan-out stays within plugin capacity.
            var sharedRoots = new Dictionary<(CdfListUse Delivery, string Variants), FormKey>();
            foreach (var context in routes.GroupBy(r => r.Source))
            {
                var admitted = new List<(CdfDirectRoute Route, string Variant, IMajorRecordGetter? Equivalent)>();
                foreach (var group in context.GroupBy(r => VariantIdentity(r.Template.Item, r.Template.Enchantment, catalog)))
                {
                    var route = group.OrderBy(r => profile.EnchantmentProviderPlugins.IndexOf(r.Template.Enchantment.ModKey.FileName.String))
                        .ThenBy(r => r.Template.Item.FormKey).First();
                    var template = route.Template;
                    if (!CdfOutputAvailable(key, template, [CdfUse(context.Key)], catalog, group.Key)) continue;
                    var equivalent = inventory.Where(item => OrdinaryEquipment(item, catalog.KeywordNames) && TryEnchantment(item, out var effect)
                        && VariantIdentity(item, effect, catalog) == group.Key && CdfSurvives(item.FormKey, CdfUse(context.Key), catalog)).ToArray();
                    if (equivalent.Any(item => catalog.CdfInventory!.DirectByPayload[item.FormKey]
                        .Any(use => MatchesSpecializedUse(CdfUse(context.Key), [CdfUse(use)], false))))
                    {
                        candidate.Reason = "CDF already supplies an equivalent counterpart in this recipient context";
                        continue;
                    }
                    // Prefer an already-admitted ordinary list emission in this
                    // exact native context, instead of also emitting a companion.
                    if (plans.Any(p => p.Source.FormKey == key && VariantIdentity(p.Template.Item, p.Template.Enchantment, catalog) == group.Key
                        && p.Parents.Any(parent => catalog.Lists.TryGetValue(parent.List, out var list) && list.Global.IsNull && list.ChanceNone.Value == 0
                            && !catalog.RuntimeControlledLists.ContainsKey(parent.List)
                            && catalog.CdfInventory!.ByPayload[parent.List].Any(use => !use.Direct
                                && MatchesSpecializedUse(CdfUse(context.Key), [CdfUse(use)], false))))) continue;
                    admitted.Add((route, group.Key, equivalent.FirstOrDefault()));
                }
                if (admitted.Count == 0) continue;
                var delivery = context.Key with { Consumer = default, Base = default };
                var variants = string.Join(';', admitted.Select(a => $"{a.Variant}|{a.Route.Template.Item.FormKey}|{a.Route.Level}|{a.Equivalent?.FormKey}").Order(StringComparer.Ordinal));
                candidate.Reason = "CDF direct counterparts: native recipient conditions and count retained";
                if (sharedRoots.TryGetValue((delivery, variants), out var shared))
                {
                    // Identical entries already sit in the shared list; only the recipient joins.
                    catalog.CdfOutputRoots.Add(new(shared, context.Key));
                    catalog.DistributionNotes.Add($"CDF direct output for {key}: {context.Key.Provenance}, recipient {context.Key.Consumer}, count {context.Key.Count}; shares the generated payload of an identical recipient.");
                    continue;
                }
                FormKey root = new(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                var list = new LeveledItem(root, SkyrimRelease.SkyrimSE)
                { EditorID = "CDF_" + candidate.EditorId, Flags = (LeveledItem.Flag)3, Entries = new() };
                catalog.AddList(root, list);
                catalog.Fallbacks.Add(root, new("cdf-direct|" + key + "|" + context.Key, candidate.Category, list));
                catalog.CdfOutputRoots.Add(new(root, context.Key));
                sharedRoots[(delivery, variants)] = root;
                foreach (var (route, variant, equivalent) in admitted)
                {
                    var parent = new ParentEntry(root, route.Level, 1);
                    var existingIndex = -1;
                    for (var i = 0; i < plans.Count; i++)
                        if (plans[i].Source.FormKey == key && VariantIdentity(plans[i].Template.Item, plans[i].Template.Enchantment, catalog) == variant)
                        { existingIndex = i; break; }
                    if (existingIndex >= 0) plans[existingIndex] = plans[existingIndex] with
                    { Parents = plans[existingIndex].Parents.Append(parent).ToArray(),
                        ParentTemplates = MergeParentTemplates(plans[existingIndex], new Dictionary<FormKey, EnchantedTemplate> { [parent.List] = route.Template }) };
                    else plans.Add(new(candidate, source, route.Template, [parent], equivalent,
                        "CDF direct addition: shared source/enchanted-context proof; native reference guards and count retained"));
                }
                catalog.DistributionNotes.Add($"CDF direct output for {key}: {context.Key.Provenance}, recipient {context.Key.Consumer}, count {context.Key.Count}; generated-only native payload.");
            }
        }
    }
}
