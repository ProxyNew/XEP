using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // A list is general loot when the lists above it reach at least this many distinct consumer
    // contexts: separate container records, or separate non-unique NPC faction groups. Decided from
    // the load order's consumers, never from list names or plugins.
    private const int GeneralLootContexts = 2;

    private static HashSet<FormKey> LootAncestors(FormKey key, Catalog catalog) => SharedMemo(catalog.LootAncestorCache, key, () =>
    {
        var found = new HashSet<FormKey>(); var queue = new Queue<FormKey>(); queue.Enqueue(key);
        while (queue.TryDequeue(out var next))
        {
            if (!found.Add(next)) continue;
            foreach (var p in catalog.DirectParents.GetValueOrDefault(next, []))
                if (catalog.Lists.TryGetValue(p.List, out var list) && !list.IsDeleted) queue.Enqueue(p.List);
        }
        return found;
    });

    private static HashSet<FormKey> LootConsumerKeys(Catalog catalog)
    {
        if (catalog.LootConsumers is not null) return catalog.LootConsumers;
        var graph = SpecializedGraph(catalog);
        var names = catalog.ConsumerRecords.CreateNames();
        foreach (var use in catalog.CdfInventory?.Uses ?? [])
            names.TryAdd(use.Consumer, "CDF reference " + use.Consumer);
        var index = graph.Uses.ToDictionary(x => x.Key, x => x.Value.Where(u => u.Known)
            .Select(u => new LootConsumerEvidence(u.Consumer, names.GetValueOrDefault(u.Consumer, "(unresolved)"), u.Role)).Distinct().ToList());
        index = index.Where(x => x.Value.Count > 0).ToDictionary(x => x.Key, x => x.Value);
        catalog.LootConsumerEvidenceIndex = index;
        return catalog.LootConsumers = index.Keys.ToHashSet();
    }

    private static bool IsGeneralRole(FormKey key, Catalog catalog) => GeneralLootEvidence(key, catalog).General;

    // Distinct consumer contexts reached from a list through its winning ancestors. Unique or
    // actor-scoped uses never count. Quest script references are not consumers (scripts reference
    // ordinary vendor lists as insertion targets), so they neither count nor disqualify.
    private static (bool General, int Contexts, string Detail) GeneralLootEvidence(FormKey key, Catalog catalog)
        => SharedMemo(catalog.GeneralLootCache, key, () => UncachedGeneralLootEvidence(key, catalog));

    // The report lists this cache in insertion order among equal names, so only the
    // review's own order may fill it; a prefetch evaluates without caching.
    private static (bool General, int Contexts, string Detail) UncachedGeneralLootEvidence(FormKey key, Catalog catalog)
    {
        if (!catalog.Lists.TryGetValue(key, out var list) || list.IsDeleted) return (false, 0, "not a winning list");
        var graph = SpecializedGraph(catalog);
        var containers = new HashSet<FormKey>(); var groups = new HashSet<string>();
        foreach (var ancestor in LootAncestors(key, catalog))
        foreach (var use in graph.Uses.GetValueOrDefault(ancestor, []))
        {
            if (!use.Known || use.ScopeOnly || use.Context.StartsWith("actor:", StringComparison.Ordinal)) continue;
            if (use.Role == "Container inventory") containers.Add(use.Consumer);
            else if (use.Role.StartsWith("NPC ", StringComparison.Ordinal))
            {
                var parts = use.Context.Split('|');
                groups.Add(parts.Length > 1 ? parts[1] : use.Context);
            }
        }
        var contexts = containers.Count + groups.Count;
        return (contexts >= GeneralLootContexts, contexts,
            $"{containers.Count} container record(s), {groups.Count} NPC faction group(s)");
    }

    // Only the unenchanted source's own route counts. Existing enchanted siblings,
    // fixed actor/container equipment and crafting recipes are not base loot distribution.
    private static bool HasBaseLootRoute(FormKey key, Catalog catalog)
        => catalog.CdfInventory?.DirectByPayload.Contains(key) == true
            || catalog.DirectParents.GetValueOrDefault(key, []).Any(p =>
                catalog.Lists.TryGetValue(p.List, out var list) && !list.IsDeleted)
            || catalog.ScriptUnlocks.GetValueOrDefault(key, []).Any(u =>
                catalog.Lists.TryGetValue(u.OriginalTarget, out var target) && !target.IsDeleted);

    private static string PlainLootEvidence(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "No supported route was found.";
        var text = value.Trim().TrimEnd('.');
        text = text.Replace("carrier group(s)", "groups of NPCs or containers", StringComparison.OrdinalIgnoreCase)
            .Replace("carrier groups", "groups of NPCs or containers", StringComparison.OrdinalIgnoreCase)
            .Replace("WEAP|2H", "two-handed weapons", StringComparison.OrdinalIgnoreCase)
            .Replace("WEAP|1H", "one-handed weapons", StringComparison.OrdinalIgnoreCase)
            .Replace("UseAll", "gives every entry", StringComparison.OrdinalIgnoreCase)
            .Replace("Use All", "gives every entry", StringComparison.OrdinalIgnoreCase)
            .Replace("selector replication", "copying the quest-controlled list structure", StringComparison.OrdinalIgnoreCase)
            .Replace("quest payload count", "a fixed item count given by a quest", StringComparison.OrdinalIgnoreCase)
            .Replace("safe isolation boundary", "a list boundary that can be copied without changing unrelated loot", StringComparison.OrdinalIgnoreCase);
        text = text.TrimEnd();
        if (text.EndsWith(" with", StringComparison.OrdinalIgnoreCase)) text = text[..^5];
        return text + ".";
    }

    private static string DecidingSpecializedReason(SpecializedAssessment? assessment, string fallback, Catalog catalog)
    {
        if (assessment is null) return PlainLootEvidence(fallback);
        if (assessment.Routes.Count > 0)
        {
            var lists = assessment.Routes.Select(r => r.Destination.List).Distinct().ToArray();
            var names = lists.Take(5).Select(k => catalog.Lists.GetValueOrDefault(k)?.EditorID ?? k.ToString());
            return $"Verified consumer context and enchanted peers support {lists.Length} list(s): {string.Join(", ", names)}"
                + (lists.Length > 5 ? $", +{lists.Length - 5} more." : ".");
        }
        var refusals = assessment.Refusals.Distinct().ToArray();
        if (refusals.Length == 0) return $"No supported specialized route; assessment stopped at {assessment.Stage}.";
        return PlainLootEvidence(refusals[0].Description) + (refusals.Length > 1
            ? $" This represents one of {refusals.Length} failed attempts; see route details for their separate reasons." : "");
    }

    // Classification. Enchanted access (EnchantedAccess.cs) comes first: an item whose carriers don't
    // already receive leveled enchanted equipment of its kind and material goes to loot review, default
    // Off, with that evidence. An item with access is general when it also reaches general loot (the
    // consumer-context rule below); its variants are then confined to the access destinations. An item
    // with access that is not general keeps the specialized recommendation (isolated enchanted routes).
    // Review evidence of the items that follow script distribution, from the gates of
    // the final selection. Only selected items' source mods are analysed (ADR 0008).
    private static void DescribeScriptDistribution(List<CandidateItem> candidates, Catalog catalog)
    {
        using var timing = PhaseTiming.Measure("review.script-distribution");
        foreach (var item in candidates.Where(c => c.SpecializedMode == SpecializedLootMode.ScriptDistribution && c.HasSpecializedOverride))
        {
            var key = FormKey.Factory(item.FormKey);
            // The same insertion chains the planner uses, nested script-filled lists included.
            var chains = item.Selected && item.Eligible ? ScriptSiteChains(key, ScriptDistributionGates(catalog), catalog) : [];
            item.SpecializedDecisionReason = item.SpecializedRecommendationReason = item.LootReviewReason = !(item.Selected && item.Eligible)
                ? "Not selected: the source scripts that hand out this item are analysed only while it is patched."
                : chains.Length == 0 ? "No accepted script insertion block hands out this item."
                : $"{chains.Length} accepted script insertion(s); counterparts are planned under the source's consumers, gates and level windows.";
            if (item.Selected && item.Eligible) item.OriginalLocationsMode = chains.Length > 0 ? SpecializedLootMode.ScriptDistribution : null;
            item.SourceDistributionSummary = string.Join("; ", chains.Select(c => string.Join(" > ", c.Select(x => $"{x.Site.Payload} → {x.Site.Receiver} L{x.Site.Level}"))).Distinct());
            item.LootReviewDetails = string.Join("\n", chains.SelectMany(c => c).Select(x => x.Site.Evidence).Distinct());
        }
    }

    private static void ApplySpecializedChoices(List<CandidateItem> candidates, Catalog catalog, ProfileSettings profile)
        => ApplyReview(candidates, catalog, profile, prefetchAssessments: false);

    // Every profile setting ApplySpecializedChoices reads.
    private static string SpecializedReviewChoices(ProfileSettings profile) => $"{profile.AllowOtherMaterialRoutes}|" + string.Join("|",
        profile.SpecializedLootOverrides.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={(int)x.Value}"));

    // A pass forked from a review with the same choices already holds that review's
    // candidates and access state; repeating it would recompute the same values.
    private static void EnsureSpecializedChoices(List<CandidateItem> candidates, Catalog catalog, ProfileSettings profile)
    {
        SettingsStore.MigrateOtherMaterialChoice(profile);
        SettingsStore.MigrateRuntimeMirrorChoice(profile);
        if (catalog.ReviewedChoices != SpecializedReviewChoices(profile)) ApplySpecializedChoices(candidates, catalog, profile);
    }

    // A fresh review of the analysis prefix: no planning state yet, so its route
    // assessments can be prefetched in parallel (see PrefetchSpecializedAssessments).
    private static void ReviewSpecializedChoices(List<CandidateItem> candidates, Catalog catalog, ProfileSettings profile)
        => ApplyReview(candidates, catalog, profile, prefetchAssessments: true);

    private static void ApplyReview(List<CandidateItem> candidates, Catalog catalog, ProfileSettings profile, bool prefetchAssessments)
    {
        using var timing = PhaseTiming.Measure("specialized-assessment");
        catalog.ReviewedChoices = null;
        SettingsStore.MigrateOtherMaterialChoice(profile);
        SettingsStore.MigrateRuntimeMirrorChoice(profile);
        catalog.LootAncestorCache.Clear(); catalog.LootConsumerCountCache.Clear(); catalog.MerchantAvailabilityCache.Clear();
        var consumers = LootConsumerKeys(catalog);
        AccessIndex index;
        using (PhaseTiming.Measure("review.access-index")) index = catalog.Access = BuildAccessIndex(catalog);
        catalog.AccessVerdicts.Clear(); catalog.AccessDestinations.Clear();
        var scriptChains = ScriptRecommendationChains(candidates, catalog, profile, key => HasBaseLootRoute(key, catalog)
            && (catalog.AccessVerdicts[key] = EvaluateAccess(index, key, catalog)).Outcome == AccessOutcome.Variants);
        // After the script recommendation chains, as in review order: their script proofs
        // are then retained and the prefetch can read them.
        var prefetched = prefetchAssessments ? PrefetchSpecializedAssessments(candidates, catalog, profile, index, consumers) : null;
        // An item without ordinary enchanted access follows script distribution when accepted
        // insertion blocks hand it out; an explicit saved choice still decides its mode.
        bool RecommendScript(CandidateItem item, IMajorRecordGetter record)
        {
            var chains = scriptChains(record.FormKey);
            if (chains.Length == 0) return false;
            item.RequiresLootReview = true;
            item.HasSpecializedEquipmentKind = CachedClass(record, catalog) is not null;
            item.OriginalLocationsMode = SpecializedLootMode.ScriptDistribution;
            item.RecommendedSpecializedMode = SpecializedLootMode.ScriptDistribution;
            item.SpecializedRecommendation = "Follow script distribution (enchanted counterparts)";
            item.HasSpecializedOverride = profile.SpecializedLootOverrides.TryGetValue(item.FormKey, out var saved) && Enum.IsDefined(saved);
            item.SpecializedMode = item.HasSpecializedOverride ? saved : SpecializedLootMode.ScriptDistribution;
            item.SpecializedDecisionReason = item.SpecializedRecommendationReason = item.LootReviewReason =
                $"{chains.Length} accepted script insertion(s); counterparts are planned under the source's consumers, gates and level windows.";
            item.SourceDistributionSummary = string.Join("; ", chains.Select(c => string.Join(" > ", c.Select(x => $"{x.Site.Payload} → {x.Site.Receiver} L{x.Site.Level}"))).Distinct());
            item.SpecializedOptionSummary = "Original locations — enchanted counterparts follow accepted script insertions under the same consumers, gates and level windows. Off adds no distribution; General loot allows broader loot locations.";
            item.LootReviewDetails = string.Join("\n", chains.SelectMany(c => c).Select(x => x.Site.Evidence).Distinct());
            return true;
        }
        var reviewed = 0;
        foreach (var item in candidates)
        {
            ScanProgress.Advance(reviewed++, candidates.Count);
            OperationProgress.Detail($"Reviewing equipment ({reviewed:N0}/{candidates.Count:N0}): {item.Plugin}: {item.Name}");
            item.ClearPlacementPreview();
            item.OriginalLocationsMode = null;
            var key = FormKey.Factory(item.FormKey);
            if (ReviewRecord(key, catalog) is not { } record) continue;
            if (profile.SpecializedLootOverrides.GetValueOrDefault(item.FormKey) == SpecializedLootMode.ScriptDistribution)
            {
                // The mode is the saved choice; its insertion evidence needs the gates of the
                // final selection and is described by DescribeScriptDistribution afterwards.
                item.RequiresLootReview = true;
                item.HasSpecializedEquipmentKind = CachedClass(record, catalog) is not null;
                item.RecommendedSpecializedMode = item.SpecializedMode = SpecializedLootMode.ScriptDistribution;
                item.HasSpecializedOverride = true;
                item.SpecializedRecommendation = "Follow script distribution (enchanted counterparts)";
                item.SpecializedOptionSummary = "Original locations — enchanted counterparts follow accepted script insertions under the same consumers, gates and level windows. Off adds no distribution; General loot allows broader loot locations.";
                item.AvailabilitySummary = "Script blocks are checked at game start, after each load and when the source mod signals completion; a switch opens only while its block's last insertion is present. Changes made without a signal wait for the next load.";
                continue;
            }
            if (!HasBaseLootRoute(key, catalog))
            {
                if (RecommendScript(item, record)) continue;
                item.RequiresLootReview = false;
                item.HasSpecializedOverride = false;
                item.SpecializedMode = item.RecommendedSpecializedMode = SpecializedLootMode.Disabled;
                item.SpecializedRecommendation = "Not applicable";
                item.SourceLootLists = "";
                item.SpecializedRecommendationReason = item.GeneralizedLootEvidence = item.LootReviewReason = item.LootReviewDetails =
                    "No base-item leveled-list distribution or verified scripted insertion; enchanted variants require an existing source route.";
                continue;
            }
            var verdict = catalog.AccessVerdicts[key] = EvaluateAccess(index, key, catalog);
            item.HasSpecializedEquipmentKind = !string.IsNullOrWhiteSpace(verdict.Kind);
            var access = verdict.Outcome == AccessOutcome.Variants;
            var ancestry = LootAncestors(key, catalog);
            var general = GeneralAccess(key, access, ancestry, consumers, catalog, k => IsGeneralRole(k, catalog));
            if (general) catalog.AccessDestinations[key] = verdict.Destinations.Select(d => d.Root).ToHashSet();
            // General is an explicit mixing permission, even when access-based
            // classification would otherwise hide the specialized choices.
            item.RequiresLootReview = !general
                || profile.SpecializedLootOverrides.GetValueOrDefault(item.FormKey) == SpecializedLootMode.GeneralLists;
            var parents = catalog.DirectParents.GetValueOrDefault(key, []);
            item.SourceLootLists = string.Join("; ", parents.Select(p => $"{catalog.Lists[p.List].EditorID} [{p.List}] L{p.Level}").Distinct());
            using (PhaseTiming.Measure("review.source-evidence")) DescribeLootReview(item, key, ancestry, catalog);
            string reason, details;
            using (PhaseTiming.Measure("review.access-evidence")) (reason, details) = DescribeAccess(index, verdict, catalog);
            // Recommend exactly the route family that the selected
            // "Specialized enchanted lists only" mode will plan. In particular,
            // the shared material-expansion permission can turn an otherwise Off
            // item into a productive specialized route; disabling it must turn
            // the recommendation back Off when no ordinary route survives.
            var canAssessSpecialized = CanAssessSpecialized(verdict, profile);
            // A prefetched assessment's notes belong here, where the review assesses it.
            if (item.RequiresLootReview && canAssessSpecialized && prefetched is not null && prefetched.Notes.Remove(key, out var notes))
            {
                // Prefetched results used the script-proof inputs of the prefetch; the review
                // loads no script instances, so they still hold (a code change must keep that).
                if (!prefetched.Inputs.Matches(RuntimeListRootHolderInputs.Of(catalog)))
                    throw new InvalidOperationException("Script analysis inputs changed after the review prefetch.");
                catalog.DistributionNotes.AddRange(notes);
            }
            var baseAssessment = item.RequiresLootReview && canAssessSpecialized ? AssessSpecializedRoutes(key, catalog, false) : null;
            var canExpand = item.RequiresLootReview && canAssessSpecialized;
            var expandedAssessment = canExpand ? AssessSpecializedRoutes(key, catalog, true) : baseAssessment;
            var assessment = profile.AllowOtherMaterialRoutes ? expandedAssessment : baseAssessment;
            item.OriginalLocationsMode = assessment?.Routes.Count > 0 ? SpecializedLootMode.SpecializedEnchantedLists : null;
            item.RecommendedSpecializedMode = assessment?.Routes.Count > 0 ? SpecializedLootMode.SpecializedEnchantedLists : SpecializedLootMode.Disabled;
            if (item.RecommendedSpecializedMode == SpecializedLootMode.Disabled && !access && RecommendScript(item, record)) continue;
            item.SpecializedRecommendation = item.RecommendedSpecializedMode == SpecializedLootMode.SpecializedEnchantedLists ? "Specialized enchanted lists only" : "Off";
            item.SpecializedDecisionReason = !item.RequiresLootReview
                ? "Compatible enchanted-loot contexts were found. Automatic placement still requires a proven route; the output preview may find no safe placement."
                : DecidingSpecializedReason(assessment, reason, catalog);
            if (canExpand)
            {
                if (expandedAssessment is { Routes.Count: > 0 })
                {
                    var expandedLists = expandedAssessment.Routes.Select(r => r.Destination.List).Distinct().Count();
                    item.SpecializedDecisionReason += profile.AllowOtherMaterialRoutes
                        ? $" New-material expansion is enabled and supplies {expandedLists} of those supported list(s)."
                        : $" New-material expansion is available and would supply {expandedLists} supported list(s) if enabled.";
                }
                else if (expandedAssessment is not null)
                    item.SpecializedDecisionReason += " New-material expansion was also evaluated and found no supported route: "
                        + DecidingSpecializedReason(expandedAssessment, reason, catalog);
            }
            if (DirectCdfRoutes(key, catalog).Length > 0)
            {
                item.RequiresLootReview = true;
                item.RecommendedSpecializedMode = SpecializedLootMode.SpecializedEnchantedLists;
                item.OriginalLocationsMode = SpecializedLootMode.SpecializedEnchantedLists;
                item.SpecializedRecommendation = "CDF enchanted counterparts";
                item.SpecializedDecisionReason = "CDF direct additions have a shared source and enchanted counterpart context; native conditions are retained.";
            }
            item.SpecializedRecommendationReason = item.SpecializedDecisionReason;
            item.HasSpecializedOverride = profile.SpecializedLootOverrides.TryGetValue(item.FormKey, out var manual) && Enum.IsDefined(manual);
            item.SpecializedMode = item.HasSpecializedOverride ? manual : item.RecommendedSpecializedMode;
            item.GeneralizedLootEvidence = PlainLootEvidence(reason);
            item.LootReviewReason = assessment is null ? reason : item.SpecializedRecommendationReason;

            if (!item.RequiresLootReview)
            {
                item.SpecializedOptionSummary = "";
                item.LootReviewDetails = details;
                continue;
            }

            var enchantedLists = assessment?.Routes.Select(r => r.Destination.List).Distinct().ToArray() ?? [];
            var listNames = enchantedLists.Take(3).Select(k => catalog.Lists.GetValueOrDefault(k)?.EditorID ?? k.ToString()).ToArray();
            item.SpecializedOptionSummary = string.Join("\n", new[]
            {
                "  Off — adds no new loot distribution.",
                $"  Original locations — {(enchantedLists.Length == 0 ? item.OriginalLocationsMode is not null ? "supported CDF enchanted counterparts" : "unavailable; no supported original route" : $"{enchantedLists.Length} enchanted list(s): {string.Join(", ", listNames)}{(enchantedLists.Length > 3 ? $", +{enchantedLists.Length - 3} more" : "")}")}",
                "  General loot — compatible general enchanted loot, including merchant loot lists; may reach places without the ordinary item."
            });
            item.LootReviewDetails = !item.RequiresLootReview ? details
                : "RECOMMENDATION: " + item.SpecializedRecommendation + "\n" + item.SpecializedRecommendationReason
                  + "\nRecommendation is an inference from record evidence, not proof of author intent. Explicit saved choices take precedence.\n\n"
                  + details + "\n\n"
                  + (assessment is null ? "" : "SPECIALIZED ROUTES\n" + string.Join("\n", assessment.Evidence) + "\n\n")
                  + (expandedAssessment is null || ReferenceEquals(expandedAssessment, assessment) ? ""
                      : "NEW-MATERIAL EXPANSION (optional assessment)\n" + string.Join("\n", expandedAssessment.Evidence) + "\n\n")
                  + item.LootReviewDetails;
        }
        catalog.ReviewedChoices = SpecializedReviewChoices(profile);
    }

    // The review's subject: unenchanted equipment records.
    private static IMajorRecordGetter? ReviewRecord(FormKey key, Catalog catalog)
    {
        IMajorRecordGetter? record = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
        return record is null || TryEnchantment(record, out _) || !LooksLikeEquipment(record) ? null : record;
    }

    private static bool GeneralAccess(FormKey key, bool access, HashSet<FormKey> ancestry, HashSet<FormKey> consumers, Catalog catalog,
        Func<FormKey, bool> isGeneralRole)
        => access && (ancestry.Any(k => isGeneralRole(k) && LootAncestors(k, catalog).Overlaps(consumers))
            || catalog.ScriptUnlocks.GetValueOrDefault(key, []).Any(u => LootAncestors(u.OriginalTarget, catalog).Any(isGeneralRole)));

    private static bool CanAssessSpecialized(AccessVerdict verdict, ProfileSettings profile)
        => verdict.Outcome == AccessOutcome.Variants || profile.AllowOtherMaterialRoutes
            && verdict.Outcome is AccessOutcome.OtherMaterialOnly or AccessOutcome.MaterialUnknown;

    private static HashSet<FormKey> PlannedLootAncestors(FormKey key, Catalog catalog)
    {
        // This synchronous walk does not mutate planning state. Refresh once,
        // then visit only incoming additions instead of rescanning all edges
        // for every ancestor. Authored-parent semantics remain unchanged.
        var edges = (catalog.PlanningEdges ??= new()).Refresh(catalog);
        var found = new HashSet<FormKey>(); var queue = new Queue<FormKey>(); queue.Enqueue(key);
        while (queue.TryDequeue(out var next))
        {
            if (!found.Add(next)) continue;
            foreach (var parent in catalog.DirectParents.GetValueOrDefault(next, [])) queue.Enqueue(parent.List);
            foreach (var edge in edges.EdgesTo(next)) queue.Enqueue(edge.Parent.List);
            foreach (var insertion in edges.PlacementsTo(next)) queue.Enqueue(insertion.Target.List);
        }
        return found;
    }

    private static void BuildSpecializedPlans(CandidateItem candidate, Catalog catalog, ProfileSettings profile, PlanningAdditions<VariantPlan> plans)
    {
        using var timing = PhaseTiming.Measure("plans.specialized");
        var key = FormKey.Factory(candidate.FormKey);
        void Skip(string reason)
        {
            candidate.Reason = "Specialized distribution: " + reason;
            catalog.DistributionNotes.Add($"{candidate.Name} [{key}]: {candidate.Reason}");
        }
        catalog.UnsafeAvailability.TryGetValue(key, out var unsafeReason);
        // Static additions to the original source pools inherit their entire ancestor topology.
        // Do not copy one global out of a Full/Lite pair or create a new route around a quest.
        var unlocks = catalog.ScriptUnlocks.GetValueOrDefault(key, []).GroupBy(u => (u.Quest, u.Stage, u.AtStart, u.OriginalTarget))
            .Select(g => g.OrderByDescending(u => u.Level).First()).ToArray();
        IMajorRecordGetter source = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter)catalog.Armors[key];
        var consumers = LootConsumerKeys(catalog);
        // A direct slot of a Give All bundle is a one-entry source pool: the
        // pool goes beside the source inside the slot's choice, never into the bundle
        // (UseAll contract C3). Other bundles stay protected.
        var slotSeeds = new HashSet<FormKey>();
        var seeds = catalog.DirectParents.GetValueOrDefault(key, []).Where(p =>
        {
            var list = catalog.Lists[p.List];
            if (((int)list.Flags & 4) != 0)
            {
                var authored = SpecializedGraph(catalog).Lists.GetValueOrDefault(p.List) ?? list;
                if (!SlotBundle(authored) || BundleSlotProblem(authored, key) is not null || catalog.RuntimeControlledLists.ContainsKey(p.List)
                    || !LootAncestors(p.List, catalog).Overlaps(consumers)) return false;
                slotSeeds.Add(p.List);
                return true;
            }
            return !list.IsDeleted && !catalog.RuntimeControlledLists.ContainsKey(p.List)
                && p.Count == 1 && list.Entries is { Count: > 0 }
                && list.Entries.All(e => e.Data is { Count: 1 } && e.ExtraData is null)
                && (list.Global.IsNull ? list.ChanceNone.Value < 1 : true)
                && LootAncestors(p.List, catalog).Overlaps(consumers);
        }).DistinctBy(p => p.List).OrderBy(p => p.List).ToArray();
        // Script evidence is item-wide, but an independently proved static branch
        // retains its own controls and consumers. Never infer independence merely
        // from a direct reference: the shared proof rejects mutated ancestry.
        if (unsafeReason is not null || unlocks.Length > 0)
            seeds = seeds.Where(seed => MaterialSourceProofsCore(key, catalog, catalog.DistributionNotes, requiredAncestor: seed.List)
                .Any(proof => proof.Unlock is null)).ToArray();
        var routes = seeds.Select(seed => (Seed: seed, Unlock: (ScriptUnlock?)null)).ToList();
        var unlock = unlocks.Length == 1 ? unlocks[0] : null;
        string? runtimeProblem = unsafeReason ?? (unlocks.Length > 1 ? "multiple runtime insertion routes require further review" : null);
        if (unlock is not null)
        {
            // A verified AddForm/AddLeveledObject source unlock can use a companion insertion
            // at the SAME original destination. It never opens a general route on this choice.
            var l = catalog.Lists.GetValueOrDefault(unlock.OriginalTarget);
            if (l is null || l.IsDeleted || (((int)l.Flags & 4) != 0 && !(ForeignRevert(l.FormKey, unlock, catalog) && RevertSwitchProblem(l.FormKey, catalog) is null)) || l.Entries is not { Count: > 0 }
                || l.Entries.Any(e => e.Data is not { Count: 1 } || e.ExtraData is not null))
                runtimeProblem = "unsupported original runtime destination";
            else runtimeProblem = OrdinaryScriptWindowProblem(key, unlock, catalog);
            if (runtimeProblem is null && !seeds.Any(seed => seed.List == unlock.OriginalTarget))
                routes.Add((new(unlock.OriginalTarget, unlock.Level, 1), unlock));
        }
        if (runtimeProblem is not null) catalog.DistributionNotes.Add($"{key}: runtime source route withheld: {runtimeProblem}");
        // Mod-author destinations narrow the original source pools; no other pool replaces them.
        if (HasAuthorDestinations(key, catalog) && routes.Count > 0 && routes.RemoveAll(r => !AuthorAllowsList(key, r.Seed.List, catalog)) > 0 && routes.Count == 0)
        { Skip("no source pool is a destination the mod author lists for this item"); return; }
        if (routes.Count == 0) { Skip(runtimeProblem ?? "no supported source pool with a winning inventory consumer (count-bearing UseAll, quest, extra-data and orphan pools are protected)"); return; }
        var cls = CachedClass(source, catalog);
        var shape = CachedShape(source, catalog);
        var templates = cls is null ? null : catalog.TemplatesByClass.GetValueOrDefault(ClassKey(cls));
        var broad = templates is not { Count: > 0 } && profile.BroadUnmatchedDistribution && shape is not null
            && (cls is null || !IsOwnBaseMaterial(cls.Material));
        if (broad) templates = catalog.Templates.Where(t => ShapeMatches(shape!, t.Class)).ToList();
        var unrestricted = templates;
        templates = SourceTemplates(key, templates, catalog);
        if (templates is not { Count: > 0 }) { Skip(unrestricted is { Count: > 0 } ? AuthorListLeavesNoTemplate : "no compatible enchantment templates"); return; }
        var selected = (broad ? TemplatesForBroadSource(source, templates, catalog) : TemplatesForExactSource(source, templates, catalog))
            .GroupBy(t => VariantIdentity(t.Item, t.Enchantment, catalog));
        var inventory = catalog.SourceEquipment!.GetValueOrDefault(key, []);
        var pools = new Dictionary<FormKey, (FormKey Pool, short FirstTier, ParentEntry Seed, ScriptUnlock? Unlock)>();
        var priorIndices = plans.Select((plan, index) => (plan, index)).Where(x => x.plan.Source.FormKey == key)
            .ToDictionary(x => VariantIdentity(x.plan.Template.Item, x.plan.Template.Enchantment, catalog), x => x.index);
        var plannedAncestry = new Dictionary<FormKey, HashSet<FormKey>>();
        var count = 0;
        foreach (var group in selected)
        {
            var template = group.OrderBy(t => profile.EnchantmentProviderPlugins.FindIndex(p => p.Equals(t.Enchantment.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(t => t.Item.FormKey).First();
            var existing = inventory.Where(i => OrdinaryEquipment(i, catalog.KeywordNames) && TryEnchantment(i, out var e)
                && VariantIdentity(i, e, catalog) == group.Key).ToArray();
            var occupied = existing.SelectMany(i => CdfCoveredAncestors(i.FormKey, LootAncestors(i.FormKey, catalog), catalog)).ToHashSet();
            var authoredOccupied = occupied.ToHashSet();
            var priorIndex = priorIndices.GetValueOrDefault(group.Key, -1);
            if (priorIndex >= 0)
                foreach (var parent in plans[priorIndex].Parents)
                {
                    if (!plannedAncestry.TryGetValue(parent.List, out var ancestry))
                        plannedAncestry[parent.List] = ancestry = PlannedLootAncestors(parent.List, catalog);
                    occupied.UnionWith(ancestry);
                }
            var parents = new List<ParentEntry>();
            var tier = Math.Max((short)1, group.SelectMany(t => t.Parents).Select(p => p.Level).DefaultIfEmpty((short)1).Min());
            foreach (var (seed, routeUnlock) in routes)
            {
                if (catalog.CdfInventory?.Removals.Any == true && !CdfOutputAvailable(key, template, SpecializedGraph(catalog).Endpoints(seed.List), catalog, group.Key)) continue;
                var ancestry = LootAncestors(seed.List, catalog);
                if (ancestry.Overlaps(occupied))
                {
                    if (ancestry.Overlaps(authoredOccupied)) catalog.AuthoredCounterpartCoverage.Add(key);
                    continue;
                }
                occupied.UnionWith(ancestry);
                if (!pools.TryGetValue(seed.List, out var pool))
                {
                    var k = new FormKey(FallbackPlanningMod, (uint)(catalog.Fallbacks.Count + 1));
                    var list = new LeveledItem(k, SkyrimRelease.SkyrimSE)
                    { EditorID = $"Specialized_{DisplayName(source)}", Flags = (LeveledItem.Flag)3, ChanceNone = new Percent(0) };
                    catalog.AddList(k, list);
                    catalog.Fallbacks.Add(k, new($"specialized|{key}|{seed.List}|{seed.Level}", candidate.Category, list));
                    pool = (k, tier, seed, routeUnlock); pools.Add(seed.List, pool);
                }
                if (tier < pool.FirstTier) pools[seed.List] = pool with { FirstTier = tier };
                parents.Add(new(pool.Pool, tier, 1));
            }
            // Unlike baseline generation, do not create unused records for an explicitly confined route.
            if (parents.Count == 0) continue;
            if (priorIndex >= 0)
            {
                var prior = plans[priorIndex];
                plans[priorIndex] = prior with
                {
                    Parents = prior.Parents.Concat(parents).Distinct().ToArray(),
                    PlacementReason = "User-approved general and specialized routes; shared semantic variant and original availability controls",
                    DiagnosticIds = [],
                    ParentTemplates = MergeParentTemplates(prior, parents.Select(p => p.List).Distinct().ToDictionary(list => list, _ => template))
                };
            }
            else
                plans.Add(new(candidate, source, template, parents, existing.FirstOrDefault(), "User-approved specialized source pool; original ancestor controls retained"));
            count++;
        }
        foreach (var pool in pools.Values)
        {
            var parent = pool.Seed with { Level = Math.Max(pool.Seed.Level, pool.FirstTier) };
            // The slot choice holds the source at level 1; the pool joins it at its first tier.
            if (pool.Unlock is null && slotSeeds.Contains(pool.Seed.List))
                parent = new(BundleSlotWrapper(pool.Seed.List, key, candidate.Category, catalog), Math.Max((short)1, pool.FirstTier), 1);
            if (pool.Unlock is { } routeUnlock)
            {
                var payload = PayloadPathControls(routeUnlock.Payload.IsNull ? key : routeUnlock.Payload, key, catalog);
                AttachOtherMaterialBranch(key, new(parent, pool.Pool, new(payload.Controls), routeUnlock), catalog);
            }
            else catalog.FallbackEdges.Add(new(parent, pool.Pool));
        }
        Skip(count == 0 ? "equivalent variants already reachable; no additions" : $"{count} variants placed in {pools.Count} original source pools; ordinary/enchanted mixing explicitly enabled");
    }
}
