using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

// SPID and CID recipients as consumers of their payloads (ticket 02 slice). Native SPID/CID
// still apply the authored rules; XEP only records who receives which payload so the shared
// route authorization counts them. No output is written for a rule itself: a payload list
// keeps normal list output, a direct item payload has no XEP route (maintainer decision).
// A rule XEP cannot bound to exact recipients blocks its payload (unknown consumer), never
// widens a known recipient set.
public static partial class PatcherEngine
{
    private sealed record SpidCidBlocker(FormKey Payload, string Role, string Reason);

    // Recipient roles of native container frameworks (CDF, CID): reference/predicate scoped
    // container consumers. Physical base stock applies inside them, never the reverse.
    private static bool NativeContainerRole(string role)
        => role.StartsWith("CDF inventory ", StringComparison.Ordinal) || role.StartsWith("CID inventory ", StringComparison.Ordinal);

    private sealed class SpidRecipients
    {
        // A form-filter entry: a resolved form of a whitelisted type, a plugin name, or an ALL
        // entry SPID can never match (resolved-unmatchable).
        private sealed record Entry(string Type, FormKey Key, string Plugin);
        // One rule's offline-evaluable filters (strings, forms, traits), shared by item rules and
        // by the outfit candidates that compete for native outfit selection.
        private sealed record Filters(Entry[] Match, Entry[] Not, Entry[] All, SpidStringFilters? Strings, SpidTraits? Traits, SpidLevels? Levels);
        // Chanced: a roll below 100 may withhold the payload. By maintainer decision (ticket 11) any chance
        // above 0 counts as received: the receipt is a consumer and evidence.
        private sealed record ItemRule(Filters Filters, FormKey Payload, bool Outfit, bool Sleep, bool Death, int Index, bool Chanced);
        private readonly List<ItemRule> rules = [];
        // One live default-parser Outfit or SleepOutfit rule competing in native selection, equipment
        // or not (an empty outfit still replaces). Null filters: recipients not evaluable, it may
        // match anyone. Chanced: its roll may fail, and native selection then tries the next one.
        // DirectArmor: SPID's race check on direct ARMO entries may reject it (next one tried).
        // Index links a candidate to its modelled rule, if any.
        private sealed record OutfitCandidate(Filters? Filters, FormKey Outfit, bool Chanced, bool DirectArmor, int Index, bool Final, bool LevelFiltered);
        // In native selection order: typed lines before generic Form lines, then parse order.
        private readonly List<OutfitCandidate> outfitCandidates = [];
        private readonly List<OutfitCandidate> sleepCandidates = [];
        // DeathOutfit and linked outfit rules also replace what an actor wears (at death, or when
        // the linked form is distributed); not modelled, so a possible match leaves it undecided.
        private readonly List<Filters?> otherDefaultReplacers = [];
        private readonly List<Filters?> otherSleepReplacers = [];
        private readonly Catalog catalog;
        private readonly IReadOnlyDictionary<FormKey, INpcGetter> npcs;
        // NPCs a leveled NPC list can spawn: SPID compares a spawned actor's template identities,
        // not its own, so an own-identity match on such an NPC is undecided.
        private readonly HashSet<FormKey> leveledChildren;
        // Without po3's EditorID cache, native string filters see no NPC EditorIDs at all.
        private readonly bool editorIds;
        // Spells SPID itself adds earlier in the pass: absent from the plugins but possibly
        // present natively, so their absence is undecided.
        private readonly HashSet<FormKey> distributedSpells = [];
        // A spell rule whose form the capture does not resolve (typed lines outside the equipment
        // scope are parsed but not resolved): any spell miss is then undecided.
        private bool unresolvedSpells;
        // Skins SPID sets on the base after items in the pass: sleep outfits, the outfit pass and
        // death distribution see them, so an Armor (skin) filter there is undecided.
        private bool skinsDistributed;
        // One rule that distributes a keyword or faction. Null filters: its recipients are not
        // evaluable. Certain: a match always adds it (no chance roll, no exclusive group, regular
        // pass); otherwise a match only may add it. Unlike payloads, a chanced keyword or faction
        // stays undecided: its roll failing makes NOT filters match, so treating it as added would
        // drop real consumers.
        private sealed record Producer(Filters? Filters, bool Certain);
        // Native pass order: keywords (each inserted at once, dependents sorted after the keywords
        // they test), then factions (appended after the faction loop), then perks, spells, items
        // and outfits, which see both.
        private readonly Dictionary<string, List<Producer>> keywordProducers = new(StringComparer.Ordinal);
        private readonly Dictionary<FormKey, List<Producer>> factionProducers = [];
        // Native keyword entries (every regular Keyword line, also impossible or unevaluable ones,
        // since all of them take part in the dependency sort) and their string filters. Keywords
        // are keyed by record identity (FormKey, or "created:" + name for keywords SPID creates):
        // distinct records sharing or differing only in case of an EditorID are distinct natively.
        private readonly List<(string Id, string Name, SpidStringFilters? Strings)> keywordEntries = [];
        private readonly Dictionary<string, string> keywordNames = new(StringComparer.Ordinal);
        // Native ordering edges: a case-sensitive exact MATCH/NOT/ALL term (ANY links only an
        // unspecified first match, so it orders nothing reliably).
        private readonly HashSet<(string From, string To)> keywordOrder = [];
        // Keywords on a dependency cycle (by any matching term), by component: native order inside
        // one is not by dependency, so members see each other as undecided.
        private readonly Dictionary<string, int> keywordCycle = new(StringComparer.Ordinal);
        // Producer names a string term can match (exact or partial), shared by every NPC view.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Term, bool Partial), string[]> termKeywords = new();
        // KID is projected: keywords it adds are not captured, so any keyword-capable miss is undecided.
        private readonly bool keywordDistributor;
        public List<SpidCidBlocker> Blockers { get; } = [];
        public HashSet<FormKey> ProtectedOutfits { get; } = [];
        public DiagnosticMessages Notes { get; } = new();
        public bool HasRules => rules.Count > 0;
        // Live outfit rules, modelled or not, can replace authored outfits.
        public bool ReplacesOutfits => outfitCandidates.Count + sleepCandidates.Count + otherDefaultReplacers.Count + otherSleepReplacers.Count > 0;

        // Effective outfits (LLOS protocol 450 swaps applied), the same projection the graph's
        // recipient rows read; null falls back to the winning records.
        private readonly IReadOnlyDictionary<FormKey, IOutfitGetter>? effectiveOutfits;
        private IOutfitGetter? OutfitOf(FormKey key)
            => key.IsNull ? null : effectiveOutfits?.GetValueOrDefault(key) ?? Winner(key) as IOutfitGetter;

        public SpidRecipients(SpidInputReport? report, Catalog catalog, IReadOnlyDictionary<FormKey, INpcGetter> npcs,
            IReadOnlyDictionary<FormKey, ILeveledNpcGetter> leveled, IReadOnlyDictionary<FormKey, IOutfitGetter>? outfits = null)
        {
            effectiveOutfits = outfits;
            this.catalog = catalog;
            this.npcs = npcs;
            leveledChildren = leveled.Values.SelectMany(l => l.Entries ?? []).Where(e => e.Data is not null)
                .Select(e => e.Data!.Reference.FormKey).ToHashSet();
            editorIds = report?.EditorIds == "available";
            keywordDistributor = report?.KeywordDistributor == true;
            if (report is null || report.Files.Count == 0) return;
            var all = report.Files.SelectMany(f => f.Rules.Select(r => (File: f.Path, Rule: r))).ToList();
            // Outfit identities SPID compares (replacement payloads and form filters) must keep
            // their identity: a recipient-isolation outfit redirect would change SPID's match.
            foreach (var (_, rule) in all)
                foreach (var reference in rule.References.Where(r => r.RecordType == "Outfit" && r.FormKey is not null))
                    ProtectedOutfits.Add(FormKey.Factory(reference.FormKey!));
            if (report.Status is not ("source-matched-7.3.2" or "accepted-major-7"))
            {
                if (report.Status is "incomplete-capture" or "accepted-other-major")
                    Notes.Add($"SPID capture status {report.Status}: its effects are not bounded, so they are not modelled; plugin-only distribution remains.");
                return;
            }
            var memo = new Dictionary<FormKey, bool>();
            var live = all.Where(x => x.Rule.Status is "accepted" or "unverified-filter" or "unverified-reference").ToList();

            // Spell and skin rules apply natively whether or not XEP resolves their forms, so
            // out-of-scope lines count too.
            var applying = all.Where(x => x.Rule.Status is "accepted" or "unverified-filter" or "unverified-reference" or "out-of-scope" && x.Rule.ChancePercent is not <= 0).ToList();
            skinsDistributed = applying.Any(x => x.Rule.Family.EndsWith("skin", StringComparison.Ordinal));
            foreach (var (_, rule) in applying.Where(x => x.Rule.Family.EndsWith("spell", StringComparison.Ordinal)))
            {
                var forms = rule.References.Where(r => r.Field == "Form" && r.FormKey is not null).ToArray();
                if (forms.Length == 0) unresolvedSpells = true;
                foreach (var form in forms) distributedSpells.Add(FormKey.Factory(form.FormKey!));
            }
            var exclusive = all.Where(x => x.Rule.Parser == "exclusive-group")
                .SelectMany(x => x.Rule.References).Where(r => r.FormKey is not null).Select(r => FormKey.Factory(r.FormKey!)).ToHashSet();
            // Native group membership: lines add MATCH and remove NOT entries in file order; keywords
            // SPID creates have no record and are members by name. A group with an unverified
            // entry keeps every member it ever named.
            var exclusiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Unverified members without a candidate record may be any keyword of that name.
            var exclusiveUnverifiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            {
                var groups = new Dictionary<string, (HashSet<string> Members, HashSet<string> Named, bool Uncertain)>(StringComparer.Ordinal);
                static string? Member(SpidReference r) => r.Status == "created-keyword" ? "created:" + r.Identifier.ToLowerInvariant()
                    : r.FormKey is { } key ? key
                    : ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Unverified ? "name:" + r.Identifier.ToLowerInvariant() : null;
                foreach (var (_, rule) in all.Where(x => x.Rule.Parser == "exclusive-group" && x.Rule.Status is "accepted" or "unverified-filter"))
                {
                    var name = rule.Group ?? "";
                    if (!groups.TryGetValue(name, out var group)) groups[name] = group = (new(StringComparer.Ordinal), new(StringComparer.Ordinal), false);
                    foreach (var key in rule.References.Where(r => r.Field == "Match").Select(Member).OfType<string>()) { group.Members.Add(key); group.Named.Add(key); }
                    foreach (var key in rule.References.Where(r => r.Field == "Not").Select(Member).OfType<string>()) group.Members.Remove(key);
                    if (rule.Status == "unverified-filter") groups[name] = group with { Uncertain = true };
                }
                exclusive.Clear();
                foreach (var key in groups.Values.SelectMany(g => g.Uncertain ? g.Named : g.Members))
                    if (key.StartsWith("created:", StringComparison.Ordinal)) exclusiveNames.Add(key["created:".Length..]);
                    else if (key.StartsWith("name:", StringComparison.Ordinal)) exclusiveUnverifiedNames.Add(key["name:".Length..]);
                    else exclusive.Add(FormKey.Factory(key));
            }
            // Keyword and faction producers. Death and linked rules add theirs at death or with a
            // linked parent, so they only may add it; an unresolved keyword is matched by name.
            foreach (var (_, rule) in live.Where(x => x.Rule.Family.EndsWith("keyword", StringComparison.Ordinal) || x.Rule.Family.EndsWith("faction", StringComparison.Ordinal)))
                foreach (var form in rule.References.Where(r => r.Field == "Form"))
                {
                    var key = form.FormKey is { } k ? FormKey.Factory(k) : FormKey.Null;
                    var name = !key.IsNull && RuntimeRecordWinners(catalog).TryGetValue(key, out var kw) ? kw.Record.EditorID ?? form.Identifier : form.Identifier;
                    var id = key.IsNull ? "created:" + name : key.ToString();
                    if (rule.Family.EndsWith("keyword", StringComparison.Ordinal)) keywordNames[id] = name;
                    if (rule.Parser == "default" && rule.Family == "keyword") keywordEntries.Add((id, name, rule.StringFilters));
                    if (rule.ChancePercent <= 0) continue;
                    var producer = new Producer(rule.Parser == "default" ? Evaluable(rule) : null,
                        rule.Parser == "default" && rule.Status == "accepted" && !(rule.ChancePercent < 100) && !exclusive.Contains(key)
                        && !(key.IsNull && exclusiveNames.Contains(form.Identifier))
                        && !exclusiveUnverifiedNames.Contains(name) && !exclusiveUnverifiedNames.Contains(form.Identifier));
                    if (rule.Family.EndsWith("keyword", StringComparison.Ordinal))
                    {
                        if (!keywordProducers.TryGetValue(id, out var list)) keywordProducers[id] = list = [];
                        list.Add(producer);
                    }
                    else if (!key.IsNull)
                    {
                        if (!factionProducers.TryGetValue(key, out var list)) factionProducers[key] = list = [];
                        list.Add(producer);
                    }
                }
            FindKeywordCycles();
            if (keywordProducers.Count + factionProducers.Count > 0)
                Notes.Add($"SPID keyword and faction distributions ({keywordProducers.Count} keywords, {factionProducers.Count} factions) are applied to later filters in native order: keywords first (dependents after the keywords they test), then factions, which keyword and faction rules themselves do not see; exclusive-group, death, linked or unevaluable producers leave a miss undecided (also chanced ones: a failed roll makes NOT filters match).");
            string Bare(SpidRule rule) => rule.Family.Replace("linked-", "").Replace("death-", "");
            Filters? Evaluable(SpidRule rule) => rule.Status == "accepted" && Unevaluable(rule) is null ? FiltersOf(rule) : null;
            // Native: chance is the first filter; NaN passes (no comparison fails it), so only a
            // chance below 100 can fail the roll.
            foreach (var (_, rule) in live.Where(x => x.Rule.Parser == "default" && Bare(x.Rule) is "outfit" or "sleep-outfit" && x.Rule.ChancePercent is not <= 0)
                .OrderBy(x => x.Rule.Type == "Form" ? 1 : 0).ThenBy(x => x.Rule.Index))
            {
                var outfitKey = rule.References.FirstOrDefault(r => r.Field == "Form" && r.FormKey is not null) is { } form ? FormKey.Factory(form.FormKey!) : FormKey.Null;
                var items = (OutfitOf(outfitKey)?.Items ?? []).Select(i => i.FormKey);
                var filters = outfitKey.IsNull || exclusive.Contains(outfitKey) ? null : Evaluable(rule);
                (Bare(rule) == "outfit" ? outfitCandidates : sleepCandidates).Add(new(filters, outfitKey, rule.ChancePercent < 100,
                    items.Any(i => catalog.Armors.ContainsKey(i)), rule.Index, rule.Final, rule.Levels is { } lv && (lv.ActorMin is not null || lv.ActorMax is not null || lv.Skills.Count + lv.Weights.Count > 0)));
            }
            // DeathOutfit and linked outfit rules are ignored (maintainer decision, 17 Oct 2026):
            // neither evidence nor blocking, so they do not unsettle authored or received outfits.
            foreach (var (file, rule) in all)
            {
                var payloadRef = rule.References.FirstOrDefault(r => r.Field == "Form" && r.FormKey is not null);
                if (payloadRef is null)
                {
                    // An unverified item payload may be any record of that name natively.
                    if (rule.Family.Replace("linked-", "").Replace("death-", "") == "item"
                        && rule.References.FirstOrDefault(r => r.Field == "Form") is { } unverified
                        && ReferenceStatus.Classify(unverified.Status) == ReferenceOutcome.Unverified)
                        foreach (var candidate in UnverifiedCandidates(unverified.Identifier, catalog).Where(c => CdfConsumers.ReachesEquipment(c, catalog, memo, [])))
                        {
                            Blockers.Add(new(candidate, $"SPID rule {file}:{rule.Line}", $"unverified item {unverified.Identifier}"));
                            Notes.Add($"SPID {file}:{rule.Line} ({rule.Key}): item {unverified.Identifier} is unverified ({unverified.Status}); enchanted placement into candidate {candidate} is refused.");
                        }
                    continue;
                }
                var payload = FormKey.Factory(payloadRef.FormKey!);
                var bare = rule.Family.Replace("linked-", "").Replace("death-", "");
                var outfit = bare is "outfit" or "sleep-outfit";
                if (bare != "item" && !outfit) continue;
                FormKey[] targets = outfit
                    ? (OutfitOf(payload)?.Items ?? []).Select(i => i.FormKey).ToArray()
                    : [payload];
                if (!targets.Any(t => CdfConsumers.ReachesEquipment(t, catalog, memo, []))) continue;
                var where = $"SPID {file}:{rule.Line} ({rule.Key})";
                // Maintainer decision (17 Oct 2026): direct items, linked forms, DeathOutfit payloads and
                // exclusive-group payloads are neither evidence nor blocking; they are diagnosed only.
                void Ignore(string reason) => Notes.AddInformation($"{where}: {reason}; neither evidence nor a blocker (maintainer decision), so it does not affect placement.");
                if (CdfConsumers.IsEquipment(payload, catalog)) { Ignore($"direct equipment payload {payload} (direct SPID item routes are not distributed)"); continue; }
                void Block(string reason)
                {
                    foreach (var target in targets) Blockers.Add(new(target, $"SPID rule {file}:{rule.Line}", reason));
                    Notes.Add($"{where}: recipients are not bounded offline ({reason}); enchanted placement into its payload is refused.");
                }
                if (rule.Status != "accepted")
                {
                    if (rule.Status is "unverified-filter" or "unverified-reference") Block($"status {rule.Status}");
                    continue;
                }
                // A chance or count that can never pass applies nothing natively: no consumer.
                // NaN passes natively (no comparison fails it), so only a non-positive chance is impossible.
                if (rule.ChancePercent <= 0 || !outfit && rule.CountMax <= 0)
                {
                    Notes.AddInformation($"{where}: never applies natively (chance {rule.ChancePercent}, count {rule.CountMax}); no recipients.");
                    continue;
                }
                if (rule.Parser == "linked") { Ignore("linked form (not modelled)"); continue; }
                if (outfit && rule.Parser == "death") { Ignore("death outfit (applied at death; not modelled)"); continue; }
                if (exclusive.Contains(payload)) { Ignore("payload in an exclusive group (not modelled)"); continue; }
                if (Unevaluable(rule) is { } reason) { Block(reason); continue; }
                if (!outfit && rule.CountMin > rule.CountMax) { Block($"count range {rule.CountMin}-{rule.CountMax} is undefined natively"); continue; }

                rules.Add(new(FiltersOf(rule), payload, outfit, bare == "sleep-outfit", rule.Parser == "death", rule.Index, rule.ChancePercent < 100));
                var runtimeParts = RuntimeParts(rule);
                Notes.AddDiagnostic($"{where}: recipients evaluated offline from its string, form, level and trait filters; payload {payload} is consumed by the matching NPCs"
                    + (runtimeParts.Length > 0 ? $"; undecided per NPC (unknown consumers, refusing dependent placement): {string.Join("; ", runtimeParts)}." : "."),
                    informational: runtimeParts.Length == 0);
            }
            if (rules.Count > 0)
                Notes.Add($"SPID string filters: NPC EditorIDs are {(editorIds ? "compared (po3 EditorID cache available)" : "absent natively (no po3 EditorID cache), as SPID sees them")}; keyword terms see plugin and race keywords and SPID-distributed keywords in native order{(keywordDistributor ? "; with KID projected, any keyword miss is undecided" : "")}. Actor names are the base names (runtime renames and race changes are not modelled).");
        }

        // Natively dropped MATCH/NOT entries (failed references) are omitted, as SPID drops them;
        // the capture keeps the rule only when MATCH still has an entry or never had one.
        private static Filters FiltersOf(SpidRule rule)
        {
            Entry[] Entries(string field) => rule.References.Where(r => r.Field == field && ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Resolved)
                .Select(r => r.Status switch
                {
                    "plugin-filter" => new Entry("Plugin", default, r.Identifier),
                    "resolved-unmatchable" => new Entry("Unmatchable", default, ""),
                    _ => new Entry(r.RecordType ?? "", FormKey.Factory(r.FormKey!), "")
                }).ToArray();
            return new(Entries("Match"), Entries("Not"), Entries("All"), rule.StringFilters, rule.Traits, rule.Levels);
        }

        // Filters whose recipients cannot be bounded at all. Live-state filters (PC-level-multiplier
        // levels, leveled/teammate/dead traits, location, perk and placed-actor forms) are not
        // here: they are undecided per NPC (symbolic), so NPCs failing another filter stay out.
        private static string? Unevaluable(SpidRule rule)
        {
            var filters = rule.References.Where(r => r.Field is "Match" or "Not" or "All").ToArray();
            if (filters.Any(r => ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Unverified)) return "an unverified filter entry";
            return null;
        }

        // The parts of a modelled rule that native SPID decides from live state, named in its note.
        private string[] RuntimeParts(SpidRule rule)
        {
            var parts = new List<string>();
            if (rule.Levels is { } l && (l.ActorMin is not null || l.ActorMax is not null || l.Skills.Count + l.Weights.Count > 0))
                parts.Add("level filters (decided from base-record levels, skills and class weights; PC-level-multiplier levels and auto-calculated skills stay undecided)");
            if (rule.Traits is { } t && (t.Leveled ?? t.Teammate ?? t.StartsDead) is not null) parts.Add("a runtime trait filter (leveled, teammate or dead)");
            foreach (var type in rule.References.Where(r => r.Field is "Match" or "Not" or "All" && r.Status == "resolved" && r.RecordType is "Location" or "Perk" or "ActorCharacter")
                .Select(r => r.RecordType).Distinct())
                parts.Add($"{type} form filters evaluated on the live actor");
            if (keywordDistributor && rule.StringFilters is { } s && s.Match.Count + s.Not.Count + s.All.Count + s.Any.Count > 0)
                parts.Add("keyword terms KID may add in play");
            if (rule.ChancePercent < 100) parts.Add($"chance {rule.ChancePercent}% (counted as received: any chance above 0 is available)");
            return [.. parts];
        }

        // The NPC record that supplies one template-inheritable field: follows the template while
        // the field is inherited; a leveled or missing template leaves it undecided (null).
        private INpcGetter? Source(INpcGetter npc, NpcConfiguration.TemplateFlag field)
        {
            var seen = new HashSet<FormKey>();
            var current = npc;
            while (current.Configuration.TemplateFlags.HasFlag(field) && !current.Template.IsNull)
            {
                if (!seen.Add(current.FormKey) || !npcs.TryGetValue(current.Template.FormKey, out var next)) return null;
                current = next;
            }
            return current;
        }

        private IMajorRecordGetter? Winner(FormKey key)
            => RuntimeRecordWinners(catalog).TryGetValue(key, out var record) ? record.Record : null;

        // Facts of one NPC branch, as native filters read them (base record, branch-resolved traits,
        // factions and inventory). Null members are facts the branch did not resolve.
        private sealed class View(SpidRecipients owner, INpcGetter npc, INpcGetter? traits, INpcGetter? factions, INpcGetter? inventory)
        {
            private readonly Lazy<string?> name = new(() => owner.Source(npc, NpcConfiguration.TemplateFlag.BaseData) is { } s ? s.Name?.String ?? "" : null);
            private readonly Lazy<string[]?> keywords = new(() =>
            {
                if (owner.Source(npc, NpcConfiguration.TemplateFlag.Keywords) is not { } source || traits is null) return null;
                var race = owner.Winner(traits.Race.FormKey) as IRaceGetter;
                return (source.Keywords ?? []).Concat(race?.Keywords ?? []).Select(k => owner.Winner(k.FormKey)?.EditorID).OfType<string>().ToArray();
            });
            public string? Name => name.Value;
            public string[]? Keywords => keywords.Value;
            // Keywords or factions read from a template record, which SPID evaluates as its own base:
            // its distributions may show through, so a miss is undecided.
            public bool KeywordsInherited => owner.Source(npc, NpcConfiguration.TemplateFlag.Keywords) is { } s && s.FormKey != npc.FormKey;
            public bool FactionsInherited => factions is not null && factions.FormKey != npc.FormKey;
            // Distributed keyword/faction results for this branch; Early > 0 while a keyword or
            // faction rule is evaluated (distributed factions are not yet visible there).
            public readonly Dictionary<string, bool?> KeywordMemo = new(StringComparer.Ordinal);
            public readonly HashSet<string> KeywordBusy = new(StringComparer.Ordinal);
            public readonly Dictionary<FormKey, bool?> FactionMemo = [];
            public readonly HashSet<FormKey> FactionBusy = [];
            public int Early;
            // The keyword whose producers are being evaluated (null for later types).
            public string? CurrentKeyword;
            // Evaluating a rule applied after the pass's skins (outfits, sleep outfits, death rules).
            public bool AfterSkins;
            // Identities SPID compares: the template (one level) and the base itself, which a
            // leveled spawn does not expose (own identity undecided for leveled children).
            public FormKey Template => npc.Template.FormKey;
            public bool OwnIdentityUndecided => owner.leveledChildren.Contains(npc.FormKey);
            // A leveled template: the spawned actor also exposes the chosen child's identity,
            // which is runtime state, so an identity miss is undecided.
            public bool TemplateLeveled => !npc.Template.IsNull && !owner.npcs.ContainsKey(npc.Template.FormKey);
            public INpcGetter Npc => npc;
            public INpcGetter? Traits => traits;
            public INpcGetter? Factions => factions;
            public INpcGetter? Inventory => inventory;
        }

        private static bool? AnyOf(IEnumerable<bool?> values)
        {
            var undecided = false;
            foreach (var value in values) { if (value == true) return true; undecided |= value is null; }
            return undecided ? null : false;
        }
        private static bool? AllOf(IEnumerable<bool?> values)
        {
            var undecided = false;
            foreach (var value in values) { if (value == false) return false; undecided |= value is null; }
            return undecided ? null : true;
        }

        private bool? Identity(View view, FormKey key)
            => key == view.Template && !key.IsNull ? true
                : key == view.Npc.FormKey ? (view.OwnIdentityUndecided ? null : true)
                : view.TemplateLeveled ? null : false;

        private bool? Form(View view, Entry entry, HashSet<FormKey> visiting)
        {
            switch (entry.Type)
            {
                case "Unmatchable": return false;
                case "Plugin":
                    {
                        bool Owned(FormKey key) => !key.IsNull && string.Equals(key.ModKey.FileName.String, entry.Plugin, StringComparison.OrdinalIgnoreCase);
                        return Owned(view.Template) ? true : Owned(view.Npc.FormKey) ? (view.OwnIdentityUndecided ? null : true)
                            : view.TemplateLeveled ? null : false;
                    }
                case "NPC": return Identity(view, entry.Key);
                case "Faction":
                    if (view.Factions is null) return null;
                    if (view.Factions.Factions.Any(f => f.Faction.FormKey == entry.Key)) return true;
                    if (!factionProducers.ContainsKey(entry.Key)) return false;
                    // A membership SPID adds (also when nested in a FormList): visible to later types;
                    // inside a keyword or faction rule only through a level-up re-pass, so undecided.
                    var distributed = DistributedFaction(view, entry.Key);
                    return distributed == false ? false : view.Early > 0 || distributed is null || view.FactionsInherited ? null : true;
                // Evaluated on the live actor (also when nested in a FormList).
                case "Perk" or "Location" or "ActorCharacter": return null;
                case "Race": return view.Traits is null ? null : view.Traits.Race.FormKey == entry.Key;
                case "VoiceType": return view.Traits is null ? null : view.Traits.Voice.FormKey == entry.Key;
                // An Armor form filter compares the actor's skin, not its inventory.
                case "Armor": return view.Traits is null || view.AfterSkins && skinsDistributed ? null : view.Traits.WornArmor.FormKey == entry.Key;
                case "Outfit": return view.Inventory is null ? null : view.Inventory.DefaultOutfit.FormKey == entry.Key;
                case "Class": return Source(view.Npc, NpcConfiguration.TemplateFlag.Stats) is { } stats ? stats.Class.FormKey == entry.Key : null;
                case "CombatStyle": return Source(view.Npc, NpcConfiguration.TemplateFlag.AIData) is { } ai ? ai.CombatStyle.FormKey == entry.Key : null;
                case "Spell":
                    {
                        if (Source(view.Npc, NpcConfiguration.TemplateFlag.SpellList) is not { } spells) return null;
                        if ((spells.ActorEffect ?? []).Any(s => s.FormKey == entry.Key)) return true;
                        return unresolvedSpells || distributedSpells.Contains(entry.Key) ? null : false;
                    }
                case "FormList":
                    {
                        if (!visiting.Add(entry.Key) || Winner(entry.Key) is not IFormListGetter list) return false;
                        var result = AnyOf((list.Items ?? []).Select(item => Form(view, new(TypeOf(Winner(item.FormKey)), item.FormKey, ""), visiting)));
                        visiting.Remove(entry.Key);
                        return result;
                    }
                default: return false;
            }
        }

        private static string TypeOf(IMajorRecordGetter? record) => record switch
        {
            IFactionGetter => "Faction", IClassGetter => "Class", ICombatStyleGetter => "CombatStyle", IRaceGetter => "Race",
            IOutfitGetter => "Outfit", INpcGetter => "NPC", IVoiceTypeGetter => "VoiceType", IFormListGetter => "FormList",
            ISpellGetter => "Spell", IArmorGetter => "Armor", IPerkGetter => "Perk", ILocationGetter => "Location",
            IPlacedNpcGetter => "ActorCharacter", _ => ""
        };

        // A string term matches the actor name, a compared EditorID or a keyword EditorID (exact or,
        // for `*` terms, substring; ASCII case-insensitive). Absence is undecided when a keyword
        // SPID itself distributes could supply the term.
        private bool? Term(View view, string term, bool partial)
        {
            if (partial && term.Length == 0) return false;
            bool Hit(string? text) => text is not null && (partial ? text.Contains(term, StringComparison.OrdinalIgnoreCase) : string.Equals(text, term, StringComparison.OrdinalIgnoreCase));
            if (Hit(view.Name) || view.Keywords?.Any(Hit) == true) return true;
            if (editorIds)
            {
                if (!view.Template.IsNull && Hit(Winner(view.Template)?.EditorID)) return true;
                if (Hit(view.Npc.EditorID)) return view.OwnIdentityUndecided ? null : true;
                if (view.TemplateLeveled) return null;
            }
            if (view.Name is null || view.Keywords is null) return null;
            // A keyword SPID adds earlier in the pass (or KID adds, projected) could supply the term.
            var undecided = keywordDistributor;
            foreach (var name in KeywordsMatching(term, partial))
            {
                var distributed = SeenKeyword(view, name, term, partial);
                if (distributed == true) return true;
                undecided |= distributed is null || view.KeywordsInherited;
            }
            return undecided ? null : false;
        }

        // Keyword identities with producers whose EditorID a string term matches.
        private string[] KeywordsMatching(string term, bool partial) => termKeywords.GetOrAdd((term, partial), k => keywordProducers.Keys
            .Where(id => NameMatches(keywordNames[id], k.Term, k.Partial)).ToArray());

        private static bool NameMatches(string name, string term, bool partial)
            => partial ? name.Contains(term, StringComparison.OrdinalIgnoreCase) : string.Equals(name, term, StringComparison.OrdinalIgnoreCase);

        // A distributed keyword as the rule being evaluated sees it. Later types (items, outfits,
        // factions) see the final pass result. A keyword rule sees it only once added: native sorts
        // a rule after a keyword it tests exactly (case-sensitive, MATCH/NOT/ALL; ANY links only its
        // first match), and a cycle member sees fellow members undecided. Otherwise the order is not
        // guaranteed, so only a certain miss is decided.
        private bool? SeenKeyword(View view, string name, string term, bool partial)
        {
            if (view.CurrentKeyword is not { } current) return DistributedKeyword(view, name);
            if (keywordCycle.TryGetValue(current, out var a) && keywordCycle.TryGetValue(name, out var b) && a == b) return null;
            var distributed = DistributedKeyword(view, name);
            return keywordOrder.Contains((current, name)) ? distributed : distributed == false ? false : null;
        }

        // Tarjan components over keyword -> tested-keyword edges (any matching term); members of a
        // component with more than one keyword or a self-test are recorded in keywordCycle.
        private void FindKeywordCycles()
        {
            var ids = keywordEntries.Select(e => e.Id).Distinct(StringComparer.Ordinal).ToArray();
            var nameOf = keywordEntries.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
            string[] Matching(string term, bool partial) => ids.Where(id => NameMatches(nameOf[id], term, partial)).ToArray();
            // A trusted edge needs the exact name to select one record: native looks it up by name
            // among every loaded keyword (distributed or not) and the keywords SPID creates.
            var exactTerms = keywordEntries.Where(e => e.Strings is not null)
                .SelectMany(e => e.Strings!.Match.Concat(e.Strings.Not).Concat(e.Strings.All)).ToHashSet(StringComparer.Ordinal);
            var loaded = new Dictionary<string, int>(StringComparer.Ordinal);
            if (exactTerms.Count > 0)
                foreach (var record in RuntimeRecordWinners(catalog).Values.Select(w => w.Record).OfType<IKeywordGetter>())
                    if (record.EditorID is { } editorId && exactTerms.Contains(editorId)) loaded[editorId] = loaded.GetValueOrDefault(editorId) + 1;
            foreach (var created in ids.Where(id => id.StartsWith("created:", StringComparison.Ordinal)).Select(id => nameOf[id]).Where(exactTerms.Contains))
                loaded[created] = loaded.GetValueOrDefault(created) + 1;
            foreach (var (id, _, strings) in keywordEntries.Where(e => e.Strings is not null))
                foreach (var term in strings!.Match.Concat(strings.Not).Concat(strings.All))
                    if (loaded.GetValueOrDefault(term) == 1 && ids.Where(other => string.Equals(nameOf[other], term, StringComparison.Ordinal)).ToArray() is [var target])
                        keywordOrder.Add((id, target));
            var edges = ids.ToDictionary(n => n, n => keywordEntries.Where(e => e.Id == n && e.Strings is not null).Select(e => e.Strings!)
                .SelectMany(s => s.Match.Concat(s.Not).Concat(s.All).SelectMany(t => Matching(t, false)).Concat(s.Any.SelectMany(t => Matching(t, true))))
                .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var low = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new Stack<string>(); var onStack = new HashSet<string>(StringComparer.Ordinal);
            var next = 0; var component = 0;
            void Visit(string v)
            {
                index[v] = low[v] = next++; stack.Push(v); onStack.Add(v);
                foreach (var w in edges[v])
                {
                    if (!index.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                    else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
                }
                if (low[v] != index[v]) return;
                var members = new List<string>();
                string m;
                do { m = stack.Pop(); onStack.Remove(m); members.Add(m); } while (!string.Equals(m, v, StringComparison.Ordinal));
                if (members.Count > 1 || edges[v].Contains(v, StringComparer.Ordinal))
                    foreach (var member in members) keywordCycle[member] = component;
                component++;
            }
            foreach (var v in edges.Keys) if (!index.ContainsKey(v)) Visit(v);
        }

        // Keyword and faction rules run before the pass's skins (a level-filtered one is re-run
        // on player level-up, after them).
        private bool? Produced(View view, List<Producer> producers)
        {
            view.Early++;
            var afterSkins = view.AfterSkins;
            try
            {
                return AnyOf(producers.Select(p =>
                {
                    view.AfterSkins = Leveled(p.Filters);
                    return Passes(view, p.Filters) switch { true => p.Certain ? true : null, var r => r };
                }));
            }
            finally { view.Early--; view.AfterSkins = afterSkins; }
        }

        private static bool Leveled(Filters? filters) => filters?.Levels is { } l && (l.ActorMin is not null || l.ActorMax is not null || l.Skills.Count + l.Weights.Count > 0);

        // Whether this branch receives a SPID-distributed keyword. A dependency cycle (or a rule
        // testing its own keyword) is undecided, as native order there is not by dependency.
        private bool? DistributedKeyword(View view, string name)
        {
            if (view.KeywordMemo.TryGetValue(name, out var known)) return known;
            if (!view.KeywordBusy.Add(name)) return null;
            var outer = view.CurrentKeyword; view.CurrentKeyword = name;
            var result = Produced(view, keywordProducers[name]);
            view.CurrentKeyword = outer; view.KeywordBusy.Remove(name);
            return view.KeywordMemo[name] = result;
        }

        private bool? DistributedFaction(View view, FormKey key)
        {
            if (view.FactionMemo.TryGetValue(key, out var known)) return known;
            if (!view.FactionBusy.Add(key)) return null;
            // Factions follow the keyword pass: their rules see final keywords.
            var outer = view.CurrentKeyword; view.CurrentKeyword = null;
            var result = Produced(view, factionProducers[key]);
            view.CurrentKeyword = outer; view.FactionBusy.Remove(key);
            return view.FactionMemo[key] = result;
        }

        private bool? Traits(View view, SpidTraits? traits)
        {
            if (traits is null) return true;
            var checks = new List<bool?>();
            if (traits.Sex is { } sex)
                checks.Add(view.Traits is null ? null : view.Traits.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) == (sex == "female"));
            if (traits.Unique is { } unique) checks.Add(view.Npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique) == unique);
            if (traits.Summonable is { } summonable) checks.Add(view.Npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Summonable) == summonable);
            if (traits.Child is { } child)
                checks.Add(view.Traits is null || Winner(view.Traits.Race.FormKey) is not IRaceGetter race ? null
                    : (race.Flags.HasFlag(Race.Flag.Child) || (race.EditorID ?? "").Contains("RaceChild", StringComparison.OrdinalIgnoreCase)) == child);
            // Leveled (CommonLib IsLeveled), teammate and dead are live actor state.
            if ((traits.Leveled ?? traits.Teammate ?? traits.StartsDead) is not null) checks.Add(null);
            return AllOf(checks);
        }

        // SPID's skill index 0-17 (One-Handed ... Enchanting) in Mutagen's skill order.
        private static readonly Skill[] SkillOrder = Enum.GetValues<Skill>().OrderBy(s => (int)s).ToArray();

        // Native level filters read the actor base: GetLevel(), playerSkills and its class's skill
        // weights, with inclusive ranges. A template may supply those base values (Stats), so the
        // own record and the Stats source must agree; a PC-level multiplier and auto-calculated
        // skills depend on live state.
        private bool? Levels(View view, SpidLevels? levels)
        {
            if (levels is null || levels.ActorMin is null && levels.ActorMax is null && levels.Skills.Count + levels.Weights.Count == 0) return true;
            bool? On(INpcGetter npc)
            {
                var checks = new List<bool?>();
                if (levels.ActorMin is not null || levels.ActorMax is not null)
                    checks.Add(npc.Configuration.Level is INpcLevelGetter fixedLevel
                        ? fixedLevel.Level >= (levels.ActorMin ?? 0) && fixedLevel.Level <= (levels.ActorMax ?? ushort.MaxValue) : null);
                if (levels.Skills.Count > 0)
                {
                    var values = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.AutoCalcStats) ? null : npc.PlayerSkills?.SkillValues;
                    checks.AddRange(levels.Skills.Select(r => values is null ? (bool?)null
                        : (values.TryGetValue(SkillOrder[r.Skill], out var v) ? v : (byte)0) is var value && value >= r.Min && value <= r.Max));
                }
                // Without a class, native skips the weight filters (they pass).
                if (levels.Weights.Count > 0 && !npc.Class.IsNull)
                {
                    var weights = (Winner(npc.Class.FormKey) as IClassGetter)?.SkillWeights;
                    checks.AddRange(levels.Weights.Select(r => weights is null ? (bool?)null
                        : (weights.TryGetValue(SkillOrder[r.Skill], out var w) ? w : (byte)0) is var weight && weight >= r.Min && weight <= r.Max));
                }
                return AllOf(checks);
            }
            var own = On(view.Npc);
            if (!view.Npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Stats) || view.Npc.Template.IsNull) return own;
            var inherited = Source(view.Npc, NpcConfiguration.TemplateFlag.Stats) is { } stats ? On(stats) : null;
            return own == inherited ? own : null;
        }

        private bool? Passes(View view, Filters? filters)
        {
            if (filters is null) return null;
            var visiting = new HashSet<FormKey>();
            var parts = new List<bool?>();
            if (filters.Match.Length > 0) parts.Add(AnyOf(filters.Match.Select(e => Form(view, e, visiting))));
            if (filters.Not.Length > 0) parts.Add(AnyOf(filters.Not.Select(e => Form(view, e, visiting))) switch { true => false, false => true, _ => null });
            if (filters.All.Length > 0) parts.Add(AllOf(filters.All.Select(e => Form(view, e, visiting))));
            if (filters.Strings is { } s)
            {
                if (s.Match.Count > 0) parts.Add(AnyOf(s.Match.Select(t => Term(view, t, false))));
                if (s.Not.Count > 0) parts.Add(AnyOf(s.Not.Select(t => Term(view, t, false))) switch { true => false, false => true, _ => null });
                if (s.All.Count > 0) parts.Add(AllOf(s.All.Select(t => Term(view, t, false))));
                if (s.Any.Count > 0) parts.Add(AnyOf(s.Any.Select(t => Term(view, t, true))));
            }
            parts.Add(Levels(view, filters.Levels));
            parts.Add(Traits(view, filters.Traits));
            return AllOf(parts);
        }

        private sealed record Selection(OutfitCandidate? Winner, List<OutfitCandidate> Possible, bool Undecided)
        {
            public bool Receives(ItemRule rule) => Winner?.Index == rule.Index || Possible.Any(p => p.Index == rule.Index);
        }

        // Native first-valid selection for one branch. Default outfits: an actor without a default
        // outfit gets none; a direct-armor candidate may fail SPID's race check. Sleep outfits are
        // set on the base: a candidate equal to the current sleeping outfit is skipped, and no race
        // check applies. A candidate whose roll may fail is a possible receipt and the walk goes on;
        // the first certain one wins and stops it. Any undecided match leaves the rest undecided.
        private Selection Select(View view, bool sleep)
        {
            var outer = view.AfterSkins; view.AfterSkins = true;
            try { return SelectAfterSkins(view, sleep); }
            finally { view.AfterSkins = outer; }
        }

        private Selection SelectAfterSkins(View view, bool sleep)
        {
            var possible = new List<OutfitCandidate>();
            if (view.Inventory is not { } inventory) return new(null, possible, (sleep ? sleepCandidates : outfitCandidates).Count > 0);
            if (!sleep && inventory.DefaultOutfit.IsNull) return new(null, possible, false);
            foreach (var candidate in sleep ? sleepCandidates : outfitCandidates)
            {
                // Only a resolved identity can equal the current sleeping outfit.
                if (sleep && !candidate.Outfit.IsNull && candidate.Outfit == inventory.SleepingOutfit.FormKey) continue;
                var match = Passes(view, candidate.Filters);
                if (match == false) continue;
                if (match is null || !sleep && candidate.DirectArmor) return new(null, possible, true);
                // A received outfit (chanced or not) that a level-up re-pass may replace is no evidence.
                if (Repass(view, candidate, sleep, possible)) return new(null, possible, true);
                if (candidate.Chanced) { possible.Add(candidate); continue; }
                return new(candidate, possible, false);
            }
            return new(null, possible, false);
        }

        // A player level-up re-runs only level-filtered candidates (first valid wins again).
        // Default outfits: a level-filtered winner wins again (earlier ones still fail); a later, or
        // an earlier chanced, level-filtered candidate with another outfit that may match can
        // overwrite a non-Final other winner. Sleep outfits: the winner is now current and skipped,
        // so any other level-filtered candidate with another outfit may replace it.
        private bool Repass(View view, OutfitCandidate candidate, bool sleep, List<OutfitCandidate> earlierChanced)
        {
            // A Final default outfit is protected; a level-filtered non-chanced winner wins again
            // (a chanced one re-rolls on the re-pass and may fail).
            if (!sleep && (candidate.Final || candidate.LevelFiltered && !candidate.Chanced)) return false;
            // Walk the level-filtered candidates in native order, as the re-pass does. Candidates
            // before the winner stay failed: a non-chanced one fails again, and SPID caches a failed
            // roll of a level-filtered entry per NPC level (a changing level is already undecided).
            // A certain valid one
            // stops the walk: with the winner's outfit it keeps it (default outfits; a sleep
            // candidate equal to the current outfit is skipped instead); with another it replaces.
            foreach (var c in sleep ? sleepCandidates : outfitCandidates)
            {
                if (c == candidate || !c.LevelFiltered) continue;
                var before = (sleep ? sleepCandidates : outfitCandidates).IndexOf(c) < (sleep ? sleepCandidates : outfitCandidates).IndexOf(candidate);
                // Sleep: an earlier candidate was either skipped as the current (authored) sleeping
                // outfit, which a re-pass can now apply, or rolled and failed (cached, stays failed).
                if (before && (!sleep || c.Chanced && !(view.Inventory is { } inv && !c.Outfit.IsNull && c.Outfit == inv.SleepingOutfit.FormKey))) continue;
                var match = Passes(view, c.Filters);
                if (match == false) continue;
                var same = !c.Outfit.IsNull && c.Outfit == candidate.Outfit;
                if (same && sleep) continue;
                if (match == true && !c.Chanced) return !same;
                if (!same) return true;
            }
            return false;
        }

        // A base's sleeping outfit reaches NPCs inheriting its inventory in ways not modelled here.
        private static bool InheritedInventory(View view) => view.Inventory is { } inventory && inventory.FormKey != view.Npc.FormKey;

        // Whether SPID replaces this branch's default / sleeping outfit: certainly (the authored
        // outfit no longer applies to the actor), possibly (undecided) or not at all. A chanced
        // replacement leaves the authored outfit in place as a possible consumer.
        public (bool DefaultReplaced, bool DefaultUndecided, bool SleepReplaced, bool SleepUndecided) OutfitReplacement(INpcGetter npc, INpcGetter? traits,
            INpcGetter? factions, INpcGetter? inventory)
        {
            var view = new View(this, npc, traits, factions, inventory);
            var outfit = Select(view, false);
            var sleep = Select(view, true);
            var inherited = InheritedInventory(view);
            view.AfterSkins = true;
            // A chanced candidate counts as received and, as its roll may fail, the authored outfit
            // stays too (maintainer decision, ticket 11: any chance above 0 is available).
            // A certain winner always replaces it, even after chanced candidates (one of them wins).
            return (outfit.Winner is not null,
                outfit.Undecided || otherDefaultReplacers.Any(f => Passes(view, f) != false),
                sleep.Winner is not null && !inherited,
                sleep.Undecided || otherSleepReplacers.Any(f => Passes(view, f) != false) || inherited && sleepCandidates.Count > 0);
        }

        // Payloads one NPC branch may receive from the evaluable rules. An undecided filter makes
        // the receipt not Certain, so the caller records an unknown consumer rather than dropping
        // or trusting it. Outfit rules follow native selection: a certain winner and the chanced
        // candidates before it are receipts (an independent roll, as for items); when the
        // selection is undecided, every modelled rule that may match is an uncertain receipt. A
        // DeathOutfit or linked outfit that may match can replace a received outfit too, so the
        // receipt is then not Certain.
        public IEnumerable<(FormKey Payload, bool Outfit, bool Sleep, bool Death, bool Certain)> Received(INpcGetter npc, INpcGetter? traits, INpcGetter? factions, INpcGetter? inventory)
        {
            var view = new View(this, npc, traits, factions, inventory);
            Selection? outfit = null, sleep = null;
            bool? laterDefault = null, laterSleep = null;
            foreach (var rule in rules)
            {
                // Level-filtered entries are re-run on player level-up, after the pass's skins.
                view.AfterSkins = rule.Outfit || rule.Death || Leveled(rule.Filters);
                var match = Passes(view, rule.Filters);
                if (match == false) continue;
                if (!rule.Outfit) { yield return (rule.Payload, false, false, rule.Death, match == true); continue; }
                var selection = rule.Sleep ? sleep ??= Select(view, true) : outfit ??= Select(view, false);
                view.AfterSkins = true;
                var later = rule.Sleep ? laterSleep ??= otherSleepReplacers.Any(f => Passes(view, f) != false)
                    : laterDefault ??= otherDefaultReplacers.Any(f => Passes(view, f) != false);
                // Each received candidate passed its own re-pass check, so a later undecided point does not
                // unsettle it.
                if (selection.Receives(rule)) yield return (rule.Payload, true, rule.Sleep, false, !later && !(rule.Sleep && InheritedInventory(view)));
                else if (selection.Undecided) yield return (rule.Payload, true, rule.Sleep, false, false);
            }
        }
    }
    // CID ADD rules as reference- or base-scoped container consumers of their payloads.
    private static (List<TypedLootUse> Uses, List<FormKey> Payloads, List<SpidCidBlocker> Blockers, List<string> Notes) CidUses(
        CidInputReport? report, Catalog catalog)
    {
        var uses = new List<TypedLootUse>(); var payloads = new List<FormKey>();
        var blockers = new List<SpidCidBlocker>(); var notes = new List<string>();
        if (report is null || report.Files.Count == 0) return (uses, payloads, blockers, notes);
        if (report.Status is not ("source-matched-2.1.3" or "accepted-major-2"))
        {
            if (report.Status is "incomplete-capture" or "accepted-other-major")
                notes.Add($"CID capture status {report.Status}: its effects are not bounded, so they are not modelled; plugin-only distribution remains.");
            return (uses, payloads, blockers, notes);
        }
        var memo = new Dictionary<FormKey, bool>();
        var rules = report.Files.SelectMany(f => f.Rules.Select(r => (File: f.Path, Rule: r))).ToList();
        FormKey? Key(CidRule rule, string field) => rule.References.FirstOrDefault(r => r.Field == field && r.Status == "resolved" && r.FormKey is not null)
            is { } r ? FormKey.Factory(r.FormKey!) : null;
        // Removals do not block additions: they join the shared removal index (CidRemovals),
        // which erases evidence per recipient as CDF removals do.
        var winners = RuntimeRecordWinners(catalog);
        // References named by predicated reference keys: their locations are looked up once, on demand.
        var predicateReferences = rules.Where(x => x.Rule.ContainerScope == "reference"
                && (Key(x.Rule, "Location") is not null || Key(x.Rule, "LocationKeyword") is not null))
            .Select(x => Key(x.Rule, "Container")).OfType<FormKey>().ToHashSet();
        Dictionary<FormKey, CidReferenceLocation>? referenceLocations = null;

        foreach (var (file, rule) in rules.Where(x => x.Rule.Operation == "add"))
        {
            var where = $"CID {file}:{rule.Line} ({rule.Container})";
            if (Key(rule, "Item") is not { } payload)
            {
                // An unverified item (ambiguous EditorID, no native EditorID cache) may be any
                // record of that name natively: block every candidate that can reach equipment.
                if (rule.References.FirstOrDefault(r => r.Field == "Item") is { } item
                    && ReferenceStatus.Classify(item.Status) == ReferenceOutcome.Unverified)
                    foreach (var candidate in UnverifiedCandidates(item.Identifier, catalog).Where(c => CdfConsumers.ReachesEquipment(c, catalog, memo, [])))
                    {
                        blockers.Add(new(candidate, $"CID rule {file}:{rule.Line}", $"unverified item {item.Identifier}"));
                        notes.Add($"{where}: item {item.Identifier} is unverified ({item.Status}); enchanted placement into candidate {candidate} is refused.");
                    }
                continue;
            }
            if (!CdfConsumers.ReachesEquipment(payload, catalog, memo, [])) continue;
            // Maintainer decision (17 Oct 2026): a direct item is neither evidence nor a blocker.
            if (CdfConsumers.IsEquipment(payload, catalog))
            {
                notes.Add($"{where}: direct equipment payload {payload} (direct CID routes are not distributed); neither evidence nor a blocker (maintainer decision), so it does not affect placement.");
                continue;
            }
            // An addition that can never pass (count 0, chance 0) applies nothing natively.
            if (rule.Status == "accepted" && (rule.Count == 0 || rule.Chance == 0))
            {
                notes.Add($"{where}: never applies natively (count {rule.Count}, chance {rule.Chance}); no recipients.");
                continue;
            }
            if (rule.Status is "unverified-reference")
            {
                blockers.Add(new(payload, $"CID rule {file}:{rule.Line}", "an unverified reference"));
                notes.Add($"{where}: recipients are not bounded (an unverified reference); enchanted placement into its payload is refused.");
                continue;
            }
            if (rule.Status != "accepted" || rule.ContainerScope is not ("base" or "reference") || Key(rule, "Container") is not { } consumer) continue;
            var containerBase = consumer;
            if (rule.ContainerScope == "reference")
            {
                // A reference recipient's family is its CONT base; removal and base-stock
                // lookups key on it. Without the placed winner the base is unknown: block.
                if (!winners.TryGetValue(consumer, out var placed) || placed.Record is not IPlacedObjectGetter p || p.Base.IsNull)
                {
                    blockers.Add(new(payload, $"CID rule {file}:{rule.Line}", "reference base unresolved"));
                    notes.Add($"{where}: reference {consumer} has no resolvable base; enchanted placement into its payload is refused.");
                    continue;
                }
                containerBase = p.Base.FormKey;
            }
            // The native location check is inverted: the rule skips references inside the location.
            var locationKey = Key(rule, "Location");
            var keywordKey = locationKey is null ? Key(rule, "LocationKeyword") : null;
            var predicate = locationKey is { } location ? $"outside:{location}" : keywordKey is { } keyword ? $"keyword:{keyword}" : "";
            var plainRole = $"CID inventory {rule.ContainerScope}";
            if (predicate.Length > 0)
            {
                // A reference key names one instance, so its predicate is decided offline when that
                // reference's current location is record-derived. A base key also reaches
                // instances placed at runtime, which no record describes: it stays symbolic.
                bool? outcome = null;
                if (rule.ContainerScope == "reference")
                {
                    referenceLocations ??= CidReferenceLocations(predicateReferences, catalog);
                    if (referenceLocations.TryGetValue(consumer, out var reference))
                        outcome = CidPredicateApplies(reference, locationKey, keywordKey, catalog);
                }
                if (outcome == false)
                {
                    notes.Add($"{where}: predicate {predicate} skips reference {consumer} (its current location is record-derived); the rule never reaches it.");
                    continue;
                }
                if (outcome == true)
                {
                    notes.Add($"{where}: predicate {predicate} applies at reference {consumer} (record-derived current location); modelled as unconditional.");
                    predicate = "";
                }
                else
                    notes.Add($"{where}: predicate {predicate} is not decided offline ({(rule.ContainerScope == "base" ? "a base key also reaches runtime-placed instances" : "the reference's current location is not record-derived")}); it stays symbolic: only unconditional evidence at the same recipient, or the identical predicate, applies inside it.");
            }
            var role = predicate.Length > 0 ? $"{plainRole} {predicate}" : plainRole;
            // An unconditional use at the same recipient holds wherever the narrower predicate fires.
            // A chance below 100 may withhold the payload: a consumer there, never evidence.
            // Natively a reference's own rules replace its base's rules there; base and reference
            // rows are separate evidence contexts, so base evidence never authorizes a reference.
            // Any chance above 0 counts as received (maintainer decision, ticket 11).
            uses.Add(new(consumer, role, role + ":" + consumer, "container:" + containerBase, true)
                { EstablishedRole = predicate.Length > 0 ? plainRole : "" });
            payloads.Add(payload);
            notes.Add($"{where}: {rule.ContainerScope} consumer {consumer}{(predicate.Length > 0 ? " (" + predicate + ")" : "")} of payload {payload}.");
        }
        if (uses.Count > 0)
            notes.Add("CID routes: variants in CID payload lists are resolved when CID distributes — on each reference's first 3D load per session, again after a respawn reset; a reference already processed this session keeps its earlier resolution until the next save load.");
        return (uses, payloads, blockers, notes);
    }

    // Record-derived current locations of specific placed references, as CID reads them
    // (`GetCurrentLocation`, compared by equality): the winning reference's own location (XLRL)
    // else its winning cell's (XLCN). A reference without either is undecided: the CommonLib
    // wrapper's result for it is unverified (ticket 11), so "no location, rule applies" is never
    // assumed. The parent cell is the last plugin that lists the reference; only the references
    // named by predicated reference keys are looked up.
    private sealed record CidReferenceLocation(FormKey Reference, bool Decided, FormKey Location);

    private static Dictionary<FormKey, CidReferenceLocation> CidReferenceLocations(IReadOnlySet<FormKey> references, Catalog catalog)
    {
        using var timing = PhaseTiming.Measure("cid.reference-locations");
        var parents = new Dictionary<FormKey, FormKey>();
        void Visit(ICellGetter cell)
        {
            foreach (var placed in (cell.Persistent ?? []).Concat(cell.Temporary ?? []))
                if (references.Contains(placed.FormKey)) parents[placed.FormKey] = cell.FormKey;
        }
        foreach (var mod in catalog.Mods)
        {
            foreach (var block in mod.Cells)
                foreach (var sub in block.SubBlocks)
                    foreach (var cell in sub.Cells) Visit(cell);
            foreach (var world in mod.Worldspaces)
            {
                if (world.TopCell is { } top) Visit(top);
                foreach (var block in world.SubCells)
                    foreach (var sub in block.Items)
                        foreach (var cell in sub.Items) Visit(cell);
            }
        }
        var winners = RuntimeRecordWinners(catalog);
        var found = new Dictionary<FormKey, CidReferenceLocation>();
        foreach (var (reference, cellKey) in parents)
        {
            // Winning records decide: a later plugin may override the cell's location or the
            // reference's own location, or delete either.
            if (!winners.TryGetValue(reference, out var placedWinner) || placedWinner.Record is not IPlacedObjectGetter placed || placed.IsDeleted
                || !winners.TryGetValue(cellKey, out var cellWinner) || cellWinner.Record is not ICellGetter cell || cell.IsDeleted)
                continue;
            var location = !placed.LocationReference.FormKey.IsNull ? placed.LocationReference.FormKey : cell.Location.FormKey;
            found[reference] = new(reference, !location.IsNull, location);
        }
        return found;
    }

    // Whether a CID predicate lets the rule apply at one reference (null: undecided offline).
    // Location: applies unless the current location IS the named one (CID's inverted check);
    // keyword: applies when the current location has it.
    private static bool? CidPredicateApplies(CidReferenceLocation reference, FormKey? location, FormKey? keyword, Catalog catalog)
    {
        if (!reference.Decided) return null;
        if (location is { } named) return reference.Location != named;
        if (keyword is { } kw)
            return RuntimeRecordWinners(catalog).TryGetValue(reference.Location, out var record) && record.Record is ILocationGetter loc && !loc.IsDeleted
                ? (loc.Keywords ?? []).Any(k => k.FormKey == kw) : null;
        return true;
    }

    // CID REMOVE and REMOVE ALL as inventory-object removals, the effect CDF removals have:
    // they join the shared removal index, so source, peer and output evidence at the
    // recipients they reach is erased exactly as for CDF. Native order is all additions, then
    // removals, then remove-alls per reference; XEP counts only physical stock (and CDF
    // additions) as the established quantity, so CID additions never offset a removal
    // (conservative). Location predicates on removals are ignored (they could only narrow the
    // erasure), a base key reaches every reference of the base (a reference key replacing it
    // natively could only spare some), and removals of leveled lists are inert natively.
    private static (List<CdfRemoval> Removals, List<string> Notes) CidRemovals(CidInputReport? report, Catalog catalog)
    {
        var removals = new List<CdfRemoval>(); var notes = new List<string>();
        if (report is null || report.Status is not ("source-matched-2.1.3" or "accepted-major-2")) return (removals, notes);
        var memo = new Dictionary<FormKey, bool>();
        var winners = RuntimeRecordWinners(catalog);
        FormKey? Key(CidRule rule, string field) => rule.References.FirstOrDefault(r => r.Field == field && r.Status == "resolved" && r.FormKey is not null)
            is { } r ? FormKey.Factory(r.FormKey!) : null;
        bool Unverified(CidRule rule, string field) => rule.References.FirstOrDefault(r => r.Field == field) is { } r
            && ReferenceStatus.Classify(r.Status) == ReferenceOutcome.Unverified;
        foreach (var (file, rule) in report.Files.SelectMany(f => f.Rules.Select(r => (File: f.Path, Rule: r))))
        {
            if (rule.Operation is not ("remove" or "remove-all") || rule.Status is not ("accepted" or "unverified-reference")) continue;
            if (rule.Chance == 0) continue;
            var where = $"CID {file}:{rule.Line} ({rule.Container})";
            var itemRef = rule.References.FirstOrDefault(r => r.Field == "Item");
            if (itemRef?.Identifier.Contains("XEnchantmentPatcher", StringComparison.OrdinalIgnoreCase) == true)
                notes.Add($"{where}: names XEP's own output; natively it may remove a generated record (under-distribution only, not modelled).");
            // A removal of zero objects removes nothing natively.
            if (rule.Operation == "remove" && rule.Count == 0)
            {
                notes.Add($"{where}: removes 0 objects; no effect.");
                continue;
            }
            // An unverified item may natively be any record carrying that name: each
            // equipment-reaching candidate is removed (lists are inert removal payloads).
            FormKey[] items = Key(rule, "Item") is { } resolved ? [resolved]
                : Unverified(rule, "Item") && itemRef is not null ? UnverifiedCandidates(itemRef.Identifier, catalog).ToArray() : [];
            items = items.Where(i => !catalog.Lists.ContainsKey(i) && CdfConsumers.ReachesEquipment(i, catalog, memo, [])).ToArray();
            if (items.Length == 0) continue;
            var count = rule.Operation == "remove-all" ? uint.MaxValue : rule.Count;
            var provenance = $"CID {file}:{rule.Line}";
            if (rule.Location is not null || rule.LocationKeyword is not null)
                notes.Add($"{where}: its location predicate is ignored for removal (the removal is modelled everywhere it could fire; conservative).");
            // Recipients: the resolved key, or for an unverified container every container
            // base carrying that EditorID.
            var targets = new List<(FormKey Consumer, FormKey Base, bool Reference)>();
            if (Key(rule, "Container") is { } container)
            {
                if (rule.ContainerScope == "reference")
                {
                    if (winners.TryGetValue(container, out var placed) && placed.Record is IPlacedObjectGetter p && !p.Base.IsNull)
                        targets.Add((container, p.Base.FormKey, true));
                    else
                    {
                        // Unknown base: the removal may reach any container holding these items.
                        foreach (var candidate in items) removals.Add(new(candidate, default, default, false, count, provenance) { EveryContainer = true, ReachesUnsafe = true });
                        notes.Add($"{where}: reference {container} has no resolvable base; its removal is applied to every container.");
                        continue;
                    }
                }
                else if (rule.ContainerScope == "base") targets.Add((container, container, false));
            }
            else if (Unverified(rule, "Container"))
                targets.AddRange(catalog.ConsumerRecords.Containers.Values
                    .Where(c => !c.IsDeleted && string.Equals(c.EditorID, rule.Container, StringComparison.OrdinalIgnoreCase))
                    .Select(c => (c.FormKey, c.FormKey, false)));
            if (targets.Count == 0) continue;
            foreach (var candidate in items)
                foreach (var (consumer, containerBase, reference) in targets)
                    removals.Add(new(candidate, consumer, containerBase, reference, count, provenance));
            notes.Add($"{where}: {(rule.Operation == "remove-all" ? "removes every" : $"removes {rule.Count}")} {string.Join(", ", items)} at {string.Join(", ", targets.Select(t => t.Consumer))}; evidence there is erased unless an established quantity survives.");
        }
        return (removals, notes);
    }

    // Records an unverified identifier may name natively: every winning list, weapon or armor
    // carrying that EditorID (ambiguous names have several; a missing native cache, one).
    private static IEnumerable<FormKey> UnverifiedCandidates(string identifier, Catalog catalog)
        => catalog.Lists.Values.Cast<Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter>()
            .Concat(catalog.Weapons.Values).Concat(catalog.Armors.Values)
            .Where(r => string.Equals(r.EditorID, identifier, StringComparison.OrdinalIgnoreCase)).Select(r => r.FormKey).Distinct();
}
