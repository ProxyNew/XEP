using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

// Scan's loaded plugins and analysis, which Preview and Generate reuse instead of
// loading again. Mod files do not change during a session (ADR 0006). Disposing
// releases the plugins and analysis.
public abstract class ScanSnapshot : IDisposable
{
    private protected ScanSnapshot() { }
    // Force re-analysis on the next operation without reading plugin files.
    // Settings dependencies are checked automatically at each operation boundary.
    public abstract void MarkStale();
    public abstract void Dispose();
}

public static partial class PatcherEngine
{
    // The completed prefix is never passed to selection, planning or output writing.
    // Loaded record getters and immutable input evidence can be shared; a pass owns
    // all changed adjacency, candidate state, decisions and graph-dependent caches.
    private sealed class ScanAnalysis : ScanSnapshot
    {
        private LoadedGame? game;
        private IReadOnlyList<ISkyrimModGetter>? mods;
        private AnalysisPass? prefix;
        private bool stale;
        private string analysisInputs = "";
        private string catalogInputs = "";
        private string baseTemplateInputs = "";
        private Catalog? baseCatalog;
        // The last reviewed catalog: its script and slice analysis (load-order facts plus
        // gates for its patched-mod set) seeds the next selection copy.
        private Catalog? scriptSource;
        // Review assessments depend on materials and origins through the graph and access
        // verdicts, so each material/origin set keeps its own reuse cache.
        private readonly Dictionary<string, ReviewAssessmentCache> assessmentsByTemplates = [];
        public int ReusedAssessments => assessmentsByTemplates.Values.Sum(c => c.Reused);
        private (string Choices, AnalysisPass State)? review;
        private PlannedPass? retainedPreview;
        private readonly SkyPatcherInputReport skyPatcher;
        private readonly CdfInputReport cdf;
        private readonly LlosInputReport llos;
        private readonly CidInputReport cid;
        private readonly SpidInputReport spid;
        private Dictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)>? winners;
        private PendingScriptAssets? scriptAssets;
        // Mod-author defaults captured with this snapshot (null when not opted in).
        private readonly AuthorOverlay? author;
        public AuthorOverlay? Author => author;

        public ScanAnalysis(AppSettings settings, IReadOnlyList<ISkyrimModGetter> mods, LoadedGame? game = null, SkyPatcherInputReport? inputs = null)
            : this(settings, mods, game, inputs, null) { }

        public ScanAnalysis(AppSettings settings, IReadOnlyList<ISkyrimModGetter> mods, LoadedGame? game, SkyPatcherInputReport? inputs, string? cdfInputData)
            : this(settings, mods, game, inputs, cdfInputData, null) { }

        public ScanAnalysis(AppSettings settings, IReadOnlyList<ISkyrimModGetter> mods, LoadedGame? game, SkyPatcherInputReport? inputs, string? cdfInputData, CdfInputReport? cdfInputs)
            : this(settings, mods, game, inputs, cdfInputData, cdfInputs, null) { }

        // Fixtures inject a captured LLOS report, as they do for the other native inputs.
        public ScanAnalysis(AppSettings settings, IReadOnlyList<ISkyrimModGetter> mods, LoadedGame? game, SkyPatcherInputReport? inputs, string? cdfInputData, CdfInputReport? cdfInputs, LlosInputReport? llosInputs)
            : this(settings, mods, game, inputs, cdfInputData, cdfInputs, llosInputs, null, null) { }

        // Frozen CID/SPID captures (tests) replace the configs read from the data folder.
        public ScanAnalysis(AppSettings settings, IReadOnlyList<ISkyrimModGetter> mods, LoadedGame? game, SkyPatcherInputReport? inputs, string? cdfInputData,
            CdfInputReport? cdfInputs, CidInputReport? cidInputs, SpidInputReport? spidInputs)
            : this(settings, mods, game, inputs, cdfInputData, cdfInputs, null, cidInputs, spidInputs) { }

        public ScanAnalysis(AppSettings settings, IReadOnlyList<ISkyrimModGetter> mods, LoadedGame? game, SkyPatcherInputReport? inputs, string? cdfInputData,
            CdfInputReport? cdfInputs, LlosInputReport? llosInputs, CidInputReport? cidInputs, SpidInputReport? spidInputs)
        {
            this.game = game;
            this.mods = mods;
            // One winner index serves SkyPatcher capture, the script-asset prefetch and
            // availability. Asset resolution reads only load-order facts, so it runs
            // while SkyPatcher inputs are captured and the catalog is built.
            using (PhaseTiming.Measure("load-order-winners")) winners = LoadOrderWinners(mods);
            if (ScriptTextOverride is null) scriptAssets = PendingScriptAssets.Start(settings.DataFolder ?? "", mods, winners);
            // SkyPatcher and LLOS read the same loaded records and native EditorID cache.
            var resolver = new NativeFormResolver(mods, () => winners);
            skyPatcher = inputs ?? SkyPatcherInputs.CaptureWith(settings.DataFolder, mods, resolver);
            skyPatcher = WithFurnitureListIrrelevance(skyPatcher, resolver, winners, scriptAssets);
            cdf = cdfInputs ?? CdfInputs.Capture(cdfInputData ?? settings.DataFolder, mods, () => winners);
            llos = llosInputs ?? LlosInputs.Capture(settings.DataFolder, mods, () => winners, resolver);
            cid = cidInputs ?? CidInputs.Capture(settings.DataFolder, mods, () => winners, resolver);
            spid = spidInputs ?? SpidInputs.Capture(settings.DataFolder, mods, () => winners, resolver);
            if (settings.ApplyAuthorRules && !string.IsNullOrWhiteSpace(settings.DataFolder))
                author = AuthorDefaults.Build(AuthorRules.LoadInstalled(settings.DataFolder,
                    new LoadedAuthorRecords(mods.Select(m => m.ModKey).ToHashSet(), winners)), mods);
            WithAuthor(settings, (effective, _) => { Analyse(effective); return 0; });
        }

        // Runs with the author overlay applied to a per-run copy of the settings, then
        // returns the engine's own profile writes to the saved settings.
        public T WithAuthor<T>(AppSettings settings, Func<AppSettings, AuthorApplication?, T> run)
        {
            if (author is null || author.IsEmpty) return run(settings, null);
            var (effective, applied) = AuthorDefaults.Apply(settings, author);
            var result = run(effective, applied);
            AuthorDefaults.CopyBack(effective, settings);
            return result;
        }

        private void Analyse(AppSettings settings)
        {
            if (settings.CurrentProfile.UseLoadOrderProviderDefaults is not false)
            {
                var providers = DiscoverProvidersLoaded(mods!, settings.CurrentProfile);
                if (providers.Count == 0) throw new InvalidOperationException("No usable enchantment origins are available in the active load order.");
            }
            var catalog = BuildNativeProfileCatalog(mods!, settings.CurrentProfile, skyPatcher, llos);
            catalog.Cdf = cdf;
            catalog.Llos = llos;
            catalog.Cid = cid;
            catalog.Spid = spid;
            // Reuse the winner index input capture already built for these plugins.
            catalog.RuntimeWinningRecords ??= winners;
            catalog.PrefetchedScriptAssets = Interlocked.Exchange(ref scriptAssets, null);
            catalog.DistributionNotes.AddRange(skyPatcher.Diagnostics);
            catalog.DistributionNotes.AddRange(cdf.Diagnostics);
            catalog.DistributionNotes.AddRange(llos.Diagnostics);
            catalog.DistributionNotes.AddRange(catalog.LlosLists?.Notes ?? []);
            catalog.DistributionNotes.AddRange(cid.Diagnostics);
            catalog.DistributionNotes.AddRange(spid.Diagnostics);
            // CDF analysis reads runtime list mutation, which availability establishes.
            InitializeAvailability(settings, catalog);
            if (catalog.ScriptAnalysisWarning is { } scriptWarning)
                catalog.DistributionNotes.Add(scriptWarning);
            // CID removals erase inventory objects as CDF removals do: one shared removal index,
            // also consulted when CDF decides which replacement predecessors are established.
            var cidRemovals = CidRemovals(cid, catalog);
            catalog.CdfInventory = CdfConsumers.Read(cdf, catalog, cidRemovals.Removals);
            if (cidRemovals.Removals.Count > 0)
                catalog.CdfInventory = catalog.CdfInventory with
                {
                    Removals = catalog.CdfInventory.Removals.With(cidRemovals.Removals, CdfConsumers.RespawningContainers(catalog).ToHashSet())
                };
            catalog.DistributionNotes.AddRange(cidRemovals.Notes);
            catalog.DistributionNotes.AddRange(catalog.CdfInventory.Notes);
            // The load-order analysis above is kept; selection-dependent state is
            // rebuilt on copies of it (Select), so selection changes skip this work.
            baseCatalog = catalog;
            assessmentsByTemplates.Clear();
            scriptSource = null;
            catalogInputs = CatalogInputs(settings);
            baseTemplateInputs = TemplateInputs(settings);
            Select(settings);
            Analyses++;
            SessionRuntime.Log($"Scan snapshot: built analysis {Analyses}.");
        }

        // Template selection, author allow-lists and candidates on a fresh copy of the
        // load-order analysis. Earlier prefixes, reviews and previews are discarded.
        private void Select(AppSettings settings)
        {
            using var timing = PhaseTiming.Measure("analysis.selection");
            var profile = settings.CurrentProfile;
            // Material families and origins only shape material resolution and the usable
            // templates; availability and CDF analysis of the load order do not read them.
            var changedTemplates = TemplateInputs(settings) != baseTemplateInputs;
            if (changedTemplates && profile.UseLoadOrderProviderDefaults is not false && DiscoverProvidersLoaded(mods!, profile).Count == 0)
                throw new InvalidOperationException("No usable enchantment origins are available in the active load order.");
            var templateKey = TemplateInputs(settings);
            Catalog catalog;
            if (changedTemplates)
            {
                var (keywords, templates) = MaterialsAndTemplates(mods!, baseCatalog!, profile);
                catalog = new Catalog(baseCatalog!) { KeywordNames = keywords, AllTemplates = templates,
                    BroadUnmatchedDistribution = profile.BroadUnmatchedDistribution };
            }
            else catalog = new Catalog(baseCatalog!) { BroadUnmatchedDistribution = profile.BroadUnmatchedDistribution };
            // Script analysis reads the load order and the patched-mod set, not selections:
            // reuse the last review's, and SetPatchedItems keeps its gates only while the
            // patched-mod set is unchanged.
            if (scriptSource is not null) { catalog.AdoptScriptAnalysis(scriptSource); ScriptReuses++; }
            if (!assessmentsByTemplates.TryGetValue(templateKey, out var assessments)) assessmentsByTemplates[templateKey] = assessments = new();
            catalog.AssessmentReuse = assessments;
            ApplyEnchantmentRecommendations(catalog, profile);
            prefix = new(catalog, FindDistributionCandidates(mods!, catalog, profile));
            analysisInputs = AnalysisInputs(settings);
        }

        // Inputs to catalog, availability and candidate analysis, plus the shared
        // material permission that changes review recommendations. Stored origin
        // order also determines mechanical-overlap recommendations.
        // Keep selections/policies out: the existing review key and each fresh plan
        // apply those to the reusable analysis.
        // Inputs to the load-order analysis (catalog, materials, origins' templates,
        // availability and CDF). Changing one of these rebuilds everything.
        private static string CatalogInputs(AppSettings settings)
            => JsonSerializer.Serialize(new { settings.DataFolder, settings.CurrentProfile.ResolveLeveledListConflicts });

        // Material families and origins: rebuilt on the copy by Select when they differ
        // from the ones the load-order analysis was built with.
        private static string TemplateInputs(AppSettings settings)
        {
            var profile = settings.CurrentProfile;
            return JsonSerializer.Serialize(new
            {
                Origins = profile.EnchantmentProviderPlugins,
                Materials = profile.MaterialFamilyOverrides.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            });
        }

        // Catalog inputs plus selection inputs (enchantment choices, author lists,
        // broad matching), which only Select reads. The material-route permission is
        // a review input (SpecializedReviewChoices), not an analysis input.
        private static string AnalysisInputs(AppSettings settings)
        {
            var profile = settings.CurrentProfile;
            return JsonSerializer.Serialize(new
            {
                Catalog = CatalogInputs(settings),
                Templates = TemplateInputs(settings),
                // Explicit script choices without a discovered source are retained
                // conservatively; adding/removing one changes the candidate prefix.
                ScriptSources = profile.SpecializedLootOverrides.Where(x => x.Value == SpecializedLootMode.ScriptDistribution)
                    .Select(x => x.Key).Order(StringComparer.OrdinalIgnoreCase),
                Enchantments = profile.EnchantmentOverrides.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase),
                AuthorEnchantments = profile.AuthorEnchantments.Order(StringComparer.OrdinalIgnoreCase),
                AuthorEnchantmentLists = profile.AuthorEnchantmentLists.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Key + "=" + string.Join(",", x.Value.Order(StringComparer.OrdinalIgnoreCase))),
                AuthorDestinationLists = profile.AuthorDestinationLists.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Key + "=" + string.Join(",", x.Value.Order(StringComparer.OrdinalIgnoreCase))),
                profile.BroadUnmatchedDistribution
            });
        }

        // Reuse state: analyses of the loaded plugins, specialized reviews and
        // planning passes.
        public int Analyses { get; private set; }
        // Selection-only changes that reused the load-order analysis.
        public int Selections { get; private set; }
        // Selections that started from the previous review's script analysis.
        public int ScriptReuses { get; private set; }
        // Reviews that kept the gates adopted from the previous review (same patched-mod set).
        public int ReusedGates { get; private set; }
        public int Reviews { get; private set; }
        public int PlanningPasses { get; private set; }

        // The specialized review caches each item's route and material assessments,
        // and the report notes they emit, for every pass with the same choices. Each
        // pass starts from a copy of the reviewed candidates and access state, so it
        // does not repeat the review. Other choices are reviewed afresh from the
        // unreviewed prefix.
        private AnalysisPass Reviewed(ProfileSettings profile)
        {
            ObjectDisposedException.ThrowIf(prefix is null, this);
            if (review is { } current && current.Choices == SpecializedReviewChoices(profile)) return current.State;
            review = null;
            var state = Fork(prefix);
            var adoptedGates = state.Catalog.ScriptGates;
            ReviewSpecializedChoices(state.Candidates, state.Catalog, profile);
            if (adoptedGates is not null && ReferenceEquals(state.Catalog.ScriptGates, adoptedGates)) ReusedGates++;
            review = (SpecializedReviewChoices(profile), state);
            scriptSource = state.Catalog;
            Reviews++;
            return state;
        }

        public override void MarkStale() => stale = true;

        // The previous analysis, its review and its retained preview are released
        // before the new analysis is built.
        public ScanAnalysis Current(AppSettings settings)
        {
            ObjectDisposedException.ThrowIf(mods is null, this);
            if (!stale && analysisInputs == AnalysisInputs(settings))
            {
                SessionRuntime.Log($"Scan snapshot: reused analysis {Analyses}.");
                return this;
            }
            if (!stale && baseCatalog is not null && catalogInputs == CatalogInputs(settings))
            {
                stale = true;
                prefix = null; review = null; retainedPreview = null;
                Select(settings);
                stale = false;
                Selections++;
                SessionRuntime.Log($"Scan snapshot: reselected analysis {Analyses} (selection change {Selections}).");
                return this;
            }
            // A failed analysis leaves the snapshot stale, so the next operation retries it.
            stale = true;
            prefix = null; review = null; retainedPreview = null; baseCatalog = null;
            Analyse(settings);
            stale = false;
            return this;
        }

        private static AnalysisPass Fork(AnalysisPass state)
        {
            using var timing = PhaseTiming.Measure("snapshot.fork");
            return new(new Catalog(state.Catalog), state.Candidates.Select(x => x.CopyForPass()).ToList());
        }

        public AnalysisPass BeginPass(AppSettings settings) => Fork(Reviewed(settings.CurrentProfile));

        public PlannedPass Plan(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies, Action<string>? progress)
        {
            var baseline = Reviewed(settings.CurrentProfile);
            var families = new Dictionary<FormKey, SpecializedLootMode>();
            var refusals = new List<string>();
            var passes = 0;
            while (true)
            {
                passes++;
                // Catalog copying is supported only on the analysis prefix. A
                // refused family must not carry copies, reservations or coverage
                // into its next attempt, including after final safety withdrawal.
                var pass = Fork(baseline);
                foreach (var (source, family) in families) pass.Catalog.BestAvailableFamilies[source] = family;
                var bundle = BuildPlans(settings, policies, pass.Catalog, pass.Candidates, progress);
                FinalizeProtectedRuntimePlacements(bundle.Plans, pass.Catalog, settings.CurrentProfile.WarningListEntries);
                var advanced = false;
                foreach (var (source, family) in pass.Catalog.BestAvailableFamilies.OrderBy(x => x.Key))
                {
                    if (family == SpecializedLootMode.GeneralLists || pass.Catalog.AuthoredCounterpartCoverage.Contains(source)
                        || bundle.Plans.Any(p => p.Source.FormKey == source && p.Parents.Count > 0)
                        || pass.Catalog.PlannedReplicatedSelectors.TryGetValue(source, out var selectors)
                            && selectors.Any(pass.Catalog.Fallbacks.ContainsKey)) continue;
                    var candidate = pass.Candidates.Single(c => c.FormKey == source.ToString());
                    refusals.Add($"{candidate.Name} [{source}]: Best available {family} attempt produced no surviving placement: {candidate.Reason}. Trying the next permitted family.");
                    // A display name is not an identity: a note naming only the item
                    // belongs to it only when no other candidate shares that name.
                    var uniqueName = pass.Candidates.Count(c => c.Name == candidate.Name) == 1;
                    refusals.AddRange(pass.Catalog.DistributionNotes.Where(note =>
                            note.Contains(source.ToString(), StringComparison.Ordinal)
                            || uniqueName && note.StartsWith(candidate.Name + ":", StringComparison.Ordinal))
                        .Select(note => $"Best available refused {family} attempt: {note}"));
                    families[source] = family == SpecializedLootMode.SpecializedEnchantedLists
                        ? SpecializedLootMode.SpecializedLists : SpecializedLootMode.GeneralLists;
                    advanced = true;
                }
                if (advanced) continue;
                pass.Catalog.DistributionNotes.AddRange(refusals);
                // Each item moves at most twice, but one move can leave another item
                // unproductive, so a run can need up to 2N+1 full passes for N items.
                if (passes > 1)
                    pass.Catalog.DistributionNotes.Add($"Best available needed {passes} planning passes: each retry replanned every item after moving refused items to their next family.");
                PlanningPasses++;
                return new(pass, bundle);
            }
        }

        // Generation writes into its planned pass, so a retained preview is used once.
        public PlannedPass PlanPreview(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies)
            => retainedPreview = Plan(settings, policies, null);

        // Preview-derived output facts, retained with the plans they were derived from.
        public void RetainDerived(PreviewDerived derived)
        {
            if (retainedPreview is { } preview) retainedPreview = preview with { Derived = derived };
        }

        public PlannedPass? TakePreview()
        {
            var preview = retainedPreview;
            retainedPreview = null;
            return preview;
        }

        public override void Dispose()
        {
            game?.Dispose();
            game = null; mods = null; winners = null; prefix = null; review = null; retainedPreview = null; baseCatalog = null; scriptSource = null;
        }
    }

    private sealed record AnalysisPass(Catalog Catalog, List<CandidateItem> Candidates);
    private sealed record PlannedPass(AnalysisPass Pass, PlanBundle Bundle)
    {
        public PreviewDerived? Derived { get; init; }
    }

    // Deterministic functions of the retained pass and settings; Generate reuses
    // them instead of recomputing projection, merging, impacts and fingerprint.
    private sealed record PreviewDerived(StaticEntryProjection Projection, Dictionary<FormKey, FormKey> Merged,
        List<ListImpact> Impacts, string Fingerprint, bool FullOutputPlugin);

    private static ScanAnalysis AnalyzeLoaded(AppSettings settings, IReadOnlyList<ISkyrimModGetter> mods)
        => new(settings, mods);

    private sealed class LoadedAuthorRecords(IReadOnlySet<ModKey> loaded,
        IReadOnlyDictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)> winners) : IAuthorRecordLookup
    {
        public bool IsLoaded(ModKey plugin) => loaded.Contains(plugin);
        public AuthorRecordKind Kind(FormKey record) => !winners.TryGetValue(record, out var winner) || winner.Record.IsDeleted ? AuthorRecordKind.Missing
            : winner.Record is IArmorGetter or IWeaponGetter ? AuthorRecordKind.Equipment
            : winner.Record is IObjectEffectGetter ? AuthorRecordKind.Enchantment
            : winner.Record is ILeveledItemGetter or IContainerGetter ? AuthorRecordKind.Destination
            : AuthorRecordKind.Other;
    }

    private static ScanAnalysis Loaded(ScanSnapshot snapshot) => snapshot as ScanAnalysis
        ?? throw new ArgumentException("Unknown Scan snapshot.", nameof(snapshot));

    internal static AuthorOverlay? AuthorOverlayOf(ScanSnapshot snapshot) => Loaded(snapshot).Author;

    private sealed partial class Catalog
    {
        public Catalog() => ObservePlanningChanges();

        // Script and slice analysis from another catalog over the same load order, with the
        // copy constructor's copy semantics. Everything here derives from the load order's
        // scripts and the patched-mod set; nothing reads templates, materials or selections.
        public void AdoptScriptAnalysis(Catalog source)
        {
            // A collection the copy constructor shares by reference is already the source's.
            static void Fill<TKey, TValue>(IDictionary<TKey, TValue> target, IEnumerable<KeyValuePair<TKey, TValue>> from)
            { if (ReferenceEquals(target, from)) return; var items = from.ToArray(); target.Clear(); foreach (var (key, value) in items) target[key] = value; }
            static void Set<T>(ISet<T> target, IEnumerable<T> from)
            { if (ReferenceEquals(target, from)) return; var items = from.ToArray(); target.Clear(); target.UnionWith(items); }
            static void List<T>(List<T> target, IEnumerable<T> from)
            { if (ReferenceEquals(target, from)) return; var items = from.ToArray(); target.Clear(); target.AddRange(items); }
            // This copy's receiver state is still the load order's, before any gates.
            GateFreeReceivers ??= (ScriptReceiverLevels.ToDictionary(x => x.Key, x => new HashSet<short>(x.Value)), new HashSet<FormKey>(ScriptUnresolvedReceivers));
            Fill(RuntimeScriptInstances, source.RuntimeScriptInstances);
            List(RuntimeBindingChecks, source.RuntimeBindingChecks);
            Fill(RuntimeHolderAssets, source.RuntimeHolderAssets);
            Fill(RuntimeHolderAttachments, source.RuntimeHolderAttachments);
            List(RuntimeHolderViewChecks, source.RuntimeHolderViewChecks);
            Fill(RuntimeHolderViewResults, source.RuntimeHolderViewResults);
            Fill(RuntimeListRootHolderVerdicts, source.RuntimeListRootHolderVerdicts);
            Fill(RuntimeBindings, source.RuntimeBindings);
            Fill(RuntimeSliceCallerCache, source.RuntimeSliceCallerCache);
            Fill(RuntimeScopeInstanceCache, source.RuntimeScopeInstanceCache);
            Fill(RuntimeSliceBindings, source.RuntimeSliceBindings);
            RuntimeSliceFormListCarriers.Clear();
            foreach (var (mod, carriers) in source.RuntimeSliceFormListCarriers) RuntimeSliceFormListCarriers[mod] = new(carriers);
            RuntimeSliceQuests.Clear();
            foreach (var (mod, quests) in source.RuntimeSliceQuests) RuntimeSliceQuests[mod] = new(quests);
            Fill(RuntimeSliceRecords, source.RuntimeSliceRecords);
            RuntimeSliceScriptAttachments = source.RuntimeSliceScriptAttachments is null ? null : new(source.RuntimeSliceScriptAttachments, source.RuntimeSliceScriptAttachments.Comparer);
            Fill(RuntimeSliceChainViews, source.RuntimeSliceChainViews);
            Fill(RuntimeSliceCarrierProblems, source.RuntimeSliceCarrierProblems);
            Fill(RuntimeSliceHolderViews, source.RuntimeSliceHolderViews);
            SliceFlows.Clear();
            foreach (var (mod, flow) in source.SliceFlows) SliceFlows[mod] = flow.CopyForPass();
            Fill(SliceSyntaxBodies, source.SliceSyntaxBodies);
            Fill(RuntimeSliceInstances, source.RuntimeSliceInstances);
            Set(RuntimeHolderChecks, source.RuntimeHolderChecks);
            Fill(RuntimeNativeContracts, source.RuntimeNativeContracts);
            Fill(RuntimeNativeContractProblems, source.RuntimeNativeContractProblems);
            Fill(RuntimeFrameEvidence, source.RuntimeFrameEvidence);
            Set(RuntimeObservedQuestCalls, source.RuntimeObservedQuestCalls);
            Fill(RuntimeFrameClosureProblems, source.RuntimeFrameClosureProblems);
            // Receivers stay gate-free, as in a fresh analysis (candidate discovery and the
            // first review steps read them); the source's gate effects wait for its gates.
            AdoptedGateReceivers = source.ScriptGates is null ? null
                : (source.ScriptReceiverLevels.ToDictionary(x => x.Key, x => new HashSet<short>(x.Value)), new HashSet<FormKey>(source.ScriptUnresolvedReceivers));
            ScriptSites = source.ScriptSites;
            ScriptGates = source.ScriptGates;
            PatchedItems = source.PatchedItems is null ? null : new(source.PatchedItems);
            PatchedMods = source.PatchedMods is null ? null : new(source.PatchedMods);
            List(ScriptGateRefusals, source.ScriptGateRefusals);
            foreach (var (root, verdict) in source.RuntimeListRootHolderVerdicts) RuntimeListRootHolderVerdicts[root] = verdict;
        }

        // Only call on the completed analysis prefix or its specialized review, never on a planned pass.
        // Defaults retain empty selection/output state. Copy seeded memo tables rather
        // than clearing them: some cached assessments already contributed report notes.
        [SetsRequiredMembers]
        public Catalog(Catalog source) : this()
        {
            PluginNames = new(source.PluginNames, source.PluginNames.Comparer);
            Mods = source.Mods;
            SkyPatcher = source.SkyPatcher;
            Cdf = source.Cdf;
            Llos = source.Llos;
            Cid = source.Cid;
            Spid = source.Spid;
            CdfInventory = source.CdfInventory;
            CdfDirectRoutes = new(source.CdfDirectRoutes);
            NativeLists = source.NativeLists;
            LlosLists = source.LlosLists;
            FrozenConsumerRecords = source.FrozenConsumerRecords;
            ScriptUnlocks = source.ScriptUnlocks;
            UnsafeAvailability = source.UnsafeAvailability;
            RuntimeControlledLists = source.RuntimeControlledLists;
            VerifiedRevertLists = source.VerifiedRevertLists;
            OtherRuntimeMutations = source.OtherRuntimeMutations;
            RuntimeMetadataMutations = source.RuntimeMetadataMutations;
            ScriptAddTargets = source.ScriptAddTargets;
            foreach (var (receiver, levels) in source.ScriptReceiverLevels) ScriptReceiverLevels[receiver] = new(levels);
            ScriptUnresolvedReceivers.UnionWith(source.ScriptUnresolvedReceivers);
            RuntimeAddPayloads = source.RuntimeAddPayloads;
            RuntimeAddPayloadProblems = source.RuntimeAddPayloadProblems;
            RuntimeBaseScripts = source.RuntimeBaseScripts;
            RuntimeBaseScriptDiagnosticEvidence = source.RuntimeBaseScriptDiagnosticEvidence;
            RuntimeScriptInstances = new(source.RuntimeScriptInstances, source.RuntimeScriptInstances.Comparer);
            RuntimeBindingChecks = new(source.RuntimeBindingChecks);
            RuntimePayloadExpressions = source.RuntimePayloadExpressions;
            RuntimeLocalArrayIdentities = source.RuntimeLocalArrayIdentities;
            RuntimeWinningRecords = source.RuntimeWinningRecords;
            RuntimeUnclosedHolderIndex = source.RuntimeUnclosedHolderIndex is null ? null : new(source.RuntimeUnclosedHolderIndex, source.RuntimeUnclosedHolderIndex.Comparer);
            RuntimeNonQuestHolderIndex = source.RuntimeNonQuestHolderIndex is null ? null : new(source.RuntimeNonQuestHolderIndex, source.RuntimeNonQuestHolderIndex.Comparer);
            RuntimeHolderAssets = new(source.RuntimeHolderAssets, source.RuntimeHolderAssets.Comparer);
            RuntimeHolderAttachments = new(source.RuntimeHolderAttachments, source.RuntimeHolderAttachments.Comparer);
            RuntimeHolderViewChecks = new(source.RuntimeHolderViewChecks);
            RuntimeHolderViewResults = new(source.RuntimeHolderViewResults, source.RuntimeHolderViewResults.Comparer);
            RuntimeFormListHolderParents = source.RuntimeFormListHolderParents is null ? null : new(source.RuntimeFormListHolderParents, source.RuntimeFormListHolderParents.Comparer);
            RuntimeAuthoredListCarriers = source.RuntimeAuthoredListCarriers is null ? null : new(source.RuntimeAuthoredListCarriers, source.RuntimeAuthoredListCarriers.Comparer);
            // Set once during availability construction and never changed afterwards.
            RuntimePossibleListCarriers = source.RuntimePossibleListCarriers;
            RuntimeQuestHolderIndex = source.RuntimeQuestHolderIndex is null ? null : new(source.RuntimeQuestHolderIndex, source.RuntimeQuestHolderIndex.Comparer);
            InitializeResolvers = source.InitializeResolvers;
            RuntimeBindings = new(source.RuntimeBindings, source.RuntimeBindings.Comparer);
            RuntimeSliceCallerCache = new(source.RuntimeSliceCallerCache, source.RuntimeSliceCallerCache.Comparer);
            RuntimeScopeInstanceCache = new(source.RuntimeScopeInstanceCache, source.RuntimeScopeInstanceCache.Comparer);
            RuntimeSliceBindings = new(source.RuntimeSliceBindings, source.RuntimeSliceBindings.Comparer);
            RuntimeListTaints = source.RuntimeListTaints;
            RuntimeSliceFormListCarriers = new(source.RuntimeSliceFormListCarriers.Select(x => KeyValuePair.Create(x.Key, new Dictionary<FormKey, FormKey[]>(x.Value))));
            RuntimeSliceQuests = source.RuntimeSliceQuests.ToDictionary(x => x.Key, x => new HashSet<FormKey>(x.Value));
            RuntimeSliceRecords = new(source.RuntimeSliceRecords, source.RuntimeSliceRecords.Comparer);
            RuntimeSliceScriptAttachments = source.RuntimeSliceScriptAttachments is null ? null : new(source.RuntimeSliceScriptAttachments, source.RuntimeSliceScriptAttachments.Comparer);
            RuntimeSliceChainViews = new(source.RuntimeSliceChainViews, source.RuntimeSliceChainViews.Comparer);
            RuntimeSliceCarrierProblems = new(source.RuntimeSliceCarrierProblems, source.RuntimeSliceCarrierProblems.Comparer);
            RuntimeSliceHolderViews = new(source.RuntimeSliceHolderViews, source.RuntimeSliceHolderViews.Comparer);
            SliceFlows = source.SliceFlows.ToDictionary(x => x.Key, x => x.Value.CopyForPass());
            SliceSyntaxBodies = source.SliceSyntaxBodies;
            RuntimeSliceInstances = new(source.RuntimeSliceInstances, source.RuntimeSliceInstances.Comparer);
            RuntimeHolderChecks = source.RuntimeHolderChecks;
            RuntimeNativeContracts = new(source.RuntimeNativeContracts, source.RuntimeNativeContracts.Comparer);
            RuntimeNativeContractProblems = new(source.RuntimeNativeContractProblems, source.RuntimeNativeContractProblems.Comparer);
            RuntimeFrameEvidence = new(source.RuntimeFrameEvidence, source.RuntimeFrameEvidence.Comparer);
            RuntimeObservedQuestCalls = new(source.RuntimeObservedQuestCalls, source.RuntimeObservedQuestCalls.Comparer);
            RuntimeFrameClosureProblems = new(source.RuntimeFrameClosureProblems, source.RuntimeFrameClosureProblems.Comparer);
            RuntimeActorReferences = source.RuntimeActorReferences is null ? null : new(source.RuntimeActorReferences, source.RuntimeActorReferences.Comparer);
            RuntimeFormLists = source.RuntimeFormLists is null ? null : new(source.RuntimeFormLists, source.RuntimeFormLists.Comparer);
            ScriptInsertionParents = source.ScriptInsertionParents;
            UnresolvedScriptPayloads = source.UnresolvedScriptPayloads;
            ScriptCalls = source.ScriptCalls;
            ScriptConflicts = source.ScriptConflicts;
            ScriptAnalysisWarning = source.ScriptAnalysisWarning;
            // Reviewed access state for these candidates; a pass fills its own index copy.
            ReviewedChoices = source.ReviewedChoices;
            Access = source.Access is null ? null : new(source.Access);
            foreach (var (key, verdict) in source.AccessVerdicts) AccessVerdicts[key] = verdict;
            foreach (var (key, roots) in source.AccessDestinations) AccessDestinations[key] = roots;
            // Review evidence over the frozen graph and load order, and merchant
            // availability, which planning clears whenever it adds a parent.
            LootConsumers = source.LootConsumers;
            LootConsumerEvidenceIndex = source.LootConsumerEvidenceIndex;
            LootAvailabilityNames = source.LootAvailabilityNames;
            foreach (var (key, availability) in source.MerchantAvailabilityCache) MerchantAvailabilityCache[key] = availability;
            // Selection-keyed script state: valid for the patched-item/mod set it was
            // built for (SetPatchedItems resets it when that set changes) and for the
            // loaded script instances (ScriptSites checks their count). Built before
            // planning, so it reads no pass-owned planning state.
            PatchedItems = source.PatchedItems is null ? null : new(source.PatchedItems);
            PatchedMods = source.PatchedMods is null ? null : new(source.PatchedMods);
            ScriptSites = source.ScriptSites;
            ScriptGates = source.ScriptGates;
            ScriptGateRefusals.AddRange(source.ScriptGateRefusals);
            foreach (var (root, verdict) in source.RuntimeListRootHolderVerdicts) RuntimeListRootHolderVerdicts[root] = verdict;
            GeneralLootCache = new(source.GeneralLootCache, source.GeneralLootCache.Comparer);
            LootAncestorCache = source.LootAncestorCache.ToDictionary(x => x.Key, x => new HashSet<FormKey>(x.Value));
            LootConsumerCountCache = new(source.LootConsumerCountCache, source.LootConsumerCountCache.Comparer);
            AvailabilityInitialized = source.AvailabilityInitialized;
            SpecializedEvidence = source.SpecializedEvidence is null ? null : new(source.SpecializedEvidence);
            AssessmentReuse = source.AssessmentReuse;
            GateFreeReceivers = source.GateFreeReceivers;
            AdoptedGateReceivers = source.AdoptedGateReceivers;
            // Built with the graph; a reused graph does not rebuild it.
            SpidProtectedOutfits = source.SpidProtectedOutfits;
            MirrorPayloadMembership = new(source.MirrorPayloadMembership, source.MirrorPayloadMembership.Comparer);
            MerchantEligibilityCache = new(source.MerchantEligibilityCache, source.MerchantEligibilityCache.Comparer);
            MerchantVendorIndex = source.MerchantVendorIndex is null ? null : new(source.MerchantVendorIndex);
            MerchantPlacedReferences = source.MerchantPlacedReferences is null ? null : new(source.MerchantPlacedReferences, source.MerchantPlacedReferences.Comparer);
            MerchantExactSourceRoutes = source.MerchantExactSourceRoutes.ToDictionary(x => x.Key, x => x.Value.ToList());
            MerchantStructuredSourceRoutes = source.MerchantStructuredSourceRoutes.ToDictionary(x => x.Key, x => (x.Value.Routes.ToList(), x.Value.Evidence));
            StaticSourceItems = source.StaticSourceItems is null ? null : new(source.StaticSourceItems, source.StaticSourceItems.Comparer);
            Armors = source.Armors;
            Weapons = source.Weapons;
            Lists = source.Lists;
            WinningLists = source.WinningLists;
            ConflictResolutions = source.ConflictResolutions;
            KeywordNames = source.KeywordNames;
            EnchantmentNames = source.EnchantmentNames;
            MagicEffectNames = source.MagicEffectNames;
            DistributedItems = new(source.DistributedItems, source.DistributedItems.Comparer);
            DirectParents = source.DirectParents.ToDictionary(x => x.Key, x => x.Value.ToList());
            Templates = source.Templates;
            AllTemplates = source.AllTemplates;
            TemplatesByClass = source.TemplatesByClass;
            SourceEnchantmentAllow = source.SourceEnchantmentAllow;
            SourceDestinationAllow = source.SourceDestinationAllow;
            ExactTemplateSelections = source.ExactTemplateSelections.ToDictionary(x => x.Key, x => new Dictionary<(int, string?, bool), EnchantedTemplate[]>(x.Value));
            DestinationCache = new(source.DestinationCache, source.DestinationCache.Comparer);
            NestedItemCache = new(source.NestedItemCache, source.NestedItemCache.Comparer);
            NestedListCache = new(source.NestedListCache, source.NestedListCache.Comparer);
            DestinationSubtypes = source.DestinationSubtypes.ToDictionary(x => x.Key, x => x.Value is null ? null : new HashSet<string>(x.Value, x.Value.Comparer));
            ExistingVariantCache = new(source.ExistingVariantCache, source.ExistingVariantCache.Comparer);
            ObjectEffects = source.ObjectEffects;
            MagicEffectRecords = source.MagicEffectRecords;
            EnchantmentRecommendations = source.EnchantmentRecommendations;
            QuestLists = source.QuestLists;
            DistributionNotes = new(source.DistributionNotes);
            EffectIdentities = new(source.EffectIdentities, source.EffectIdentities.Comparer);
            SourceEquipment = source.SourceEquipment;
            DistributedBases = new(source.DistributedBases, source.DistributedBases.Comparer);
            WinningPlugins = source.WinningPlugins;
            BroadUnmatchedDistribution = source.BroadUnmatchedDistribution;
            consumerRecords = source.ConsumerRecords;
            InitializeResolvers?.Invoke(this);
        }
    }
}
