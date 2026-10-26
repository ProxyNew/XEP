using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    public const string OutputFileName = "XEnchantmentPatcher.esp";

    private sealed record EquipmentClass(string Signature, string Material, string Type, ArmorType? ArmorType)
    {
        public override string ToString() => Material == "Jewelry" ? $"Jewelry {Type}"
            : ArmorType is null ? $"{Material} {Type}" : $"{Material} {ArmorType} {Type}";
    }

    private sealed record EquipmentShape(string Signature, string Type, ArmorType? ArmorType, string[] Materials)
    {
        public string Group => Signature == "WEAP" ? $"WEAP|{(Type is "Greatsword" or "Battleaxe" or "Warhammer" ? "2H" : "1H")}"
            : IsJewelryType(Type) ? $"ARMO|Jewelry|{Type}" : $"ARMO|{ArmorType}";
        public override string ToString() => $"{string.Join('/', Materials)} {ArmorType} {Type}".Replace("  ", " ").Trim();
    }

    private sealed record ParentEntry(FormKey List, short Level, short Count);

    private sealed class EnchantedTemplate
    {
        public required IMajorRecordGetter Item { get; init; }
        public required IMajorRecordGetter BaseItem { get; init; }
        public required EquipmentClass Class { get; init; }
        public required FormKey Enchantment { get; init; }
        public required string EnchantmentName { get; init; }
        public required List<ParentEntry> Parents { get; init; }
    }

    private sealed partial class Catalog
    {
        public Dictionary<IMajorRecordGetter, EquipmentFacts> EquipmentFacts { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<ModKey, string> PluginNames { get; } = [];
        public IReadOnlyList<ISkyrimModGetter> Mods { get; init; } = [];
        public SkyPatcherInputReport? SkyPatcher { get; set; }
        public CdfInputReport? Cdf { get; set; }
        public LlosInputReport? Llos { get; set; }
        public CidInputReport? Cid { get; set; }
        public SpidInputReport? Spid { get; set; }
        public CdfConsumers? CdfInventory { get; set; }
        public SkyPatcherLists? NativeLists { get; init; }
        public LlosLists? LlosLists { get; init; }
        private WinningConsumerRecords? consumerRecords;
        public WinningConsumerRecords? FrozenConsumerRecords { get; init; }
        public WinningConsumerRecords ConsumerRecords => consumerRecords ??= FrozenConsumerRecords ?? new(Mods, LlosLists is { HasContainerEdits: true } llos ? llos.ContainerSwap : null, LlosLists is { HasContainerEdits: true } withheld ? withheld.ContainerWithheld : null);
        public Dictionary<FormKey, List<ScriptUnlock>> ScriptUnlocks { get; } = [];
        public Dictionary<FormKey, string> UnsafeAvailability { get; } = [];
        public Dictionary<FormKey, string> RuntimeControlledLists { get; } = [];
        public Dictionary<FormKey, HashSet<ModKey>> VerifiedRevertLists { get; } = [];
        public HashSet<FormKey> OtherRuntimeMutations { get; } = [];
        public HashSet<FormKey> RuntimeMetadataMutations { get; } = [];
        public HashSet<FormKey> ScriptAddTargets { get; } = [];
        // Possible fixed payloads, independent of whether execution is proved.
        // A tainted target can never use the source-disjoint runtime proof.
        public Dictionary<FormKey, List<RuntimeAddPayload>> RuntimeAddPayloads { get; } = [];
        public Dictionary<FormKey, string> RuntimeAddPayloadProblems { get; } = [];
        public Dictionary<string, (string? Text, string Evidence)> RuntimeBaseScripts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RuntimeBaseScriptDiagnosticEvidence { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ScriptInstance> RuntimeScriptInstances { get; } = new(StringComparer.Ordinal);
        public List<RuntimeBindingCheck> RuntimeBindingChecks { get; } = [];
        public List<RuntimePayloadExpressionDiagnostic> RuntimePayloadExpressions { get; } = [];
        public List<RuntimeLocalArrayIdentity> RuntimeLocalArrayIdentities { get; } = [];
        public Dictionary<FormKey, (ISkyrimMajorRecordGetter Record, ModKey Plugin)>? RuntimeWinningRecords { get; set; }
        public Dictionary<FormKey, string[]>? RuntimeUnclosedHolderIndex { get; set; }
        public Dictionary<FormKey, RuntimePublicHolder[]>? RuntimeNonQuestHolderIndex { get; set; }
        public Func<string, RuntimeHolderAsset>? RuntimeHolderAssetResolver { get; set; }
        public Dictionary<string, RuntimeHolderAsset> RuntimeHolderAssets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RuntimeHolderAttachment> RuntimeHolderAttachments { get; } = new(StringComparer.Ordinal);
        public List<RuntimeHolderViewCheck> RuntimeHolderViewChecks { get; } = [];
        public Dictionary<FormKey, RuntimeHolderViewCheck> RuntimeHolderViewResults { get; } = [];
        // Empty-frame, non-slice holder verdicts by list root. Each records the mutable
        // inputs it read and is reused only while they match, including in later passes.
        public Dictionary<FormKey, RuntimeListRootHolderVerdict> RuntimeListRootHolderVerdicts { get; } = [];
        public Dictionary<FormKey, FormKey[]>? RuntimeFormListHolderParents { get; set; }
        public Dictionary<FormKey, RuntimeListCarrier[]>? RuntimeAuthoredListCarriers { get; set; }
        public Dictionary<FormKey, RuntimeListCarrier[]>? RuntimePossibleListCarriers { get; set; }
        public Dictionary<FormKey, RuntimeQuestHolder[]>? RuntimeQuestHolderIndex { get; set; }
        // Rebind lazy script proofs to the pass that owns their writes.
        public Action<Catalog>? InitializeResolvers { get; set; }
        public Dictionary<(ScriptInstance, string, bool), RuntimePropertyBinding> RuntimeBindings { get; } = [];
        public Dictionary<(FormKey, ModKey), (IReadOnlyList<ScriptInstance> Callers, string? Problem)> RuntimeSliceCallerCache { get; } = [];
        public Dictionary<ModKey, IReadOnlyList<ScriptInstance>> RuntimeScopeInstanceCache { get; } = [];
        public Func<ScriptInstance, string, bool, RuntimePropertyBinding>? RuntimeBindingResolver { get; set; }
        // The resolver InitializeResolvers bound to this catalog, and the analysis-wide
        // family it belongs to. A replaced resolver no longer matches its origin.
        public (Delegate Resolver, object Family)? RuntimeBindingResolverOrigin { get; set; }
        // Script distribution only (ADR 0008/0009): source-mod and slice-bounded proofs,
        // and the script behind every list taint, so other routes keep whole-frame proofs.
        public Func<ScriptInstance, string, bool, RuntimePropertyBinding>? RuntimeSliceBindingResolver { get; set; }
        // Its results carry warnings from the patched mods, so a new selection clears them.
        public Dictionary<(ScriptInstance, string, bool), RuntimePropertyBinding> RuntimeSliceBindings { get; } = [];
        public Dictionary<FormKey, List<RuntimeListTaint>> RuntimeListTaints { get; } = [];
        // Slice proofs are per mod (ADR 0008): each cache is keyed by the mod whose scope it proves.
        public ConcurrentDictionary<ModKey, Dictionary<FormKey, FormKey[]>> RuntimeSliceFormListCarriers { get; } = new();
        public Dictionary<ModKey, HashSet<FormKey>> RuntimeSliceQuests { get; } = [];
        public ConcurrentDictionary<(FormKey Key, ModKey Mod), bool> RuntimeSliceRecords { get; } = new();
        public Dictionary<string, FormKey[]>? RuntimeSliceScriptAttachments { get; set; }
        public Dictionary<string, (ScriptInstance? View, string? Problem)> RuntimeSliceChainViews { get; } = new(StringComparer.Ordinal);
        public Dictionary<(FormKey Carrier, ModKey Source), string?> RuntimeSliceCarrierProblems { get; } = [];
        public Dictionary<(FormKey Target, ModKey Source), RuntimeHolderViewCheck> RuntimeSliceHolderViews { get; } = [];
        public Dictionary<ModKey, SliceFlowState> SliceFlows { get; } = [];
        // Guards the slice-analysis memo tables and lazy loaders while per-mod flows run in parallel.
        public object SliceLock { get; } = new();
        // Immutable body syntax shared by passes of this analysis; no flow verdicts.
        public Dictionary<(ModKey Mod, string Identity), SliceBody> SliceSyntaxBodies { get; } = [];
        public Func<FormKey, ModKey, IReadOnlyList<ScriptInstance>>? RuntimeSliceCallers { get; set; }
        // Every quest and alias script of a mod's scope, loaded on first use.
        public Func<ModKey, IReadOnlyList<ScriptInstance>>? RuntimeScopeInstances { get; set; }
        // Scope instances loaded only for slice proofs, kept apart from other routes' instances.
        public Dictionary<string, ScriptInstance> RuntimeSliceInstances { get; } = new(StringComparer.Ordinal);
        // Mods being patched: their own analyses are combined on lists they share (ADR 0008).
        // Null until a selection is known; then only each source mod counts.
        public HashSet<FormKey>? PatchedItems { get; set; }
        public HashSet<ModKey>? PatchedMods { get; set; }
        public HashSet<RuntimeHolderCheck> RuntimeHolderChecks { get; } = [];
        public ConcurrentDictionary<string, bool> RuntimeNativeContracts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RuntimeNativeContractProblems { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(FormKey Owner, string Property, bool Receiver), string> RuntimeFrameEvidence { get; } = [];
        public HashSet<RuntimeQuestDispatch> RuntimeObservedQuestCalls { get; } = [];
        public Dictionary<FormKey, string> RuntimeFrameClosureProblems { get; } = [];
        public Dictionary<FormKey, bool>? RuntimeActorReferences { get; set; }
        public Dictionary<FormKey, IFormListGetter>? RuntimeFormLists { get; set; }
        // Possible third-party AddForm edges. These are proof evidence, not XEP placements.
        public Dictionary<FormKey, HashSet<FormKey>> ScriptInsertionParents { get; } = [];
        public HashSet<FormKey> UnresolvedScriptPayloads { get; } = [];
        public List<ScriptCallDiagnostic> ScriptCalls { get; } = [];
        public List<string> ScriptConflicts { get; } = [];
        public string? ScriptAnalysisWarning { get; set; }
        public Dictionary<FormKey, (bool General, int Contexts, string Detail)> GeneralLootCache { get; } = [];
        public Dictionary<FormKey, HashSet<FormKey>> LootAncestorCache { get; } = [];
        public Dictionary<FormKey, int> LootConsumerCountCache { get; } = [];
        public PlanningAdditions<RuntimePlacement> RuntimePlacements { get; } = [];
        public RuntimeMirrorCounterpartCache? MirrorCounterparts { get; set; }
        // Consumed once by InitializeAvailability on the analysis catalog; never copied.
        public PendingScriptAssets? PrefetchedScriptAssets { get; set; }
        public Dictionary<MirrorGateIdentity, MirrorGateResult> MirrorOriginalGates { get; } = [];
        public ScriptSiteScan? ScriptSites { get; set; }
        public ScriptGate[]? ScriptGates { get; set; }
        public ScriptChainIndex? ScriptChains { get; set; }
        public List<string> ScriptGateRefusals { get; } = [];
        public Dictionary<FormKey, HashSet<short>> ScriptReceiverLevels { get; } = [];
        // Receiver state before any gates were built (set by AdoptScriptAnalysis). Gate
        // building adds the patched mods' effects; a new patched-mod set starts from here.
        public (Dictionary<FormKey, HashSet<short>> Levels, HashSet<FormKey> Unresolved)? GateFreeReceivers { get; set; }
        // Receiver state after the adopted gates were built (set by AdoptScriptAnalysis).
        // A fresh analysis only has these effects once it builds gates, so they apply when
        // ScriptDistributionGates first returns the kept gates, and are dropped with them.
        public (Dictionary<FormKey, HashSet<short>> Levels, HashSet<FormKey> Unresolved)? AdoptedGateReceivers { get; set; }
        public HashSet<FormKey> ScriptUnresolvedReceivers { get; } = [];
        // Source/semantic variant/terminal triples written directly into a flat
        // enchanted destination. Final planning can withdraw only these entries
        // if the terminal would otherwise require probability-changing overflow.
        public HashSet<(FormKey Source, string Variant, FormKey Target)> ShapePreservingDirectPlacements { get; } = [];
        public Dictionary<FormKey, MaterialChance> MaterialChanceApplications { get; } = [];
        public Dictionary<FormKey, (FormKey List, FormKey Global)> MaterialGlobalApplications { get; } = [];
        public Dictionary<(FormKey Child, MaterialChance Chance), FormKey> MaterialChanceWrappers { get; } = [];
        public Dictionary<(FormKey Child, FormKey List, FormKey Global), FormKey> MaterialGlobalWrappers { get; } = [];
        public Dictionary<(FormKey Child, FormKey List, int Maximum), FormKey> MaterialWindowWrappers { get; } = [];
        public FormKey AvailabilityEmptyLeaf { get; set; }
        // One control chain per (parent entry, controls); its container holds every member pool.
        public Dictionary<(ParentEntry Parent, string Controls), (FormKey Container, FormKey Top)> SharedAvailabilityChains { get; } = [];
        public HashSet<FormKey> SharedAvailabilityMembers { get; } = [];
        public Dictionary<FormKey, List<LootConsumerEvidence>>? LootConsumerEvidenceIndex { get; set; }
        public Dictionary<FormKey, string>? LootAvailabilityNames { get; set; }
        public HashSet<FormKey>? LootConsumers { get; set; }
        public bool AvailabilityInitialized { get; set; }
        public SpecializedEvidence? SpecializedEvidence { get; set; }
        // Prefetched review assessments kept across selection changes of one load-order
        // analysis and material/origin set (see ReviewAssessmentCache). Shared by forks.
        public ReviewAssessmentCache? AssessmentReuse { get; set; }
        public Dictionary<(FormKey Payload, FormKey Source), bool> MirrorPayloadMembership { get; } = [];
        // Enchanted-access evidence, rebuilt by every ApplySpecializedChoices call. Verdicts and
        // destinations are frozen there so planning pools never become evidence.
        public AccessIndex? Access { get; set; }
        // The review choices (SpecializedReviewChoices) this catalog's candidates and
        // access evidence were reviewed with, until planning begins; null otherwise.
        public string? ReviewedChoices { get; set; }
        public Dictionary<FormKey, AccessVerdict> AccessVerdicts { get; } = [];
        public Dictionary<FormKey, HashSet<FormKey>> AccessDestinations { get; } = [];
        public Dictionary<string, FormKey> SpecializedPathClones { get; } = [];
        // Isolated list copy -> the boundary it is reached through, narrowed to an isolated
        // loot recipient or merchant edge. Recorded when the copy is made.
        public Dictionary<FormKey, IsolatedCopyScope> IsolatedCopyScopes { get; } = [];
        // Context-isolated copy -> the original list it was copied from. XEP additions
        // to the original are mirrored into its copies when the output is written.
        public PlanningCopyTopology CopyTopology { get; } = new();
        public IReadOnlyDictionary<FormKey, FormKey> MaterialFamilyOriginals => CopyTopology.Families;
        public IReadOnlyDictionary<FormKey, FormKey> SpecializedCloneOriginals => CopyTopology.Originals;
        public Dictionary<(FormKey Selector, string Variant), FormKey> ReplicatedSelectors { get; } = [];
        public Dictionary<FormKey, ILeveledItemGetter> SpecializedRewrites { get; } = [];
        // Authored Give All bundles whose direct slots were rewritten in place to slot
        // choices (UseAllBundleSlots). Written even when no addition targets them.
        public HashSet<FormKey> BundleSlotOwners { get; } = [];
        // Merchant container -> generated stock root. These are output attachments,
        // rather than leveled-list edges, and therefore form additional reachability roots.
        public Dictionary<FormKey, FormKey> MerchantContainerRoots { get; } = [];
        public Dictionary<FormKey, FormKey> MerchantSourcePools { get; } = [];
        public HashSet<(FormKey Container, FormKey Source)> MerchantAttachments { get; } = [];
        public Dictionary<FormKey, FormKey[]> MerchantEligibilityCache { get; } = [];
        public Dictionary<FormKey, MerchantAvailability> MerchantAvailabilityCache { get; } = [];
        public List<MerchantVendor>? MerchantVendorIndex { get; set; }
        public Dictionary<FormKey, FormKey[]>? MerchantPlacedReferences { get; set; }
        public Dictionary<FormKey, List<MerchantSourceRoute>> MerchantExactSourceRoutes { get; } = [];
        public Dictionary<FormKey, (List<MerchantSourceRoute> Routes, string[] Evidence)> MerchantStructuredSourceRoutes { get; } = [];
        public HashSet<FormKey>? StaticSourceItems { get; set; }
        public List<CountedMerchantRequest> CountedMerchantRequests { get; } = [];
        // Pass-local planning facts. Each specialized planner records sources whose
        // authored enchanted counterpart already reaches a route's consumer, and the
        // replicated selectors it planned; Best available reads them to decide
        // whether its chosen family placed anything. Authored coverage is distinct
        // from planned parents, which can still be withdrawn by safety finalization.
        public Dictionary<FormKey, SpecializedLootMode> BestAvailableFamilies { get; } = [];
        public HashSet<FormKey> AuthoredCounterpartCoverage { get; } = [];
        public Dictionary<FormKey, HashSet<FormKey>> PlannedReplicatedSelectors { get; } = [];
        public Dictionary<(FormKey Container, int Index), MerchantInventoryRewrite> MerchantInventoryRewrites { get; } = [];
        public Dictionary<string, LootRecipientRewrite> LootRecipientRewrites { get; } = [];
        public required Dictionary<FormKey, IArmorGetter> Armors { get; init; }
        public required Dictionary<FormKey, IWeaponGetter> Weapons { get; init; }
        public required Dictionary<FormKey, ILeveledItemGetter> WinningLists { get; init; }
        public Dictionary<FormKey, LeveledListConflictResolution> ConflictResolutions { get; init; } = [];
        public required Dictionary<FormKey, string> KeywordNames { get; init; }
        public required Dictionary<FormKey, string> EnchantmentNames { get; init; }
        public required Dictionary<FormKey, string> MagicEffectNames { get; init; }
        public required HashSet<FormKey> DistributedItems { get; init; }
        public required Dictionary<FormKey, List<ParentEntry>> DirectParents { get; init; }
        public required List<EnchantedTemplate> Templates { get; set; }
        public required List<EnchantedTemplate> AllTemplates { get; init; }
        public required Dictionary<string, List<EnchantedTemplate>> TemplatesByClass { get; set; }
        // Only frozen catalog-owned collections participate. Transient filtered
        // inputs may change and must use the uncached ranking path.
        public Dictionary<List<EnchantedTemplate>, Dictionary<(int Value, string? Subtype, bool Jewelry), EnchantedTemplate[]>> ExactTemplateSelections { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<(FormKey, string), bool> DestinationCache { get; } = [];
        public Dictionary<(FormKey, FormKey), bool> NestedItemCache { get; } = [];
        public Dictionary<(FormKey, FormKey), bool> NestedListCache { get; } = [];
        public Dictionary<FormKey, HashSet<string>?> DestinationSubtypes { get; } = [];
        public Dictionary<(FormKey List, FormKey Source, FormKey Enchantment), bool> ExistingVariantCache { get; } = [];
        public Dictionary<FormKey, FallbackNode> Fallbacks { get; } = [];
        public PlanningAdditions<FallbackEdge> FallbackEdges { get; } = [];
        public HashSet<FormKey> RebuiltFallbackRoots { get; } = [];
        public Dictionary<FormKey, IObjectEffectGetter> ObjectEffects { get; init; } = [];
        public HashSet<FormKey> QuestLists { get; init; } = [];
        public NoteList DistributionNotes { get; } = new();
        // Loot recipient copies already allocated for an (edge, path) pair of this
        // catalog; cleared when path clones are withdrawn.
        public Dictionary<(LootRecipientEdge Edge, IReadOnlyList<FormKey> Path), FormKey> RecipientCopies { get; } = new(RecipientCopyKey.Instance);
        // Other-material branch notes already formatted into DistributionNotes.
        public HashSet<(FormKey Source, FormKey Root, string Control, byte Kind)> OtherMaterialNotes { get; } = [];
        public List<RouteRejection> RouteDiagnostics { get; } = [];
        public Dictionary<FormKey, string> EffectIdentities { get; } = [];
        public Dictionary<FormKey, IMajorRecordGetter[]>? SourceEquipment { get; set; }
        public HashSet<FormKey> DistributedBases { get; set; } = [];
        public required Dictionary<FormKey, ModKey> WinningPlugins { get; init; }
        public bool BroadUnmatchedDistribution { get; init; }
    }

    private sealed record VariantPlan(CandidateItem Candidate, IMajorRecordGetter Source, EnchantedTemplate Template,
        IReadOnlyList<ParentEntry> Parents, IMajorRecordGetter? Reused = null, string? PlacementReason = null,
        IReadOnlyList<int>? DiagnosticIds = null, bool PreserveParentMultiplicity = false,
        IReadOnlyDictionary<FormKey, EnchantedTemplate>? ParentTemplates = null)
    {
        // A plan replacement is observable by the owning collection. Its parents
        // cannot change through a retained caller-owned array/list behind that owner.
        private IReadOnlyList<ParentEntry> parents = Array.AsReadOnly(Parents.ToArray());
        public IReadOnlyList<ParentEntry> Parents
        {
            get => parents;
            init => parents = Array.AsReadOnly(value.ToArray());
        }
    }
    private static List<RouteRejection> UsedRouteDiagnostics(Catalog catalog, List<VariantDecision> decisions)
    {
        var used = decisions.SelectMany(x => x.DiagnosticIds).ToHashSet();
        return catalog.RouteDiagnostics.Where(x => used.Contains(x.Id)).ToList();
    }

    private sealed record PlanBundle(List<CandidateItem> Candidates, PlanningAdditions<VariantPlan> Plans, int SourceCount);

    // Distribution notes are read through Distinct and Contains, so a repeated note
    // adds nothing: it is dropped on insertion and the first occurrence keeps its place.
    // The list base keeps every reader, including fixtures that cast to List<string>;
    // the interface is re-implemented so evidence sinks deduplicate as well.
    private sealed class NoteList : DiagnosticMessages, ICollection<string>
    {
        private readonly HashSet<string> present;
        public NoteList() { present = new(StringComparer.Ordinal); }
        public NoteList(NoteList source) : base(source) { present = new(source.present, StringComparer.Ordinal); }
        public override void Add(string note) { if (present.Add(note)) base.Add(note); }
        void ICollection<string>.Add(string note) => Add(note);
        public new void AddRange(IEnumerable<string> range) { foreach (var note in range) Add(note); }
        public new bool Contains(string note) => present.Contains(note);
        bool ICollection<string>.Contains(string note) => present.Contains(note);
    }

    private sealed class RecipientCopyKey : IEqualityComparer<(LootRecipientEdge Edge, IReadOnlyList<FormKey> Path)>
    {
        public static readonly RecipientCopyKey Instance = new();
        public bool Equals((LootRecipientEdge Edge, IReadOnlyList<FormKey> Path) x, (LootRecipientEdge Edge, IReadOnlyList<FormKey> Path) y)
            => ReferenceEquals(x.Edge, y.Edge) && ReferenceEquals(x.Path, y.Path);
        public int GetHashCode((LootRecipientEdge Edge, IReadOnlyList<FormKey> Path) key)
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Edge), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Path));
    }
    private sealed record CloneJob(int PatchIndex, FormKey Key, string EditorId, string Name, VariantPlan Plan);

    private static IMajorRecord CloneVariant(CloneJob job)
    {
        var source = job.Plan.Source;
        var template = job.Plan.Template;
        if (source is IWeaponGetter weapon && template.Item is IWeaponGetter enchantedWeapon)
        {
            var clone = weapon.Duplicate(job.Key);
            clone.EditorID = job.EditorId;
            clone.Name = job.Name;
            clone.Template.SetTo(source.FormKey);
            clone.ObjectEffect.SetTo(template.Enchantment);
            clone.EnchantmentAmount = enchantedWeapon.EnchantmentAmount;
            clone.BasicStats ??= new WeaponBasicStats();
            clone.BasicStats.Value = AdjustValue(weapon.BasicStats?.Value ?? 0, template.BaseItem, enchantedWeapon);
            return clone;
        }
        var armor = (IArmorGetter)source;
        var enchantedArmor = (IArmorGetter)template.Item;
        var armorClone = armor.Duplicate(job.Key);
        armorClone.EditorID = job.EditorId;
        armorClone.Name = job.Name;
        armorClone.TemplateArmor.SetTo(source.FormKey);
        armorClone.ObjectEffect.SetTo(template.Enchantment);
        armorClone.EnchantmentAmount = enchantedArmor.EnchantmentAmount;
        armorClone.Value = AdjustValue(armor.Value, template.BaseItem, enchantedArmor);
        return armorClone;
    }

    // Parallel replacement for a sequential loop over 0..count-1: every iteration runs,
    // and the lowest-index failure is rethrown unchanged, which is the exception the
    // sequential loop would have stopped at. Later iterations only fill result slots
    // or staging files, which a failure discards.
    private static void ParallelInOrder(int count, Action<int> body)
    {
        var failures = new Exception?[count];
        Parallel.For(0, count, i =>
        {
            try { body(i); }
            catch (Exception ex) { failures[i] = ex; }
        });
        if (failures.FirstOrDefault(f => f is not null) is { } first) ExceptionDispatchInfo.Capture(first).Throw();
    }
    private sealed record PendingEntry(ParentEntry Parent, FormKey Generated, FormKey Source, string SourceName,
        string GeneratedName, string EnchantmentName);

    public static List<EnchantmentProviderOption> DiscoverProviders(AppSettings settings)
    {
        using var session = PhaseTiming.Begin("discoverproviders");
        SettingsStore.SelectProfile(settings);
        using var game = LoadOrderProvider.Load(settings, OutputFileName);
        return DiscoverProvidersLoaded(game.Mods, settings.CurrentProfile);
    }

    private static List<EnchantmentProviderOption> DiscoverProvidersLoaded(IReadOnlyList<ISkyrimModGetter> mods, ProfileSettings profile)
    {
        var armors = mods.SelectMany(x => x.Armors).GroupBy(x => x.FormKey).ToDictionary(x => x.Key, x => x.Last());
        var weapons = mods.SelectMany(x => x.Weapons).GroupBy(x => x.FormKey).ToDictionary(x => x.Key, x => x.Last());
        var objectEffects = mods.SelectMany(x => x.ObjectEffects).GroupBy(x => x.FormKey).ToDictionary(x => x.Key, x => x.Last());
        var keywordNames = new KeywordIndex(mods.SelectMany(x => x.Keywords).GroupBy(x => x.FormKey)
            .ToDictionary(x => x.Key, x => x.Last().EditorID ?? ""));
        ResolveMaterials(mods, armors, weapons, keywordNames, profile.MaterialFamilyOverrides);
        var enchantNames = objectEffects.ToDictionary(x => x.Key,
            x => x.Value.Name?.String ?? x.Value.EditorID ?? x.Key.ToString());
        var lists = mods.SelectMany(x => x.LeveledItems).GroupBy(x => x.FormKey).ToDictionary(x => x.Key, x => x.Last());
        var directParents = BuildDirectParents(lists.Values);
        var activePlugins = mods.Select((mod, index) => (mod.ModKey, Index: index)).ToDictionary(x => x.ModKey, x => x.Index);
        var templates = BuildUsableTemplates(armors, weapons, objectEffects, keywordNames, enchantNames, directParents);

        var providers = templates.GroupBy(x => x.Enchantment.ModKey)
            .Where(x => activePlugins.ContainsKey(x.Key))
            .Select(x => new EnchantmentProviderOption(x.Key.FileName.String,
                x.Select(y => y.Enchantment).Distinct().Count(), x.Count()))
            .OrderByDescending(x => activePlugins[ModKey.FromFileName(x.Plugin)])
            .ToList();
        if (profile.UseLoadOrderProviderDefaults is not false)
        {
            profile.UseLoadOrderProviderDefaults = true;
            profile.EnchantmentProviderPlugins = providers.Select(x => x.Plugin).ToList();
            profile.EnchantmentProviderOrder = profile.EnchantmentProviderPlugins.ToList();
            profile.ProviderOrderMigrated = true;
        }
        return providers;
    }

    public static ScanResult Scan(AppSettings settings)
    {
        var result = Scan(settings, out var snapshot);
        snapshot.Dispose();
        return result;
    }

    // The caller owns the returned snapshot and disposes it when the session ends
    // or a new Scan replaces it.
    public static ScanResult Scan(AppSettings settings, out ScanSnapshot snapshot)
        => ScanWithCdfInputs(settings, out snapshot, null);

    // Private validation can vary only CDF input bytes while retaining the same
    // loaded game/profile. Normal calls always capture the projected Data folder.
    internal static ScanResult ScanWithCdfInputs(AppSettings settings, out ScanSnapshot snapshot, string? cdfInputData)
    {
        using var session = PhaseTiming.Begin("scan");
        SettingsStore.SelectProfile(settings);
        var game = LoadOrderProvider.Load(settings, OutputFileName);
        try
        {
            var analysis = new ScanAnalysis(settings, game.Mods, game, null, cdfInputData);
            var result = analysis.WithAuthor(settings, (effective, author) => ScanLoaded(effective, analysis, author));
            snapshot = analysis;
            return result;
        }
        catch { game.Dispose(); throw; }
    }

    // A later Scan of the same session, without loading the plugins again.
    public static ScanResult Scan(AppSettings settings, ScanSnapshot snapshot)
    {
        using var session = PhaseTiming.Begin("scan");
        SettingsStore.SelectProfile(settings);
        var analysis = Loaded(snapshot);
        return analysis.WithAuthor(settings, (effective, author) => ScanLoaded(effective, analysis.Current(effective), author));
    }

    private static ScanResult ScanLoaded(AppSettings settings, ScanAnalysis analysis, AuthorApplication? author = null)
    {
        var profile = settings.CurrentProfile;
        // The specialized review runs when the pass begins (unless already cached).
        ScanProgress.Enter(ScanStage.Review);
        var pass = analysis.BeginPass(settings);
        var catalog = pass.Catalog;
        var items = pass.Candidates;
        var rows = items.GroupBy(x => x.Plugin, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var prior = profile.Policies.GetValueOrDefault(g.Key, PatchPolicy.Review);
                prior = SettingsStore.NormalizePolicy(prior);
                return new PluginDecision
                {
                    Plugin = g.Key,
                    EligibleItems = g.Count(x => x.Eligible),
                    DistributedItems = g.Count(x => x.Eligible && x.AlreadyDistributed),
                    ScriptCandidateItems = g.Count(x => x.Eligible && (x.HasScriptDistributionSource
                        || x.SpecializedMode == SpecializedLootMode.ScriptDistribution || x.RecommendedSpecializedMode == SpecializedLootMode.ScriptDistribution)),
                    ExcludedItems = g.Count(x => !x.Eligible),
                    Examples = string.Join(", ", g.Where(x => x.Eligible).Take(4).Select(x => x.Name)),
                    Policy = prior,
                    AuthorPolicy = author?.Policies.GetValueOrDefault(g.Key)?.Value,
                    AuthorNote = author?.Policies.GetValueOrDefault(g.Key)?.Describe("patch policy") ?? ""
                };
            }).ToList();
        ScanProgress.Enter(ScanStage.Selection);
        EnsureSpecializedChoices(items, catalog, profile);
        foreach (var item in items) item.Selected = IsSelected(item, profile.Policies, profile);
        if (author is not null) ApplyAuthorProvenance(items, author, catalog);
        SetPatchedSelection(catalog, items);
        DescribeScriptDistribution(items, catalog);
        return new ScanResult
        {
            Plugins = rows,
            Items = items,
            Enchantments = BuildEnchantmentOptionsWithAuthor(catalog, profile, author),
            ActivePluginCount = catalog.Mods.Count,
            TemplateCount = catalog.Templates.Count,
            // Other mods' script Revert conflicts stay in the report, not the scan warnings.
            Warnings = (catalog.SkyPatcher?.Diagnostics ?? [])
                .Concat(catalog.Cdf?.Diagnostics ?? [])
                .Concat(catalog.CdfInventory?.Notes.Where(n => n.StartsWith("CDF analytical import unavailable:", StringComparison.Ordinal)
                    || n.StartsWith("CDF keyword membership unresolved", StringComparison.Ordinal)) ?? [])
                .Concat(catalog.NativeLists?.Notes ?? [])
                .Concat(catalog.LlosLists?.Notes ?? [])
                .Concat(catalog.Llos?.Diagnostics ?? [])
                .Concat(catalog.Cid?.Diagnostics ?? [])
                .Concat(catalog.Spid?.Diagnostics ?? [])
                .Concat(analysis.Author?.Rules.Diagnostics.Where(d => d.Severity != AuthorRuleSeverity.Info)
                    .Select(d => $"Author rules: {d}") ?? [])
                .Except(FrameworkInformation(catalog), StringComparer.Ordinal).ToList(),
            SkyPatcher = catalog.SkyPatcher,
            Cdf = catalog.Cdf,
            Llos = catalog.Llos,
            Cid = catalog.Cid,
            Spid = catalog.Spid,
            ScriptAnalysisWarning = catalog.ScriptAnalysisWarning,
            TemplateCategories = catalog.TemplatesByClass
                .OrderBy(x => x.Key)
                .Select(x => $"{x.Value[0].Class}: {x.Value.Count}")
                .ToList(),
            ScannedAt = DateTime.Now
        };
    }

    private static IEnumerable<string> FrameworkInformation(Catalog catalog) =>
        (catalog.SkyPatcher?.Information ?? [])
            .Concat(catalog.Cdf?.Information ?? [])
            .Concat(catalog.Llos?.Information ?? [])
            .Concat(catalog.Cid?.Information ?? [])
            .Concat(catalog.Spid?.Information ?? [])
            .Concat(catalog.NativeLists?.Notes.Information ?? [])
            .Concat(catalog.LlosLists?.Notes.Information ?? [])
            .Concat(catalog.DistributionNotes.Information);

    public static GenerationPreview Preview(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies)
    {
        using var session = PhaseTiming.Begin("preview");
        var requestedOutput = RequestedOutput(settings);
        using var game = LoadOrderProvider.Load(settings, Path.GetFileName(requestedOutput));
        return PreviewAnalysis(settings, policies, AnalyzeLoaded(settings, game.Mods));
    }

    // The snapshot retains these plans for a following Generate that reuses them.
    public static GenerationPreview Preview(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies, ScanSnapshot snapshot)
    {
        using var session = PhaseTiming.Begin("preview");
        var analysis = Loaded(snapshot);
        return analysis.WithAuthor(settings, (effective, author) =>
            PreviewAnalysis(effective, author?.EffectivePolicies(policies) ?? policies, analysis.Current(effective)));
    }

    private static GenerationPreview PreviewAnalysis(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies,
        ScanAnalysis analysis)
    {
        var requestedOutput = RequestedOutput(settings);
        var (pass, bundle) = analysis.PlanPreview(settings, policies);
        var catalog = pass.Catalog;
        var candidates = pass.Candidates;
        var protectedTargets = RevertSwitchGroups(catalog);
        OperationProgress.Stage("Projecting leveled-list changes…");
        var projection = EffectiveStaticEntries.Project(catalog, bundle.Plans);
        OperationProgress.Stage("Checking generated lists…");
        var merged = IdenticalGeneratedLists(catalog, projection);
        var collapse = CollapseGeneratedLists(catalog, projection, merged);
        ReportLlosStartupChanges(catalog, projection, merged);
        var impacts = BuildListImpacts(projection, settings.CurrentProfile.WarningListEntries)
            .Where(i => !merged.ContainsKey(FormKey.Factory(i.FormKey)) && !collapse.Removed(FormKey.Factory(i.FormKey))).ToList();
        ValidateRuntimeCapacity(projection, catalog);
        impacts = RevertSwitchImpacts(impacts, catalog, settings.CurrentProfile.WarningListEntries);
        var sublistCount = projection.ChangedLists.Sum(x => x.Tree.ChildCount) + catalog.Fallbacks.Count
            - MergedSublistCount(merged, projection) - collapse.RemovedCount;
        var newVariantCount = bundle.Plans.Count(p => p.Reused is null
            && (p.Parents.Count > 0 || settings.CurrentProfile.KeepUndistributedVariants));
        var layout = OutputLayout.For(settings.CurrentProfile);
        var newRecords = newVariantCount + sublistCount + LootRecipientRecordCount(catalog) + RuntimeRecordCount(catalog);
        var patchCount = layout.PatchCount(newRecords);
        var outputs = OutputPaths(requestedOutput, patchCount);
        catalog.NativeLists?.ValidateOutputPlugins(outputs);
        var fingerprint = Fingerprint(bundle.Plans, outputs, settings.CurrentProfile, catalog);
        analysis.RetainDerived(new PreviewDerived(projection, merged, impacts, fingerprint, settings.CurrentProfile.FullOutputPlugin));
        var previousFingerprint = PreviousFingerprint(settings, requestedOutput);
        var alternateFullOutput = !settings.CurrentProfile.FullOutputPlugin;
        var alternateIdentityChanged = false;
        // Compare both choices before confirmation, using the same identity inputs as generation.
        // A full layout beyond its capacity is not an available confirmation choice.
        if (previousFingerprint is { Length: > 0 } priorFingerprint &&
            (!alternateFullOutput || newRecords <= OutputLayout.Full.IdCount))
        {
            var alternateLayout = alternateFullOutput ? OutputLayout.Full : OutputLayout.Light;
            var alternateOutputs = OutputPaths(requestedOutput, alternateLayout.PatchCount(newRecords));
            alternateIdentityChanged = priorFingerprint != Fingerprint(bundle.Plans, alternateOutputs,
                settings.CurrentProfile, catalog, alternateFullOutput);
        }
        OperationProgress.Stage("Building preview report…");
        var warnings = new List<string>();
        // Script Revert conflicts and Revert-protection notices are report evidence, not preview warnings.
        warnings.AddRange(catalog.DistributionNotes.Except(FrameworkInformation(catalog), StringComparer.Ordinal));
        var undistributed = bundle.Plans.Count(p => p.Parents.Count == 0 && (p.Reused is null || !catalog.DistributedItems.Contains(p.Reused.FormKey)));
        if (undistributed > 0) warnings.Add(settings.CurrentProfile.KeepUndistributedVariants
            ? $"{undistributed} variant combinations have no distribution. Newly generated variants are retained as unreachable equipment records, remain absent from loot, and keep their rejection evidence in the report."
            : $"{undistributed} variant combinations have no distribution; their rejection evidence is retained in the report, but new unreachable equipment records are not written.");
        if (settings.CurrentProfile.StrictSubtypeMatching)
            warnings.Add("Strict subtype classification is enabled. Source-item distribution determines context; unknown subtypes remain undistributed.");
        if (settings.CurrentProfile.BroadUnmatchedDistribution)
            warnings.Add("Broader unmatched distribution is enabled. New source-gated branches are added only to proven enchanted-only structures and can change category selection shares.");
        if (settings.CurrentProfile.ResolveLeveledListConflicts)
            warnings.Add($"Leveled-list conflict resolution is enabled: {catalog.ConflictResolutions.Count} winning overrides recover {catalog.ConflictResolutions.Values.Sum(x => x.RestoredEntries)} entries omitted by plugins that do not depend on their source. Distribution analysis uses the resolved lists.");
        if (catalog.Fallbacks.Count > 0)
            warnings.Add($"{catalog.Fallbacks.Count} source-specific enchanted pools will be connected to permitted routes. Explicit specialized choices may mix enchanted and ordinary loot. This changes selection probabilities.");
        if (catalog.MerchantContainerRoots.Count > 0)
            warnings.Add($"Merchant fallback will override {catalog.MerchantContainerRoots.Count} compatible merchant chest(s). Each chest receives one XEP stock list with 50% Chance None; vendor matching uses shared equipment/buy-sell keywords from active records.");
        foreach (var root in catalog.RebuiltFallbackRoots)
            warnings.Add($"{catalog.Lists[root].EditorID} [{root}] will become a category parent: original entries are preserved in level-grouped children alongside the new subtype branches.");
        if (impacts.Any(x => x.GeneratedSublists > 0))
            warnings.Add($"{impacts.Count(x => x.GeneratedSublists > 0)} overflowing lists will be split into {impacts.Sum(x => x.GeneratedSublists)} new sublists. Levels and quantities are preserved; nested selection can change individual loot probabilities.");
        var plannedSourceKeys = PlannedSourceKeys(bundle.Plans);
        var unplaced = bundle.Candidates.Where(x => x.Selected && !plannedSourceKeys.Contains(x.FormKey)).ToArray();
        foreach (var item in unplaced) warnings.Add($"Skipped {item.Name} [{item.FormKey}]: {item.Reason}");
        foreach (var impact in impacts.Where(x => x.OverWarningLimit))
            warnings.Add($"{impact.EditorId} will contain {impact.After} entries (warning threshold {settings.CurrentProfile.WarningListEntries}).");
        if (previousFingerprint is { Length: > 0 } old && old != fingerprint)
            warnings.Add("Generated record identities differ from the previous run for this profile; replacing a patch already used in saves can orphan existing instances.");
        return new GenerationPreview
        {
            Information = FrameworkInformation(catalog).Distinct(StringComparer.Ordinal).ToList(),
            CdfFiles = catalog.CdfOutputRoots.Count == 0 ? [] : [CdfOutputPath(RequestedOutput(settings))],
            SkyPatcher = catalog.SkyPatcher,
            Cdf = catalog.Cdf,
            Llos = catalog.Llos,
            Cid = catalog.Cid,
            Spid = catalog.Spid,
            StrictSubtypeMatching = settings.CurrentProfile.StrictSubtypeMatching,
            BroadUnmatchedDistribution = settings.CurrentProfile.BroadUnmatchedDistribution,
            ResolveLeveledListConflicts = settings.CurrentProfile.ResolveLeveledListConflicts,
            KeepUndistributedVariants = settings.CurrentProfile.KeepUndistributedVariants,
            FullOutputPlugin = settings.CurrentProfile.FullOutputPlugin,
            NewRecords = newRecords,
            ConflictResolvedLists = catalog.ConflictResolutions.Count,
            ConflictRestoredEntries = catalog.ConflictResolutions.Values.Sum(x => x.RestoredEntries),
            EnchantmentProviderPlugins = settings.CurrentProfile.EnchantmentProviderPlugins.ToList(),
            EnabledEnchantments = catalog.Templates.Select(x => x.Enchantment).Distinct().Count(),
            SelectedSourceItems = bundle.SourceCount,
            Variants = newVariantCount,
            ReusedVariants = bundle.Plans.Count(p => p.Reused is not null),
            UndistributedVariants = undistributed,
            OutputFiles = patchCount,
            GeneratedSublists = sublistCount + protectedTargets.Count * 5,
            FallbackSublists = catalog.Fallbacks.Count - merged.Count - collapse.RemovedCount,
            FallbackConnections = catalog.FallbackEdges.Where(e => !catalog.Fallbacks.ContainsKey(e.Parent.List))
                .Select(e => $"{catalog.Lists[e.Parent.List].EditorID} [{e.Parent.List}] → {catalog.Lists[e.Child].EditorID} (level {e.Parent.Level}, count {e.Parent.Count})")
                .Concat(catalog.MerchantContainerRoots.Select(e => $"Merchant container {catalog.ConsumerRecords.Containers[e.Key].EditorID} [{e.Key}] → {catalog.Lists[e.Value].EditorID} (50% occasional stock)"))
                .ToList(),
            TargetLists = impacts.Count,
            SkippedSelectedItems = candidates.Count(x => !x.Eligible && settings.CurrentProfile.ItemOverrides.GetValueOrDefault(x.FormKey)),
            Fingerprint = fingerprint,
            IdentityChanged = previousFingerprint is { Length: > 0 } prior && prior != fingerprint,
            AlternateLayoutIdentityChanged = alternateIdentityChanged,
            OutputNames = outputs.Select(x => Path.GetFileName(x)!).ToList(),
            Lists = impacts,
            Placements = bundle.Plans.GroupBy(x => x.Source.FormKey).Select(g => new ItemPlacement(
                g.Key.ToString(), g.First().Candidate.Name, g.First().Candidate.Category,
                string.Join("; ", g.Select(x => x.Template.BaseItem).DistinctBy(x => x.FormKey)
                    .OrderBy(x => x.FormKey).Select(x => $"{DisplayName(x)} [{x.FormKey}]")),
                // Identical and collapsed generated lists: name the written lists that hold the entry.
                g.Count(), g.SelectMany(x => x.Parents).SelectMany(x => collapse.Holders(x.List, merged)).Select(x => $"{catalog.Lists[x].EditorID} [{x}]").Distinct().Order().ToList())).ToList(),
            Warnings = warnings
        };
    }

    private static PlanBundle BuildPlans(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies,
        Catalog catalog, List<CandidateItem> candidates, Action<string>? progress = null)
        => BuildSourcePlans(settings, policies, catalog, candidates, progress);


    private static bool IsSelected(CandidateItem item, IReadOnlyDictionary<string, PatchPolicy> policies, ProfileSettings profile)
    {
        var scriptFollowing = item.SpecializedMode == SpecializedLootMode.ScriptDistribution;
        if (!item.Eligible || !item.AlreadyDistributed && !scriptFollowing) return false;
        if (profile.ItemOverrides.TryGetValue(item.FormKey, out var manual)) return manual;
        return SettingsStore.NormalizePolicy(policies.GetValueOrDefault(item.Plugin, PatchPolicy.Exclude)) switch
        {
            PatchPolicy.ExistingDistribution => item.AlreadyDistributed || scriptFollowing,
            _ => false
        };
    }

    // Enchantment compatibility is checked before this independent placement check.
    // All equipment reachable from a destination must have the same specific family.
    // Unknown leaves and cycles fail closed instead of borrowing a faction's loot pool.
    private static bool DestinationMatches(FormKey listKey, string family, Catalog catalog)
    {
        if (catalog.DestinationCache.TryGetValue((listKey, family), out var cached)) return cached;
        var active = new HashSet<FormKey>();
        bool Visit(FormKey key)
        {
            if (!active.Add(key)) return false;
            try
            {
                if (catalog.Lists.TryGetValue(key, out var list))
                    return list.Entries is { Count: > 0 } && list.Entries.All(e => e.Data is { } data && Visit(data.Reference.FormKey));
                IMajorRecordGetter? item = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
                if (item is null) return false;
                if (TryBase(item, out var baseKey)) return Visit(baseKey);
                return CachedClass(item, catalog)?.Material.Equals(family, StringComparison.OrdinalIgnoreCase) == true;
            }
            finally { active.Remove(key); }
        }
        return catalog.DestinationCache[(listKey, family)] = Visit(listKey);
    }

    private static string? DistributionSubtype(IMajorRecordGetter item, Catalog catalog)
        => Facts(item, catalog).Subtype;

    private static string? ComputeDistributionSubtype(IMajorRecordGetter item, Catalog catalog)
    {
        var visited = new HashSet<FormKey>();
        while (TryBase(item, out var baseKey))
        {
            if (!visited.Add(baseKey)) return null;
            item = catalog.Weapons.GetValueOrDefault(baseKey) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(baseKey)!;
            if (item is null) return null;
        }
        var shape = CachedShape(item, catalog);
        if (shape is null) return null;
        if (item is IArmorGetter) return IsJewelryType(shape.Type)
            ? $"ARMO|Jewelry|{shape.Type}"
            : $"ARMO|{shape.ArmorType}|{shape.Type}";
        var labels = Facts(item, catalog).Labels;
        var specific = labels.Where(k => k.StartsWith("ShWeapType", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[10..].ToLowerInvariant()).Distinct().ToArray();
        // Preserve explicit subtype identities, including unfamiliar ShWeapType suffixes.
        // OCF's broad polearm families do not distinguish a spear from a trident.
        // Recognize type-tag conventions across frameworks. Unknown tags retain
        // their complete keyword identity rather than becoming ordinary swords.
        var custom = labels.Where(k => k.Contains("WeapType", StringComparison.OrdinalIgnoreCase)
            || k.Contains("WeaponType", StringComparison.OrdinalIgnoreCase))
            .Where(k => !k.StartsWith("ShWeapType", StringComparison.OrdinalIgnoreCase)).ToArray();
        string? Normalize(string value) => value switch
        {
            "sword" or "sword1h" => "sword",
            "shortsword" or "sword1h_shortsword" => "shortsword",
            "shortspear" or "pole1h_thrust" => "shortspear",
            "spear" or "pole2h_spear" => "spear",
            "trident" or "pole2h_trident" => "trident",
            "glaive" or "pole2h_glaive" => "glaive",
            "halberd" or "pole2h_halberd" => "halberd",
            "quarterstaff" or "quarterstaff2h" => "quarterstaff",
            "katana" or "sword1h_katana" => "katana",
            "dagger" or "dagger1h" => "dagger",
            "mace" or "mace1h" => "mace",
            "waraxe" or "axe1h" => "waraxe",
            "greatsword" or "sword2h" => "greatsword",
            "battleaxe" or "axe2h" => "battleaxe",
            "warhammer" or "hammer2h" => "warhammer",
            "bow" => "bow", "crossbow" => "crossbow",
            _ => null
        };
        if (specific.Length > 1) return null;
        var subtype = specific.Length == 1 ? Normalize(specific[0]) ?? specific[0] : null;
        string DynamicType(string keyword)
        {
            var marker = keyword.IndexOf("WeapType", StringComparison.OrdinalIgnoreCase);
            var length = 8;
            if (marker < 0) { marker = keyword.IndexOf("WeaponType", StringComparison.OrdinalIgnoreCase); length = 10; }
            var suffix = keyword[(marker + length)..].TrimStart('_').ToLowerInvariant();
            var known = Normalize(suffix);
            // Handedness is already carried separately in the descriptor.
            if (known is null && (suffix.EndsWith("1h") || suffix.EndsWith("2h"))) known = Normalize(suffix[..^2]);
            return known ?? "keyword:" + keyword.ToLowerInvariant();
        }
        if (subtype is null && custom.Length > 0)
        {
            var mapped = custom.Select(DynamicType).Where(t => t != shape.Type.ToLowerInvariant()).Distinct().ToArray();
            if (mapped.Length > 1) return null;
            if (mapped.Length == 1) subtype = mapped[0];
        }
        subtype ??= shape.Type.ToLowerInvariant();
        var weapon = (IWeaponGetter)item;
        var handedness = weapon.Data?.AnimationType is WeaponAnimationType.TwoHandSword or WeaponAnimationType.TwoHandAxe ? "2H" : "1H";
        return $"WEAP|{handedness}|{subtype}";
    }

    private static bool DestinationSubtypeMatches(FormKey listKey, IMajorRecordGetter source, Catalog catalog)
    {
        var sourceType = DistributionSubtype(source, catalog);
        if (sourceType is null) return false;
        if (!catalog.DestinationSubtypes.TryGetValue(listKey, out var types))
        {
            var found = new HashSet<string>();
            var active = new HashSet<FormKey>();
            bool Visit(FormKey key)
            {
                if (!active.Add(key)) return false;
                try
                {
                    if (catalog.Lists.TryGetValue(key, out var list))
                        return list.Entries is { Count: > 0 } && list.Entries.All(e => e.Data is { } data && Visit(data.Reference.FormKey));
                    var item = catalog.Weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)catalog.Armors.GetValueOrDefault(key);
                    var type = item is null ? null : DistributionSubtype(item, catalog);
                    if (type is null) return false;
                    found.Add(type);
                    return true;
                }
                finally { active.Remove(key); }
            }
            types = Visit(listKey) ? found : null;
            catalog.DestinationSubtypes[listKey] = types;
        }
        return types?.Contains(sourceType) == true;
    }

    // A direct occurrence and an occurrence reached through a child list give the
    // same item more than one selection path. Keep only the deepest direct placement.
    private static bool ContainsItemThroughSublist(FormKey listKey, FormKey itemKey, Catalog catalog)
    {
        if (catalog.NestedItemCache.TryGetValue((listKey, itemKey), out var cached)) return cached;
        var active = new HashSet<FormKey>();
        bool Contains(FormKey key)
        {
            if (key == itemKey) return true;
            if (!catalog.Lists.TryGetValue(key, out var list) || !active.Add(key)) return false;
            try
            {
                return (list.Entries ?? []).Any(entry => entry.Data is { } data && Contains(data.Reference.FormKey));
            }
            finally { active.Remove(key); }
        }

        var result = catalog.Lists.TryGetValue(listKey, out var parent) &&
            (parent.Entries ?? []).Any(entry => entry.Data is { } data && catalog.Lists.ContainsKey(data.Reference.FormKey) && Contains(data.Reference.FormKey));
        return catalog.NestedItemCache[(listKey, itemKey)] = result;
    }

    private static bool ContainsNestedList(FormKey parentKey, FormKey childKey, Catalog catalog)
    {
        if (catalog.NestedListCache.TryGetValue((parentKey, childKey), out var cached)) return cached;
        var active = new HashSet<FormKey>();
        bool Visit(FormKey key)
        {
            if (key == childKey) return true;
            if (!catalog.Lists.TryGetValue(key, out var list) || !active.Add(key)) return false;
            try
            {
                return (list.Entries ?? []).Any(entry => entry.Data is { } data &&
                    catalog.Lists.ContainsKey(data.Reference.FormKey) && Visit(data.Reference.FormKey));
            }
            finally { active.Remove(key); }
        }

        var result = catalog.Lists.TryGetValue(parentKey, out var parent) &&
            (parent.Entries ?? []).Any(entry => entry.Data is { } data &&
                catalog.Lists.ContainsKey(data.Reference.FormKey) && Visit(data.Reference.FormKey));
        return catalog.NestedListCache[(parentKey, childKey)] = result;
    }

    private static void ValidateRuntimeCapacity(StaticEntryProjection projection, Catalog catalog)
    {
        var protectedTargets = RevertSwitchGroups(catalog);
        foreach (var group in catalog.RuntimePlacements.GroupBy(r => r.Target.List))
        {
            // Reserved positions include suppressed reused proposals; projected
            // content includes native additions to generated lists. Use the larger.
            var reserved = projection.ReservationFor(group.Key)?.EntriesAfterSplitting;
            var projected = projection.RuntimeEntryCount(group.Key);
            var count = reserved is null && projected is null ? catalog.Lists[group.Key].Entries?.Count ?? 0
                : Math.Max(reserved ?? 0, projected ?? 0);
            var slots = protectedTargets.ContainsKey(group.Key) ? 1 : group.Count();
            if (count + slots > 255)
                throw new InvalidDataException($"Runtime insertion target {catalog.Lists[group.Key].EditorID} lacks capacity: {count} static entries plus {slots} planned positions exceeds 255. No output was installed.");
        }
    }

    private static List<ListImpact> BuildListImpacts(StaticEntryProjection projection, int warningLimit)
        => projection.ChangedLists.Select(list =>
            new ListImpact(list.List.ToString(), list.EditorId, list.Before, list.EffectiveCount - list.Before,
                list.EffectiveCount, list.EffectiveCount > warningLimit, list.Tree.ChildCount, list.Tree.EntryCount))
            .OrderByDescending(i => i.After).ThenBy(i => i.EditorId).ToList();

    // Resolve only inherited additions. A wrapped copy keeps its inherited equipment
    // inside Preserved; its explicit outer family links must not be redirected.
    private static IEnumerable<FormKey> InheritedCopyTargets(FormKey original,
        ILookup<FormKey, FormKey> copies, Catalog catalog)
        => copies[original].Select(copy => catalog.MaterialFamilyOriginals.ContainsKey(original)
            ? copy : MaterialContents(copy, catalog));

    // Additions made to an original list are also made to every context-isolated copy
    // of it, except the isolation links that point at other specialized copies.
    private static IEnumerable<ParentEntry> MirroredCloneAdditions(IEnumerable<ParentEntry> planParents, Catalog catalog)
        => ProjectMirroredCloneAdditions(planParents, catalog, (parent, target) => parent with { List = target });

    // Capacity snapshots count the same targets without allocating entry copies;
    // output reservations still project complete entries through this one walk.
    private static IEnumerable<T> ProjectMirroredCloneAdditions<T>(IEnumerable<ParentEntry> planParents, Catalog catalog,
        Func<ParentEntry, FormKey, T> project)
    {
        var copies = catalog.SpecializedCloneOriginals.ToLookup(x => x.Value, x => x.Key);
        foreach (var parent in planParents)
            foreach (var target in InheritedCopyTargets(parent.List, copies, catalog)) yield return project(parent, target);
        foreach (var edge in catalog.FallbackEdges.Where(e => InheritableEdge(e, catalog)))
            foreach (var target in InheritedCopyTargets(edge.Parent.List, copies, catalog)) yield return project(edge.Parent, target);
    }

    private static string Fingerprint(IEnumerable<VariantPlan> plans, IEnumerable<string> outputs, ProfileSettings profile, Catalog catalog, bool? fullOutputPlugin = null)
    {
        // One buffer: the identity text is large, and appending sections to an
        // immutable string copied all of it once per section.
        var providers = string.Join(",", profile.EnchantmentProviderPlugins);
        var keyText = new Dictionary<FormKey, string>();
        string K(FormKey key) => keyText.TryGetValue(key, out var known) ? known : keyText[key] = key.ToString();
        var text = new StringBuilder();
        text.AppendJoin('\n', outputs.Select(Path.GetFileName)).Append($"\n{providers}|template-priority=v19-jewelry-coverage|strict={profile.StrictSubtypeMatching}|broad={profile.BroadUnmatchedDistribution}|resolve-lvli={profile.ResolveLeveledListConflicts}|keep-undistributed={profile.KeepUndistributedVariants}{((fullOutputPlugin ?? profile.FullOutputPlugin) ? "|full-output-plugin" : "")}\n")
            .AppendJoin('\n', plans.Select(x => $"{K(x.Source.FormKey)}|{K(x.Template.Item.FormKey)}|reuse={(x.Reused is null ? "" : K(x.Reused.FormKey))}|{string.Join(',', x.Parents.Select(p => K(p.List)))}"))
            .Append('\n').AppendJoin('\n', catalog.Fallbacks.Select(x => $"{x.Key}|{x.Value.Identity}"))
            .Append('\n').AppendJoin('\n', catalog.FallbackEdges.Select(e => $"{e.Parent.List}|{e.Child}|{e.Parent.Level}|{e.Parent.Count}"))
            .Append("\nmerchant-fallback-v1\n").AppendJoin('\n', catalog.MerchantContainerRoots.OrderBy(x => x.Key).Select(x => $"{x.Key}|{x.Value}"))
            .Append("\nmerchant-inventory-isolation-v1\n").AppendJoin('\n', catalog.MerchantInventoryRewrites.Values.OrderBy(r => r.Edge.Container).ThenBy(r => r.Edge.Index).Select(r => $"{r.Edge.Identity}|{r.Root}"))
            .Append("\nloot-recipient-isolation-v1\n").AppendJoin('\n', catalog.LootRecipientRewrites.Values.OrderBy(r => r.Edge.Identity, StringComparer.Ordinal).Select(r => $"{r.Edge.Identity}|{r.Root}"))
            .Append("\navailability-v1\n").AppendJoin('\n', catalog.Fallbacks.Select(n => $"{n.Key}|global={n.Value.Original.Global.FormKey}"))
            .Append('\n').AppendJoin('\n', catalog.RuntimePlacements.Select(r => $"{r.Source}|{r.Target}|{r.Child}|{r.Unlock.Quest}|{r.Unlock.Stage}|start={r.Unlock.AtStart}|owner={r.Unlock.OwnerQuest}"))
            .Append("\nrevert-switch-v3\n").AppendJoin('\n', RevertSwitchGroups(catalog).Keys.Order())
            .Append("\nconflict-resolution-v1\n").AppendJoin('\n', catalog.ConflictResolutions.Values.OrderBy(x => x.List)
                .Select(x => $"{x.List}|{string.Join(',', (x.Resolved.Entries ?? []).Select(EntryIdentity))}"));
        if (catalog.SkyPatcher is { Fingerprint.Length: > 0 } skyPatcher)
            text.Append("\nskypatcher-inputs=").Append(skyPatcher.Fingerprint);
        if (catalog.Cdf is { Fingerprint.Length: > 0 } cdf)
            text.Append("\ncdf-inputs=").Append(cdf.Fingerprint);
        if (catalog.Llos is { Fingerprint.Length: > 0 } llos)
            text.Append("\nllos-inputs=").Append(llos.Fingerprint);
        if (catalog.Cid is { Fingerprint.Length: > 0 } cid)
            text.Append("\ncid-inputs=").Append(cid.Fingerprint);
        if (catalog.Spid is { Fingerprint.Length: > 0 } spid)
            text.Append("\nspid-inputs=").Append(spid.Fingerprint);
        if (UsedScriptGates(catalog) is { Length: > 0 } scriptGates)
        {
            text.Append("\nscript-gates-v2|presence-of-last-block-insertion|closed100|checks16|finite-completion-events\n").AppendJoin('\n', scriptGates.Select(g =>
                $"{g.Block}|{g.Placeholder}|{g.Representative.Receiver}|{g.Representative.Payload}|{string.Join(",", g.CompletionEvents)}|" + string.Join(';', g.Members.Select(m => $"{m.Receiver}:{m.Payload}:{m.Level}:{m.Count}"))));
            foreach (var name in new[] { "XEP_ScriptGate", "XEP_ScriptGatePlayerAlias" })
            foreach (var suffix in new[] { "psc", "pex" })
            {
                using var stream = typeof(PatcherEngine).Assembly.GetManifestResourceStream($"XEnchantmentPatcher.Runtime.{name}.{suffix}")!;
                text.Append($"\n{name}.{suffix}={Convert.ToHexString(SHA256.HashData(stream))}");
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encoder = Encoding.UTF8.GetEncoder();
        var bytes = new byte[1 << 20];
        foreach (var chunk in text.GetChunks())
        {
            var span = chunk.Span;
            while (span.Length > 0)
            {
                encoder.Convert(span, bytes, flush: false, out var used, out var written, out _);
                hash.AppendData(bytes, 0, written);
                span = span[used..];
            }
        }
        encoder.Convert(ReadOnlySpan<char>.Empty, bytes, flush: true, out _, out var tail, out _);
        hash.AppendData(bytes, 0, tail);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string? PreviousFingerprint(AppSettings settings, string requestedOutput)
    {
        if (settings.CurrentProfile.LastGenerationFingerprint is { Length: > 0 } saved) return saved;
        var manifestPath = Path.ChangeExtension(requestedOutput, ".manifest.json");
        try
        {
            if (!File.Exists(manifestPath)) return null;
            var manifest = JsonSerializer.Deserialize(AtomicFile.ReadText(manifestPath), JsonContext.Default.OutputManifest);
            return manifest?.ProfileName.Equals(settings.ProfileName, StringComparison.OrdinalIgnoreCase) == true
                ? manifest.Fingerprint : null;
        }
        catch { return null; }
    }

    public static PatchReport Generate(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies, Action<string>? progress = null)
    {
        using var session = PhaseTiming.Begin("generate");
        var requestedOutput = RequestedOutput(settings);
        var requestedOutputName = Path.GetFileName(requestedOutput);
        RequireDecisions(policies);

        using var game = LoadOrderProvider.Load(settings, requestedOutputName);
        return GenerateLoaded(settings, policies, game.Mods, progress);
    }

    // With reusePreview, the caller asserts that the snapshot's last Preview is
    // current for these settings and policies; its plans are then written unchanged.
    public static PatchReport Generate(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies, ScanSnapshot snapshot,
        bool reusePreview, Action<string>? progress = null)
    {
        using var session = PhaseTiming.Begin("generate");
        var analysis = Loaded(snapshot);
        return analysis.WithAuthor(settings, (effective, author) =>
        {
            var effectivePolicies = author?.EffectivePolicies(policies) ?? policies;
            RequireDecisions(effectivePolicies);
            return GenerateAnalysis(effective, effectivePolicies, analysis.Current(effective), reusePreview, progress);
        });
    }

    private static void RequireDecisions(IReadOnlyDictionary<string, PatchPolicy> policies)
    {
        if (policies.Any(x => x.Value == PatchPolicy.Review))
            throw new InvalidOperationException("Every listed plugin must be assigned Existing Distribution or Exclude before generation.");
    }

    private static PatchReport GenerateLoaded(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies,
        IReadOnlyList<ISkyrimModGetter> mods, Action<string>? progress = null)
    {
        using var session = PhaseTiming.Begin("generate-loaded");
        var requestedOutput = RequestedOutput(settings);
        // Hold through planning/manifest reads, staging, backup, stale-file
        // deletion and final installation. OS handle release also covers crashes.
        using var destinationLease = GenerationLease.Acquire(requestedOutput);
        progress?.Invoke($"Loaded {mods.Count} active plugins.");
        return GeneratePlanned(settings, policies, AnalyzeLoaded(settings, mods).Plan(settings, policies, progress), progress);
    }

    private static PatchReport GenerateAnalysis(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies,
        ScanAnalysis analysis, bool reusePreview, Action<string>? progress = null)
    {
        using var destinationLease = GenerationLease.Acquire(RequestedOutput(settings));
        // Release unused preview plans before planning again.
        var retained = analysis.TakePreview();
        var planned = (reusePreview ? retained : null) ?? analysis.Plan(settings, policies, progress);
        return GeneratePlanned(settings, policies, planned, progress);
    }

    private static PatchReport GeneratePlanned(AppSettings settings, IReadOnlyDictionary<string, PatchPolicy> policies,
        PlannedPass planned, Action<string>? progress)
    {
        OperationProgress.Stage("Preparing output records…", GenerationStage.Records);
        var requestedOutput = RequestedOutput(settings);
        var (pass, bundle) = planned;
        var catalog = pass.Catalog;
        var mods = catalog.Mods;
        var candidates = pass.Candidates;
        var plans = bundle.Plans;

        var derived = planned.Derived;
        StaticEntryProjection projection;
        Dictionary<FormKey, FormKey> merged;
        List<ListImpact> impacts;
        projection = derived?.Projection ?? EffectiveStaticEntries.Project(catalog, plans);
        merged = derived?.Merged ?? IdenticalGeneratedLists(catalog, projection);
        var collapse = CollapseGeneratedLists(catalog, projection, merged);
        if (derived is not null) impacts = derived.Impacts;
        else
        {
            impacts = BuildListImpacts(projection, settings.CurrentProfile.WarningListEntries)
                .Where(i => !merged.ContainsKey(FormKey.Factory(i.FormKey)) && !collapse.Removed(FormKey.Factory(i.FormKey))).ToList();
            ValidateRuntimeCapacity(projection, catalog);
            impacts = RevertSwitchImpacts(impacts, catalog, settings.CurrentProfile.WarningListEntries);
        }
        // A reused Preview already reported these; notes are kept once.
        ReportLlosStartupChanges(catalog, projection, merged);
        using var recordTiming = PhaseTiming.Measure("output-records");
        var sublistCount = projection.ChangedLists.Sum(x => x.Tree.ChildCount) + catalog.Fallbacks.Count
            - MergedSublistCount(merged, projection) - collapse.RemovedCount;
        var newVariantCount = plans.Count(p => p.Reused is null
            && (p.Parents.Count > 0 || settings.CurrentProfile.KeepUndistributedVariants));
        var layout = OutputLayout.For(settings.CurrentProfile);
        var patchCount = layout.PatchCount(newVariantCount + sublistCount + LootRecipientRecordCount(catalog) + RuntimeRecordCount(catalog));
        var outputPaths = OutputPaths(requestedOutput, patchCount);
        catalog.NativeLists?.ValidateOutputPlugins(outputPaths);
        var patches = outputPaths.Select(path =>
        {
            var mod = new SkyrimMod(ModKey.FromFileName(Path.GetFileName(path)), SkyrimRelease.SkyrimSE);
            layout.Apply(mod);
            return mod;
        }).ToArray();
        var usedIds = patches.Select(_ => new HashSet<uint>()).ToArray();
        // Do not introduce a new binding or ambiguity for a captured native
        // identifier. Explicit SkyPatcher selectors/payloads keep their old meaning.
        var usedEditorIds = new HashSet<string>(catalog.NativeLists?.ReferencedEditorIds ?? [], StringComparer.OrdinalIgnoreCase);
        usedEditorIds.UnionWith(catalog.LlosLists?.ReferencedEditorIds ?? []);
        usedEditorIds.UnionWith(projection.GeneratedEditorIds.Values);
        usedEditorIds.UnionWith(projection.OverflowEditorIds.Values.SelectMany(names => names));
        var pendingEntries = new List<PendingEntry>();
        var generatedReferences = new Dictionary<int, FormKey>();
        var changes = new List<PatchChange>();
        var changedLists = new HashSet<FormKey>();
        var decisions = new List<VariantDecision>();
        var permittedUnreachable = new HashSet<FormKey>();
        // One owned condition Global per script insertion block, closed (100) until
        // XEP_ScriptGate sees the block's representative insertion. First plugin, so no
        // earlier plugin depends on a later one.
        var scriptGateGlobals = new Dictionary<FormKey, FormKey>();
        foreach (var scriptGate in UsedScriptGates(catalog))
        {
            var globalKey = StableKey(patches[0], "script-gate-v1|" + scriptGate.Block, usedIds[0]);
            patches[0].Globals.Add(new GlobalFloat(globalKey, SkyrimRelease.SkyrimSE) { EditorID = EditorId("ScriptGate", usedEditorIds), Data = 100f });
            scriptGateGlobals[scriptGate.Placeholder] = globalKey;
        }
        var ownedGlobalCount = scriptGateGlobals.Count;
        var newOrdinal = ownedGlobalCount;

        var cloneJobs = new List<CloneJob>();
        for (var planIndex = 0; planIndex < plans.Count; planIndex++)
        {
            var plan = plans[planIndex];
            OperationProgress.Record("Generating equipment", planIndex + 1, plans.Count, plan.Candidate.Plugin, plan.Candidate.Name);
            var patchIndex = layout.PatchIndex(newOrdinal);
            if (plan.Reused is not null)
            {
                foreach (var parent in plan.Parents)
                    pendingEntries.Add(new(parent, plan.Reused.FormKey, plan.Source.FormKey, DisplayName(plan.Source), DisplayName(plan.Reused), plan.Template.EnchantmentName));
                var distributed = plan.Parents.Count > 0 || catalog.DistributedItems.Contains(plan.Reused.FormKey);
                decisions.Add(new(plan.Source.FormKey.ToString(), plan.Candidate.Name, plan.Reused.FormKey.ToString(), plan.Template.Enchantment.ToString(),
                    distributed ? "Reused" : "ReusedUndistributed", distributed
                        ? plan.Parents.Count > 0
                            ? $"Existing equivalent variant reused; {plan.PlacementReason ?? "placed through a proven route"}"
                            : "Existing equivalent variant already present in load-order distribution"
                        : "Existing variant has no authorized destination") { DiagnosticIds = distributed ? [] : plan.DiagnosticIds ?? [] });
                continue;
            }
            if (plan.Parents.Count == 0 && !settings.CurrentProfile.KeepUndistributedVariants)
            {
                decisions.Add(new(plan.Source.FormKey.ToString(), plan.Candidate.Name, "", plan.Template.Enchantment.ToString(),
                    "Undistributed", plan.PlacementReason ?? "No proven enchanted destination; record not written")
                    { DiagnosticIds = plan.DiagnosticIds ?? [] });
                continue;
            }
            newOrdinal++;
            var patch = patches[patchIndex];
            var source = plan.Source;
            var template = plan.Template;
            if (planIndex == 0 || plans[planIndex - 1].Source.FormKey != source.FormKey)
                progress?.Invoke($"{plan.Candidate.Plugin}: {plan.Candidate.Name}");

            var identity = $"{source.FormKey}|{template.Item.FormKey}";
            var newKey = StableKey(patch, identity, usedIds[patchIndex]);
            var generatedName = VariantName(source, template.BaseItem, template.Item, template.EnchantmentName);
            var editorId = EditorId(generatedName, usedEditorIds);
            // Usable templates share their base's record kind, so class matching
            // pairs a source only with templates of its own kind.
            if (!(source is IWeaponGetter && template.Item is IWeaponGetter) && !(source is IArmorGetter && template.Item is IArmorGetter))
                throw new InvalidDataException($"Variant plan pairs {source.FormKey} with template {template.Item.FormKey} of a different record kind. No output was installed.");
            cloneJobs.Add(new CloneJob(patchIndex, newKey, editorId, generatedName, plan));
            generatedReferences.Add(planIndex, newKey);
            if (plan.Parents.Count == 0) permittedUnreachable.Add(newKey);

            foreach (var parent in plan.Parents)
                pendingEntries.Add(new PendingEntry(parent, newKey, source.FormKey, DisplayName(source),
                    generatedName, template.EnchantmentName));
            decisions.Add(new(source.FormKey.ToString(), plan.Candidate.Name, newKey.ToString(), template.Enchantment.ToString(),
                plan.Parents.Count > 0 ? "Generated" : "GeneratedUndistributed",
                plan.PlacementReason ?? (plan.Parents.Count > 0 ? "Placed through a proven enchanted-only route" : "No proven enchanted destination")) { DiagnosticIds = plan.Parents.Count > 0 ? [] : plan.DiagnosticIds ?? [] });
        }

        OperationProgress.Stage("Cloning generated equipment…", GenerationStage.Cloning);
        var clones = new IMajorRecord[cloneJobs.Count];
            ParallelInOrder(cloneJobs.Count, i => clones[i] = CloneVariant(cloneJobs[i]));
        OperationProgress.Stage("Checking generated equipment…", GenerationStage.CheckingRecords);
        for (var i = 0; i < cloneJobs.Count; i++)
        {
            var job = cloneJobs[i];
            OperationProgress.Record("Checking generated equipment", i + 1, cloneJobs.Count, job.Plan.Candidate.Plugin, job.Plan.Candidate.Name);
            var patch = patches[job.PatchIndex];
            switch (clones[i])
            {
                case Weapon weapon: patch.Weapons.Add(weapon); break;
                case Armor armor: patch.Armors.Add(armor); break;
            }
            ValidateCdfGeneratedKeywords(job.Plan.Source, clones[i], catalog);
            if (catalog.LlosLists?.GeneratedKeywordProblem(job.Plan.Source.FormKey, clones[i]) is { } keywordProblem)
                throw new InvalidDataException(keywordProblem + " No output was installed.");
        }

        OperationProgress.Stage("Building leveled-list overrides…", GenerationStage.Lists);
        var distributionPatch = patches[^1];
        foreach (var list in projection.ChangedLists) list.ValidateForWriting();
        var nextRecord = newVariantCount + ownedGlobalCount;
        var remapped = new Dictionary<FormKey, FormKey>();
        var fallbackEditorIds = new Dictionary<FormKey, string>();
        var materializing = new HashSet<FormKey>();
        LeveledItem AllocateList(string identity, string name, ILeveledItemGetter original, List<LeveledItemEntry> entries, string? reservedEditorId = null)
        {
            var patchIndex = layout.PatchIndex(nextRecord++);
            var owner = patches[patchIndex];
            var key = StableKey(owner, identity, usedIds[patchIndex]);
            var child = new LeveledItem(key, SkyrimRelease.SkyrimSE)
            {
                EditorID = reservedEditorId ?? EditorId(name, usedEditorIds), Flags = original.Flags,
                ChanceNone = new Percent(0), Entries = new ExtendedList<LeveledItemEntry>(entries)
            };
            owner.LeveledItems.Add(child);
            return child;
        }
        FormKey BindReference(StaticEntryReference reference) => reference.GeneratedOccurrence is { } occurrence
            ? generatedReferences[occurrence] : reference.Existing;
        List<LeveledItemEntry> MaterializeEntries(FormKey planningKey)
        {
            var original = catalog.Lists[planningKey];
            var nodeIndex = 0;
            List<LeveledItemEntry> Materialize(StaticOverflowNode node)
            {
                var result = node.Leaves.Select(projected =>
                {
                    var entry = projected.Payload;
                    var (resolved, level) = collapse.Resolve(projected, merged);
                    var reference = BindReference(resolved);
                    entry.Data!.Reference.SetTo(remapped.GetValueOrDefault(reference, reference));
                    if (level is { } kept) entry.Data.Level = kept;
                    return entry;
                }).ToList();
                foreach (var branch in node.Branches)
                {
                    var childEntries = Materialize(branch.Child);
                    var index = nodeIndex++;
                    var child = AllocateList($"overflow|{planningKey}|{index}",
                        $"Sublist_{original.EditorID}_{nodeIndex}", original, childEntries,
                        projection.OverflowEditorIds.TryGetValue(planningKey, out var names) ? names[index] : null);
                    result.Add(new LeveledItemEntry { Data = new LeveledItemEntryData
                        { Reference = new FormLink<IItemGetter>(child.FormKey), Level = branch.Level, Count = 1 } });
                }
                return result;
            }
            return Materialize(projection[planningKey].Tree);
        }
        FormKey MaterializeFallback(FormKey planningKey)
        {
            if (remapped.TryGetValue(planningKey, out var allocated)) return allocated;
            if (merged.TryGetValue(planningKey, out var representative))
            {
                // Identical written content: reference the representative's record.
                var shared = MaterializeFallback(representative);
                remapped[planningKey] = shared;
                fallbackEditorIds[planningKey] = fallbackEditorIds[representative];
                return shared;
            }
            if (!materializing.Add(planningKey)) throw new InvalidDataException("Cyclic fallback route.");
            if (collapse.Removed(planningKey))
            {
                // Not written: references take its single entry. Reports name that entry's record.
                var only = collapse.Inlined.GetValueOrDefault(planningKey) ?? collapse.AbsorbedChildren[planningKey];
                var target = BindReference(collapse.Resolve(only, merged).Reference);
                if (catalog.Fallbacks.ContainsKey(target)) target = MaterializeFallback(target);
                remapped[planningKey] = target;
                fallbackEditorIds[planningKey] = fallbackEditorIds.GetValueOrDefault(target, "");
                materializing.Remove(planningKey);
                return target;
            }
            // Dependencies retain their authored order and are allocated before
            // this list's overflow children. Binding owns fresh entry copies.
            foreach (var entry in projection[planningKey].Entries)
            {
                var reference = BindReference(collapse.Resolve(entry, merged).Reference);
                if (catalog.Fallbacks.ContainsKey(reference)) MaterializeFallback(reference);
            }
            var node = catalog.Fallbacks[planningKey];
            var list = AllocateList($"fallback|{node.Identity}", catalog.Lists[planningKey].EditorID!,
                node.Original, MaterializeEntries(planningKey), projection.GeneratedEditorIds.GetValueOrDefault(planningKey));
            // An absorbing window carries its removed child's control.
            var controls = collapse.Absorbing.TryGetValue(planningKey, out var absorbed) ? catalog.Fallbacks[absorbed].Original : node.Original;
            list.Global.SetTo(scriptGateGlobals.GetValueOrDefault(controls.Global.FormKey, controls.Global.FormKey));
            list.ChanceNone = controls.ChanceNone;
            remapped[planningKey] = list.FormKey;
            fallbackEditorIds[planningKey] = list.EditorID!;
            materializing.Remove(planningKey);
            return list.FormKey;
        }
        foreach (var key in catalog.Fallbacks.Keys) MaterializeFallback(key);
        WriteMerchantInventoryIsolations(catalog, distributionPatch, remapped);
        Outfit AllocateOutfit(IOutfitGetter original, string identity)
        {
            var patchIndex = layout.PatchIndex(nextRecord++);
            var owner = patches[patchIndex];
            var key = StableKey(owner, identity, usedIds[patchIndex]);
            var clone = owner.Outfits.DuplicateInAsNewRecord(original, key);
            clone.EditorID = EditorId("RecipientOutfit_" + original.EditorID, usedEditorIds);
            // The effective outfit holds LLOS's generated lists; the written clone keeps
            // their physical targets, which LLOS swaps in every outfit at startup.
            // Withheld swaps are blank in the effective outfit; the physical entry is kept.
            if (catalog.LlosLists is not null
                && catalog.Mods.SelectMany(m => m.Outfits).LastOrDefault(o => o.FormKey == original.FormKey)?.Items is { } physical
                && physical.Count == clone.Items!.Count)
                for (var i = 0; i < clone.Items.Count; i++) clone.Items[i] = new FormLink<IOutfitTargetGetter>(physical[i].FormKey);
            return clone;
        }
        WriteLootRecipientIsolations(catalog, distributionPatch, remapped, AllocateOutfit);
        foreach (var (containerKey, planningRoot) in catalog.MerchantContainerRoots)
        {
            if (!catalog.ConsumerRecords.Containers.TryGetValue(containerKey, out var original) || original.IsDeleted)
                throw new InvalidDataException($"Merchant fallback container {containerKey} is no longer available.");
            var root = remapped[planningRoot];
            var container = distributionPatch.Containers.GetOrAddAsOverride(original);
            container.Items ??= [];
            if (!container.Items.Any(e => e.Item.Item.FormKey == root))
                container.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = new FormLink<IItemGetter>(root), Count = 1 } });
        }
        foreach (var projected in projection.ChangedLists.Where(x => !catalog.Fallbacks.ContainsKey(x.List)))
        {
            var key = projected.List;
            var list = distributionPatch.LeveledItems.GetOrAddAsOverride(catalog.Lists[key]);
            list.Entries = new ExtendedList<LeveledItemEntry>(MaterializeEntries(key));
            changedLists.Add(key);
        }
        var keyText = new Dictionary<FormKey, string>();
        string KeyText(FormKey key) => keyText.TryGetValue(key, out var known) ? known : keyText[key] = key.ToString();
        var destinationText = new Dictionary<FormKey, string>();
        string Destination(FormKey list)
        {
            if (!destinationText.TryGetValue(list, out var destination))
                destinationText[list] = destination =
                    $"{remapped.GetValueOrDefault(list, list)} [{fallbackEditorIds.GetValueOrDefault(list, catalog.Lists[list].EditorID ?? "")}]";
            return destination;
        }
        foreach (var entry in pendingEntries)
            // A removed list holds no record: the change names the written lists that now hold the variant.
            foreach (var holder in collapse.Holders(entry.Parent.List, merged))
                changes.Add(new PatchChange(KeyText(entry.Source), entry.SourceName, KeyText(entry.Generated),
                    entry.GeneratedName, entry.EnchantmentName, Destination(holder)));
        impacts = impacts.Select(i => remapped.TryGetValue(FormKey.Factory(i.FormKey), out var mapped)
            ? i with { FormKey = mapped.ToString(), EditorId = fallbackEditorIds[FormKey.Factory(i.FormKey)] } : i).ToList();
        if (nextRecord != newVariantCount + sublistCount + LootRecipientRecordCount(catalog) + ownedGlobalCount)
            throw new InvalidDataException("Generated sublist count differs from preview.");
        WriteRevertSwitches(catalog, patches, usedIds, usedEditorIds, remapped, ref nextRecord, changedLists);
        var protectedTargets = RevertSwitchGroups(catalog);
        foreach(var runtime in catalog.RuntimePlacements.Where(r => !protectedTargets.ContainsKey(r.Target.List)))
        {
            var runtimeTarget = remapped.GetValueOrDefault(runtime.Target.List, runtime.Target.List);
            var writtenTarget = patches.Select(p => p.LeveledItems.TryGetValue(runtimeTarget, out var l) ? l : null).FirstOrDefault(l => l is not null);
            var targetCount = writtenTarget is not null
                ? writtenTarget.Entries?.Count ?? 0 : catalog.Lists[runtime.Target.List].Entries?.Count ?? 0;
            if(targetCount + catalog.RuntimePlacements.Count(r=>r.Target.List==runtime.Target.List)>255)
                throw new InvalidDataException($"Runtime insertion target {catalog.Lists[runtime.Target.List].EditorID} lacks capacity for the planned additions. No output was installed.");
            var index = layout.PatchIndex(nextRecord++);
            var owner = patches[index];
            var identity = $"runtime|{runtime.Source}|{runtime.Target.List}|{runtime.Unlock.Quest}|{runtime.Unlock.Stage}|{runtime.Target.Level}" + (runtime.Unlock.AtStart ? "|start" : "");
            var quest = new Quest(StableKey(owner,identity,usedIds[index]), SkyrimRelease.SkyrimSE)
            {
                EditorID = EditorId("Runtime_"+catalog.Lists[runtime.Child].EditorID,usedEditorIds),
                Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce,
                VirtualMachineAdapter = new QuestAdapter()
            };
            var script = new ScriptEntry {Name="XEP_QuestInsertion"};
            void Link(string name, FormKey key) => script.Properties.Add(new ScriptObjectProperty {Name=name,Flags=ScriptProperty.Flag.Edited,Object=new FormLink<ISkyrimMajorRecordGetter>(key)});
            void Int(string name, int value) => script.Properties.Add(new ScriptIntProperty {Name=name,Flags=ScriptProperty.Flag.Edited,Data=value});
            // A start-up (OnInit) insertion has no source stage: the controller waits on its own
            // start-up stage 0 instead, which is done as soon as the controller quest starts.
            if(runtime.Unlock.AtStart) quest.Stages.Add(new QuestStage{Index=0,Flags=QuestStage.Flag.StartUpStage});
            Link("SourceQuest",runtime.Unlock.AtStart ? quest.FormKey : runtime.Unlock.Quest);Link("TargetList",runtimeTarget);Link("VariantList",remapped[runtime.Child]);
            Int("SourceStage",runtime.Unlock.AtStart ? 0 : runtime.Unlock.Stage);Int("EntryLevel",runtime.Target.Level);
            quest.VirtualMachineAdapter.Scripts.Add(script);owner.Quests.Add(quest);
        }
        WriteScriptGates(catalog, patches, usedIds, usedEditorIds, scriptGateGlobals);
        recordTiming?.Dispose();
        using var reportTiming = PhaseTiming.Measure("report.prepare");
        // The output layout is chosen after the preview; its fingerprint names the preview's files.
        var fingerprint = derived is not null && derived.FullOutputPlugin == settings.CurrentProfile.FullOutputPlugin
            ? derived.Fingerprint : Fingerprint(plans, outputPaths, settings.CurrentProfile, catalog);
        var plannedSources = plans.Select(p => p.Source.FormKey.ToString()).ToHashSet();
        var skipped = candidates.Where(x => !plannedSources.Contains(x.FormKey)).ToList();
        // Every skipped record states why, including records left unselected by specialized loot review.
        foreach (var item in skipped.Where(x => string.IsNullOrWhiteSpace(x.Reason)))
            item.Reason = !item.Selected
                ? item.RequiresLootReview ? "Not selected (specialized loot review): " + (string.IsNullOrWhiteSpace(item.LootReviewReason) ? item.SpecializedRecommendation : item.LootReviewReason) : "Not selected"
                : "No variant planned";
        var routeDiagnostics = UsedRouteDiagnostics(catalog, decisions);
        var report = new PatchReport
        {
            SkyPatcher = catalog.SkyPatcher,
            Cdf = catalog.Cdf,
            Llos = catalog.Llos,
            Cid = catalog.Cid,
            Spid = catalog.Spid,
            RuntimeQuests = catalog.RuntimePlacements.Count - protectedTargets.Values.Sum(r => r.Length)
                + protectedTargets.Values.Sum(r => SwitchFamilies(r).Count())
                + (scriptGateGlobals.Count > 0 ? 1 : 0),
            ScriptGates = UsedScriptGates(catalog).Select(g => DescribeScriptGate(g, scriptGateGlobals[g.Placeholder], catalog)).ToList(),
            ScriptGateRefusals = catalog.ScriptGateRefusals.Distinct().ToList(),
            RuntimeInsertions = catalog.RuntimePlacements.Select(r => $"{r.Source}: {remapped[r.Child]} → {remapped.GetValueOrDefault(r.Target.List, r.Target.List)}, level {r.Target.Level}; {r.Unlock.Quest} {(r.Unlock.AtStart ? "at game start" : $"stage {r.Unlock.Stage}")}; {(protectedTargets.ContainsKey(r.Target.List) ? "AddForm into isolated selector behind saved-global Revert-protected switch" : "AddForm")}; {r.Unlock.Evidence}").ToList(),
            ScriptCalls = catalog.ScriptCalls,
            ScriptConflicts = catalog.ScriptConflicts,
            GeneralLootLists = catalog.GeneralLootCache.Where(x => x.Value.General).OrderBy(x => catalog.Lists[x.Key].EditorID)
                .Select(x => $"{catalog.Lists[x.Key].EditorID} [{x.Key}]: {x.Value.Detail}").ToList(),
            RebuiltFallbackLists = catalog.RebuiltFallbackRoots.Select(k => $"{catalog.Lists[k].EditorID} [{k}]").ToList(),
            ReusedVariants = plans.Count(p => p.Reused is not null),
            UndistributedVariants = decisions.Count(d => d.Status.EndsWith("Undistributed")),
            VariantDecisions = decisions,
            RouteDiagnostics = routeDiagnostics,
            DistributionNotes = catalog.DistributionNotes.Distinct(StringComparer.Ordinal).ToList(),
            StrictSubtypeMatching = settings.CurrentProfile.StrictSubtypeMatching,
            BroadUnmatchedDistribution = settings.CurrentProfile.BroadUnmatchedDistribution,
            ResolveLeveledListConflicts = settings.CurrentProfile.ResolveLeveledListConflicts,
            KeepUndistributedVariants = settings.CurrentProfile.KeepUndistributedVariants,
            FullOutputPlugin = settings.CurrentProfile.FullOutputPlugin,
            ConflictResolvedLists = catalog.ConflictResolutions.Count,
            ConflictRestoredEntries = catalog.ConflictResolutions.Values.Sum(x => x.RestoredEntries),
            ConflictResolutionDetails = catalog.ConflictResolutions.Values.Select(x => x.Detail).Order().ToList(),
            ConflictResolutionChanges = catalog.ConflictResolutions.Values.OrderBy(x => x.Resolved.EditorID).Select(x =>
                new LeveledListConflictChange(x.List.ToString(), x.Resolved.EditorID ?? "", x.WinningPlugin.FileName.String,
                    x.RestoredEntries, x.RestoredByPlugin.ToDictionary(y => y.Key.FileName.String, y => y.Value, StringComparer.OrdinalIgnoreCase))).ToList(),
            Output = outputPaths[0],
            Outputs = outputPaths.ToList(),
            GeneratedAt = DateTime.Now,
            ProfileName = settings.ProfileName,
            EnchantmentProviderPlugins = settings.CurrentProfile.EnchantmentProviderPlugins.ToList(),
            EnabledEnchantments = catalog.Templates.Select(x => x.Enchantment.ToString()).Distinct().Order().ToList(),
            Fingerprint = fingerprint,
            IdentityChanged = PreviousFingerprint(settings, requestedOutput) is { Length: > 0 } previous && previous != fingerprint,
            ActivePlugins = mods.Count,
            SelectedPlugins = policies.Count(x => SettingsStore.NormalizePolicy(x.Value) == PatchPolicy.ExistingDistribution),
            SourceItems = bundle.SourceCount,
            Variants = patches.Sum(x => x.Weapons.Count + x.Armors.Count),
            LeveledListsOverridden = changedLists.Count,
            GeneratedSublists = sublistCount + protectedTargets.Count * 5,
            FallbackSublists = catalog.Fallbacks.Count - merged.Count - collapse.RemovedCount,
            // A removed list holds no record: edges into it resolve to the entry the writer
            // placed in its referencing list, and its own edge is no longer written.
            FallbackPlacements = catalog.FallbackEdges
                .Where(e => !collapse.Removed(merged.GetValueOrDefault(e.Parent.List, e.Parent.List)))
                .Select(e => new FallbackPlacement(
                    remapped.GetValueOrDefault(e.Parent.List, e.Parent.List).ToString(),
                    fallbackEditorIds.GetValueOrDefault(e.Parent.List, catalog.Lists[e.Parent.List].EditorID ?? ""),
                    remapped[e.Child].ToString(), fallbackEditorIds[e.Child],
                    collapse.Resolve(new StaticEntryReference(e.Child), e.Parent.Level, merged).Level ?? e.Parent.Level, e.Parent.Count)).ToList(),
            ListImpacts = impacts,
            Changes = changes,
            Skipped = skipped,
            Setup = new ReportSetup(settings.ProfileName,
                JsonSerializer.Deserialize(JsonSerializer.Serialize(settings.CurrentProfile, JsonContext.Default.ProfileSettings), JsonContext.Default.ProfileSettings)!,
                policies.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                settings.ItemExclusions.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                mods.Select(m => m.ModKey.FileName.String).ToList())
        };
        reportTiming?.Dispose();
        var cdfOutput = BuildCdfOutput(catalog, remapped, requestedOutput, outputPaths);
        if (cdfOutput is not null) report.CdfFiles.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(requestedOutput)!, cdfOutput.RelativePath)));
        catalog.LlosLists?.RestoreContainerTargets(patches, mods);
        OperationProgress.Stage("Writing and validating output plugins…", GenerationStage.Writing);
        WriteAndValidate(patches, mods, requestedOutput, outputPaths, report, permittedUnreachable, cdfOutput);
        OperationProgress.Fraction(1);
        settings.CurrentProfile.LastGenerationFingerprint = fingerprint;
        return report;
    }

    private static Catalog BuildCatalog(IReadOnlyList<ISkyrimModGetter> mods, IReadOnlyCollection<string> providerNames)
    {
        var catalog = BuildCatalogCore(mods, providerNames, false, false, null);
        SelectEnchantmentTemplates(catalog, null);
        return catalog;
    }

    private static Catalog BuildCatalogForProfile(IReadOnlyList<ISkyrimModGetter> mods, ProfileSettings profile)
    {
        var catalog = BuildProfileCatalog(mods, profile);
        ApplyEnchantmentRecommendations(catalog, profile);
        return catalog;
    }

    private static Catalog BuildProfileCatalog(IReadOnlyList<ISkyrimModGetter> mods, ProfileSettings profile, SkyPatcherInputReport? skyPatcher = null)
        => BuildNativeProfileCatalog(mods, profile, skyPatcher, null);

    // Separate name: fixtures reach BuildProfileCatalog by reflection with three arguments.
    private static Catalog BuildNativeProfileCatalog(IReadOnlyList<ISkyrimModGetter> mods, ProfileSettings profile, SkyPatcherInputReport? skyPatcher, LlosInputReport? llos)
        => BuildCatalogCore(mods, profile.EnchantmentProviderPlugins, profile.BroadUnmatchedDistribution, profile.ResolveLeveledListConflicts, profile.MaterialFamilyOverrides, skyPatcher, llos);

    private static KeywordIndex NewKeywordIndex(IReadOnlyList<ISkyrimModGetter> mods)
        => new(mods.SelectMany(x => x.Keywords).GroupBy(x => x.FormKey).ToDictionary(x => x.Key, x => x.Last().EditorID ?? ""));

    // Material resolution and usable templates for new material families or origins, over
    // an existing catalog's records and lists (the same steps BuildCatalogCore runs).
    private static (KeywordIndex Keywords, List<EnchantedTemplate> Templates) MaterialsAndTemplates(IReadOnlyList<ISkyrimModGetter> mods, Catalog catalog, ProfileSettings profile)
    {
        using var timing = PhaseTiming.Measure("analysis.materials-templates");
        if (profile.EnchantmentProviderPlugins.Count == 0) throw new InvalidOperationException("Select at least one enchantment origin.");
        var providerKeys = profile.EnchantmentProviderPlugins.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => ModKey.FromFileName(x)).ToHashSet();
        var active = mods.Select(x => x.ModKey).ToHashSet();
        var inactive = providerKeys.Where(x => !active.Contains(x)).Select(x => x.FileName.String).OrderBy(x => x).ToArray();
        if (inactive.Length > 0) throw new InvalidOperationException($"The selected enchantment origin(s) are not active: {string.Join(", ", inactive)}.");
        var keywords = NewKeywordIndex(mods);
        ResolveMaterials(mods, catalog.Armors, catalog.Weapons, keywords, profile.MaterialFamilyOverrides);
        var templates = BuildUsableTemplates(catalog.Armors, catalog.Weapons, catalog.ObjectEffects, keywords, catalog.EnchantmentNames, catalog.DirectParents, providerKeys);
        if (templates.Count == 0)
            throw new InvalidOperationException($"No enabled enchantments from the selected origins have a usable ordinary-equipment template: {string.Join(", ", profile.EnchantmentProviderPlugins)}.");
        return (keywords, templates);
    }

    private static Catalog BuildCatalogCore(IReadOnlyList<ISkyrimModGetter> mods, IReadOnlyCollection<string> providerNames,
        bool broadUnmatchedDistribution,
        bool resolveLeveledListConflicts = false, IReadOnlyDictionary<string, string>? materialFamilyOverrides = null, SkyPatcherInputReport? skyPatcher = null, LlosInputReport? llos = null)
    {
        using var timing = PhaseTiming.Measure("catalog");
        ScanProgress.Enter(ScanStage.Catalog);
        if (providerNames.Count == 0)
            throw new InvalidOperationException("Select at least one enchantment origin.");
        var providerKeys = providerNames.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => ModKey.FromFileName(x)).ToHashSet();
        var activeKeys = mods.Select(x => x.ModKey).ToHashSet();
        var inactive = providerKeys.Where(x => !activeKeys.Contains(x)).Select(x => x.FileName.String).OrderBy(x => x).ToArray();
        if (inactive.Length > 0)
            throw new InvalidOperationException($"The selected enchantment origin(s) are not active: {string.Join(", ", inactive)}.");
        var armors = mods.SelectMany(x => x.Armors).GroupBy(x => x.FormKey).ToDictionary(x => x.Key, x => x.Last());
        var weapons = mods.SelectMany(x => x.Weapons).GroupBy(x => x.FormKey).ToDictionary(x => x.Key, x => x.Last());
        var winningLists = mods.SelectMany(x => x.LeveledItems).GroupBy(x => x.FormKey).ToDictionary(x => x.Key, x => x.Last());
        var conflictResolutions = resolveLeveledListConflicts
            ? ResolveLeveledListConflicts(mods, winningLists, weapons, armors)
            : [];
        var lists = winningLists.ToDictionary(x => x.Key,
            x => conflictResolutions.TryGetValue(x.Key, out var resolution)
                ? (ILeveledItemGetter)resolution.Resolved
                : x.Value);
        var nativeLists = SkyPatcherLists.Read(skyPatcher, lists);
        // A conflict repair is also an output mutation. Withhold it on uncertain
        // native targets instead of allowing an automatic write past route refusal.
        foreach (var key in nativeLists.Problems.Keys)
            if (conflictResolutions.Remove(key)) lists[key] = winningLists[key];
        // LLOS runs after plugin loading on the lists SkyPatcher leaves behind.
        var llosLists = LlosLists.Read(llos, lists, nativeLists);
        foreach (var key in llosLists.Problems.Keys)
            if (conflictResolutions.Remove(key)) lists[key] = winningLists[key];
        var keywordNames = NewKeywordIndex(mods);
        ResolveMaterials(mods, armors, weapons, keywordNames, materialFamilyOverrides);
        var objectEffects = mods.SelectMany(x => x.ObjectEffects).GroupBy(x => x.FormKey).ToDictionary(x => x.Key, x => x.Last());
        var magicEffects = mods.SelectMany(x => x.MagicEffects).GroupBy(x => x.FormKey).ToDictionary(x => x.Key, x => x.Last());
        var magicEffectNames = magicEffects.ToDictionary(x => x.Key, x => x.Value.Name?.String ?? x.Value.EditorID ?? x.Key.ToString());
        var winningPlugins = mods.SelectMany(m => m.Armors.Select(r => (r.FormKey, m.ModKey))
                .Concat(m.Weapons.Select(r => (r.FormKey, m.ModKey)))
                .Concat(m.LeveledItems.Select(r => (r.FormKey, m.ModKey))))
            .GroupBy(x => x.FormKey).ToDictionary(g => g.Key, g => g.Last().ModKey);
        var enchantNames = objectEffects.ToDictionary(x => x.Key,
            x => x.Value.Name?.String ?? x.Value.EditorID ?? x.Key.ToString());
        var distributed = lists.Values.SelectMany(x => x.Entries ?? []).Select(x => x.Data?.Reference.FormKey ?? FormKey.Null).Where(x => !x.IsNull).ToHashSet();
        var directParents = BuildDirectParents(lists.Values);
        var allTemplates = BuildUsableTemplates(armors, weapons, objectEffects, keywordNames, enchantNames, directParents, providerKeys);
        if (allTemplates.Count == 0)
            throw new InvalidOperationException($"No enabled enchantments from the selected origins have a usable ordinary-equipment template: {string.Join(", ", providerNames)}.");
        var catalog = new Catalog
        {
            Mods = mods,
            SkyPatcher = skyPatcher,
            NativeLists = nativeLists,
            LlosLists = llosLists,
            Armors = armors,
            Weapons = weapons,
            Lists = lists,
            WinningLists = winningLists,
            ConflictResolutions = conflictResolutions,
            KeywordNames = keywordNames,
            EnchantmentNames = enchantNames,
            MagicEffectNames = magicEffectNames,
            DistributedItems = distributed,
            DirectParents = directParents,
            Templates = [],
            AllTemplates = allTemplates,
            ObjectEffects = objectEffects,
            MagicEffectRecords = magicEffects,
            QuestLists = mods.SelectMany(m => m.Quests).GroupBy(q => q.FormKey).Select(g => g.Last())
                .SelectMany(q => q.EnumerateFormLinks()).Select(l => l.FormKey).Where(lists.ContainsKey).ToHashSet(),
            TemplatesByClass = []
            ,WinningPlugins = winningPlugins,
            BroadUnmatchedDistribution = broadUnmatchedDistribution
        };
        foreach (var (key, problem) in nativeLists.Problems)
        {
            catalog.RuntimeControlledLists[key] = problem;
            catalog.OtherRuntimeMutations.Add(key);
            catalog.RuntimeMetadataMutations.Add(key);
        }
        catalog.DistributionNotes.AddRange(nativeLists.Notes);
        foreach (var (key, reason) in llosLists.ConditionalMembership) catalog.RuntimeControlledLists.TryAdd(key, reason);
        // A withheld list keeps its plugin contents only analytically; LLOS still edits it at startup.
        foreach (var (key, problem) in llosLists.Problems) catalog.RuntimeControlledLists.TryAdd(key, problem);
        // SkyPatcher may also edit a generated list at startup, in an unverified order.
        if (nativeLists.HasInputs)
            foreach (var key in llosLists.Generated.Keys) catalog.RuntimeControlledLists.TryAdd(key, "LLOS generates this list and SkyPatcher may change it; their order is unverified.");
        foreach (var key in llosLists.WithheldOutfitPayloads.Where(lists.ContainsKey)) catalog.RuntimeControlledLists.TryAdd(key, "LLOS places this list in outfits through a swap XEP does not interpret.");
        return catalog;
    }

    // Author-filled rows report the rules file and keep the user's saved overrides as
    // the only manual ones; author values become the defaults the UI restores.
    private static void ApplyAuthorProvenance(List<CandidateItem> items, AuthorApplication author, Catalog catalog)
    {
        foreach (var item in items)
        {
            var notes = new List<string>();
            if (author.Include.TryGetValue(item.FormKey, out var include))
            {
                item.AuthorInclude = include.Value;
                notes.Add(include.Describe("include"));
            }
            if (author.Material.TryGetValue(item.FormKey, out var family)
                && item.MaterialFamilyChoices.Contains(family.Value, StringComparer.OrdinalIgnoreCase))
            {
                item.RecommendedMaterialFamily = item.MaterialFamily;
                item.HasMaterialFamilyOverride = false;
                item.AuthorMaterial = true;
                notes.Add(family.Describe("material family"));
            }
            if (author.Mode.TryGetValue(item.FormKey, out var mode) && item.HasSpecializedOverride && item.SpecializedMode == mode.Value)
            {
                item.RecommendedSpecializedMode = mode.Value;
                item.HasSpecializedOverride = false;
                item.AuthorMode = true;
                notes.Add(mode.Describe("distribution mode"));
            }
            string Names(AuthorValue<FormKey[]> list) => string.Join(", ", list.Value.Select(k => catalog.EnchantmentNames.GetValueOrDefault(k, k.ToString())));
            if (author.EnchantmentLists.TryGetValue(item.FormKey, out var allowed))
            {
                item.HasAuthorEnchantmentList = true;
                notes.Add($"Author enchantment list ({allowed.Rule}): only {Names(allowed)}" + (string.IsNullOrWhiteSpace(allowed.Reason) ? "" : $". {allowed.Reason}"));
            }
            else if (author.IgnoredEnchantmentLists.TryGetValue(item.FormKey, out var ignored))
            {
                item.HasAuthorEnchantmentList = true;
                item.IgnoreAuthorEnchantmentList = true;
                notes.Add($"Author enchantment list ignored by your choice: {Names(ignored)}");
            }
            string Places(AuthorValue<FormKey[]> list) => string.Join(", ", list.Value.Select(k =>
                (catalog.Lists.GetValueOrDefault(k)?.EditorID ?? catalog.ConsumerRecords.Containers.GetValueOrDefault(k)?.EditorID) is { Length: > 0 } id ? $"{id} [{k}]" : k.ToString()));
            if (author.DestinationLists.TryGetValue(item.FormKey, out var destinations))
            {
                item.HasAuthorDestinationList = true;
                notes.Add($"Author destination list ({destinations.Rule}): only {Places(destinations)}" + (string.IsNullOrWhiteSpace(destinations.Reason) ? "" : $". {destinations.Reason}"));
            }
            else if (author.IgnoredDestinationLists.TryGetValue(item.FormKey, out var ignoredDestinations))
            {
                item.HasAuthorDestinationList = true;
                item.IgnoreAuthorDestinationList = true;
                notes.Add($"Author destination list ignored by your choice: {Places(ignoredDestinations)}");
            }
            item.AuthorNote = string.Join("\n", notes);
        }
    }

    private static List<EnchantmentSelectionOption> BuildEnchantmentOptions(Catalog catalog, ProfileSettings profile)
        => BuildEnchantmentOptionsWithAuthor(catalog, profile, null);

    private static List<EnchantmentSelectionOption> BuildEnchantmentOptionsWithAuthor(Catalog catalog, ProfileSettings profile, AuthorApplication? author)
    {
        var providers = profile.EnchantmentProviderPlugins.Select(x => ModKey.FromFileName(x)).ToHashSet();
        var templates = catalog.AllTemplates.GroupBy(x => x.Enchantment).ToDictionary(x => x.Key, x => x.ToArray());
        // Tiers are grouped by the game's own Base Enchantment link (the record the
        // enchanting table uses); a record without one is its own family root.
        static FormKey FamilyKey(IObjectEffectGetter effect) => effect.BaseEnchantment.IsNull ? effect.FormKey : effect.BaseEnchantment.FormKey;
        string Name(IObjectEffectGetter effect) => effect.Name?.String ?? effect.EditorID ?? effect.FormKey.ToString();
        return catalog.ObjectEffects.Values.Where(x => providers.Contains(x.FormKey.ModKey) && !x.IsDeleted)
            .GroupBy(FamilyKey)
            .Select(family =>
            {
                var members = family.OrderBy(x => x.EditorID ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(x => x.FormKey).ToArray();
                var root = catalog.ObjectEffects.GetValueOrDefault(family.Key);
                var tiers = members.Select(effect =>
                {
                    var usable = templates.GetValueOrDefault(effect.FormKey, []);
                    var authorValue = author?.Enchantment.GetValueOrDefault(effect.FormKey.ToString());
                    // Overlap cancellations outrank an author request to enable the tier.
                    if (authorValue is { Value: true } && catalog.EnchantmentRecommendations.ContainsKey(effect.FormKey)) authorValue = null;
                    return (Effect: effect, Usable: usable, Tier: new EnchantmentTierOption(effect.FormKey.ToString(), effect.EditorID ?? "",
                        DefaultSelected: authorValue?.Value ?? RecommendedEnchantmentSelection(effect.FormKey, usable.Any(x => HasTemplateDistribution(x, catalog)), catalog), Available: usable.Length > 0,
                        AuthorDefault: authorValue is not null));
                }).ToArray();
                bool Selected(EnchantmentTierOption tier) => tier.Available
                    && EffectiveEnchantmentSelection(FormKey.Factory(tier.FormKey), tier.DefaultSelected, catalog, profile);
                var available = tiers.Count(x => x.Tier.Available);
                var defaults = tiers.Count(x => x.Tier.DefaultSelected);
                var selected = tiers.Count(x => Selected(x.Tier));
                var reason = available == 0 ? "No eligible ordinary-equipment template for any tier; unique/artifact and other existing safety checks apply"
                    : defaults > 0 ? $"Enabled by default: {defaults} of {tiers.Length} tiers already have an eligible template in leveled-list or CDF distribution"
                    : members.Any(e => catalog.EnchantmentRecommendations.ContainsKey(e.FormKey)) ? "Disabled by recommendation: lower-priority overlapping tiers"
                    : "Disabled by default: usable templates have no existing leveled-list or CDF placement";
                if (available < tiers.Length && available > 0) reason += $"; {tiers.Length - available} tier(s) have no usable template";
                var effects = string.Join("; ", members.SelectMany(effect => effect.Effects).Select(x =>
                {
                    var description = catalog.MagicEffectRecords.GetValueOrDefault(x.BaseEffect.FormKey)?.Description?.String;
                    return string.IsNullOrWhiteSpace(description)
                        ? catalog.MagicEffectNames.GetValueOrDefault(x.BaseEffect.FormKey, x.BaseEffect.FormKey.ToString())
                        : description.Replace("<mag>", "[magnitude]", StringComparison.OrdinalIgnoreCase)
                            .Replace("<dur>", "[duration]", StringComparison.OrdinalIgnoreCase)
                            .Replace("<area>", "[area]", StringComparison.OrdinalIgnoreCase);
                }).Distinct());
                var display = root is not null && providers.Contains(root.FormKey.ModKey) ? root : members[0];
                return new EnchantmentSelectionOption
                {
                    FormKey = family.Key.ToString(),
                    Provider = string.Join(", ", members.Select(x => x.FormKey.ModKey.FileName.String).Distinct(StringComparer.OrdinalIgnoreCase)),
                    Name = Name(display), EditorId = display.EditorID ?? "",
                    Effects = effects, Reason = reason + "\n" + string.Join("\n", members.Select(e => catalog.EnchantmentRecommendations.GetValueOrDefault(e.FormKey)?.Reason).OfType<string>().Distinct()),
                    HasManualOverride = members.Any(e => (author?.User ?? profile).EnchantmentOverrides.ContainsKey(e.FormKey.ToString())),
                    AuthorNote = string.Join("\n", members.Select(e => (Key: e.FormKey, Value: author?.Enchantment.GetValueOrDefault(e.FormKey.ToString())))
                        .Where(x => x.Value is not null)
                        .Select(x => x.Value!.Value && catalog.EnchantmentRecommendations.ContainsKey(x.Key)
                            ? $"The rules file asks to enable {x.Key}, but the overlap recommendation keeps it off (overlap rules outrank author rules)."
                            : x.Value.Describe("enabled")).Distinct()),
                    DefaultSelected = defaults > 0, Available = available > 0,
                    TemplateCount = tiers.Sum(x => x.Usable.Length), LeveledTemplateCount = tiers.Sum(x => x.Usable.Count(t => t.Parents.Count > 0)),
                    Tiers = tiers.Select(x => x.Tier).ToList(), SelectedTierCount = selected, IsSelected = selected > 0
                };
            })
            // Families with no usable tier (trap, staff, creature and other non-equipment
            // enchantments) cannot be generated and are not listed.
            .Where(x => x.Available)
            .OrderBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FormKey, StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<FormKey, List<ParentEntry>> BuildDirectParents(IEnumerable<ILeveledItemGetter> lists)
    {
        var result = new Dictionary<FormKey, List<ParentEntry>>();
        foreach (var list in lists)
            foreach (var entry in list.Entries ?? [])
                if (entry.Data is { } data)
                {
                    if (!result.TryGetValue(data.Reference.FormKey, out var parents))
                        result[data.Reference.FormKey] = parents = [];
                    parents.Add(new ParentEntry(list.FormKey, data.Level, data.Count));
                }
        return result;
    }

    private static List<EnchantedTemplate> BuildUsableTemplates(
        IReadOnlyDictionary<FormKey, IArmorGetter> armors,
        IReadOnlyDictionary<FormKey, IWeaponGetter> weapons,
        IReadOnlyDictionary<FormKey, IObjectEffectGetter> objectEffects,
        IReadOnlyDictionary<FormKey, string> keywordNames,
        IReadOnlyDictionary<FormKey, string> enchantNames,
        IReadOnlyDictionary<FormKey, List<ParentEntry>> directParents,
        IReadOnlySet<ModKey>? enchantmentOrigins = null)
    {
        var templates = new List<EnchantedTemplate>();
        // A WEAP template names a weapon and an ARMO template an armor. A cross-kind
        // base is malformed: its class would pair the enchanted record with sources
        // of the other kind, which cannot be generated.
        IMajorRecordGetter? SameKind(IMajorRecordGetter item, FormKey key) => item switch
        {
            IWeaponGetter => weapons.GetValueOrDefault(key),
            IArmorGetter => armors.GetValueOrDefault(key),
            _ => null
        };
        var equipment = armors.Values.Cast<IMajorRecordGetter>().Concat(weapons.Values).OrderBy(x => x.FormKey);
        foreach (var item in equipment)
        {
            if (!OrdinaryEquipment(item, keywordNames) || !TryEnchantment(item, out var enchantment) ||
                enchantmentOrigins is not null && !enchantmentOrigins.Contains(enchantment.ModKey) ||
                !objectEffects.TryGetValue(enchantment, out var effect) || effect.IsDeleted ||
                !TryBase(item, out var baseKey)) continue;
            var parents = directParents.GetValueOrDefault(item.FormKey, []);

            var baseItem = SameKind(item, baseKey);
            var chain = new HashSet<FormKey>();
            while (baseItem is not null && TryEnchantment(baseItem, out _))
            {
                if (!chain.Add(baseItem.FormKey) || !TryBase(baseItem, out var next)) { baseItem = null; break; }
                baseItem = SameKind(baseItem, next);
            }
            if (baseItem is null || !OrdinaryEquipment(baseItem, keywordNames)) continue;
            var cls = Classify(baseItem, keywordNames);
            if (cls is null) continue;
            templates.Add(new EnchantedTemplate
            {
                Item = item,
                BaseItem = baseItem,
                Class = cls,
                Enchantment = enchantment,
                EnchantmentName = enchantNames.GetValueOrDefault(enchantment, enchantment.ToString()),
                Parents = parents.Distinct().ToList()
            });
        }
        return templates;
    }

    // Keep the complete catalog: even equipment without a distribution route can
    // supply templates, materials, overrides or script evidence for other sources.
    // Only per-item candidate analysis is restricted, before its expensive work.
    private static List<CandidateItem> FindDistributionCandidates(IReadOnlyList<ISkyrimModGetter> mods, Catalog catalog, ProfileSettings profile)
    {
        HashSet<FormKey> sources;
        using (PhaseTiming.Measure("scan-candidate-sources"))
        {
            var scriptSources = ScriptDistributionSources(catalog);
            sources = catalog.Weapons.Values.Cast<IMajorRecordGetter>().Concat(catalog.Armors.Values)
                .Where(record => ExclusionReason(record, catalog.KeywordNames).Length == 0
                    && (HasBaseLootRoute(record.FormKey, catalog) || scriptSources.Contains(record.FormKey)
                        || profile.SpecializedLootOverrides.GetValueOrDefault(record.FormKey.ToString()) == SpecializedLootMode.ScriptDistribution))
                .Select(record => record.FormKey).ToHashSet();
        }
        return FindCandidatesCore(mods, catalog, sources);
    }
    private static List<CandidateItem> FindCandidates(IReadOnlyList<ISkyrimModGetter> mods, Catalog catalog)
        => FindCandidatesCore(mods, catalog, null);
    private static List<CandidateItem> FindCandidatesCore(IReadOnlyList<ISkyrimModGetter> mods, Catalog catalog, HashSet<FormKey>? restrictTo)
    {
        using var timing = PhaseTiming.Measure("scan-candidates");
        ScanProgress.Enter(ScanStage.Candidates);
        EnsureSourceIndex(catalog);
        var result = new List<CandidateItem>();
        var recordsTotal = catalog.Weapons.Keys.Concat(catalog.Armors.Keys).Count(key => restrictTo is null || restrictTo.Contains(key));
        var recordsDone = 0;
        foreach (var record in catalog.Weapons.Values.Cast<IMajorRecordGetter>().Concat(catalog.Armors.Values)
                     .Where(x => restrictTo is null || restrictTo.Contains(x.FormKey))
                     .OrderBy(x => x.FormKey.ModKey.FileName.String).ThenBy(x => x.FormKey))
        {
            var plugin = record.FormKey.ModKey.FileName.String;
            ScanProgress.Advance(recordsDone++, recordsTotal);
            OperationProgress.Detail($"Scanning equipment ({recordsDone:N0}/{recordsTotal:N0}): {plugin}: {DisplayName(record)}");
            var signature = record is IWeaponGetter ? "WEAP" : "ARMO";
            var playable = record switch
            {
                IWeaponGetter weapon => !weapon.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable),
                IArmorGetter armor => !armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable),
                _ => false
            };
            var reason = ExclusionReason(record, catalog.KeywordNames);
            var cls = CachedClass(record, catalog);
            var shape = CachedShape(record, catalog);
            var classTemplates = cls is null ? null : catalog.TemplatesByClass.GetValueOrDefault(ClassKey(cls));
            var useBroad = catalog.BroadUnmatchedDistribution && shape is not null
                && (cls is null || !IsOwnBaseMaterial(cls.Material))
                && (cls is null || classTemplates is null || classTemplates.Count == 0);
            var broadTemplates = useBroad ? catalog.Templates.Where(t => ShapeMatches(shape!, t.Class)).ToList() : null;
            var materialIndex = catalog.KeywordNames as KeywordIndex;
            var materialResolution = materialIndex?.Resolved.GetValueOrDefault(record.FormKey);
            var materialProblem = materialIndex?.Unresolved.GetValueOrDefault(record.FormKey);
            if (reason.Length == 0 && cls is null && !useBroad)
                reason = "Unsupported or ambiguous material/type/slot" + (materialProblem is null ? "" : ": " + materialProblem);
            var unrestrictedTemplates = useBroad ? broadTemplates : classTemplates;
            var selectedTemplates = SourceTemplates(record.FormKey, unrestrictedTemplates, catalog);
            var variantCount = selectedTemplates is null || selectedTemplates.Count == 0 ? 0
                : (useBroad ? TemplatesForBroadSource(record, selectedTemplates, catalog) : TemplatesForExactSource(record, selectedTemplates, catalog)).Length;
            if (reason.Length == 0 && variantCount == 0 && cls is not null)
                variantCount = AssessSpecialized(record.FormKey, catalog).Routes.Select(r => VariantIdentity(r.Template.Item, r.Template.Enchantment, catalog)).Distinct().Count();
            if (reason.Length == 0 && variantCount == 0)
                reason = catalog.SourceEnchantmentAllow.ContainsKey(record.FormKey) && unrestrictedTemplates is { Count: > 0 }
                    ? AuthorListLeavesNoTemplate : "No matching archetype for the selected enchantment origins";
            var eligible = reason.Length == 0;
            var baseDistributed = HasBaseLootRoute(record.FormKey, catalog);
            if (eligible && !baseDistributed) reason = "No base-item leveled-list or verified insertion route; enchanted variants require an existing source route.";
            if (!eligible && cls is null && !LooksLikeEquipment(record)) continue;
            result.Add(new CandidateItem
            {
                Plugin = plugin, FormKey = record.FormKey.ToString(), Signature = signature,
                EditorId = record.EditorID ?? "", Name = DisplayName(record), Category = cls is not null ? cls + (materialResolution?.Inferred == true ? " (inferred)" : "") : useBroad ? $"Broad fallback: {shape}" : "Unclassified",
                MaterialRule = materialResolution?.Rule ?? "unresolved",
                MaterialEvidence = materialResolution?.Evidence ?? materialProblem ?? "",
                MaterialFamilyChoices = materialResolution is null ? []
                    : materialResolution.AvailableChoices.Length > 0 ? materialResolution.AvailableChoices.ToList() : [materialResolution.Material],
                RecommendedMaterialFamily = materialResolution?.RecommendedMaterial ?? materialResolution?.Material ?? "",
                MaterialFamily = materialResolution?.Material ?? "",
                HasMaterialFamilyOverride = materialResolution is not null && materialResolution.AvailableChoices.Length > 1
                    && materialResolution.Rule == "user-selected material family",
                IsPlayable = playable, Eligible = eligible, AlreadyDistributed = baseDistributed,
                AlreadyEnchanted = TryEnchantment(record, out _),
                Reason = reason, AvailableVariants = variantCount
            });
        }
        return result;
    }

    private static string ExclusionReason(IMajorRecordGetter record, IReadOnlyDictionary<FormKey, string> keywordNames)
    {
        if (record.IsDeleted) return "Deleted winning record";
        if (string.IsNullOrWhiteSpace(DisplayName(record))) return "Unnamed record";
        if (TryEnchantment(record, out _)) return "Already enchanted";
        if (record is IArmorGetter armor && armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable)) return "Non-playable armor";
        if (record is IWeaponGetter weapon && weapon.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable)) return "Non-playable weapon";
        var names = Keywords(record).Select(x => keywordNames.GetValueOrDefault(x.FormKey, "")).ToArray();
        if (names.Any(x => x.Contains("DisallowEnchanting", StringComparison.OrdinalIgnoreCase) ||
                           x.Contains("DaedricArtifact", StringComparison.OrdinalIgnoreCase) ||
                           x.Contains("Artifact", StringComparison.OrdinalIgnoreCase))) return "Artifact or disenchanting restriction keyword";
        if (record is IWeaponGetter w && w.Data?.AnimationType is WeaponAnimationType.Staff) return "Unsupported weapon animation type";
        return "";
    }

    private static EquipmentClass? Classify(IMajorRecordGetter record, IReadOnlyDictionary<FormKey, string> keywordNames)
    {
        var shape = ClassifyShape(record, keywordNames);
        if (shape is null || shape.Materials.Length != 1) return null;
        return new(shape.Signature, shape.Materials[0], shape.Type, shape.ArmorType);
    }

    private static EquipmentShape? ClassifyShape(IMajorRecordGetter record, IReadOnlyDictionary<FormKey, string> keywordNames)
        => ClassifyShapeFromLabels(record, keywordNames,
            Keywords(record).Select(x => keywordNames.GetValueOrDefault(x.FormKey, "")).Where(x => x.Length > 0).ToArray());

    private static EquipmentShape? ClassifyShapeFromLabels(IMajorRecordGetter record, IReadOnlyDictionary<FormKey, string> keywordNames, string[] labels)
    {
        // Skyrim stores wearable jewelry in ARMO records, but jewelry normally has
        // no ArmorMaterial keyword. Classify its biped slot first and use a neutral
        // family so gem/metal naming never makes rings, amulets, or circlets
        // unsupported. The three slots remain distinct routing subtypes.
        if (record is IArmorGetter jewelry && jewelry.BodyTemplate is { } jewelryBody)
        {
            var flags = jewelryBody.FirstPersonFlags;
            var circletKeyword = labels.Any(x => x.Contains("Jewelry", StringComparison.OrdinalIgnoreCase)
                || x.Contains("Circlet", StringComparison.OrdinalIgnoreCase));
            var occupiesArmorSlot = flags.HasFlag(BipedObjectFlag.Head) || flags.HasFlag(BipedObjectFlag.Hair)
                || flags.HasFlag(BipedObjectFlag.Body) || flags.HasFlag(BipedObjectFlag.Hands)
                || flags.HasFlag(BipedObjectFlag.Forearms) || flags.HasFlag(BipedObjectFlag.Feet)
                || flags.HasFlag(BipedObjectFlag.Calves) || flags.HasFlag(BipedObjectFlag.Shield);
            var jewelrySlots = new[]
            {
                (Matches: flags.HasFlag(BipedObjectFlag.Ring) && !occupiesArmorSlot, Type: "Ring"),
                (Matches: flags.HasFlag(BipedObjectFlag.Amulet) && !occupiesArmorSlot, Type: "Amulet"),
                // Helmets commonly reserve the Circlet slot as an exclusion slot.
                // A wearable circlet uses that slot without Head or Hair and carries
                // explicit jewelry/circlet classification evidence. Circlet-slot
                // bandages, wigs, and other accessories therefore stay unsupported.
                (Matches: flags.HasFlag(BipedObjectFlag.Circlet)
                    && !occupiesArmorSlot
                    && circletKeyword, Type: "Circlet")
            }.Where(x => x.Matches).Select(x => x.Type).Distinct().ToArray();
            if (jewelrySlots.Length > 1) return null;
            if (jewelrySlots.Length == 1)
                return new("ARMO", jewelrySlots[0], null, ["Jewelry"]);
        }
        var materials = labels.Select(MaterialFromKeyword).Where(x => x is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keywordNames is KeywordIndex resolved && resolved.Resolved.TryGetValue(record.FormKey, out var materialResolution))
            materials = [materialResolution.Material];
        // These specific families can retain their broader vanilla material tag.
        // Resolve only known redundant pairs; unrelated conflicts remain ambiguous.
        else materials = ReduceKnownMaterialPair(record, materials);
        // No vanilla-form material keyword: a framework <prefix>Material<Name> keyword naming a known
        // material, else the crafting-evidence inference made while building the catalog.
        if (materials.Length == 0 && keywordNames is KeywordIndex index)
        {
            materials = index.FrameworkMaterials(labels).Materials;
            if (materials.Length == 0 && index.Inferred.TryGetValue(record.FormKey, out var inferred)) materials = [inferred.Material];
        }
        if (materials.Length == 0) return null;
        var (equipmentType, armorType) = EquipmentType(record, labels);
        return equipmentType is null ? null : new(record is IWeaponGetter ? "WEAP" : "ARMO", equipmentType, armorType, materials);
    }

    // Slot/animation type of an armor or weapon, independent of its material.
    private static (string? Type, ArmorType? ArmorType) EquipmentType(IMajorRecordGetter record, string[] labels)
    {
        if (record is IWeaponGetter weapon)
        {
            var type = weapon.Data?.AnimationType switch
            {
                WeaponAnimationType.OneHandSword => "Sword",
                WeaponAnimationType.OneHandDagger => "Dagger",
                WeaponAnimationType.OneHandAxe => "WarAxe",
                WeaponAnimationType.OneHandMace => "Mace",
                WeaponAnimationType.TwoHandSword => "Greatsword",
                WeaponAnimationType.TwoHandAxe when labels.Any(x => x.Contains("Warhammer", StringComparison.OrdinalIgnoreCase)) => "Warhammer",
                WeaponAnimationType.TwoHandAxe => "Battleaxe",
                WeaponAnimationType.Bow => "Bow",
                WeaponAnimationType.Crossbow => "Crossbow",
                _ => null
            };
            return (type, null);
        }
        if (record is IArmorGetter armor && armor.BodyTemplate is { } body)
        {
            var flags = body.FirstPersonFlags;
            var type = flags.HasFlag(BipedObjectFlag.Shield) ? "Shield" :
                flags.HasFlag(BipedObjectFlag.Body) ? "Cuirass" :
                flags.HasFlag(BipedObjectFlag.Hands) ? "Gauntlets" :
                flags.HasFlag(BipedObjectFlag.Feet) ? "Boots" :
                flags.HasFlag(BipedObjectFlag.Head) || flags.HasFlag(BipedObjectFlag.Hair) ? "Helmet" : null;
            return (type, body.ArmorType);
        }
        return (null, null);
    }

    private static bool IsJewelryType(string type) => type is "Ring" or "Amulet" or "Circlet";

    private static bool ShapeMatches(EquipmentShape shape, EquipmentClass cls)
        => shape.Signature.Equals(cls.Signature, StringComparison.OrdinalIgnoreCase)
           && shape.Type.Equals(cls.Type, StringComparison.OrdinalIgnoreCase)
           && shape.ArmorType == cls.ArmorType;

    private static string? MaterialFromKeyword(string label)
    {
        foreach (var marker in new[] { "WeapMaterial", "WeaponMaterial", "ArmorMaterial" })
        {
            var index = label.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var value = label[(index + marker.Length)..].Trim('_');
            if (value.Length > 0) return NormalizeMaterial(value);
        }
        return null;
    }

    private static string NormalizeMaterial(string value) => value.ToLowerInvariant() switch
    {
        "dragonbone" or "dragonscale" or "dragonplate" => "Dragon",
        "draugrhoned" => "DraugrHoned",
        "falmerhoned" => "FalmerHoned",
        "steelplate" => "SteelPlate",
        "chitinheavy" => "ChitinHeavy",
        "chitinlight" => "ChitinLight",
        _ => char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant()
    };

    private static IMajorRecordGetter ChooseArchetype(IMajorRecordGetter source, IEnumerable<EnchantedTemplate> templates,
        Catalog catalog, bool broad = false)
    {
        var sourceValue = CachedValue(source, catalog);
        var subtype = broad ? DistributionSubtype(source, catalog) : null;
        var group = broad ? SubtypeGroup(subtype) : null;
        var seen = new HashSet<FormKey>();
        IMajorRecordGetter? best = null;
        (int Type, int Group, int Distance, FormKey Key) bestRank = default;
        foreach (var template in templates)
        {
            var item = template.BaseItem;
            if (!seen.Add(item.FormKey)) continue; // Preserve the first copy of each base.
            var type = broad ? DistributionSubtype(item, catalog) : null;
            var rank = (broad && type != subtype ? 1 : 0,
                broad && SubtypeGroup(type) != group ? 1 : 0,
                Math.Abs(sourceValue - CachedValue(item, catalog)), item.FormKey);
            if (best is null || rank.CompareTo(bestRank) < 0) { best = item; bestRank = rank; }
        }
        return best ?? throw new InvalidOperationException("Sequence contains no elements");
    }

    private static EnchantedTemplate[] TemplatesForSource(IMajorRecordGetter source, IEnumerable<EnchantedTemplate> templates, Catalog catalog)
    {
        return templates.GroupBy(x => x.Enchantment.ModKey)
            .SelectMany(group =>
            {
                var archetype = ChooseArchetype(source, group, catalog);
                return group.Where(x => x.BaseItem.FormKey == archetype.FormKey);
            })
            .DistinctBy(x => (x.Item.FormKey, x.Enchantment))
            .OrderBy(x => PluginName(x.Enchantment.ModKey, catalog), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Item.FormKey)
            .ToArray();
    }

    private static int ItemValue(IMajorRecordGetter item) => item switch
    {
        IWeaponGetter w => checked((int)(w.BasicStats?.Value ?? 0)),
        IArmorGetter a => checked((int)a.Value),
        _ => 0
    };

    private static uint AdjustValue(uint sourceValue, IMajorRecordGetter baseItem, IMajorRecordGetter enchantedItem)
    {
        var delta = Math.Max(0, ItemValue(enchantedItem) - ItemValue(baseItem));
        return (uint)Math.Clamp((long)sourceValue + delta, 0, uint.MaxValue);
    }

    private static string VariantName(IMajorRecordGetter source, IMajorRecordGetter baseItem, IMajorRecordGetter template, string enchantmentName)
    {
        var sourceName = DisplayName(source);
        var baseName = DisplayName(baseItem);
        var templateName = DisplayName(template);
        if (templateName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
            return sourceName + templateName[baseName.Length..];
        if (templateName.EndsWith(baseName, StringComparison.OrdinalIgnoreCase))
            return templateName[..^baseName.Length] + sourceName;
        return $"{sourceName} of {enchantmentName}";
    }

    // Names below a root's remembered suffix were all taken when they were probed,
    // and the used set only grows, so resuming there yields the same smallest free name.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HashSet<string>, Dictionary<string, int>> EditorIdSuffixes = new();

    private static string EditorId(string actualItemName, HashSet<string> used)
    {
        var words = EditorIdInvalidCharacters().Replace(actualItemName.Trim(), "_").Trim('_');
        if (words.Length == 0) words = "Item";
        var root = "XEP_" + words;
        root = root[..Math.Min(120, root.Length)];
        if (used.Add(root)) return root;
        var next = EditorIdSuffixes.GetOrCreateValue(used);
        for (var number = next.GetValueOrDefault(root, 2); ; number++)
        {
            var suffix = $"_{number:00}";
            var candidate = root[..Math.Min(root.Length, 120 - suffix.Length)] + suffix;
            if (used.Add(candidate)) { next[root] = number + 1; return candidate; }
        }
    }

    private static FormKey StableKey(SkyrimMod output, string identity, HashSet<uint> used)
    {
        var layout = OutputLayout.For(output);
        if (used.Count >= layout.IdCount)
            throw new InvalidOperationException(layout.IsLight
                ? "An ESPFE partition exceeded Skyrim's 4,096-new-record capacity."
                : "The full output plugin exceeded its new-record capacity.");

        var bytes = System.Text.Encoding.UTF8.GetBytes(identity);
        uint hash = 2166136261;
        foreach (var b in bytes) hash = (hash ^ b) * 16777619;
        var start = hash % layout.IdCount;
        for (uint offset = 0; offset < layout.IdCount; offset++)
        {
            var id = layout.FirstId + (start + offset) % layout.IdCount;
            if (used.Add(id)) return new FormKey(output.ModKey, id);
        }
        throw new InvalidOperationException($"No free FormID remained in the 0x{layout.FirstId:X3}–0x{layout.LastId:X} range.");
    }

    private static string[] OutputPaths(string requestedOutput, int count)
    {
        if (count == 1) return [requestedOutput];
        var directory = Path.GetDirectoryName(requestedOutput)!;
        var stem = Path.GetFileNameWithoutExtension(requestedOutput);
        return Enumerable.Range(0, count).Select(index => Path.Combine(directory, $"{stem}_{index}.esp")).ToArray();
    }

    private static string RequestedOutput(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.OutputFolder))
            throw new ArgumentException("Choose an output folder.");
        return Path.Combine(Path.GetFullPath(settings.OutputFolder), OutputFileName);
    }

    private static void WriteAndValidate(IReadOnlyList<SkyrimMod> patches, IReadOnlyList<ISkyrimModGetter> mods,
        string requestedOutput, IReadOnlyList<string> outputs, PatchReport report,
        IReadOnlySet<FormKey> permittedUnreachable, CdfOutput? cdfOutput = null)
    {
        using var timing = PhaseTiming.Measure("write-and-validate");
        var directory = Path.GetDirectoryName(requestedOutput)!;
        Directory.CreateDirectory(directory);
        var stage = Path.Combine(directory, ".tvp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        // Support files (CDF companion, runtime scripts, SEQ) install into nested folders.
        // They are staged flat, so staging adds only the short staging-folder prefix instead
        // of their nesting (the deep companion stages shorter than its target): under MO2's
        // file-system hooks, moves and deletes past MAX_PATH fail, even in extended-length form.
        var support = new List<(string Staged, string Relative)>();
        string StageSupport(string relative)
        {
            var staged = Path.Combine(stage, $"s{support.Count}-{Path.GetFileName(relative)}");
            support.Add((staged, relative));
            return staged;
        }
        Task? reportWrite = null;
        try
        {
            var stagedPlugins = outputs.Select(output => Path.Combine(stage, Path.GetFileName(output))).ToList();
            var selections = new List<ListSelection>[patches.Count];
            var variantCounts = new int[patches.Count];
            using (PhaseTiming.Measure("write.plugins"))
                ParallelInOrder(patches.Count, index =>
                {
                    var patch = patches[index];
                    var staged = stagedPlugins[index];
                    var knownLoadOrder = mods.Concat(patches.Take(index).Cast<ISkyrimModGetter>()).ToArray();
                    // Master index zero plus local ID zero encodes the null reference.
                    // Keep an external input first so cross-output ID zero remains representable.
                    patch.BeginWrite.ToPath(staged).WithLoadOrder(knownLoadOrder)
                        .WithExtraIncludedMasters(mods.Take(1).Select(m => m.ModKey)).Write();

                    using var check = SkyrimMod.CreateFromBinaryOverlay(staged, SkyrimRelease.SkyrimSE);
                    var layout = OutputLayout.For(patch);
                    if (check.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small) != layout.IsLight)
                        throw new InvalidDataException(layout.IsLight
                            ? "Output validation failed: the ESP is not marked as an ESL/light plugin."
                            : "Output validation failed: the full output plugin is marked as an ESL/light plugin.");
                    if (layout.IsLight && check.ModHeader.Stats.Version < 1.71f)
                        throw new InvalidDataException("Output validation failed: expanded ESPFE FormIDs require HEDR version 1.71 or newer.");
                    if (check.Weapons.Count + check.Armors.Count != patch.Weapons.Count + patch.Armors.Count)
                        throw new InvalidDataException($"Output validation failed for {patch.ModKey}: generated variant count changed during serialization.");
                    if (check.LeveledItems.Count != patch.LeveledItems.Count)
                        throw new InvalidDataException($"Output validation failed for {patch.ModKey}: leveled-list override count changed during serialization.");
                    var newRecords = check.EnumerateMajorRecords().Where(x => x.FormKey.ModKey == check.ModKey).ToArray();
                    if (newRecords.Length > layout.IdCount || newRecords.Any(x => !layout.Contains(x.FormKey.ID)))
                        throw new InvalidDataException($"Output validation failed: a generated record is outside the {layout.Kind} FormID range.");
                    if (check.LeveledItems.Any(x => (x.Entries?.Count ?? 0) > 255))
                        throw new InvalidDataException($"Output validation failed for {patch.ModKey}: a leveled list exceeds 255 entries.");
                    variantCounts[index] = check.Weapons.Count + check.Armors.Count;
                    selections[index] = check.LeveledItems.Select(list => new ListSelection(
                        list.FormKey.ToString(), (int)list.Flags, list.Global.IsNull ? "" : list.Global.FormKey.ToString(),
                        // Chance None is a whole percent in the record; round away the fraction's binary error.
                        Math.Round(list.ChanceNone.Value * 100), (list.Entries ?? []).Where(entry => entry.Data is not null).Select(entry => new ListSelectionEntry(
                            entry.Data!.Reference.FormKey.ToString(), unchecked((ushort)entry.Data.Level), entry.Data.Count)).ToList())).ToList();
                });
            foreach (var selection in selections) report.ListSelections.AddRange(selection);

            // The validated overlays already counted every written variant.
            if (variantCounts.Sum() != report.Variants)
                throw new InvalidDataException("Output validation failed: total generated variant count changed during serialization.");

            if (patches.Sum(p => p.LeveledItems.Count(l => l.FormKey.ModKey == p.ModKey)) != report.GeneratedSublists)
                throw new InvalidDataException("Output validation failed: generated sublist count differs from the report.");
            if (patches.SelectMany(p => p.LeveledItems.Where(l => l.FormKey.ModKey == p.ModKey))
                .Any(l => l.EditorID is null || !l.EditorID.StartsWith("XEP_", StringComparison.Ordinal)))
                throw new InvalidDataException("Output validation failed: every generated sublist EditorID must use the XEP_ prefix.");

            var reportPath = ReportPath(requestedOutput);
            var manifestPath = Path.ChangeExtension(requestedOutput, ".manifest.json");
            // The report names the backup folder, so the backup is planned now; it is
            // created only after every validation below passes, as before.
            var backup = PlanOutputBackup(requestedOutput);
            report.BackupFolder = backup?.Folder;
            var stagedReport = Path.Combine(stage, Path.GetFileName(reportPath));
            var stagedManifest = Path.Combine(stage, Path.GetFileName(manifestPath));
            // The report is complete once the written plugins are validated; serialize
            // it while reference and reachability validation run. A failure still
            // waits for the writer before the staging folder is removed.
            reportWrite = Task.Run(() => WriteReport(stagedReport, report));
            OperationProgress.Stage("Validating output references…", GenerationStage.References);
            using (PhaseTiming.Measure("validation.references"))
                ValidateAllReferences(stagedPlugins, mods);
            StageCdfOutput(cdfOutput, requestedOutput, StageSupport, stagedPlugins);
            OperationProgress.Stage("Validating output reachability…", GenerationStage.Reachability);
            using (PhaseTiming.Measure("validation.reachability"))
                ValidateReachabilityCore(stagedPlugins, permittedUnreachable, cdfOutput?.Roots ?? new HashSet<FormKey>());

            if(report.RuntimeQuests > 0)
            {
                var resources = new List<string> { "XEP_QuestInsertion" };
                if (report.ScriptGates.Count > 0) resources.AddRange(["XEP_ScriptGate", "XEP_ScriptGatePlayerAlias"]);
                foreach(var (resource,folder) in resources.SelectMany(name => new[]{(name+".pex","Scripts"),(name+".psc",Path.Combine("Source","Scripts"))}))
                {
                    var target=StageSupport(Path.Combine(folder,resource));
                    using var input=typeof(PatcherEngine).Assembly.GetManifestResourceStream("XEnchantmentPatcher.Runtime."+resource)
                        ?? throw new InvalidDataException("Runtime script is missing from this build.");
                    using var output=File.Create(target);input.CopyTo(output);
                }
                foreach(var stagedPlugin in stagedPlugins)
                {
                    using var patch=SkyrimMod.CreateFromBinaryOverlay(stagedPlugin,SkyrimRelease.SkyrimSE);
                    if(patch.Quests.Count==0)continue;
                    using var seq=new BinaryWriter(File.Create(StageSupport(Path.Combine("SEQ",patch.ModKey.Name+".seq"))));
                    foreach(var quest in patch.Quests)seq.Write(((uint)patch.ModHeader.MasterReferences.Count << 24) | quest.FormKey.ID);
                }
            }

            OperationProgress.Stage("Saving output report and backup…", GenerationStage.Report);
            CreateOutputBackup(backup);
            // The phase covers the remaining wait for the background report and the manifest.
            using var reportWriteTiming = PhaseTiming.Measure("report.write");
            reportWrite.GetAwaiter().GetResult();
            File.WriteAllText(stagedManifest, JsonSerializer.Serialize(new OutputManifest
            {
                GeneratedAt = report.GeneratedAt, ProfileName = report.ProfileName,
                EnchantmentProviderPlugins = report.EnchantmentProviderPlugins.ToList(),
                Fingerprint = report.Fingerprint, Outputs = outputs.Select(x => Path.GetFileName(x)!).ToList(),
                CdfFiles = cdfOutput is null ? [] : [cdfOutput.RelativePath]
            }, JsonContext.Default.OutputManifest));

            reportWriteTiming?.Dispose();
            using var installTiming = PhaseTiming.Measure("write.install");
            OperationProgress.Stage("Installing validated output…", GenerationStage.Install);
            RemoveStaleOutputs(requestedOutput, outputs);
            foreach(var old in OwnedRuntimeFiles(requestedOutput).Where(File.Exists))File.Delete(old);
            foreach(var (staged,relative) in support)
            {
                var target=Path.Combine(directory,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Move(staged,target,true);
            }
            for (var index = 0; index < outputs.Count; index++)
                File.Move(stagedPlugins[index], outputs[index], true);
            File.Move(stagedReport, reportPath, true);
            if (File.Exists(LegacyReportPath(requestedOutput))) File.Delete(LegacyReportPath(requestedOutput));
            File.Move(stagedManifest, manifestPath, true);
        }
        finally
        {
            // A failure after the report writer started waits for it to release the staged file.
            if (reportWrite is { IsCompleted: false }) { try { reportWrite.GetAwaiter().GetResult(); } catch { /* the original failure is reported */ } }
            try { Directory.Delete(stage, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var warning = $"WARNING: Could not remove temporary staging folder '{stage}': {ex.Message}";
                // Cleanup and its diagnostics must not replace the generation result.
                try { SessionRuntime.Log(warning); } catch { }
                try { Console.Error.WriteLine(warning); } catch { }
            }
        }
    }

    // Every generated equipment record and leveled list must be reachable from an
    // overridden authored loot recipient/list or a runtime insertion quest.
    private static void ValidateReachability(IReadOnlyList<string> stagedPlugins)
        => ValidateReachabilityWithExceptions(stagedPlugins, new HashSet<FormKey>());

    private static void ValidateReachabilityWithExceptions(IReadOnlyList<string> stagedPlugins,
        IReadOnlySet<FormKey> permittedUnreachable)
        => ValidateReachabilityCore(stagedPlugins, permittedUnreachable, new HashSet<FormKey>());

    private static void ValidateReachabilityCore(IReadOnlyList<string> stagedPlugins,
        IReadOnlySet<FormKey> permittedUnreachable, IReadOnlySet<FormKey> externalRoots)
    {
        var checks = stagedPlugins.Select(path => SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE)).ToArray();
        try
        {
            var owned = checks.Select(x => x.ModKey).ToHashSet();
            var lists = new Dictionary<FormKey, ILeveledItemGetter>();
            foreach (var check in checks) foreach (var list in check.LeveledItems) lists[list.FormKey] = list;
            var generated = checks.SelectMany(x => x.Weapons.Select(r => r.FormKey)
                    .Concat(x.Armors.Select(r => r.FormKey)).Concat(x.LeveledItems.Select(r => r.FormKey)).Concat(x.Outfits.Select(r => r.FormKey)))
                .Where(k => owned.Contains(k.ModKey)).ToHashSet();
            var outfits = checks.SelectMany(c => c.Outfits).GroupBy(o => o.FormKey).ToDictionary(g => g.Key, g => g.Last());
            var queue = new Queue<FormKey>(externalRoots);
            foreach (var list in lists.Values.Where(l => !owned.Contains(l.FormKey.ModKey))) queue.Enqueue(list.FormKey);
            foreach (var check in checks) foreach (var quest in check.Quests) foreach (var link in quest.EnumerateFormLinks()) queue.Enqueue(link.FormKey);
            foreach (var check in checks) foreach (var container in check.Containers)
                foreach (var entry in container.Items ?? []) queue.Enqueue(entry.Item.Item.FormKey);
            foreach (var check in checks) foreach (var npc in check.Npcs)
            {
                if ((npc.Configuration.TemplateFlags & NpcConfiguration.TemplateFlag.Inventory) == 0)
                {
                    foreach (var entry in npc.Items ?? []) queue.Enqueue(entry.Item.Item.FormKey);
                    queue.Enqueue(npc.DefaultOutfit.FormKey); queue.Enqueue(npc.SleepingOutfit.FormKey);
                }
                queue.Enqueue(npc.DeathItem.FormKey);
            }
            var seen = new HashSet<FormKey>();
            while (queue.TryDequeue(out var key))
            {
                if (!seen.Add(key)) continue;
                if (outfits.TryGetValue(key, out var outfit)) foreach (var item in outfit.Items ?? []) queue.Enqueue(item.FormKey);
                if (lists.TryGetValue(key, out var list)) foreach (var entry in list.Entries ?? []) if (entry.Data is { } data) queue.Enqueue(data.Reference.FormKey);
            }
            var missingRetained = permittedUnreachable.Where(k => !generated.Contains(k)).OrderBy(k => k).ToArray();
            if (missingRetained.Length > 0)
                throw new InvalidDataException($"Output validation failed: {missingRetained.Length} retained undistributed records were not serialized (first: {string.Join(", ", missingRetained.Take(10))}).");
            var unreachable = generated.Where(k => !seen.Contains(k) && !permittedUnreachable.Contains(k)).OrderBy(k => k).ToArray();
            if (unreachable.Length > 0)
                throw new InvalidDataException($"Output validation failed: {unreachable.Length} generated records are not reachable from any authored loot recipient/list override or runtime quest (first: {string.Join(", ", unreachable.Take(10))}).");
        }
        finally { foreach (var check in checks) check.Dispose(); }
    }

    private static void ValidateAllReferences(IReadOnlyList<string> stagedPlugins, IReadOnlyList<ISkyrimModGetter> mods)
    {
        // COED's second word is a rank for faction owners, but a global FormID
        // for NPC owners. Standalone overlays cannot type an owner in a master:
        // they expose an UntypedOwner and mistake rank -1 for a FFFFFF link.
        // Resolve ownership against the complete input + output record set before
        // enumerating links. Do not ignore sentinel IDs or skip inventory data.
        var untyped = stagedPlugins.Select(path => SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE)).ToArray();
        try
        {
            using var links = mods.Concat(untyped).ToImmutableLinkCache();
            var checks = stagedPlugins.Select(path => SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE,
                new BinaryReadParameters { LinkCache = links })).ToArray();
            try { ValidateReferenceRecords(checks, mods); }
            finally { foreach (var check in checks) check.Dispose(); }
        }
        finally { foreach (var check in untyped) check.Dispose(); }
    }

    private static void ValidateReferenceRecords(IReadOnlyList<ISkyrimModGetter> checks, IReadOnlyList<ISkyrimModGetter> mods)
    {
        using var enumerationTiming = PhaseTiming.Measure("validation.reference-links");
        var references = new FormKey[checks.Count][];
            ParallelInOrder(checks.Count, i => references[i] = checks[i].EnumerateFormLinks().Select(l => l.FormKey).ToArray());
        enumerationTiming?.Dispose();
        // The engine creates the player reference whenever its master loads; no plugin
        // stores it, so it must not force a nested traversal of every source.
        var playerPresent = mods.Any(m => m.ModKey == PlayerReference.ModKey);
        var missing = ReferenceKeyIndex.FindMissing(references.SelectMany(r => r).Where(k => !playerPresent || k != PlayerReference), mods, checks);
        using var resolutionTiming = PhaseTiming.Measure("validation.reference-errors");
        // The engine creates the player reference whenever its master loads; no plugin stores it.
        if (mods.Any(m => m.ModKey == PlayerReference.ModKey)) missing.Remove(PlayerReference);
        for (var i = 0; i < checks.Count; i++)
        {
            var check = checks[i];
            var unresolved = references[i].Where(missing.Contains).Distinct().Take(10).ToArray();
            if (unresolved.Length > 0)
                throw new InvalidDataException($"Output validation failed for {check.ModKey}: unresolved references {string.Join(", ", unresolved)}.");
            var masters = check.ModHeader.MasterReferences.Select(x => x.Master).ToHashSet();
            var missingMasters = references[i].Select(x => x.ModKey)
                .Where(x => !x.IsNull && x != check.ModKey && !masters.Contains(x)).Distinct().ToArray();
            if (missingMasters.Length > 0)
                throw new InvalidDataException($"Output validation failed for {check.ModKey}: missing master declarations {string.Join(", ", missingMasters)}.");
        }
    }

    // Planning names the backup without touching disk; creation copies the owned files.
    private sealed record OutputBackup(string Folder, string Directory, string[] Files);
    private static OutputBackup? PlanOutputBackup(string requestedOutput)
    {
        var files = OwnedOutputFiles(requestedOutput).Where(File.Exists).ToArray();
        if (files.Length == 0) return null;
        var directory = Path.GetDirectoryName(requestedOutput)!;
        return new(Path.Combine(directory, "Backups", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")), directory, files);
    }

    private static string? CreateOutputBackup(OutputBackup? plan)
    {
        if (plan is null) return null;
        Directory.CreateDirectory(plan.Folder);
        foreach (var file in plan.Files)
        {
            var destination=Path.Combine(plan.Folder,Path.GetRelativePath(plan.Directory,file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination,true);
        }
        return plan.Folder;
    }

    private static IEnumerable<string> OwnedOutputFiles(string requestedOutput)
    {
        var directory = Path.GetDirectoryName(requestedOutput)!;
        var stem = Path.GetFileNameWithoutExtension(requestedOutput);
        foreach (var path in Directory.EnumerateFiles(directory, stem + "*.esp", SearchOption.TopDirectoryOnly))
        {
            var fileStem = Path.GetFileNameWithoutExtension(path);
            if (fileStem.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                fileStem.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) && int.TryParse(fileStem[(stem.Length + 1)..], out _))
                yield return path;
        }
        yield return ReportPath(requestedOutput);
        yield return LegacyReportPath(requestedOutput);
        yield return Path.ChangeExtension(requestedOutput, ".manifest.json");
        foreach(var support in OwnedRuntimeFiles(requestedOutput))yield return support;
    }

    internal static string ReportPath(string requestedOutput) => Path.ChangeExtension(requestedOutput, ".report.json.gz");
    // Uncompressed reports from earlier versions; installing a new report removes it.
    private static string LegacyReportPath(string requestedOutput) => Path.ChangeExtension(requestedOutput, ".report.json");

    private static IEnumerable<string> OwnedRuntimeFiles(string requestedOutput)
    {
        if (OwnedCdfFile(requestedOutput) is { } cdfFile) yield return cdfFile;
        var directory=Path.GetDirectoryName(requestedOutput)!;
        yield return Path.Combine(directory,"Scripts","XEP_QuestInsertion.pex");
        yield return Path.Combine(directory,"Source","Scripts","XEP_QuestInsertion.psc");
        // The XEP_RuntimeMirror* names are retired mode 7 scripts. They are kept here only so
        // regenerating over an earlier mode 7 install removes its leftover script files.
        foreach (var name in new[] { "XEP_RuntimeMirror", "XEP_RuntimeMirrorPlayerAlias", "XEP_RuntimeMirrorRow", "XEP_RuntimeMirrorWrapper", "XEP_RuntimeMirrorScheduler", "XEP_ScriptGate", "XEP_ScriptGatePlayerAlias" })
        {
            yield return Path.Combine(directory,"Scripts",name+".pex");
            yield return Path.Combine(directory,"Source","Scripts",name+".psc");
        }
        var seq=Path.Combine(directory,"SEQ");var stem=Path.GetFileNameWithoutExtension(requestedOutput);
        if(!Directory.Exists(seq))yield break;
        foreach(var path in Directory.EnumerateFiles(seq,stem+"*.seq"))
        {
            var name=Path.GetFileNameWithoutExtension(path);
            if(name.Equals(stem,StringComparison.OrdinalIgnoreCase)||name.StartsWith(stem+"_",StringComparison.OrdinalIgnoreCase)&&int.TryParse(name[(stem.Length+1)..],out _))yield return path;
        }
    }

    private static void RemoveStaleOutputs(string requestedOutput, IReadOnlyList<string> currentOutputs)
    {
        var directory = Path.GetDirectoryName(requestedOutput)!;
        var stem = Path.GetFileNameWithoutExtension(requestedOutput);
        var keep = currentOutputs.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, stem + "*.esp", SearchOption.TopDirectoryOnly))
        {
            var fileStem = Path.GetFileNameWithoutExtension(path);
            var isBase = fileStem.Equals(stem, StringComparison.OrdinalIgnoreCase);
            var isNumbered = fileStem.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(fileStem[(stem.Length + 1)..], out _);
            if ((isBase || isNumbered) && !keep.Contains(Path.GetFullPath(path))) File.Delete(path);
        }
    }

    private static bool TryBase(IMajorRecordGetter item, out FormKey key)
    {
        key = item switch
        {
            IWeaponGetter w => w.Template.FormKey,
            IArmorGetter a => a.TemplateArmor.FormKey,
            _ => FormKey.Null
        };
        return !key.IsNull;
    }

    private static bool TryEnchantment(IMajorRecordGetter item, out FormKey key)
    {
        key = item switch
        {
            IWeaponGetter w => w.ObjectEffect.FormKey,
            IArmorGetter a => a.ObjectEffect.FormKey,
            _ => FormKey.Null
        };
        return !key.IsNull;
    }

    private static IEnumerable<IFormLinkGetter<IKeywordGetter>> Keywords(IMajorRecordGetter item) => item switch
    {
        IWeaponGetter w => w.Keywords ?? [],
        IArmorGetter a => a.Keywords ?? [],
        _ => []
    };

    private static string DisplayName(IMajorRecordGetter item) => item switch
    {
        IWeaponGetter w => w.Name?.String ?? w.EditorID ?? "",
        IArmorGetter a => a.Name?.String ?? a.EditorID ?? "",
        _ => item.EditorID ?? ""
    };

    private static bool LooksLikeEquipment(IMajorRecordGetter item) => item is IWeaponGetter or IArmorGetter;
    private static string ClassKey(EquipmentClass cls) => $"{cls.Signature}|{cls.Material}|{cls.Type}|{cls.ArmorType}".ToLowerInvariant();
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex EditorIdInvalidCharacters();
}
