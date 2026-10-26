using System.Collections.Concurrent;
using System.IO;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // ChangeId identifies the authored change; a replacement's own erasure is
    // excluded when proving that its predecessor is present for it to replace.
    private sealed record CdfRemoval(FormKey Item, FormKey Consumer, FormKey Base, bool Reference, uint Count, string Provenance,
        string ChangeId = "")
    {
        // Reaches every container (a rule without a positive selector); Consumer and
        // Base are unused. Conservatively ignores its location/exclusion predicates.
        public bool EveryContainer { get; init; }
        // With bypassUnsafeContainers the effect also reaches non-respawning bases.
        public bool ReachesUnsafe { get; init; }
    }

    // Removal effects indexed by the recipient scopes they reach. A base rule
    // reaches every reference of its CONT; a reference rule reaches only that
    // reference; physical base inventory is reached by any rule on that base.
    private sealed class CdfRemovalScopes(IEnumerable<CdfRemoval> removals, IReadOnlySet<FormKey> respawning)
    {
        private readonly ILookup<(FormKey, string), CdfRemoval> byFamily = removals.Where(r => !r.EveryContainer).ToLookup(r => (r.Item, "container:" + r.Base));
        private readonly ILookup<(FormKey, string), CdfRemoval> baseRules = removals.Where(r => !r.Reference && !r.EveryContainer).ToLookup(r => (r.Item, "container:" + r.Base));
        private readonly ILookup<(FormKey, FormKey), CdfRemoval> referenceRules = removals.Where(r => r.Reference).ToLookup(r => (r.Item, r.Consumer));
        private readonly ILookup<FormKey, CdfRemoval> everyContainer = removals.Where(r => r.EveryContainer).ToLookup(r => r.Item);
        // A null item is a conservative unknown destructive form, scoped exactly
        // like known forms without expanding it to the whole equipment catalog.
        public ILookup<FormKey, CdfRemoval> ByItem { get; } = removals.ToLookup(r => r.Item);
        public bool Affects(FormKey item) => ByItem.Contains(item) || ByItem.Contains(default);


        public IEnumerable<CdfRemoval> Effects(FormKey item, TypedLootUse use)
        {
            if (use.Role != "Container inventory" && !NativeContainerRole(use.Role)) return [];
            var recipientBase = RecipientBase(use);
            var scoped = use.Role == "Container inventory" || use.Family == "container:" + use.Consumer
                ? byFamily[(item, "container:" + recipientBase)]
                : baseRules[(item, use.Family)].Concat(referenceRules[(item, use.Consumer)]);
            // All-container effects reach the bases their guards admit, the same set
            // additions fan out to: respawning bases, or every base with the bypass.
            var effects = scoped.Concat(respawning.Contains(recipientBase) ? everyContainer[item] : everyContainer[item].Where(r => r.ReachesUnsafe));
            return item.IsNull ? effects : effects.Concat(Effects(default, use));
        }
    }

    // The CONT base a container or CDF recipient use belongs to.
    private static FormKey RecipientBase(TypedLootUse use)
        => use.Role == "Container inventory" ? use.Consumer : FormKey.Factory(use.Family.Substring("container:".Length));

    // CDF removes resulting inventory objects, not leveled-list definitions.
    // This immutable effect index is shared by source, counterpart and output
    // checks. Possible list/random outcomes cannot prove a residual quantity.
    private sealed class CdfRemovals(IEnumerable<CdfRemoval> removals, IEnumerable<CdfListUse> uses,
        IEnumerable<CdfRemoval>? variantErasures = null, IReadOnlySet<FormKey>? respawning = null)
    {
        private readonly CdfRemoval[] all = removals.ToArray();
        private readonly CdfListUse[] allUses = uses.ToArray();
        private readonly CdfRemoval[] allVariantErasures = (variantErasures ?? []).ToArray();
        private readonly CdfRemovalScopes erasures = new(removals, respawning ?? new HashSet<FormKey>());
        // The same index with another native framework's removals added (CID): every removal
        // of an inventory object at a container is one effect set, whichever framework wrote it.
        public CdfRemovals With(IEnumerable<CdfRemoval> extra, IReadOnlySet<FormKey> respawningBases)
            => new(all.Concat(extra), allUses, allVariantErasures, respawningBases);
        // A keyword replacement re-adds matching payloads after its erasure, so the
        // payload survives while a generated variant copying its keywords, already
        // delivered during additions, does not. Keyed by the variant's source form.
        private readonly CdfRemovalScopes variantErasures = new(variantErasures ?? [], respawning ?? new HashSet<FormKey>());
        private readonly ILookup<FormKey, CdfListUse> establishedByConsumer = uses
            .Where(u => u.Direct && u.EstablishedPredicate.Length == 0).ToLookup(u => u.Consumer);
        // Interim replacement proofs query their established role. Do not register
        // broader-role aliases: conditional metadata cannot describe an unconditional use.
        private readonly ILookup<(FormKey, string), CdfListUse> contexts = uses.Where(u => u.Reference && u.InventoryPredicate.Length > 0).SelectMany(u => u.EstablishedPredicate.Length == 0
                ? new[] { u } : new[] { u, u with { Predicate = u.EstablishedPredicate, EstablishedPredicate = "" } })
            .ToLookup(u => (u.Consumer, "CDF inventory " + u.Predicate));
        private readonly ConcurrentDictionary<(FormKey, TypedLootUse), bool> survivors = new();
        public bool Any => erasures.ByItem.Count > 0 || variantErasures.ByItem.Count > 0;
        public bool Affects(FormKey item) => erasures.Affects(item) || variantErasures.Affects(item);

        public bool VariantSurvives(FormKey source, TypedLootUse use) => !variantErasures.Effects(source, use).Any();

        public bool Survives(FormKey item, TypedLootUse use, Catalog catalog)
        {
            if (!erasures.Affects(item)) return true;
            return survivors.GetOrAdd((item, use), _ =>
            {
                using var timing = PhaseTiming.Measure("cdf.removal-survival");
                var effects = erasures.Effects(item, use).ToArray();
                // Every effect reaches this recipient's own base, which an all-container
                // effect does not record, so read it from the recipient.
                return effects.Length == 0 || Remains(item, use, RecipientBase(use), effects, catalog);
            });
        }

        // Some candidate has a positive established quantity after every removal
        // except the named change. Unlike Survives, absence of removals proves
        // nothing. Only inventory actually present at the recipient is checked,
        // so a broad keyword predicate costs no more than that inventory.
        public bool AnyEstablished(IReadOnlySet<FormKey> items, TypedLootUse use, FormKey baseKey, string exceptChangeId, Catalog catalog)
            => Present(baseKey, use, catalog).Where(items.Contains).Distinct().Any(item => Remains(item, use, baseKey,
                erasures.Effects(item, use).Where(r => r.ChangeId != exceptChangeId).ToArray(), catalog));

        private IEnumerable<FormKey> Present(FormKey baseKey, TypedLootUse use, Catalog catalog)
            => (catalog.ConsumerRecords.Containers.TryGetValue(baseKey, out var container) ? container.Items ?? [] : [])
                .Where(entry => entry.Item.Count > 0).Select(entry => entry.Item.Item.FormKey)
                .Concat(ApplicableAdditions(use, baseKey).Select(addition => addition.Payload));

        private IEnumerable<CdfListUse> ApplicableAdditions(TypedLootUse use, FormKey baseKey)
        {
            var exact = establishedByConsumer[use.Consumer].Where(addition => MatchesSpecializedUse(use, [CdfUse(addition)], false));
            var destination = contexts[(use.Consumer, use.Role)].FirstOrDefault(u => u.Reference && u.Base == baseKey && u.InventoryPredicate.Length > 0);
            if (destination is null) return exact;
            // Only a recognized precise CDF reference may borrow its own base's
            // guaranteed inventory. Another reference and physical/base queries cannot.
            return exact.Concat(establishedByConsumer[baseKey].Where(addition => !addition.Reference && addition.Base == baseKey
                && addition.InventoryPredicate.Length > 0 && (addition.InventoryPredicate == destination.InventoryPredicate
                    || addition.InventoryPredicate == destination.BroaderInventoryPredicate)));
        }

        private bool Remains(FormKey item, TypedLootUse use, FormKey baseKey, CdfRemoval[] effects, Catalog catalog)
        {
            // The native implementation casts uint count to int32. Do not
            // turn an overflowing count into an invented positive budget.
            if (effects.Any(r => r.Count == 0 || r.Count > int.MaxValue)) return false;
            var removed = effects.Aggregate(0UL, (n, r) => n + r.Count);
            ulong established = 0;
            if (catalog.ConsumerRecords.Containers.TryGetValue(baseKey, out var container))
                foreach (var entry in container.Items ?? [])
                    if (entry.Item.Item.FormKey == item && entry.Item.Count > 0) established += (uint)entry.Item.Count;
            foreach (var addition in ApplicableAdditions(use, baseKey))
                if (addition.Payload == item) established += addition.Count;
            return established <= int.MaxValue && established > removed;
        }
    }

    private static bool CdfSurvives(FormKey item, TypedLootUse use, Catalog catalog)
        => catalog.CdfInventory?.Removals.Survives(item, use, catalog) != false;

    // Only removals of the route's own source, base, template or reused variant (or a
    // variant erasure of its source) can refuse it. Checked before any recipient work:
    // route assessment runs this for every route, and most removal corpora touch few items.
    private static bool CdfRemovalsReach(FormKey source, EnchantedTemplate template, FormKey reused, Catalog catalog)
        => catalog.CdfInventory?.Removals is { Any: true } removals && (removals.Affects(source) || removals.Affects(template.BaseItem.FormKey)
            || removals.Affects(template.Item.FormKey) || !reused.IsNull && removals.Affects(reused));

    private static bool CdfOutputSafe(FormKey source, EnchantedTemplate template, IEnumerable<TypedLootUse> recipients,
        Catalog catalog, FormKey reused = default, bool reportRefusal = true)
    {
        if (!CdfRemovalsReach(source, template, reused, catalog)) return true;
        foreach (var recipient in recipients)
            foreach (var item in new[] { source, template.BaseItem.FormKey, template.Item.FormKey, reused }.Where(k => !k.IsNull).Distinct())
                if (!CdfSurvives(item, recipient, catalog) || item == source && reused.IsNull && !catalog.CdfInventory!.Removals.VariantSurvives(source, recipient))
                {
                    // Reached from the parallel review prefetch: keep the note in review order.
                    if (reportRefusal) ReviewNote(catalog, $"CDF removal withholds {source} at {recipient.Consumer}: {item} has no proved survivor after full/count-limited native removals; generated output cannot bypass that availability.");
                    return false;
                }
        return true;
    }

    private static IEnumerable<IMajorRecordGetter> CdfEquivalentOutputs(FormKey source, string identity, Catalog catalog)
        => catalog.SourceEquipment!.GetValueOrDefault(source, []).Where(item => OrdinaryEquipment(item, catalog.KeywordNames)
            && TryEnchantment(item, out var enchantment) && VariantIdentity(item, enchantment, catalog) == identity);

    // The generation identity is distinct from the templates proving a context:
    // a source-owned weapon with another effect may legitimately prove its route.
    private static bool TryCdfOutput(FormKey source, IReadOnlyList<(EnchantedTemplate Template, TypedLootUse[] Recipients)> proofs,
        IEnumerable<IMajorRecordGetter> equivalents, Catalog catalog, out IMajorRecordGetter? reused, bool reportRefusal = true)
    {
        foreach (var output in equivalents.Cast<IMajorRecordGetter?>().Append(null))
            if (proofs.All(proof => !CdfRemovalsReach(source, proof.Template, output?.FormKey ?? default, catalog)
                || proof.Recipients.Length > 0 && CdfOutputSafe(source, proof.Template, proof.Recipients, catalog, output?.FormKey ?? default, reportRefusal: false)))
            { reused = output; return true; }
        reused = null;
        if (reportRefusal)
            foreach (var proof in proofs) CdfOutputSafe(source, proof.Template, proof.Recipients, catalog);
        return false;
    }

    private static bool CdfOutputAvailable(FormKey source, EnchantedTemplate template, IEnumerable<TypedLootUse> recipients,
        Catalog catalog, string generationIdentity, bool reportRefusal = true)
        => !CdfRemovalsReach(source, template, default, catalog)
            || TryCdfOutput(source, [(template, recipients.ToArray())], CdfEquivalentOutputs(source, generationIdentity, catalog),
                catalog, out _, reportRefusal);

    private static bool CdfRouteAvailable(FormKey source, SpecializedRoute route, Catalog catalog)
        => !CdfRemovalsReach(source, route.Template, default, catalog)
            || CdfOutputAvailable(source, route.Template, CdfRouteRecipients(route, catalog), catalog,
                VariantIdentity(route.Template.Item, route.Template.Enchantment, catalog));

    private static TypedLootUse[] CdfRouteRecipients(SpecializedRoute route, Catalog catalog)
        => BoundaryRecipients(new(route.IsolationPath is { Count: > 0 } path ? path[^1] : route.Destination.List,
            route.RecipientIsolation, route.MerchantIsolation), catalog).ToArray();

    // The recipients a placement at an isolation boundary reaches: every recipient of the
    // boundary, or only an isolated loot recipient's or merchant's own edge. Shared by route
    // admission, other-material expansion and the output validator.
    private static IEnumerable<TypedLootUse> BoundaryRecipients(IsolatedCopyScope scope, Catalog catalog)
    {
        IEnumerable<TypedLootUse> uses = SpecializedGraph(catalog).Endpoints(scope.Boundary);
        if (scope.Recipient is { } recipient) uses = uses.Where(u => u.Consumer == recipient.Consumer && u.Role == recipient.Role);
        if (scope.Merchant is { } merchant) uses = uses.Where(u => u.Consumer == merchant.Container && u.Role == "Container inventory");
        return uses;
    }

    // Erased authored equivalents do not cover a placement. Keep planned coverage
    // separate; these helpers apply only to the inventory records supplying it.
    private static IEnumerable<FormKey> CdfCoveredAncestors(FormKey item, IEnumerable<FormKey> ancestors, Catalog catalog)
    {
        if (catalog.CdfInventory?.Removals.Affects(item) != true) return ancestors;
        var recipients = CdfOutputRecipients(catalog);
        return ancestors.Where(parent => recipients(parent) is { Length: > 0 } uses && uses.All(use => CdfSurvives(item, use, catalog)));
    }

    private static HashSet<(FormKey Consumer, string Role)> CdfCoveredRecipients(FormKey item,
        IEnumerable<(FormKey Consumer, string Role)> contexts, Catalog catalog)
    {
        if (catalog.CdfInventory?.Removals.Affects(item) != true) return contexts.ToHashSet();
        var uses = CdfOutputRecipients(catalog)(item).ToLookup(use => (use.Consumer, use.Role));
        // CDF never acts on NPC equipment/death roles. Container coverage needs its
        // actual context, including predicate/reference scope; absence proves nothing.
        return contexts.Where(context => context.Role != "Container inventory" && !context.Role.StartsWith("CDF inventory ", StringComparison.Ordinal)
            || uses[context].Any() && uses[context].All(use => CdfSurvives(item, use, catalog))).ToHashSet();
    }

    private static HashSet<(FormKey Consumer, string Role)> CdfLootRecipientCoverage(IEnumerable<FormKey> starts, Catalog catalog)
    {
        if (catalog.CdfInventory?.Removals.Any != true) return LootRecipientCoverage(starts, catalog);
        var affected = new HashSet<FormKey>();
        var unchanged = new List<FormKey>();
        foreach (var item in starts)
            if ((catalog.Weapons.ContainsKey(item) || catalog.Armors.ContainsKey(item)) && catalog.CdfInventory.Removals.Affects(item)) affected.Add(item);
            else unchanged.Add(item);
        var result = LootRecipientCoverage(unchanged, catalog);
        foreach (var item in affected)
            result.UnionWith(CdfCoveredRecipients(item, LootRecipientCoverage([item], catalog), catalog));
        return result;
    }

    private static Func<FormKey, TypedLootUse[]> CdfOutputRecipients(Catalog catalog)
    {
        var graph = SpecializedGraph(catalog);
        var recipients = new Dictionary<FormKey, TypedLootUse[]>();
        // An isolated copy is reached only through its boundary, so it has the recipients
        // admission checked there; its original's other parents never see it. Any other
        // copy keeps the conservative mapping to every recipient of its original.
        IEnumerable<TypedLootUse> Recipients(FormKey list) => catalog.IsolatedCopyScopes.TryGetValue(list, out var scope)
            ? BoundaryRecipients(scope, catalog)
            : graph.Endpoints(catalog.SpecializedCloneOriginals.GetValueOrDefault(list, list));
        return list =>
        {
            if (recipients.TryGetValue(list, out var found)) return found;
            var ancestry = PlannedLootAncestors(list, catalog);
            return recipients[list] = catalog.CdfOutputRoots.Where(r => ancestry.Contains(r.Root)).Select(r => CdfUse(r.Source))
                .Concat(ancestry.SelectMany(Recipients)).Distinct().ToArray();
        };
    }

    private static void FinalizeCdfOutputPlans(PlanningAdditions<VariantPlan> plans, Catalog catalog)
    {
        if (catalog.CdfInventory?.Removals.Any != true) return;
        using var timing = PhaseTiming.Measure("cdf.output-selection");
        var recipients = CdfOutputRecipients(catalog);
        var count = plans.Count;
        var changed = new HashSet<CandidateItem>();
        for (var index = 0; index < count; index++)
        {
            var plan = plans[index];
            var affected = false;
            foreach (var parent in plan.Parents)
                if (CdfRemovalsReach(plan.Source.FormKey, plan.ParentTemplates?.GetValueOrDefault(parent.List) ?? plan.Template,
                    plan.Reused?.FormKey ?? default, catalog)) { affected = true; break; }
            if (!affected) continue;
            var groups = plan.Parents.GroupBy(parent => parent.List).ToArray();
            EnchantedTemplate Template(FormKey parent) => plan.ParentTemplates?.GetValueOrDefault(parent) ?? plan.Template;
            var identity = VariantIdentity(plan.Template.Item, plan.Template.Enchantment, catalog);
            var equivalents = CdfEquivalentOutputs(plan.Source.FormKey, identity, catalog)
                .OrderByDescending(item => item.FormKey == plan.Reused?.FormKey).ToArray();
            var proofs = groups.Select(group => (Template(group.Key), recipients(group.Key))).ToArray();
            if (TryCdfOutput(plan.Source.FormKey, proofs, equivalents, catalog, out var common, reportRefusal: false))
            {
                plans[index] = plan with { Reused = common };
                if (common?.FormKey != plan.Reused?.FormKey) changed.Add(plan.Candidate);
                continue;
            }
            var partitions = new Dictionary<FormKey, (IMajorRecordGetter? Reused, List<ParentEntry> Parents)>();
            foreach (var group in groups)
            {
                if (!TryCdfOutput(plan.Source.FormKey, [(Template(group.Key), recipients(group.Key))], equivalents, catalog, out var selected))
                    throw new InvalidDataException($"CDF output selection refused for {plan.Source.FormKey} at {group.Key}: no safe equivalent or fresh output. No output was published.");
                var key = selected?.FormKey ?? default;
                if (!partitions.TryGetValue(key, out var partition)) partitions[key] = partition = (selected, []);
                partition.Parents.AddRange(group);
            }
            changed.Add(plan.Candidate);
            var first = true;
            foreach (var partition in partitions.Values)
            {
                var parents = partition.Parents.Select(parent => parent.List).ToHashSet();
                var selected = plan with { Parents = partition.Parents, Reused = partition.Reused,
                    ParentTemplates = plan.ParentTemplates?.Where(pair => parents.Contains(pair.Key)).ToDictionary() };
                if (first) { plans[index] = selected; first = false; }
                else plans.Add(selected);
            }
        }
        foreach (var candidate in changed)
        {
            var own = plans.Where(plan => ReferenceEquals(plan.Candidate, candidate)).ToArray();
            candidate.Reason = $"CDF recipient survival: {own.Count(plan => plan.Reused is null && plan.Parents.Count > 0)} new; "
                + $"{own.Count(plan => plan.Reused is not null)} reused; {own.Count(plan => plan.Parents.Count > 0)} placements planned";
        }
        ValidateCdfOutputPlans(plans, catalog, recipients);
    }

    // Verify the complete projected output with the actual selected record.
    // Unmodelled recipient lowerings must fail before any file is published.
    private static void ValidateCdfOutputPlans(IEnumerable<VariantPlan> plans, Catalog catalog, Func<FormKey, TypedLootUse[]> recipients)
    {
        using var timing = PhaseTiming.Measure("cdf.removal-output");
        foreach (var plan in plans)
        {
            var reused = plan.Reused?.FormKey ?? default;
            foreach (var parent in plan.Parents)
            {
                // Each parent is checked against the template that proved it.
                var template = plan.ParentTemplates?.GetValueOrDefault(parent.List) ?? plan.Template;
                if (!CdfRemovalsReach(plan.Source.FormKey, template, reused, catalog)) continue;
                var uses = recipients(parent.List);
                if (uses.Length == 0 || !CdfOutputSafe(plan.Source.FormKey, template, uses, catalog, reused))
                    throw new InvalidDataException($"CDF removal output refused for {plan.Source.FormKey} at {parent.List}: projected recipient survival is unproved. "
                        + $"Template {template.Item.FormKey}, reused {plan.Reused?.FormKey.ToString() ?? "none"}, {uses.Length} recipient(s), "
                        + $"plan '{plan.PlacementReason}', list '{(catalog.Fallbacks.TryGetValue(parent.List, out var fallback) ? fallback.Identity : "authored")}'"
                        + (uses.Length == 0 ? "; no recipient could be traced" : "") + ". No output was published.");
            }
        }
    }
}
