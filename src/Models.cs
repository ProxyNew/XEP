using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace XEnchantmentPatcher;

// Keep retired AllEligible (2) and CreateDistribution (4) values for old numeric
// settings/callers only. SettingsStore migrates them to ExistingDistribution.
public enum PatchPolicy { Review = 0, ExistingDistribution = 1, AllEligible = 2, Exclude = 3, CreateDistribution = 4 }
public sealed record PatchPolicyChoice(PatchPolicy Value, string Label)
{
    public string Explanation => Value switch
    {
        PatchPolicy.Review => "Leave this plugin undecided. Choose Existing Distribution or Exclude for every plugin before generating a patch.",
        PatchPolicy.ExistingDistribution => "Select eligible equipment with existing loot distribution by default and show this plugin in equipment review. Individual equipment overrides and specialized distribution choices still apply; eligibility and placement safety checks remain enforced.",
        PatchPolicy.Exclude => "Leave this plugin's equipment unselected by default and hide it from equipment review. Saved individual equipment inclusion overrides still apply.",
        _ => "This legacy rule is migrated to Existing Distribution when settings are loaded."
    };
}
// Numeric value 3 used to mean the simultaneous union of source and general
// routes. Reusing it for BestAvailable migrates existing numeric JSON to the
// safer fallback behavior instead of silently retaining the broad union.
// OtherMaterialLists (5) and RuntimeMirror (7) are retired choices kept only so old
// numeric settings still decode; SettingsStore migrates them when settings are normalized.
public enum SpecializedLootMode { Disabled = 0, SpecializedLists = 1, GeneralLists = 2, BestAvailable = 3, SpecializedEnchantedLists = 4, OtherMaterialLists = 5, MerchantFallback = 6, RuntimeMirror = 7, ScriptDistribution = 8 }
public sealed record SpecializedLootChoice(SpecializedLootMode Value, string Label)
{
    public string Explanation => CandidateItem.ExplainSpecializedChoice(Value);
}
public sealed record MaterialFamilyChoice(string Value, string Label)
{
    public string Explanation => $"Treat this item as {Value} when matching enchanted equipment templates and loot destinations. Other eligibility and distribution checks still apply.";
}
public static class SpecializedLootChoices
{
    public static IReadOnlyList<SpecializedLootChoice> All { get; } = [
        new(SpecializedLootMode.Disabled, "Off"),
        new(SpecializedLootMode.SpecializedEnchantedLists, "Specialized enchanted lists only"),
        new(SpecializedLootMode.ScriptDistribution, "Follow script distribution (enchanted counterparts)"),
        new(SpecializedLootMode.SpecializedLists, "Specialized source lists (allow mixing)"),
        new(SpecializedLootMode.BestAvailable, "Best available (enchanted → source → general)"),
        new(SpecializedLootMode.MerchantFallback, "Merchant fallback (otherwise undistributed)"),
        new(SpecializedLootMode.GeneralLists, "General enchanted loot (expert: ignores base-item locations)")];
    public static IReadOnlyList<SpecializedLootChoice> Batch { get; } = All
        .Where(x => x.Value != SpecializedLootMode.GeneralLists).ToArray();
}
// User-facing intents are separate from persisted route modes: old profiles and
// mod-author defaults retain their exact routing and stable numeric values.
public enum SpecializedDistributionOption { Off, OriginalLocations, GeneralLoot }
public sealed record SpecializedDistributionChoice(SpecializedDistributionOption Value, string Label, string Explanation);
public static class SpecializedDistributionChoices
{
    public static IReadOnlyList<SpecializedDistributionChoice> All { get; } = [
        new(SpecializedDistributionOption.Off, "Off", "Adds no new loot distribution for this item. Its Include checkbox stays unchanged."),
        new(SpecializedDistributionOption.OriginalLocations, "Original locations", "Uses supported enchanted lists or verified source insertions in the source mod's existing locations, under its conditions. New-material expansion requires the shared material option."),
        new(SpecializedDistributionOption.GeneralLoot, "General loot", "Allows compatible general enchanted loot, including merchant loot lists, even where the ordinary item is not found. Requires an individual choice or a mod-author rule.")];
    // Broadening general loot still requires an explicit individual choice.
    public static IReadOnlyList<SpecializedDistributionChoice> Batch { get; } = All.Where(x => x.Value != SpecializedDistributionOption.GeneralLoot).ToArray();
    public static SpecializedDistributionOption? ForMode(SpecializedLootMode mode) => mode switch
    {
        SpecializedLootMode.Disabled => SpecializedDistributionOption.Off,
        SpecializedLootMode.SpecializedEnchantedLists or SpecializedLootMode.ScriptDistribution => SpecializedDistributionOption.OriginalLocations,
        SpecializedLootMode.GeneralLists => SpecializedDistributionOption.GeneralLoot,
        _ => null
    };
}
public enum DistributionTarget { MatchingProviderLists, SourceItemLists }

public sealed class PluginDecision : INotifyPropertyChanged
{
    private PatchPolicy _policy;
    public required string Plugin { get; init; }
    public int EligibleItems { get; init; }
    public int DistributedItems { get; init; }
    public int ScriptCandidateItems { get; init; }
    public bool HasDistributionCandidates => DistributedItems > 0 || ScriptCandidateItems > 0;
    public int ExcludedItems { get; init; }
    public string Examples { get; init; } = "";
    public PatchPolicy Policy { get => _policy; set { if (_policy == value) return; _policy = value; OnPropertyChanged(); } }
    // The rules file's plugin policy when it fills this row; choosing it saves nothing.
    public PatchPolicy? AuthorPolicy { get; init; }
    public string AuthorNote { get; init; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class CandidateItem : INotifyPropertyChanged
{
    internal CandidateItem CopyForPass()
    {
        var copy = (CandidateItem)MemberwiseClone();
        copy.PropertyChanged = null;
        copy._originalLocationsHadNoPlacement = false;
        copy.materialFamilyChoices = MaterialFamilyChoices.ToList();
        return copy;
    }

    private bool _selected;
    private SpecializedLootMode _specializedMode;
    private SpecializedLootMode _recommendedSpecializedMode;
    private string _materialFamily = "";
    private bool _hasMaterialFamilyOverride;
    public bool RequiresLootReview { get; set; }
    public SpecializedLootMode RecommendedSpecializedMode { get => _recommendedSpecializedMode; set { if (_recommendedSpecializedMode == value) return; _recommendedSpecializedMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpecializedChoices)); OnPropertyChanged(nameof(SpecializedDecisionSummary)); } }
    private SpecializedLootMode? _originalLocationsMode;
    private bool _originalLocationsHadNoPlacement;
    // Populated by the existing review assessments; a recommendation/override is
    // not proof that a productive original route exists.
    public SpecializedLootMode? OriginalLocationsMode { get => _originalLocationsMode; set { if (_originalLocationsMode == value) return; _originalLocationsMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanUseOriginalLocations)); OnPropertyChanged(nameof(SpecializedChoices)); OnPropertyChanged(nameof(SpecializedSelection)); OnPropertyChanged(nameof(SpecializedSavedRuleLabel)); OnPropertyChanged(nameof(SpecializedDecisionSummary)); OnPropertyChanged(nameof(SpecializedChoiceImpact)); } }
    public bool CanUseOriginalLocations => Eligible && AvailableVariants > 0 && OriginalLocationsMode is not null && !_originalLocationsHadNoPlacement;
    public IReadOnlyList<SpecializedDistributionChoice> SpecializedChoices => SpecializedDistributionChoices.All
        .Where(x => x.Value != SpecializedDistributionOption.OriginalLocations || CanUseOriginalLocations)
        .Select(x => x.Value == SpecializedDistributionChoices.ForMode(RecommendedSpecializedMode) ? x with { Label = x.Label + (AuthorMode ? " (Author default)" : " (Recommended)") } : x).ToArray();
    public SpecializedDistributionOption? SpecializedSelection
    {
        get
        {
            var choice = SpecializedDistributionChoices.ForMode(SpecializedMode);
            return choice == SpecializedDistributionOption.OriginalLocations && !CanUseOriginalLocations ? null : choice;
        }
        set
        {
            // WPF clears selection when the offered choices change. That must
            // never change an item's saved distribution rule.
            if (value is null) return;
            if (value == SpecializedDistributionOption.OriginalLocations)
            {
                if (CanUseOriginalLocations) SpecializedMode = OriginalLocationsMode!.Value;
            }
            else SpecializedMode = value == SpecializedDistributionOption.Off ? SpecializedLootMode.Disabled : SpecializedLootMode.GeneralLists;
        }
    }
    public string SpecializedSavedRuleLabel => SpecializedDistributionChoices.ForMode(SpecializedMode) == SpecializedDistributionOption.OriginalLocations
        ? "Original locations (unavailable)" : AuthorMode && !HasSpecializedOverride ? "Mod-author distribution rule" : "Saved distribution rule";
    // Mod-author rules-file values applied to this row (their defaults replace the
    // engine recommendation, so restoring recommendations restores them).
    public bool? AuthorInclude { get; set; }
    public bool AuthorMaterial { get; set; }
    public bool AuthorMode { get; set; }
    public string AuthorNote { get; set; } = "";
    // The rules file restricts this item's enchantments; the user may ignore the list.
    public bool HasAuthorEnchantmentList { get; set; }
    private bool _ignoreAuthorEnchantmentList;
    public bool IgnoreAuthorEnchantmentList { get => _ignoreAuthorEnchantmentList; set { if (_ignoreAuthorEnchantmentList == value) return; _ignoreAuthorEnchantmentList = value; OnPropertyChanged(); } }
    // The rules file restricts where this item's variants may be placed.
    public bool HasAuthorDestinationList { get; set; }
    private bool _ignoreAuthorDestinationList;
    public bool IgnoreAuthorDestinationList { get => _ignoreAuthorDestinationList; set { if (_ignoreAuthorDestinationList == value) return; _ignoreAuthorDestinationList = value; OnPropertyChanged(); } }
    public string SpecializedRecommendation { get; set; } = "Off";
    public string SpecializedRecommendationReason { get; set; } = "";
    private bool _hasSpecializedOverride;
    public bool HasSpecializedOverride { get => _hasSpecializedOverride; set { if (_hasSpecializedOverride == value) return; _hasSpecializedOverride = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpecializedDecisionSummary)); OnPropertyChanged(nameof(SpecializedSavedRuleLabel)); } }
    public string LootReviewReason { get; set; } = "";
    public string GeneralizedLootEvidence { get; set; } = "";
    public string LootReviewDetails { get; set; } = "";
    public string SourceLootLists { get; set; } = "";
    // A parsed script payload can hand out this item. This is a conservative
    // discovery hint, not a gate/placement proof, and remains true when Off.
    public bool HasScriptDistributionSource { get; set; }
    public bool HasSpecializedEquipmentKind { get; set; }
    public string SpecializedDecisionReason { get; set; } = "";
    public string SourceDistributionSummary { get; set; } = "";
    public string SpecializedOptionSummary { get; set; } = "";
    public string AvailabilitySummary { get; set; } = "No availability controls found in the source paths.";
    private string _lastPreviewSummary = "No exact preview has been calculated for this choice.";
    public string LastPreviewSummary { get => _lastPreviewSummary; set { if (_lastPreviewSummary == value) return; _lastPreviewSummary = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpecializedDecisionSummary)); OnPropertyChanged(nameof(SelectionEvidence)); } }
    internal static string ExplainSpecializedChoice(SpecializedLootMode mode) => mode switch
    {
        SpecializedLootMode.Disabled => "Adds no new loot distribution for this item. Its Include checkbox stays unchanged.",
        SpecializedLootMode.ScriptDistribution => "Places enchanted counterparts through the specialized and material planners wherever the source mod's script inserts the item, under the same consumers, gates and level windows. One owned switch per block of script insertions opens only while that block's last insertion is present in its list, so counterparts follow the script's current result.",
        SpecializedLootMode.SpecializedEnchantedLists => "Uses only verified enchanted lists reached by the item's specialized consumer context. Material expansion is used only when its shared option is enabled.",
        SpecializedLootMode.SpecializedLists => "Adds an enchanted pool beside the ordinary item in supported source lists. This intentionally mixes ordinary and enchanted selection and changes that list's share.",
        SpecializedLootMode.BestAvailable => "Tries verified specialized enchanted lists, then supported source lists, then general enchanted loot, stopping after the first productive route family.",
        SpecializedLootMode.MerchantFallback => "Creates occasional enchanted stock at merchants whose record-defined buy/sell filters accept the item. Proven globals and chance controls are retained; quest unlocks and ambiguous source paths stay blocked.",
        SpecializedLootMode.GeneralLists => "Allows compatible general enchanted loot, including merchant loot lists, even where the ordinary item is not found. Requires an individual choice or a mod-author rule.",
        SpecializedLootMode.OtherMaterialLists => "Legacy material-expansion choice; saved profiles migrate it to Specialized enchanted lists only plus the shared material-expansion setting.",
        _ => "No explanation is available."
    };
    private string OriginalLocationsUnavailableReason => "Original locations unavailable: " + (!Eligible
        ? $"this item is ineligible for variant generation. {Reason}"
        : AvailableVariants <= 0 ? "no compatible enabled enchantments can produce variants."
        : _originalLocationsHadNoPlacement ? "the last preview produced no loot placement. See the preview warnings and per-item evidence."
        : string.IsNullOrWhiteSpace(SpecializedDecisionReason) ? "no supported enchanted destination or verified source insertion was found."
        : SpecializedDecisionReason);
    public string SpecializedChoiceImpact => ExplainSpecializedChoice(SpecializedMode)
        + (CanUseOriginalLocations ? "" : "\n" + OriginalLocationsUnavailableReason);
    public string SpecializedDecisionSummary
    {
        get
        {
            string Label(SpecializedLootMode mode) => SpecializedDistributionChoices.All.FirstOrDefault(x => x.Value == SpecializedDistributionChoices.ForMode(mode))?.Label
                ?? SpecializedLootChoices.All.FirstOrDefault(x => x.Value == mode)?.Label ?? mode.ToString();
            var selected = Label(SpecializedMode);
            var recommended = Label(RecommendedSpecializedMode);
            var same = SpecializedMode == RecommendedSpecializedMode && !HasSpecializedOverride;
            var recommendedUnavailable = SpecializedDistributionChoices.ForMode(RecommendedSpecializedMode) == SpecializedDistributionOption.OriginalLocations && !CanUseOriginalLocations;
            var selectedUnavailable = SpecializedDistributionChoices.ForMode(SpecializedMode) == SpecializedDistributionOption.OriginalLocations && !CanUseOriginalLocations;
            var defaultSource = AuthorMode ? "mod-author default" : recommendedUnavailable ? "default rule" : "recommended";
            var lines = new List<string> { $"{Name} · {Plugin} · {Category}" };
            lines.Add($"Choice: {selected} ({(same ? defaultSource : "your override")}{(selectedUnavailable ? "; unavailable" : "")}) → {ExplainSpecializedChoice(SpecializedMode)}");
            if (!same)
                lines.Add($"{(AuthorMode ? "Mod-author default" : recommendedUnavailable ? "Default rule" : "Recommended")}: {recommended}{(recommendedUnavailable ? " (unavailable)" : "")} → {ExplainSpecializedChoice(RecommendedSpecializedMode)}");
            lines.Add("");
            lines.Add($"Why {recommended}");
            if (AuthorMode && !string.IsNullOrWhiteSpace(AuthorNote)) lines.Add(AuthorNote);
            if (!CanUseOriginalLocations) lines.Add(OriginalLocationsUnavailableReason);
            else lines.Add(string.IsNullOrWhiteSpace(SpecializedDecisionReason) ? SpecializedRecommendationReason : SpecializedDecisionReason);
            lines.Add("");
            lines.Add("Where the base item is found");
            lines.Add(string.IsNullOrWhiteSpace(SourceDistributionSummary) ? "No direct leveled-list source was found." : SourceDistributionSummary);
            lines.Add("");
            lines.Add("What the options would do");
            lines.Add(string.IsNullOrWhiteSpace(SpecializedOptionSummary) ? "Run a rescan to calculate the available distribution routes." : SpecializedOptionSummary);
            lines.Add("");
            lines.Add("Availability: " + AvailabilitySummary);
            lines.Add("Last preview: " + LastPreviewSummary);
            return string.Join("\n", lines);
        }
    }
    internal void ApplyPlacementPreview(ItemPlacement? placement)
    {
        LastPreviewSummary = placement is not null
            ? $"{placement.Variants} variant(s) in {placement.Destinations.Count} destination list(s)."
            : Selected ? "No placement was produced; see the preview warnings." : "Item was not selected for this preview.";
        var original = Selected && SpecializedDistributionChoices.ForMode(SpecializedMode) == SpecializedDistributionOption.OriginalLocations;
        var productive = placement is { Variants: > 0 } && placement.Destinations.Count > 0;
        // A saved script rule may have been scanned while unchecked, before its
        // source scripts were analysed. A productive exact plan supplies that proof.
        if (original && productive) OriginalLocationsMode = SpecializedMode;
        // The exact plan can rule out a route that looked possible during scan,
        // for example because equivalent loot is already reachable or lists are full.
        SetOriginalLocationsNoPlacement(original && !productive);
    }
    internal void ClearPlacementPreview() => SetOriginalLocationsNoPlacement(false);
    private void SetOriginalLocationsNoPlacement(bool value)
    {
        if (_originalLocationsHadNoPlacement == value) return;
        _originalLocationsHadNoPlacement = value;
        OnPropertyChanged(nameof(CanUseOriginalLocations));
        OnPropertyChanged(nameof(SpecializedChoices));
        OnPropertyChanged(nameof(SpecializedSelection));
        OnPropertyChanged(nameof(SpecializedDecisionSummary));
        OnPropertyChanged(nameof(SpecializedChoiceImpact));
    }
    public SpecializedLootMode SpecializedMode { get => _specializedMode; set { if (_specializedMode == value) return; _specializedMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpecializedChoiceImpact)); OnPropertyChanged(nameof(SpecializedDecisionSummary)); OnPropertyChanged(nameof(SpecializedSelection)); OnPropertyChanged(nameof(SpecializedSavedRuleLabel)); } }
    public required string Plugin { get; init; }
    public required string FormKey { get; init; }
    public required string Signature { get; init; }
    public required string EditorId { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public bool IsPlayable { get; init; } = true;
    public bool Eligible { get; init; }
    public bool AlreadyDistributed { get; init; }
    public bool AlreadyEnchanted { get; init; }
    public required string Reason { get; set; }
    public int AvailableVariants { get; init; }
    public string MaterialRule { get; init; } = "";
    public string MaterialEvidence { get; init; } = "";
    private List<string> materialFamilyChoices = [];
    public List<string> MaterialFamilyChoices { get => materialFamilyChoices; init => materialFamilyChoices = value; }
    [JsonIgnore]
    public IReadOnlyList<MaterialFamilyChoice> MaterialFamilyOptions => MaterialFamilyChoices
        .Select(family => new MaterialFamilyChoice(family, family.Equals(RecommendedMaterialFamily, StringComparison.OrdinalIgnoreCase)
            ? family + (AuthorMaterial ? " (Author default)" : " (Recommended)") : family)).ToArray();
    public bool CanChooseMaterialFamily => MaterialFamilyChoices.Count > 1;
    public string RecommendedMaterialFamily { get; set; } = "";
    public string MaterialFamily { get => _materialFamily; set { if (_materialFamily == value) return; _materialFamily = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectionEvidence)); } }
    public bool HasMaterialFamilyOverride { get => _hasMaterialFamilyOverride; set { if (_hasMaterialFamilyOverride == value) return; _hasMaterialFamilyOverride = value; OnPropertyChanged(); OnPropertyChanged(nameof(MaterialFamilyDecisionSource)); } }
    public string MaterialFamilyDecisionSource => HasMaterialFamilyOverride ? "User override" : AuthorMaterial ? "Author default" : "Recommendation";
    public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectionEvidence)); } }
    [JsonIgnore]
    public string SelectionEvidence
    {
        get
        {
            var lines = new List<string> { $"{Name} — {Plugin}",
                $"Patch: {(Eligible ? Selected ? "Included" : "Excluded" : "Unavailable")}. Category: {Category}.",
                $"Eligibility: {(string.IsNullOrWhiteSpace(Reason) ? Eligible ? "Eligible equipment." : "Not eligible for variant generation." : Reason)}" };
            if (!string.IsNullOrWhiteSpace(AuthorNote)) lines.Add(AuthorNote);
            if (!string.IsNullOrWhiteSpace(MaterialFamily)) lines.Add($"Material family: {MaterialFamily}. {(AuthorMaterial ? "Author default" : "Recommended")}: {RecommendedMaterialFamily}.");
            if (!string.IsNullOrWhiteSpace(MaterialRule)) lines.Add("Material rule: " + MaterialRule);
            if (!string.IsNullOrWhiteSpace(MaterialEvidence)) lines.Add("Material evidence: " + MaterialEvidence);
            lines.Add("Base distribution: " + (!string.IsNullOrWhiteSpace(SourceDistributionSummary) ? SourceDistributionSummary
                : !string.IsNullOrWhiteSpace(SourceLootLists) ? SourceLootLists
                : AlreadyDistributed ? "Found in base-item loot lists." : "No base-item loot route found; enchanted variants require an existing source route."));
            if (RequiresLootReview) lines.Add("Distribution choices are in Specialized loot: " + (string.IsNullOrWhiteSpace(SpecializedDecisionReason) ? LootReviewReason : SpecializedDecisionReason));
            lines.Add("Availability: " + AvailabilitySummary);
            lines.Add($"Potential variants: {AvailableVariants:N0}; exact placement depends on the output preview.");
            lines.Add("Last preview: " + LastPreviewSummary);
            lines.Add($"Record: {EditorId} [{FormKey}]");
            return string.Join("\n", lines);
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class ScanResult
{
    public SkyPatcherInputReport? SkyPatcher { get; init; }
    public CdfInputReport? Cdf { get; init; }
    public LlosInputReport? Llos { get; init; }
    public CidInputReport? Cid { get; init; }
    public SpidInputReport? Spid { get; init; }
    public required List<PluginDecision> Plugins { get; init; }
    public required List<CandidateItem> Items { get; init; }
    public List<EnchantmentSelectionOption> Enchantments { get; init; } = [];
    public required int ActivePluginCount { get; init; }
    public required int TemplateCount { get; init; }
    public List<string> TemplateCategories { get; init; } = [];
    // Patcher analysis setup, separate from load-order conflicts; also retained in output reports.
    public string? ScriptAnalysisWarning { get; init; }
    // Load-time warnings about the load order itself (script conflicts between mods).
    public List<string> Warnings { get; init; } = [];
    public required DateTime ScannedAt { get; init; }
}

public sealed class ProfileSettings
{
    public Dictionary<string, PatchPolicy> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowOtherMaterialRoutes { get; set; } = true;
    public Dictionary<string, SpecializedLootMode> SpecializedLootOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> ItemOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> EnchantmentOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MaterialFamilyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Answers to mod-author rules-file conflicts, keyed by AuthorConflict.AnswerKey.
    public Dictionary<string, string> AuthorConflictAnswers { get; set; } = new(StringComparer.Ordinal);
    // Run-only: enchantment overrides filled from the mod-author rules file. Overlap
    // cancellations outrank these (but not the user's own choices). Never serialized.
    internal HashSet<string> AuthorEnchantments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Items whose mod-author enchantment list the user chose to ignore.
    public HashSet<string> IgnoredAuthorEnchantmentLists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Run-only: per-item enchantment allow-lists from the rules file (item key -> enchantment keys).
    internal Dictionary<string, string[]> AuthorEnchantmentLists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Items whose mod-author destination list the user chose to ignore.
    public HashSet<string> IgnoredAuthorDestinationLists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Run-only: per-item allowed destinations (original leveled lists or containers).
    internal Dictionary<string, string[]> AuthorDestinationLists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    private List<string> providerPlugins = [];
    public List<string> EnchantmentProviderPlugins
    {
        get => providerPlugins;
        set { providerPlugins = value; UseLoadOrderProviderDefaults ??= false; }
    }
    // Null distinguishes untouched/legacy profiles until normalization; an explicit
    // list, including an empty list, is a saved choice unless automatic mode is stored.
    public bool? UseLoadOrderProviderDefaults { get; set; }
    public List<string> EnchantmentProviderOrder { get; set; } = [];
    [JsonPropertyName("DistributionProviderPlugins")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyDistributionProviderPlugins { get; set; }
    public bool ProviderOrderMigrated { get; set; }
    [JsonPropertyName("EnchantmentProviderPlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyEnchantmentProviderPlugin { get; set; }
    public DistributionTarget DistributionTarget { get; set; } = DistributionTarget.MatchingProviderLists;
    // Retained in serialized profiles for backward compatibility. Normalization
    // forces this on; subtype recognition is now an invariant rather than a setting.
    public bool StrictSubtypeMatching { get; set; } = true;
    public bool BroadUnmatchedDistribution { get; set; }
    public bool ResolveLeveledListConflicts { get; set; }
    public bool KeepUndistributedVariants { get; set; }
    // Off keeps the light (ESPFE) split output; on writes one regular plugin with IDs from 0x800.
    // Chosen in the generation confirmation; remembered as the next default.
    public bool FullOutputPlugin { get; set; }
    public int WarningListEntries { get; set; } = 240;
    public string? LastGenerationFingerprint { get; set; }
}

public sealed class AppSettings
{
    public bool DarkTheme { get; set; }
    public string? DataFolder { get; set; }
    public string? OutputFolder { get; set; }
    [JsonPropertyName("OutputPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyOutputPath { get; set; }
    [JsonPropertyName("ThaumaturgyPlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyProviderPlugin { get; set; }
    public string ProfileName { get; set; } = "Default";
    public Dictionary<string, ProfileSettings> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PatchPolicy> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ItemExclusions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public ProfileSettings CurrentProfile => Profiles[ProfileName];
    // Session-only opt-in: Scan reads the mod-author rules file. The UI sets it;
    // CLI runs and fixtures leave it off and ignore the file.
    [JsonIgnore] public bool ApplyAuthorRules { get; set; }
}

public sealed record ListImpact(string FormKey, string EditorId, int Before, int Added, int After, bool OverWarningLimit,
    int GeneratedSublists = 0, int? EntriesAfterSplitting = null);
public sealed class EnchantmentProviderOption : INotifyPropertyChanged
{
    private bool _isSelected;
    public EnchantmentProviderOption(string plugin, int enchantmentCount, int templateCount)
    {
        Plugin = plugin;
        EnchantmentCount = enchantmentCount;
        TemplateCount = templateCount;
    }
    public string Plugin { get; }
    public int EnchantmentCount { get; }
    public int TemplateCount { get; }
    public string Display => $"{Plugin} — {EnchantmentCount:N0} enchantments · {TemplateCount:N0} usable templates";
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

// AuthorDefault: DefaultSelected comes from the mod-author rules file.
public sealed record EnchantmentTierOption(string FormKey, string EditorId, bool DefaultSelected, bool Available, bool AuthorDefault = false);

public sealed class EnchantmentSelectionOption : INotifyPropertyChanged
{
    private bool _isSelected;
    public required string FormKey { get; init; }
    public required string Provider { get; init; }
    public required string Name { get; init; }
    public required string EditorId { get; init; }
    public required string Effects { get; init; }
    public required string Reason { get; init; }
    public bool DefaultSelected { get; init; }
    public bool Available { get; init; }
    public int TemplateCount { get; init; }
    public int LeveledTemplateCount { get; init; }
    // One row represents an enchantment family: every tier whose Base Enchantment
    // is this record (or the record itself). Choices are still stored per tier.
    public List<EnchantmentTierOption> Tiers { get; init; } = [];
    private bool _hasManualOverride;
    public bool HasManualOverride { get => _hasManualOverride; set { if (_hasManualOverride == value) return; _hasManualOverride = value; PropertyChanged?.Invoke(this, new(nameof(DecisionSource))); PropertyChanged?.Invoke(this, new(nameof(SelectionEvidence))); } }
    public string DecisionSource => HasManualOverride ? "Manual override" : Tiers.Any(t => t.AuthorDefault) ? "Author default" : "Recommendation";
    public string AuthorNote { get; init; } = "";
    public int AvailableTierCount => Tiers.Count(x => x.Available);
    private int _selectedTierCount;
    public int SelectedTierCount { get => _selectedTierCount; set { if (_selectedTierCount == value) return; _selectedTierCount = value; PropertyChanged?.Invoke(this, new(nameof(SelectedTierCount))); PropertyChanged?.Invoke(this, new(nameof(TierSummary))); PropertyChanged?.Invoke(this, new(nameof(SelectionEvidence))); } }
    public string TierSummary => $"{SelectedTierCount}/{AvailableTierCount}";
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); PropertyChanged?.Invoke(this, new(nameof(SelectionEvidence))); } }
    [JsonIgnore]
    public string SelectionEvidence => string.Join("\n", new[] {
        $"{Name} — {Provider}",
        "Effects: " + Effects,
        "Decision source: " + DecisionSource,
        AuthorNote,
        $"Enabled tiers: {SelectedTierCount}/{AvailableTierCount}. Recommended tiers: {Tiers.Count(t => t.Available && t.DefaultSelected)}/{AvailableTierCount}.",
        $"Eligible equipment templates: {TemplateCount:N0}; already in loot lists: {LeveledTemplateCount:N0}.",
        Reason,
        "Checking the row enables all usable tiers; recommendations restore each tier independently. Enabling a family does not bypass item or distribution safety checks.",
        $"Record: {EditorId} [{FormKey}]"
    }.Where(line => !string.IsNullOrWhiteSpace(line)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class GenerationPreview
{
    public List<string> Information { get; init; } = [];
    public List<string> CdfFiles { get; init; } = [];
    public SkyPatcherInputReport? SkyPatcher { get; init; }
    public CdfInputReport? Cdf { get; init; }
    public LlosInputReport? Llos { get; init; }
    public CidInputReport? Cid { get; init; }
    public SpidInputReport? Spid { get; init; }
    public int EnabledEnchantments { get; init; }
    public int ReusedVariants { get; init; }
    public int UndistributedVariants { get; init; }
    public int FallbackSublists { get; init; }
    public List<string> FallbackConnections { get; init; } = [];
    public bool StrictSubtypeMatching { get; init; }
    public bool BroadUnmatchedDistribution { get; init; }
    public bool ResolveLeveledListConflicts { get; init; }
    public bool KeepUndistributedVariants { get; init; }
    public bool FullOutputPlugin { get; init; }
    // All new records the output needs, whichever layout is chosen at generation.
    public int NewRecords { get; init; }
    public int ConflictResolvedLists { get; init; }
    public int ConflictRestoredEntries { get; init; }
    public List<string> EnchantmentProviderPlugins { get; init; } = [];
    public List<ItemPlacement> Placements { get; init; } = [];
    public int SelectedSourceItems { get; init; }
    public int Variants { get; init; }
    public int OutputFiles { get; init; }
    public int GeneratedSublists { get; init; }
    public int TargetLists { get; init; }
    public int SkippedSelectedItems { get; init; }
    public string Fingerprint { get; init; } = "";
    public bool IdentityChanged { get; init; }
    // Identity comparison for the other layout offered by generation confirmation.
    public bool AlternateLayoutIdentityChanged { get; init; }
    public List<string> OutputNames { get; init; } = [];
    public List<ListImpact> Lists { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record ItemPlacement(string Source, string Name, string Family, string Archetype, int Variants, List<string> Destinations);

public sealed record PatchChange(string Source, string SourceName, string Generated, string GeneratedName,
    string Enchantment, string TargetList);
public sealed record VariantDecision(string Source, string SourceName, string Item, string Enchantment,
    string Status, string Reason)
{
    public IReadOnlyList<int> DiagnosticIds { get; init; } = [];
}

public sealed record RouteRejection(int Id, string Stage, string Code, string List, string EditorId, string Detail);

public sealed record FallbackPlacement(string Parent, string ParentEditorId, string Child, string ChildEditorId,
    short Level, short Count);
public sealed record LeveledListConflictChange(string FormKey, string EditorId, string WinningPlugin,
    int RestoredEntries, Dictionary<string, int> RestoredByPlugin);

// One leveled-list call found in a quest or alias script, and what the availability analysis concluded.
public sealed record ScriptCallDiagnostic(string Quest, string Script, string Function, string Line, string Method, string Target, string Payload, string Trigger, string Verdict, string Detail);

// Raw selection facts read from written LVLIs. A Global reference takes precedence
// over fixed Chance None; its live value is not evaluated by the offline report.
public sealed record ListSelection(string FormKey, int Flags, string Global, double ChanceNonePercent,
    List<ListSelectionEntry> Entries);
public sealed record ListSelectionEntry(string Reference, int Level, int Count);

public sealed class PatchReport
{
    public List<string> CdfFiles { get; init; } = [];
    public SkyPatcherInputReport? SkyPatcher { get; init; }
    public CdfInputReport? Cdf { get; init; }
    public LlosInputReport? Llos { get; init; }
    public CidInputReport? Cid { get; init; }
    public SpidInputReport? Spid { get; init; }
    public List<RouteRejection> RouteDiagnostics { get; init; } = [];
    public int RuntimeQuests { get; init; }
    public List<string> RuntimeInsertions { get; init; } = [];
    public List<string> ScriptGates { get; init; } = [];
    public List<string> ScriptGateRefusals { get; init; } = [];
    public List<ScriptCallDiagnostic> ScriptCalls { get; init; } = [];
    public List<string> ScriptConflicts { get; init; } = [];
    public List<string> GeneralLootLists { get; init; } = [];
    public List<string> EnabledEnchantments { get; init; } = [];
    public int ReusedVariants { get; init; }
    public int UndistributedVariants { get; init; }
    public List<VariantDecision> VariantDecisions { get; init; } = [];
    public List<string> DistributionNotes { get; init; } = [];
    public List<string> RebuiltFallbackLists { get; init; } = [];
    public int FallbackSublists { get; init; }
    // Written in the report file's compact sections (see ReportWriting.cs), not here.
    [JsonIgnore] public List<FallbackPlacement> FallbackPlacements { get; init; } = [];
    public bool StrictSubtypeMatching { get; init; }
    public bool BroadUnmatchedDistribution { get; init; }
    public bool ResolveLeveledListConflicts { get; init; }
    public bool KeepUndistributedVariants { get; init; }
    public bool FullOutputPlugin { get; init; }
    public int ConflictResolvedLists { get; init; }
    public int ConflictRestoredEntries { get; init; }
    public List<string> ConflictResolutionDetails { get; init; } = [];
    public List<LeveledListConflictChange> ConflictResolutionChanges { get; init; } = [];
    public string Output { get; init; } = "";
    public List<string> Outputs { get; init; } = [];
    public DateTime GeneratedAt { get; init; }
    public string ProfileName { get; init; } = "";
    public List<string> EnchantmentProviderPlugins { get; init; } = [];
    public string Fingerprint { get; init; } = "";
    public bool IdentityChanged { get; init; }
    public string? BackupFolder { get; set; }
    public int ActivePlugins { get; init; }
    public int SelectedPlugins { get; init; }
    public int SourceItems { get; init; }
    public int Variants { get; init; }
    public int LeveledListsOverridden { get; init; }
    public int GeneratedSublists { get; init; }
    public List<ListImpact> ListImpacts { get; init; } = [];
    [JsonIgnore] public List<ListSelection> ListSelections { get; init; } = [];
    [JsonIgnore] public List<PatchChange> Changes { get; init; } = [];
    public List<CandidateItem> Skipped { get; init; } = [];
    public ReportSetup? Setup { get; init; }
}

// What a bug report needs to describe the user's setup: the generating profile, the effective
// plugin policies and the active load order. Machine paths are deliberately left out.
public sealed record ReportSetup(string ProfileName, ProfileSettings Profile, Dictionary<string, PatchPolicy> Policies,
    List<string> ItemExclusions, List<string> LoadOrder);

public sealed class OutputManifest
{
    public DateTime GeneratedAt { get; init; }
    public string ProfileName { get; init; } = "";
    public List<string> EnchantmentProviderPlugins { get; init; } = [];
    public string Fingerprint { get; init; } = "";
    public List<string> Outputs { get; init; } = [];
    public List<string> CdfFiles { get; init; } = [];
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(PatchReport))]
[JsonSerializable(typeof(ScanResult))]
[JsonSerializable(typeof(GenerationPreview))]
[JsonSerializable(typeof(OutputManifest))]
internal partial class JsonContext : JsonSerializerContext { }
