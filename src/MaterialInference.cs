using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    private sealed record MaterialInference(string Material, string Evidence);
    private sealed record MaterialResolution(string Material, string Rule, string Evidence, bool Inferred = false,
        string[]? Choices = null, string? Recommended = null)
    {
        public string[] AvailableChoices => Choices ?? [];
        public string RecommendedMaterial => Recommended ?? Material;
    }

    // Keyword EditorIDs, plus materials inferred from crafting evidence for equipment that has
    // no material keyword. Classification order: vanilla-form material keyword, then a
    // <prefix>Material<Name> keyword whose Name is already a known material, then the inference.
    private sealed class KeywordIndex : Dictionary<FormKey, string>
    {
        public KeywordIndex(IDictionary<FormKey, string> names) : base(names)
        {
            // Material names the load order's own ArmorMaterial/WeapMaterial/WeaponMaterial keywords define.
            Vocabulary = Values.Select(MaterialFromKeyword).Where(x => x is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        public HashSet<string> Vocabulary { get; }
        public Dictionary<FormKey, MaterialInference> Inferred { get; } = [];
        public Dictionary<FormKey, MaterialResolution> Resolved { get; } = [];
        public Dictionary<FormKey, string> Unresolved { get; } = [];

        // <prefix>Material<Name> keywords from other frameworks. Only names that are already materials in
        // the vocabulary are accepted; a framework's own tiers (e.g. names no vanilla-form keyword uses)
        // are left to crafting evidence. Returns the accepted materials and the keywords that gave them.
        public (string[] Materials, string[] Keywords) FrameworkMaterials(IEnumerable<string> labels)
        {
            var hits = labels.Select(label => (Label: label, Match: FrameworkMaterialKeyword.Match(label)))
                .Where(x => x.Match.Success)
                .Select(x => (x.Label, Material: NormalizeMaterial(x.Match.Groups[1].Value)))
                .Where(x => Vocabulary.Contains(x.Material)).ToArray();
            return (hits.Select(x => x.Material).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), hits.Select(x => x.Label).Distinct().ToArray());
        }
    }
    private static readonly System.Text.RegularExpressions.Regex FrameworkMaterialKeyword =
        new(@"^[A-Za-z0-9]{1,8}_?Material_?([A-Za-z][A-Za-z0-9]*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private const string NoClassIngredientEvidence = "its recipe ingredients are not used by any classified equipment of this weight class";

    // Minimum share of the best material over the runner-up before an inference is accepted.
    private const double InferenceMargin = 1.5;
    // Armor rating / damage must fall inside the learned range of the material, widened by these factors.
    private const double RatingLowFactor = 0.8, RatingHighFactor = 1.25;

    private static string[] MaterialTokens(IMajorRecordGetter record, IReadOnlyDictionary<FormKey, string> keywords)
        => Keywords(record).Select(k => MaterialFromKeyword(keywords.GetValueOrDefault(k.FormKey, "")))
            .Where(x => x is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string[] ReduceKnownMaterialPair(IMajorRecordGetter record, string[] materials)
    {
        if (materials.Length == 2 && materials.Contains("Hide", StringComparer.OrdinalIgnoreCase)
            && materials.Contains("Scaled", StringComparer.OrdinalIgnoreCase)) return ["Scaled"];
        if (record is IWeaponGetter && materials.Length == 2
            && materials.Contains("Steel", StringComparer.OrdinalIgnoreCase)
            && materials.Contains("Skyforge", StringComparer.OrdinalIgnoreCase)) return ["Skyforge"];
        if (materials.Length == 2 && materials.Contains("Steel", StringComparer.OrdinalIgnoreCase)
            && materials.Contains("Silver", StringComparer.OrdinalIgnoreCase)) return ["Silver"];
        return materials;
    }

    private static string[] KeywordLabels(IMajorRecordGetter record, IReadOnlyDictionary<FormKey, string> keywords)
        => Keywords(record).Select(k => keywords.GetValueOrDefault(k.FormKey, "")).Where(x => x.Length > 0).ToArray();

    private static bool IsOwnBaseMaterial(string material)
        => material.StartsWith("Own:", StringComparison.OrdinalIgnoreCase);

    // Resolve every type-supported non-clothing base to one deterministic family.
    // Declared material combinations use ordinary-record frequencies, with fixed
    // reviewed pairs first and tied minima retained as canonical composites.
    // Clothing remains unchanged for its later dedicated rollout.
    private static void ResolveMaterials(IReadOnlyList<ISkyrimModGetter> mods,
        IReadOnlyDictionary<FormKey, IArmorGetter> armors,
        IReadOnlyDictionary<FormKey, IWeaponGetter> weapons, KeywordIndex keywords,
        IReadOnlyDictionary<string, string>? materialOverrides = null)
    {
        InferMaterials(mods, armors, weapons, keywords);
        var equipment = armors.Values.Cast<IMajorRecordGetter>().Concat(weapons.Values).Where(r => !r.IsDeleted).ToArray();
        string[] Labels(IMajorRecordGetter r) => KeywordLabels(r, keywords);
        string Signature(IMajorRecordGetter r) => r is IWeaponGetter ? "WEAP" : "ARMO";
        bool PlayableOrdinary(IMajorRecordGetter r) => !TryEnchantment(r, out _)
            && r switch
            {
                IWeaponGetter w => !w.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable),
                IArmorGetter a => !a.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable),
                _ => false
            };

        var raw = equipment.ToDictionary(r => r.FormKey, r => MaterialTokens(r, keywords));
        var occurrences = equipment.Where(PlayableOrdinary)
            .Where(r => EquipmentType(r, Labels(r)).Type is not null)
            .Select(r => (Record: r, Tokens: raw[r.FormKey].ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .Where(x => x.Tokens.Count > 0)
            .GroupBy(x => Signature(x.Record))
            .ToDictionary(g => g.Key, g => g.SelectMany(x => x.Tokens)
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase));

        MaterialResolution? Direct(IMajorRecordGetter record)
        {
            if (EquipmentType(record, Labels(record)) is not { Type: not null }) return null;
            var tokens = ReduceKnownMaterialPair(record, raw[record.FormKey]);
            if (tokens.Length == 1)
            {
                var original = raw[record.FormKey];
                var rule = original.Length == 1 ? "single keyword" : "reviewed redundant pair";
                var choices = original.Length > 1 ? MaterialChoices(original) : [];
                return new(tokens[0], rule, $"Material rule: {rule}; {string.Join(" + ", original)} -> {tokens[0]}",
                    Choices: choices, Recommended: tokens[0]);
            }
            if (tokens.Length > 1)
            {
                var counts = occurrences.GetValueOrDefault(Signature(record), new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
                var ranked = tokens.Select(token => (Token: token, Count: counts.GetValueOrDefault(token)))
                    .OrderBy(x => x.Count).ThenBy(x => x.Token, StringComparer.OrdinalIgnoreCase).ToArray();
                var evidence = string.Join(", ", ranked.Select(x => $"{x.Token}={x.Count}"));
                if (ranked[0].Count < ranked[1].Count)
                    return new(ranked[0].Token, "rarest keyword",
                        $"Material rule: rarest keyword; {evidence}; selected {ranked[0].Token}",
                        Choices: MaterialChoices(tokens), Recommended: ranked[0].Token);
                var composite = $"Composite({string.Join("+", tokens.Order(StringComparer.OrdinalIgnoreCase))})";
                return new(composite, "composite keyword family",
                    $"Material rule: composite keyword family; tied lowest ordinary counts ({evidence}); retained {composite}",
                    Choices: MaterialChoices(tokens), Recommended: composite);
            }

            var framework = keywords.FrameworkMaterials(Labels(record));
            if (framework.Materials.Length == 1)
                return new(framework.Materials[0], "framework keyword",
                    $"Material rule: framework keyword {string.Join(", ", framework.Keywords)}");
            if (framework.Materials.Length > 1)
            {
                var composite = $"Composite({string.Join("+", framework.Materials.Order(StringComparer.OrdinalIgnoreCase))})";
                return new(composite, "composite framework family",
                    $"Material rule: composite framework family; {string.Join(", ", framework.Keywords)} -> {composite}",
                    Choices: MaterialChoices(framework.Materials), Recommended: composite);
            }
            if (keywords.Inferred.TryGetValue(record.FormKey, out var recipe))
                return new(recipe.Material, "crafting inference", recipe.Evidence, Inferred: true);
            return null;
        }

        static string[] MaterialChoices(IEnumerable<string> materials)
        {
            var tokens = materials.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (tokens.Length < 2) return tokens;
            return tokens.Append($"Composite({string.Join("+", tokens)})").ToArray();
        }

        // Clothing keeps the pre-resolver classification path until clothing and jewelry
        // receive their dedicated integration pass.
        bool IsClothing(IMajorRecordGetter r) => r is IArmorGetter && EquipmentType(r, Labels(r)).ArmorType == ArmorType.Clothing;
        IMajorRecordGetter? BaseRecord(FormKey key) => weapons.GetValueOrDefault(key) ?? (IMajorRecordGetter?)armors.GetValueOrDefault(key);

        MaterialResolution ApplyOverride(IMajorRecordGetter record, MaterialResolution resolution)
        {
            if (resolution.AvailableChoices.Length < 2 || materialOverrides is null
                || !materialOverrides.TryGetValue(record.FormKey.ToString(), out var selected)
                || !resolution.AvailableChoices.Contains(selected, StringComparer.OrdinalIgnoreCase)) return resolution;
            var canonical = resolution.AvailableChoices.First(x => x.Equals(selected, StringComparison.OrdinalIgnoreCase));
            return resolution with
            {
                Material = canonical,
                Rule = "user-selected material family",
                Evidence = $"Material rule: user selected {canonical}; automatic recommendation was {resolution.RecommendedMaterial}. {resolution.Evidence}"
            };
        }

        foreach (var record in equipment)
        {
            if (IsClothing(record)) continue;
            if (Direct(record) is { } resolution) keywords.Resolved[record.FormKey] = ApplyOverride(record, resolution);
        }

        foreach (var record in equipment)
        {
            if (keywords.Resolved.ContainsKey(record.FormKey)) continue;
            var type = EquipmentType(record, Labels(record));
            if (type.Type is null) continue;
            if (IsClothing(record)) continue;
            var canonical = record;
            var seen = new HashSet<FormKey>();
            var valid = true;
            while (TryEnchantment(canonical, out _))
            {
                if (!seen.Add(canonical.FormKey) || !TryBase(canonical, out var parent)) { valid = false; break; }
                var next = BaseRecord(parent);
                if (next is null || next.IsDeleted) { valid = false; break; }
                canonical = next;
            }
            if (!valid) { keywords.Unresolved[record.FormKey] = "Missing or cyclic enchanted base chain"; continue; }
            var family = $"Own:{canonical.FormKey}";
            keywords.Resolved[record.FormKey] = new(family, "own-base family",
                $"Material rule: own-base family {family}; no shared material evidence");
        }

        // Resolve upstream first so record/load-order iteration cannot change inherited families.
        var inheritedCache = new Dictionary<FormKey, MaterialResolution?>();
        MaterialResolution? Inherited(IMajorRecordGetter record, HashSet<FormKey> active)
        {
            if (inheritedCache.TryGetValue(record.FormKey, out var cached)) return cached;
            if (!TryBase(record, out var baseKey) || !active.Add(record.FormKey)) return null;
            try
            {
                var baseItem = BaseRecord(baseKey);
                if (baseItem is null || baseItem.IsDeleted) return null;
                var resolution = Inherited(baseItem, active) ?? keywords.Resolved.GetValueOrDefault(baseKey);
                if (resolution is null) return null;
                // Unenchanted stat templates do not merge distinct own-base identities.
                if (IsOwnBaseMaterial(resolution.Material) && !TryEnchantment(record, out _)) return null;
                return inheritedCache[record.FormKey] = resolution with { Rule = "base inheritance",
                    Evidence = $"Material rule: inherited from canonical base {baseItem.EditorID ?? baseKey.ToString()}; {resolution.Evidence}" };
            }
            finally { active.Remove(record.FormKey); }
        }
        foreach (var record in equipment)
        {
            if (IsClothing(record)) continue;
            if (Inherited(record, []) is { } inherited) keywords.Resolved[record.FormKey] = inherited;
        }

    }

    // Learns, from equipment whose material keyword is known, which crafting and tempering inputs
    // (workbench keyword + ingredient) belong to which material for each weight class, and the
    // armor rating / damage range of each material per slot. Equipment without a material keyword
    // is then placed by its own recipes and checked against those ranges. No plugin, keyword
    // prefix or EditorID is special-cased: the vocabulary is whatever the load order's own
    // keyword-classified equipment uses.
    private static void InferMaterials(IReadOnlyList<ISkyrimModGetter> mods, IReadOnlyDictionary<FormKey, IArmorGetter> armors,
        IReadOnlyDictionary<FormKey, IWeaponGetter> weapons, KeywordIndex keywords)
    {
        var recipes = mods.SelectMany(m => m.ConstructibleObjects).GroupBy(c => c.FormKey).Select(g => g.Last())
            .Where(c => !c.IsDeleted && !c.CreatedObject.IsNull)
            .GroupBy(c => c.CreatedObject.FormKey).ToDictionary(g => g.Key, g => g.ToArray());
        if (recipes.Count == 0) return;
        var itemNames = mods.SelectMany(m => m.MiscItems.Select(x => (x.FormKey, Name: x.Name?.String ?? x.EditorID))
                .Concat(m.Ingredients.Select(x => (x.FormKey, Name: x.Name?.String ?? x.EditorID)))
                .Concat(m.Ingestibles.Select(x => (x.FormKey, Name: x.Name?.String ?? x.EditorID))))
            .GroupBy(x => x.FormKey).ToDictionary(g => g.Key, g => g.Last().Name ?? g.Key.ToString());

        IEnumerable<(FormKey Bench, FormKey Ingredient)> Features(FormKey item) =>
            recipes.GetValueOrDefault(item, []).SelectMany(c => (c.Items ?? [])
                .Select(i => (c.WorkbenchKeyword.FormKey, i.Item.Item.FormKey))).Distinct();
        static string WeightClass(IMajorRecordGetter r) => r is IArmorGetter a ? $"ARMO|{a.BodyTemplate?.ArmorType}" : "WEAP";
        static float Rating(IMajorRecordGetter r) => r switch
        {
            IArmorGetter a => a.ArmorRating,
            IWeaponGetter w => w.BasicStats?.Damage ?? 0,
            _ => 0
        };
        string[] Labels(IMajorRecordGetter r) => KeywordLabels(r, keywords);

        var counts = new Dictionary<string, Dictionary<(FormKey, FormKey), Dictionary<string, int>>>();
        var bands = new Dictionary<(string Class, string Type, string Material), (float Min, float Max)>();
        var equipment = armors.Values.Cast<IMajorRecordGetter>().Concat(weapons.Values).Where(r => !r.IsDeleted).ToArray();
        var unknown = new List<(IMajorRecordGetter Record, string Type, string[] Candidates)>();
        foreach (var record in equipment)
        {
            var shape = ClassifyShape(record, keywords);
            if (shape is not null && IsJewelryType(shape.Type)) continue;
            if (shape is null)
            {
                if (EquipmentType(record, Labels(record)).Type is { } t) unknown.Add((record, t, []));
                continue;
            }
            if (shape.Materials.Length != 1) { unknown.Add((record, shape.Type, shape.Materials)); continue; }
            var material = shape.Materials[0]; var weightClass = WeightClass(record);
            if (!counts.TryGetValue(weightClass, out var byFeature)) counts[weightClass] = byFeature = [];
            foreach (var f in Features(record.FormKey))
            {
                if (!byFeature.TryGetValue(f, out var byMaterial)) byFeature[f] = byMaterial = [];
                byMaterial[material] = byMaterial.GetValueOrDefault(material) + 1;
            }
            var rating = Rating(record);
            if (rating <= 0) continue;
            var band = (weightClass, shape.Type, material);
            bands[band] = bands.TryGetValue(band, out var b) ? (Math.Min(b.Min, rating), Math.Max(b.Max, rating)) : (rating, rating);
        }

        foreach (var (record, type, candidates) in unknown)
        {
            var weightClass = WeightClass(record);
            var features = Features(record.FormKey).ToArray();
            if (features.Length == 0) { keywords.Unresolved[record.FormKey] = "no material keyword and no crafting or tempering recipe"; continue; }
            if (!counts.TryGetValue(weightClass, out var byFeature)) { keywords.Unresolved[record.FormKey] = "no classified equipment of this weight class to learn from"; continue; }
            var materialsInClass = byFeature.Values.SelectMany(x => x.Keys).Distinct().Count();
            var scores = new Dictionary<string, double>(); var used = new List<((FormKey Bench, FormKey Ingredient) Feature, double Weight)>();
            foreach (var f in features)
            {
                if (!byFeature.TryGetValue(f, out var byMaterial)) continue;
                double total = byMaterial.Values.Sum();
                // Inputs shared by many materials (leather strips) carry little information.
                var entropy = -byMaterial.Values.Sum(c => c / total * Math.Log(c / total));
                var weight = materialsInClass <= 1 ? 1 : Math.Max(0.05, 1 - entropy / Math.Log(materialsInClass));
                foreach (var (material, count) in byMaterial) scores[material] = scores.GetValueOrDefault(material) + weight * count / total;
                used.Add((f, weight));
            }
            if (scores.Count == 0) { keywords.Unresolved[record.FormKey] = NoClassIngredientEvidence; continue; }
            var rating = Rating(record);
            var ranked = scores.Where(x => candidates.Length == 0 || candidates.Contains(x.Key, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
            if (ranked.Count == 0) { keywords.Unresolved[record.FormKey] = candidates.Length == 0
                    ? NoClassIngredientEvidence
                    : $"recipe evidence does not support any declared material token ({string.Join(", ", candidates)})"; continue; }
            var fitting = ranked.Where(x => rating <= 0 || !bands.TryGetValue((weightClass, type, x.Key), out var band)
                || rating >= band.Min * RatingLowFactor && rating <= band.Max * RatingHighFactor).ToList();
            if (fitting.Count == 0)
            {
                keywords.Unresolved[record.FormKey] = $"recipe points to {ranked[0].Key}, but rating {rating:0.#} is outside every candidate's {type} range";
                continue;
            }
            if (fitting.Count > 1 && fitting[0].Value < fitting[1].Value * InferenceMargin)
            {
                keywords.Unresolved[record.FormKey] = $"recipe evidence is ambiguous between {fitting[0].Key} and {fitting[1].Key}";
                continue;
            }
            var chosen = fitting[0].Key;
            var inputs = used.OrderByDescending(x => x.Weight).Take(2)
                .Select(x => itemNames.GetValueOrDefault(x.Feature.Ingredient, x.Feature.Ingredient.ToString())).Distinct();
            var rangeText = bands.TryGetValue((weightClass, type, chosen), out var range) ? $"; rating {rating:0.#} within {chosen} {type} range {range.Min:0.#}–{range.Max:0.#}" : "";
            keywords.Inferred[record.FormKey] = new(chosen, $"Material inferred from crafting evidence: {string.Join(", ", inputs)}{rangeText}"
                + (fitting.Count < ranked.Count ? $"; excluded by rating: {string.Join(", ", ranked.Except(fitting).Select(x => x.Key))}" : ""));
        }
    }
}
