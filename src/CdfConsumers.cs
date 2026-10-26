using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Native inventory effects are consumers of existing records, not entries in
    // those lists or fabricated CONT overrides. Native CDF still applies them.
    private sealed record CdfListUse(FormKey Payload, FormKey Consumer, string Predicate, uint Count, string Provenance,
        bool Direct = false, bool Reference = false, string Conditions = "{}", FormKey Base = default,
        string EstablishedPredicate = "")
    {
        // Replacement payloads exist only while one of their predecessors, the
        // explicit form or a certain keyword member, is present.
        public IReadOnlySet<FormKey>? Predecessors { get; init; }
        // The same recipient without its location-family predicates. Evidence there
        // holds wherever this narrower native predicate can fire, never the reverse.
        public string BroaderPredicate { get; init; } = "";
        // Inventory at a precise reference also contains applicable base additions.
        // These identities omit only positive selector scope, never native guards.
        public string InventoryPredicate { get; init; } = "";
        public string BroaderInventoryPredicate { get; init; } = "";
        public string ChangeId { get; init; } = "";
    }
    private sealed partial record CdfConsumers(IReadOnlyList<CdfListUse> Uses, IReadOnlyList<string> Notes)
    {
        // Native CDF re-evaluates these at each initialization/reset, and every output
        // for a CDF recipient is CDF-delivered with the authored predicate copied, so
        // they stay symbolic: part of the recipient identity, never evaluated offline.
        private static readonly string[] LocationFamilies =
            ["locations", "!locations", "worldspaces", "!worldspaces", "locationKeywords", "!locationKeywords"];
        // Predicates copied verbatim into outputs and kept in the identity in authored
        // spelling. Negated selectors are included: a rule an unresolvable exclusion
        // disables natively must never share a live identity. Their resolved entries
        // additionally prune additions (see below).
        // Runtime state (player skills, globals, quest stages) is read per rule in the
        // same native pass, so a copied predicate sees exactly what its source saw.
        private static readonly string[] RuntimePredicates = ["playerSkills", "!playerSkills", "globals", "!globals", "questConditions"];
        private static readonly string[] VerbatimPredicates = [.. LocationFamilies, "!containers", "!references", .. RuntimePredicates];
        public CdfRemovals Removals { get; init; } = new([], []);
        public CdfKeywordMembership? KeywordMembership { get; init; }
        public ILookup<FormKey, CdfListUse> DirectByPayload { get; } = Uses.Where(u => u.Direct).ToLookup(u => u.Payload);
        public ILookup<FormKey, CdfListUse> ByPayload { get; } = Uses.ToLookup(u => u.Payload);
        private static readonly JsonElement NoConditions = JsonDocument.Parse("{}").RootElement.Clone();
        // Native parsing drops a whole runtime field (or the quest condition) on any
        // malformed entry, which can skip the rule; only shapes it parses fully are
        // imported. Skill names are copied unchecked: they are looked up at runtime.
        private static bool RuntimePredicateWellFormed(string field, JsonElement value)
        {
            // std::stof consumes a numeric prefix. Keep the supported decimal subset
            // and existing range safeguards; a complete out-of-range exponent never
            // falls back to its mantissa. Other strtof extensions remain unsupported.
            static bool DecimalPrefix(string number)
            {
                var at = 0;
                while (at < number.Length && number[at] is ' ' or '\t' or '\n' or '\r' or '\v' or '\f') at++;
                if (at < number.Length && number[at] is '+' or '-') at++;
                if (number.AsSpan(at).StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
                var digits = 0; var nonzero = false;
                void Digits()
                {
                    while (at < number.Length && number[at] is >= '0' and <= '9')
                    { nonzero |= number[at] != '0'; digits++; at++; }
                }
                Digits();
                if (at < number.Length && number[at] == '.') { at++; Digits(); }
                if (digits == 0) return false;
                var end = at;
                if (at < number.Length && number[at] is 'e' or 'E')
                {
                    at++;
                    if (at < number.Length && number[at] is '+' or '-') at++;
                    var exponent = at;
                    while (at < number.Length && number[at] is >= '0' and <= '9') at++;
                    if (at > exponent) end = at;
                }
                return double.TryParse(number.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    && (parsed == 0 ? !nonzero : Math.Abs(parsed) is >= 1.17549435E-38 /* FLT_MIN */ and <= float.MaxValue);
            }
            static bool Pair(JsonElement entry) => entry.ValueKind == JsonValueKind.String
                && entry.GetString()!.Split('|') is [{ Length: > 0 }, var number] && DecimalPrefix(number);
            return field switch
            {
                "playerSkills" or "!playerSkills" or "globals" or "!globals" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(Pair),
                "questConditions" => value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty("questID", out var quest) && quest.ValueKind == JsonValueKind.String
                    && (value.TryGetProperty("stageDone", out var stages) && stages.ValueKind != JsonValueKind.Null
                        || value.TryGetProperty("completed", out var completed) && completed.ValueKind != JsonValueKind.Null)
                    && (!value.TryGetProperty("stageDone", out var done) || done.ValueKind != JsonValueKind.Array
                        || done.EnumerateArray().All(stage => CdfInputs.NativeUInt(stage, out _))),
                _ => true
            };
        }
        // Proof that a rule cannot touch weapon/armor inventory. Anything unresolved,
        // unknown or possibly native-erroring counts as touching it; an empty keyword
        // predicate matches everything. Leveled payloads are followed to any depth.
        private static bool NoEquipmentEffect(CdfRule rule, Catalog catalog, Func<CdfKeywordMembership> membership, Dictionary<FormKey, bool> reaches,
            IReadOnlySet<FormKey> equipmentPredecessors)
        {
            if (rule.NativeDisposition is not ("unsupported-effect" or "unsupported-predicate")) return false;
            if (rule.Diagnostics.Any(n => n.StartsWith("Unknown field changes", StringComparison.Ordinal)
                || n.StartsWith("Duplicate field changes", StringComparison.Ordinal) || n.EndsWith("is not an object.", StringComparison.Ordinal))) return false;
            bool Equipment(FormKey key) => IsEquipment(key, catalog);
            foreach (var change in rule.Changes)
            {
                if (change.Disposition is "skipped-invalid-change" or "skipped-unresolved-form" or "no-operation") continue;
                if (change.Disposition != "unsupported-effect") return false;
                var refs = rule.References.Where(r => r.Field.StartsWith($"changes[{change.Index}].", StringComparison.Ordinal)).ToArray();
                if (refs.Any(r => r.Status != "resolved")) return false;
                FormKey[] Keys(string field) => refs.Where(r => r.Field == $"changes[{change.Index}].{field}").Select(r => FormKey.Factory(r.FormKey!)).ToArray();
                if (Keys("add").Any(key => ReachesEquipment(key, catalog, reaches, []) || equipmentPredecessors.Contains(key))) return false;
                // Removing a replacement's non-equipment predecessor can stop equipment
                // payloads from being delivered, so that removal is not inert.
                if (Keys("remove").Any(key => Equipment(key) || equipmentPredecessors.Contains(key))) return false;
                if (change.Operation is "keyword-removal" or "keyword-replacement"
                    && Keys("removeByKeywords").Distinct().Order().ToArray() is var keywords && (keywords.Length == 0 || membership().Match(keywords).Length > 0)) return false;
            }
            return true;
        }
        internal static bool IsEquipment(FormKey key, Catalog catalog) => catalog.Weapons.ContainsKey(key) || catalog.Armors.ContainsKey(key);
        // Whether a form is equipment, a form that is not loaded (unknown, so possibly
        // equipment), or a leveled list that can yield either at any depth.
        internal static bool ReachesEquipment(FormKey key, Catalog catalog, Dictionary<FormKey, bool> memo, HashSet<FormKey> visiting)
        {
            if (IsEquipment(key, catalog) || !RuntimeRecordWinners(catalog).ContainsKey(key)) return true;
            if (!catalog.Lists.TryGetValue(key, out var list)) return false;
            // A list scripts or other frameworks change at runtime may gain equipment.
            if (SourceListHasRuntimeMutation(key, catalog)) return true;
            if (memo.TryGetValue(key, out var known)) return known;
            if (!visiting.Add(key)) return false;
            var root = visiting.Count == 1;
            var result = (list.Entries ?? []).Any(entry => entry.Data?.Reference.FormKey is { IsNull: false } item && ReachesEquipment(item, catalog, memo, visiting));
            visiting.Remove(key);
            // Inside a cycle a branch back to a list still being explored reads as false,
            // so only a root's complete exploration may cache a negative answer.
            if (result || root) memo[key] = result;
            return result;
        }
        // Respawning winning CONT bases, computed once per analysis for fan-out rules.
        internal static FormKey[] RespawningContainers(Catalog catalog) => catalog.ConsumerRecords.Containers.Values
            .Where(c => !c.IsDeleted && c.Flags.HasFlag(Container.Flag.Respawns)).Select(c => c.FormKey).Order().ToArray();
        private static FormKey[] AllContainers(Catalog catalog) => catalog.ConsumerRecords.Containers.Values
            .Where(c => !c.IsDeleted).Select(c => c.FormKey).Order().ToArray();
        // foreignRemovals: other native frameworks' inventory removals (CID). They decide whether
        // a replacement's predecessor is established, as CDF's own removals do; the caller adds
        // them to the returned index (With), so they are not repeated in it here.
        public static CdfConsumers Read(CdfInputReport report, Catalog catalog, IReadOnlyList<CdfRemoval>? foreignRemovals = null)
        {
            using var timing = PhaseTiming.Measure("cdf.analysis");
            var uses = new List<CdfListUse>();
            // Initial direct inventory needed to trigger replacements, kept out of
            // equipment consumers and graph eligibility. Possible draws never enter it.
            var inventoryAdditions = new List<CdfListUse>();
            var notes = new List<string>();
            var removals = new List<CdfRemoval>();
            var variantErasures = new List<CdfRemoval>();
            var reachesEquipment = new Dictionary<FormKey, bool>();
            // Across every active rule: predecessors whose replacement delivers equipment, and
            // whether any keyword erasure exists (its non-equipment matches are not modelled).
            var activeChanges = report.Files.SelectMany(f => f.Rules).Where(r => r.NativeDisposition is "unsupported-effect" or "unsupported-predicate")
                .SelectMany(r => r.Changes.Where(c => c.Disposition == "unsupported-effect").Select(c => (Rule: r, Change: c))).ToArray();
            FormKey[] ChangeKeys(CdfRule r, CdfChange c, string field) => r.References
                .Where(x => x.Field == $"changes[{c.Index}].{field}" && x.Status == "resolved").Select(x => FormKey.Factory(x.FormKey!)).ToArray();
            var equipmentPredecessors = activeChanges.Where(x => x.Change.Operation == "replacement"
                    && ChangeKeys(x.Rule, x.Change, "add").Any(k => ReachesEquipment(k, catalog, reachesEquipment, [])))
                .SelectMany(x => ChangeKeys(x.Rule, x.Change, "remove")).ToHashSet();
            var keywordErasure = activeChanges.Any(x => x.Change.Operation is "keyword-removal" or "keyword-replacement");
            var inert = 0; var analysed = 0;
            FormKey[]? respawning = null, allContainers = null;
            // Guard semantics are read from the verified source; other majors import
            // with a warning and must not have live removals pruned on that basis.
            var trustedGuards = report.Status is "source-matched-3.1.0" or "accepted-major-3";
            HashSet<FormKey>? respawningSet = null;
            CdfKeywordMembership? keywordMembership = null;
            // Every captured rule's disposition is reported even when one blocks the import.
            IEnumerable<string> Dispositions() => report.Files.SelectMany(f => f.Rules.Select(r => $"CDF rule disposition: {f.Path} rule {r.Index} ({r.FriendlyName}): "
                + (r.NativeDisposition is not ("unsupported-effect" or "unsupported-predicate") ? $"natively inactive ({r.NativeDisposition})"
                    : NoEquipmentEffect(r, catalog, () => keywordMembership ??= new(catalog, notes), reachesEquipment, equipmentPredecessors) ? "no equipment effect"
                    : "equipment-affecting")));
            CdfConsumers Unsupported(string reason)
            {
                // Start over across the complete capture: an early positive-import refusal
                // must neither lose later removals nor count earlier ones twice.
                var negative = DestructiveFallback(report, catalog, trustedGuards, new());
                return new([], [$"CDF analytical import unavailable: {reason}. Existing plugin-only analysis remains enabled; no partial CDF effect grants permission. Native destructive constraints remain active.",
                    .. negative.Notes, .. Dispositions()]) { Removals = negative.Removals, KeywordMembership = negative.KeywordMembership };
            }
            if (report.Status is "absent" or "missing-dll") return new([], []);
            if (report.Status is not ("source-matched-3.1.0" or "accepted-major-3" or "accepted-other-major")) return Unsupported("capture is incomplete");
            if (report.Files.Any(f => f.Status is "malformed-json" or "unreadable")) return Unsupported("a file has an unresolved native footprint");
            foreach (var file in report.Files)
            foreach (var rule in file.Rules)
            {
                var provenance = $"{file.Path} rule {rule.Index} ({rule.FriendlyName})";
                if (rule.NativeDisposition.StartsWith("stops-file-", StringComparison.Ordinal)
                    || rule.NativeDisposition is "skipped-file-tail" or "skipped-empty-condition" or "no-changes") continue;
                // A rule that provably never adds, removes or replaces equipment contributes
                // nothing to equipment analysis, whatever its guards, predicates or
                // operations, so it never makes the import unavailable.
                if (NoEquipmentEffect(rule, catalog, () => keywordMembership ??= new(catalog, notes), reachesEquipment, equipmentPredecessors))
                {
                    inert++;
                    notes.Add($"CDF {provenance}: no equipment effect; every resolved payload, removal and keyword predicate is outside weapon/armor inventory, so the rule is not analysed further.");
                    continue;
                }
                analysed++;
                using var document = JsonDocument.Parse(rule.Json, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var raw = document.RootElement;
                if (raw.ValueKind != JsonValueKind.Object || rule.NativeDisposition != "unsupported-effect") return Unsupported(provenance + ": unsupported rule/predicate");
                if (rule.Diagnostics.Any(n => n.StartsWith("Unknown field", StringComparison.Ordinal) || n.StartsWith("Duplicate field", StringComparison.Ordinal)))
                    return Unsupported(provenance + ": unknown or duplicate field");
                // Unresolved verbatim entries are dropped natively; the copied predicate
                // drops them identically, so they cannot widen an output. An unresolved
                // quest (field `questConditions.questID`) creates no condition and skips
                // the rule natively, so it deliberately stays refused here.
                // References of changes native validation skips have no effect.
                var resolvedSelectors = rule.References.Where(r => r.Status == "resolved"
                    && r.Field is "conditions.containers" or "conditions.references").Select(r => r.Field).ToHashSet(StringComparer.Ordinal);
                bool OptionalSelector(CdfReference reference) => resolvedSelectors.Contains(reference.Field) && KnownAbsentSelector(reference);
                var skippedChanges = rule.Changes.Where(c => c.Disposition is "skipped-invalid-change" or "skipped-unresolved-form")
                    .Select(c => $"changes[{c.Index}].").ToArray();
                if (rule.References.Any(r => r.Status != "resolved" && !OptionalSelector(r) && !VerbatimPredicates.Any(f => r.Field == "conditions." + f)
                    && !skippedChanges.Any(prefix => r.Field.StartsWith(prefix, StringComparison.Ordinal))))
                    return Unsupported(provenance + ": unresolved loaded reference");
                // Natively a location family, or `!containers` without a surviving
                // `containers` condition, with no resolvable entry skips the whole rule;
                // `!references` always applies. XEP's resolver can disagree with the
                // game's (EditorID caches), and skipping a live removal would not be
                // conservative, so stay unavailable.
                var byField = rule.References.GroupBy(r => r.Field).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
                CdfReference[] Entries(string field) => byField.GetValueOrDefault("conditions." + field, []);
                bool Unresolvable(string family) => Entries(family) is { Length: > 0 } entries && entries.All(r => r.Status != "resolved");
                if (LocationFamilies.Any(Unresolvable) || Unresolvable("globals") || Unresolvable("!globals")
                    || (Unresolvable("!containers") && Entries("containers").Length == 0))
                    return Unsupported(provenance + ": symbolic predicate has no resolvable entry; native rule activity is unproved");
                if (rule.Guards is null) return Unsupported(provenance + ": unresolved native guard");
                var random = rule.Guards.RandomAdd;
                // Guards are copied verbatim, so each build applies its own vendor and
                // safe-container semantics. Only the respawn flag, static record data, is
                // evaluated here: without the bypass, CDF never touches a base lacking it.
                var bypass = rule.Guards.BypassUnsafeContainers;
                // A rule without conditions applies to every container, like one without
                // a positive selector; both fan out below.
                if (!raw.TryGetProperty("conditions", out var conditions) || conditions.ValueKind == JsonValueKind.Null)
                    conditions = NoConditions;
                if (conditions.ValueKind != JsonValueKind.Object)
                    return Unsupported(provenance + ": malformed conditions can change the native footprint");
                foreach (var field in conditions.EnumerateObject())
                {
                    if (field.Value.ValueKind != JsonValueKind.Null && !VerbatimPredicates.Contains(field.Name) && field.Name is not
                        ("plugins" or "containers" or "references" or "bypassUnsafeContainers" or "allowVendors" or "onlyVendors" or "randomAdd"))
                        return Unsupported(provenance + ": condition " + field.Name + " requires its semantic slice");
                    if (field.Value.ValueKind != JsonValueKind.Null && !RuntimePredicateWellFormed(field.Name, field.Value))
                        return Unsupported(provenance + ": malformed " + field.Name + " makes native rule activity unproved");
                    if (field.Name is "containers" or "references" or "!containers" or "!references" && field.Value.ValueKind != JsonValueKind.Null
                        && (field.Value.ValueKind != JsonValueKind.Array || field.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)))
                        return Unsupported(provenance + ": malformed selector can change the native footprint");
                }
                FormKey[] Keys(string field) => Entries(field).Where(r => r.Status == "resolved")
                    .Select(r => FormKey.Factory(r.FormKey!)).Distinct().Order().ToArray();
                var bases = Keys("containers"); var references = Keys("references");
                var excludedBases = Keys("!containers").ToHashSet(); var excludedReferences = Keys("!references").ToHashSet();
                var hasReferences = conditions.TryGetProperty("references", out var referenceField) && referenceField.ValueKind != JsonValueKind.Null;
                if (hasReferences && referenceField.ValueKind != JsonValueKind.Array) return Unsupported(provenance + ": native reference predicate can disappear");
                // Without a positive selector the rule reaches every container base its
                // guards admit: respawning bases, or every base with the bypass. Recipients
                // stay base-scoped and symbolic; references are never guessed from
                // locations. Destructive effects use one all-container scope.
                var everyContainer = !hasReferences
                    && !(conditions.TryGetProperty("containers", out var containerField) && containerField.ValueKind != JsonValueKind.Null);
                if (everyContainer) bases = bypass ? (allContainers ??= AllContainers(catalog)) : (respawning ??= RespawningContainers(catalog));
                // Empty positive references are always false, unlike absent refs.
                var erasureConsumers = bases;
                FormKey BaseOf(FormKey reference) => ((IPlacedObjectGetter)RuntimeRecordWinners(catalog)[reference].Record).Base.FormKey;
                if (hasReferences)
                {
                    var winners = RuntimeRecordWinners(catalog);
                    erasureConsumers = references.Where(key => winners.TryGetValue(key, out var found)
                        && found.Record is IPlacedObjectGetter placed
                        && catalog.ConsumerRecords.Containers.ContainsKey(placed.Base.FormKey)
                        && (bases.Length == 0 || bases.Contains(placed.Base.FormKey))).ToArray();
                }
                // The game reads the same winning respawn flag, so without the bypass a base
                // lacking it never receives additions. Destructive effects are pruned too
                // only for builds whose guard semantics are trusted (verified or same major):
                // pruning a live removal would authorize output it erases.
                var additionRecipients = erasureConsumers;
                if (!bypass && !everyContainer)
                {
                    var admitted = respawningSet ??= (respawning ??= RespawningContainers(catalog)).ToHashSet();
                    additionRecipients = erasureConsumers.Where(key => admitted.Contains(BaseFor(key))).ToArray();
                    if (trustedGuards) erasureConsumers = additionRecipients;
                }
                // Resolved exclusions prune additions only, which can just withhold output.
                // Destructive effects keep the unpruned scope: an exclusion XEP resolves
                // (e.g. by EditorID) may be dropped natively, leaving the effect live there.
                var consumers = additionRecipients.Where(key => hasReferences
                    ? !excludedReferences.Contains(key) && !excludedBases.Contains(BaseOf(key))
                    : !excludedBases.Contains(key)).ToArray();
                // Vendor status and never-reset zones stay symbolic: CDF applies them at each
                // initialization/reset. Guards widen where a rule fires, so only identical
                // authored guards share an identity; they never fall back to one another.
                string Guard(string field) => conditions.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetRawText() : "-";
                var guards = $"|bypass:{Guard("bypassUnsafeContainers")}|vendors:{Guard("allowVendors")}|onlyVendors:{Guard("onlyVendors")}|random:false";
                var identity = (hasReferences ? "reference" : "base") + "|bases:" + (everyContainer ? "*" : string.Join(',', bases)) + guards;
                string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                // Authored spelling is the identity: equivalent but differently written
                // predicates stay distinct, which can only withhold correlation.
                var where = string.Join('|', VerbatimPredicates
                    .Select(f => conditions.TryGetProperty(f, out var value) && value.ValueKind != JsonValueKind.Null ? f + "=" + JsonSerializer.Serialize(value) : "")
                    .Where(part => part.Length > 0));
                var predicate = Hash(identity + (where.Length == 0 ? "" : "|where:" + where));
                var broader = where.Length == 0 ? "" : Hash(identity);
                var inventoryPredicate = Hash(guards + (where.Length == 0 ? "" : "|where:" + where));
                var broaderInventoryPredicate = where.Length == 0 ? "" : Hash(guards);
                CdfErasureScope? erasureScope = null;
                void Erase(List<CdfRemoval> target, FormKey item, uint count, string changeId = "")
                    => (erasureScope ??= ErasureScope(rule, catalog, trustedGuards)).Add(target, item, count, provenance, changeId);
                FormKey BaseFor(FormKey consumer) => hasReferences
                    ? ((IPlacedObjectGetter)RuntimeRecordWinners(catalog)[consumer].Record).Base.FormKey : consumer;
                foreach (var change in rule.Changes)
                {
                    if (change.Disposition is "skipped-invalid-change" or "skipped-unresolved-form" or "no-operation") continue;
                    if (change.Operation == "keyword-removal" && change.Disposition == "unsupported-effect")
                    {
                        keywordMembership ??= new(catalog, notes);
                        var keywords = byField.GetValueOrDefault($"changes[{change.Index}].removeByKeywords", [])
                            .Select(r => FormKey.Factory(r.FormKey!)).Distinct().Order().ToArray();
                        var matches = keywordMembership.Match(keywords);
                        foreach (var item in matches)
                            Erase(removals, item, 0);
                        notes.Add($"CDF analysed {provenance}, change {change.Index}: keyword removal requires all {keywords.Length} keyword(s), matches {matches.Length} equipment form(s), removes full quantities regardless of count; empty predicates match all equipment.");
                        continue;
                    }
                    if (change.Operation == "removal" && change.Disposition == "unsupported-effect" && change.Count is { } removedCount)
                    {
                        var item = FormKey.Factory(byField[$"changes[{change.Index}].remove"].Single().FormKey!);
                        Erase(removals, item, removedCount);
                        notes.Add($"CDF analysed {provenance}, change {change.Index}: explicit removal of {item}, count {removedCount} (zero means all), {(everyContainer ? "every container" : erasureConsumers.Length + " recipient scope(s)")}; applied after additions, including expanded list results.");
                        continue;
                    }
                    var replacement = change.Operation is "replacement" or "keyword-replacement" && change.Disposition == "unsupported-effect";
                    if (!replacement && (change.Operation != "addition" || change.Disposition != "unsupported-effect" || change.Count is null))
                        return Unsupported(provenance + ": operation " + change.Operation + " requires its semantic slice");
                    if (!replacement && change.Count == 0) continue;
                    var changeId = $"{file.Path}|{rule.Index}|{change.Index}";
                    var payloads = byField.GetValueOrDefault($"changes[{change.Index}].add", [])
                        .Select(r => FormKey.Factory(r.FormKey!)).ToArray();
                    // Count is independent draws with replacement, not a promise
                    // that every alternative occurs. Equal alternatives have one
                    // possible outcome; multiplicity remains in the authored JSON.
                    if (random) payloads = payloads.Distinct().ToArray();
                    var drawn = random && payloads.Length > 1;
                    IReadOnlySet<FormKey>? predecessors = null;
                    IReadOnlySet<FormKey> restored = new HashSet<FormKey>();
                    var erased = Array.Empty<FormKey>();
                    var scope = predicate;
                    if (replacement)
                    {
                        // Native replacements run after additions and all removals, explicit
                        // ones before keyword ones. They erase every predecessor, ignoring
                        // count, and add the payloads only when one is present at that point.
                        // Keyword predecessors use the shared membership index: possible
                        // members are erased, but only certain members prove the rule fires.
                        var facts = ReplacementFacts(rule, change, payloads, drawn, catalog, notes, ref keywordMembership);
                        predecessors = facts.Predecessors;
                        erased = facts.Erased;
                        restored = facts.Restored;
                        foreach (var item in erased)
                        {
                            var target = !restored.Contains(item) ? removals : change.Operation == "keyword-replacement" ? variantErasures : null;
                            if (target is not null) Erase(target, item, 0, changeId);
                        }
                        scope = predicate + "|replace:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changeId)));
                    }
                    var possible = drawn || replacement;
                    var count = replacement ? 1u : change.Count!.Value;
                    for (var occurrence = 0; occurrence < payloads.Length; occurrence++)
                    {
                        var payload = payloads[occurrence];
                        if (change.Operation == "replacement" && restored.Contains(payload)) continue;
                        var choice = drawn ? scope + "|draw:" + Convert.ToHexString(SHA256.HashData(
                            Encoding.UTF8.GetBytes($"{changeId}|{payload}"))) : scope;
                        if (!catalog.Lists.TryGetValue(payload, out var list))
                        {
                            if (catalog.Weapons.ContainsKey(payload) || catalog.Armors.ContainsKey(payload))
                                foreach (var consumer in consumers) uses.Add(new(payload, consumer, choice, count,
                                    $"{provenance}, change {change.Index}, payload {occurrence}", true, hasReferences, conditions.GetRawText(), BaseFor(consumer), possible ? predicate : "")
                                    { Predecessors = predecessors, ChangeId = changeId, BroaderPredicate = broader,
                                        InventoryPredicate = inventoryPredicate, BroaderInventoryPredicate = broaderInventoryPredicate });
                            else
                            {
                                if (!possible && equipmentPredecessors.Contains(payload))
                                    foreach (var consumer in consumers) inventoryAdditions.Add(new(payload, consumer, choice, count,
                                        $"{provenance}, change {change.Index}, payload {occurrence}", true, hasReferences, conditions.GetRawText(), BaseFor(consumer))
                                        { ChangeId = changeId, BroaderPredicate = broader,
                                        InventoryPredicate = inventoryPredicate, BroaderInventoryPredicate = broaderInventoryPredicate });
                                notes.Add($"CDF {provenance}: non-equipment direct payload {payload} remains authored inventory; guaranteed replacement predecessors are retained only as inventory evidence.");
                            }
                            continue;
                        }
                        if (list.IsDeleted) return Unsupported(provenance + ": deleted list payload");
                        foreach (var consumer in consumers) uses.Add(new(payload, consumer, choice, count, provenance,
                            Reference: hasReferences, Base: BaseFor(consumer), EstablishedPredicate: possible ? predicate : "") { Predecessors = predecessors, ChangeId = changeId, BroaderPredicate = broader,
                                        InventoryPredicate = inventoryPredicate, BroaderInventoryPredicate = broaderInventoryPredicate });
                    }
                    if (replacement) notes.Add($"CDF analysed {provenance}, change {change.Index}: {(change.Operation == "replacement" ? "explicit" : "keyword")} replacement erases "
                        + $"{erased.Length - restored.Count} form(s) in full ({predecessors!.Count} certain trigger(s)), re-adds {restored.Count}; {payloads.Length} payload(s) occur only while a predecessor is present"
                        + (drawn ? ", one draw per replaced item" : "") + $"; {consumers.Length} recipient scope(s); runs after additions and all removals"
                        + (change.Operation == "replacement" ? "." : " and explicit replacements; keyword predicates require every keyword, empty predicates match all inventory."));
                    else if (random) notes.Add($"CDF random additions {provenance}, change {change.Index}: {payloads.Length} distinct alternative(s), {change.Count} draw(s) with replacement; "
                        + (drawn ? "possible outcomes remain choice-scoped; independent draws do not establish co-occurrence. Choice-dependent direct companion delivery is unavailable." : "one established payload outcome; native selection and count retained."));
                    if (!replacement) notes.Add($"CDF analysed {provenance}, change {change.Index}: {payloads.Count(catalog.Lists.ContainsKey)} list payload(s), count {change.Count}, {consumers.Length} recipient scope(s); native guards and lifecycle retained, no inventory baked into output.");
                }
            }
            notes.Add($"CDF rule dispositions: {analysed} analysed, {inert} with no equipment effect; any unsupported equipment-affecting rule makes the import unavailable instead.");
            notes.AddRange(Dispositions());
            // A replacement payload is established only where its predecessor
            // has a proved positive quantity after every removal except that
            // replacement's own erasure. Keyword erasures only model equipment, so
            // while any exists a non-equipment predecessor is never proved present. Otherwise it stays choice-scoped, so it
            // supplies no peer evidence and no independently fired companion.
            if (uses.Any(u => u.Predecessors is not null))
            {
                var interim = new CdfRemovals(removals.Concat(foreignRemovals ?? []), uses.Concat(inventoryAdditions), null, (respawning ??= RespawningContainers(catalog)).ToHashSet());
                var upgraded = 0; var conditional = 0;
                for (var index = 0; index < uses.Count; index++)
                {
                    var use = uses[index];
                    if (use.Predecessors is null) continue;
                    var established = use with { Predicate = use.EstablishedPredicate, EstablishedPredicate = "" };
                    if (!use.Predicate.Contains("|draw:", StringComparison.Ordinal)
                        && interim.AnyEstablished(keywordErasure ? use.Predecessors.Where(p => IsEquipment(p, catalog)).ToHashSet() : use.Predecessors,
                            CdfUse(established), use.Base, use.ChangeId, catalog))
                    { uses[index] = established; upgraded++; }
                    else conditional++;
                }
                notes.Add($"CDF replacements: {upgraded} payload recipient use(s) established by a surviving predecessor; {conditional} remain conditional on predecessor presence and cannot drive direct companion output.");
            }
            return new(uses.AsReadOnly(), notes.AsReadOnly()) { Removals = new(removals, uses.Concat(inventoryAdditions), variantErasures, (respawning ?? RespawningContainers(catalog)).ToHashSet()), KeywordMembership = keywordMembership };
        }
    }
}
