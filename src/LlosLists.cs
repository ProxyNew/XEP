using System.Collections.Immutable;
using System.IO;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Ticket 25: one note per written list LLOS changes at startup, plus one summary line. Lists merged into an
    // identical generated list are not written, so they are not reported.
    private static void ReportLlosStartupChanges(Catalog catalog, StaticEntryProjection projection, IReadOnlyDictionary<FormKey, FormKey> merged)
    {
        var changes = projection.LlosStartupChanges.Where(c => !merged.ContainsKey(c.Key)).OrderBy(c => c.Value.Note, StringComparer.Ordinal).ToList();
        if (changes.Count == 0) return;
        foreach (var (_, change) in changes) catalog.DistributionNotes.AddInformation(change.Note);
        catalog.DistributionNotes.AddInformation($"LLOS startup changes reported for {changes.Count} XEP-written lists: {changes.Sum(c => c.Value.Swaps)} swaps, "
            + $"{changes.Sum(c => c.Value.Removals)} removals, {changes.Sum(c => c.Value.Insertions)} insertions.");
    }

    // LLOS changes lists at DataLoaded, after plugin loading and before Papyrus.
    // Like SkyPatcherLists, this is an analytical overlay: decisions read the
    // effective lists, while written records keep the physical baseline so LLOS
    // applies each change exactly once in game.
    private sealed class LlosLists
    {
        // One batch rule (Manager::ProcessBatchProtocol) on a matched target entry.
        // Once-per-list protocols (0–3, 100–104) fire at the first matching entry;
        // the others at every occurrence. Swaps and removals drop the occurrence.
        private sealed record Insert(string Location, FormKey Target, FormKey Payload, ushort Protocol,
            short Count, short Level, bool UseAll)
        {
            public bool Once => Protocol is <= 3 or (>= 100 and <= 104) or 300;
            public bool Removes => Protocol >= 100;
            public bool Inserts => Protocol is not (104 or 154);
            // A Chance strictly between the native draw's bounds ([0, 99.99)): each
            // match may or may not fire at startup.
            public bool Conditional { get; init; }
            // Chance at or below zero: never fires, but occupies its native rule position.
            public bool Inert { get; init; }
            public short MaxCount { get; init; } = Count;
            public short MaxLevel { get; init; } = Level;
            bool InheritsLevel => Protocol < 200 && Protocol % 50 is 2 or 3;
            bool InheritsCount => Protocol < 200 && Protocol % 50 is 1 or 3;
            // The authored level (count) is drawn from [Level, MaxLevel] per insertion.
            public bool RangedLevel => !InheritsLevel && MaxLevel != Level;
            public bool RangedCount => !InheritsCount && MaxCount != Count;
            public LeveledItemEntry Entry(ProjectedStaticEntry matched, Outcome outcome = Outcome.Established) => new()
            {
                Data = new()
                {
                    Reference = new FormLink<IItemGetter>(Payload),
                    // Within each family, +1 inherits count, +2 level, +3 both. A drawn
                    // level is the latest possible one when establishing availability.
                    Count = InheritsCount ? matched.Count : Count,
                    Level = InheritsLevel ? matched.Level ?? 1 : outcome == Outcome.PossibleLow ? Level : MaxLevel
                }
            };
        }

        // Established: what every startup produces (conditional insertions absent,
        // conditional removals applied, drawn levels at their latest). Possible:
        // conditional insertions fire everywhere and removals do not, at either
        // drawn-level extreme; used only to check output composition.
        internal enum Outcome { Established, PossibleLow, PossibleHigh }
        private bool hasUncertain;

        // Native draws Chance from [0, 99.99): values at or above 99.99 always fire,
        // values at or below zero (or NaN) never do.
        private static bool? Fires(float? chance) => chance is null ? null
            : float.IsNaN(chance.Value) || chance.Value <= 0 ? false : chance.Value >= 99.99f ? true : null;

        private readonly Dictionary<FormKey, Insert[]> byTarget = [];
        // Keyword-targeted batch rules (keyword maps), matched through each entry's
        // keywords in record order after its explicit-form rules.
        private readonly Dictionary<FormKey, Insert[]> byKeyword = [];
        private readonly Dictionary<FormKey, string> withheldKeywords = [];
        private IReadOnlyDictionary<FormKey, FormKey[]> keywordOrder = new Dictionary<FormKey, FormKey[]>();
        // A generated variant copies its source's keywords; the writer supplies the
        // source of each generated occurrence while it projects one plan.
        [ThreadStatic] private static Func<int, FormKey>? generatedSources;
        public static IDisposable GeneratedSources(Func<int, FormKey> sources)
        {
            var prior = generatedSources;
            generatedSources = sources;
            return new SourceScope(prior);
        }
        private sealed class SourceScope(Func<int, FormKey>? prior) : IDisposable
        {
            public void Dispose() => generatedSources = prior;
        }
        // Ticket 25: LLOS's known startup change to one XEP-written list, reported per list. The writer's projection
        // collects them; planning-time checks (WritingProblem, GeneratedProblem) run without a sink and report nothing.
        internal sealed record StartupChange(string Note, int Swaps, int Removals, int Insertions);
        [ThreadStatic] private static Dictionary<FormKey, StartupChange>? startupChanges;
        public static IDisposable CollectStartupChanges(Dictionary<FormKey, StartupChange> sink)
        {
            var prior = startupChanges;
            startupChanges = sink;
            return new ChangeScope(prior);
        }
        private sealed class ChangeScope(Dictionary<FormKey, StartupChange>? prior) : IDisposable
        {
            public void Dispose() => startupChanges = prior;
        }
        // Codex review round 1: entries XEP appends to a written list after its projection (runtime AddForm insertions
        // and the Revert selector), per list. The writer's projection and its planning check supply them.
        [ThreadStatic] private static Func<FormKey, int>? laterAppends;
        public static IDisposable LaterAppends(Func<FormKey, int> appends)
        {
            var prior = laterAppends;
            laterAppends = appends;
            return new AppendScope(prior);
        }
        private sealed class AppendScope(Func<FormKey, int>? prior) : IDisposable
        {
            public void Dispose() => laterAppends = prior;
        }
        private FormKey[] KeywordsOf(ProjectedStaticEntry entry)
        {
            if (keywordOrder.Count == 0) return [];
            var key = entry.Reference.GeneratedOccurrence is not { } occurrence ? entry.Reference.Existing
                : occurrence < 0 ? FormKey.Null
                : generatedSources?.Invoke(occurrence)
                    ?? throw new InvalidDataException("LLOS keyword rules: a generated variant's source is unknown. No output was installed.");
            return key.IsNull ? [] : keywordOrder.GetValueOrDefault(key) ?? [];
        }
        public bool KeywordMatches(FormKey item) => keywordOrder.ContainsKey(item);
        // The writer projects variant keywords from the source; verify the record.
        public string? GeneratedKeywordProblem(FormKey source, IMajorRecordGetter generated)
        {
            if (keywordOrder.Count == 0) return null;
            var expected = keywordOrder.GetValueOrDefault(source) ?? [];
            var actual = generated is IKeywordedGetter<IKeywordGetter> keyworded
                ? (keyworded.Keywords ?? []).Select(k => k.FormKey).Where(k => byKeyword.ContainsKey(k) || withheldKeywords.ContainsKey(k)).ToArray() : [];
            return expected.Where(k => byKeyword.ContainsKey(k) || withheldKeywords.ContainsKey(k)).SequenceEqual(actual) ? null
                : "LLOS keyword rules: generated keywords differ from the source membership used by distribution analysis.";
        }
        // Focus edits (Manager::ProcessFocusLeveledList) keyed by the named list:
        // removals by entry form (300/350), then insertions appended (200).
        private sealed record Focus(List<Insert> Inserts, Dictionary<FormKey, List<Insert>> Removals);
        private readonly Dictionary<FormKey, Focus> focus = [];
        private readonly Dictionary<FormKey, ILeveledItemGetter> effective = [];
        private readonly Dictionary<FormKey, int> inserted = [];
        private readonly Dictionary<FormKey, string> withheldTargets = [];
        // Swaps that keep their predecessor's slot: a unit-count, metadata-free
        // occurrence replaced by a unit-count payload that survives the cap.
        private readonly Dictionary<FormKey, HashSet<(FormKey Source, FormKey Predecessor)>> replacementPeers = [];
        public bool HasReplacementPeer(FormKey list, FormKey source, FormKey predecessor)
            => replacementPeers.TryGetValue(list, out var peers) && peers.Contains((source, predecessor));
        public Dictionary<FormKey, ILeveledItemGetter> Physical { get; } = [];
        // Lists LLOS creates at startup (protocol 170): analytical nodes under a
        // planning ModKey. They are never written; Locations name their rules.
        public static readonly ModKey GeneratedMod = ModKey.FromFileName("__XEP_LlosGenerated.esp");
        internal sealed record GeneratedGroup(string Identity, FormKey Target, bool Keyword, int Instruction, List<string> Locations, ushort Protocol = 170)
        {
            // Protocol 450 lists replace outfit entries; they are never in leveled lists.
            public bool Outfit => Protocol == 450;
        }
        // Outfit entries LLOS swaps for an outfit-domain generated list (protocol 450).
        private readonly Dictionary<FormKey, FormKey> outfitSwaps = [];
        // Generated groups with an unsupported rule are never built; outfits holding a
        // withheld 450 target lose their route instead of keeping an authorization
        // native LLOS replaces at startup.
        private readonly HashSet<(ushort Protocol, bool Keyword, FormKey Target)> generatedWithheld = [];
        private readonly Dictionary<FormKey, string> outfitWithheld = [];
        public string? OutfitWithheld(FormKey item) => outfitWithheld.GetValueOrDefault(item);
        // Payloads of withheld outfit groups gain consumers XEP cannot see.
        private readonly HashSet<FormKey> withheldOutfitPayloads = [];
        public IReadOnlyCollection<FormKey> WithheldOutfitPayloads => withheldOutfitPayloads;
        public FormKey? OutfitSwap(FormKey item) => outfitSwaps.TryGetValue(item, out var list) ? list : null;
        // The physical target an outfit-domain generated list replaces (written outfits keep it).
        public FormKey? OutfitTarget(FormKey list) => generated.GetValueOrDefault(list) is { Outfit: true } group ? group.Target : null;
        // Container and NPC-inventory entries LLOS swaps (protocol 750). Native
        // ProcessBatchContainerLite edits only records with fewer than 64 entries and
        // keeps each entry's count and owner.
        public const int ContainerSwapLimit = 64;
        private readonly Dictionary<FormKey, FormKey> containerSwaps = [];
        public FormKey? ContainerSwap(FormKey item) => containerSwaps.TryGetValue(item, out var list) ? list : null;
        public bool HasContainerSwaps => containerSwaps.Count > 0;
        // Written containers and NPCs keep the physical target; LLOS swaps it at startup.
        // Withheld 750 targets: their entries are blanked in effective small records, as
        // native LLOS replaces them with a list XEP does not model.
        private readonly HashSet<FormKey> containerWithheld = [];
        public bool ContainerWithheld(FormKey item) => containerWithheld.Contains(item);
        public bool HasContainerEdits => containerSwaps.Count > 0 || containerWithheld.Count > 0;
        // Written containers and NPCs keep the physical entries LLOS swaps at startup:
        // each generated or blanked slot is restored from the winning plugin record.
        // NPCs whose written inventory XEP copied from a provider's plugin record: they
        // hold no generated or blanked entry, and their own plugin slots do not apply.
        private readonly HashSet<FormKey> physicalInventories = [];
        public void MarkPhysicalInventory(FormKey npc) => physicalInventories.Add(npc);
        public void RestoreContainerTargets(IEnumerable<ISkyrimMod> patches, IReadOnlyList<ISkyrimModGetter> mods)
        {
            if (!HasContainerEdits) return;
            var physicalContainers = mods.SelectMany(m => m.Containers).GroupBy(c => c.FormKey).ToDictionary(g => g.Key, g => g.Last().Items);
            var physicalNpcs = mods.SelectMany(m => m.Npcs).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.Last().Items);
            // A blank is an edit only where the plugin slot names a withheld target;
            // authored empty slots stay as they are (native skips them).
            bool Edited(ContainerEntry e, IReadOnlyList<IContainerEntryGetter>? physical, int i) => containerSwaps.ContainsValue(e.Item.Item.FormKey)
                || (e.Item.Item.FormKey.IsNull && physical is not null && i < physical.Count && containerWithheld.Contains(physical[i].Item.Item.FormKey));
            void Restore(IList<ContainerEntry>? items, IReadOnlyList<IContainerEntryGetter>? physical, string? name)
            {
                if (items is null || !items.Where((e, i) => Edited(e, physical, i)).Any()) return;
                // Native swaps only records below the limit; planning keeps written records below it.
                if (items.Count >= ContainerSwapLimit || physical is null)
                    throw new InvalidDataException($"LLOS container swap: written record {name} no longer matches LLOS's admission. No output was installed.");
                for (var i = 0; i < items.Count && i < physical.Count; i++)
                    if (Edited(items[i], physical, i)) items[i] = physical[i].DeepCopy();
                if (items.Any(e => containerSwaps.ContainsValue(e.Item.Item.FormKey))) throw new InvalidDataException($"LLOS container swap: written record {name} holds a generated entry XEP cannot restore. No output was installed.");
            }
            foreach (var mod in patches)
            {
                foreach (var container in mod.Containers) Restore(container.Items, physicalContainers.GetValueOrDefault(container.FormKey), container.EditorID);
                foreach (var npc in mod.Npcs.Where(n => !physicalInventories.Contains(n.FormKey))) Restore(npc.Items, physicalNpcs.GetValueOrDefault(npc.FormKey), npc.EditorID);
            }
        }
        // Adding an entry to a swapped small record would lift it to the native limit.
        public string? ContainerAppendProblem(IReadOnlyList<IContainerEntryGetter>? effective, IReadOnlyList<IContainerEntryGetter>? physical)
            => effective is { Count: >= ContainerSwapLimit - 1 } && (effective.Any(e => containerSwaps.ContainsValue(e.Item.Item.FormKey))
                || (physical ?? []).Any(e => containerWithheld.Contains(e.Item.Item.FormKey)))
                ? "LLOS swaps an entry of this inventory at startup only while it has fewer than 64 entries; another entry would stop that swap" : null;
        private readonly Dictionary<FormKey, GeneratedGroup> generated = [];
        public bool IsGenerated(FormKey list) => generated.ContainsKey(list);
        public bool HasGenerated => generated.Count > 0;
        // A copy of a generated list on the path is a real list of its forms, and its
        // parent's copy references that clone, so no generated reference remains. A
        // form-target generated list held off the path is written back as its target
        // (Unswap). A keyword-target one has no single form to write back.
        public bool TouchesGenerated(IReadOnlyDictionary<FormKey, ILeveledItemGetter> lists, IEnumerable<FormKey> path)
        {
            if (generated.Count == 0) return false;
            var nodes = path.ToHashSet();
            // A copied generated node is batch-processed at startup, unlike G itself:
            // no rule may match its contents. References to copied path nodes are
            // redirected to their clones; other generated references must unswap.
            return nodes.Any(key => (IsGenerated(key) && (rangedGenerated.Contains(key) || (lists[key].Entries ?? []).Any(e => e.Data is { } data && Targeted(data.Reference.FormKey))))
                || (lists.GetValueOrDefault(key)?.Entries ?? []).Any(e => e.Data is { } data && !nodes.Contains(data.Reference.FormKey)
                    && IsGenerated(data.Reference.FormKey) && !CanUnswap(data.Reference.FormKey)));
        }
        // A generated list with a drawn level: a written copy would freeze one draw
        // (the established latest level) and could delay that entry for its consumers.
        private readonly HashSet<(ushort Protocol, bool Keyword, FormKey Target)> rangedGroups = [];
        private readonly HashSet<FormKey> rangedGenerated = [];
        private bool Targeted(FormKey form) => byTarget.ContainsKey(form) || withheldTargets.ContainsKey(form) || keywordOrder.ContainsKey(form);
        // A written-back target changes what SkyPatcher sees before LLOS creates
        // its generated list; unswapping it needs a joint generated-group proof.
        private bool skyPatcherInputs;
        public bool SkyPatcherBeforeLlos { get; private init; }
        // Writing T back is exact only when the reinsertion is the only live rule on T:
        // another rule could insert beside T again or read the count/level the swap
        // forced to 1. A keyword-target list has no single form to write back.
        private bool CanUnswap(FormKey list) => !skyPatcherInputs && generated.GetValueOrDefault(list) is { Keyword: false } group
            && !keywordOrder.ContainsKey(group.Target)
            && (byTarget.GetValueOrDefault(group.Target) ?? []).All(rule => rule.Inert || rule.Payload == list);
        internal IReadOnlyDictionary<FormKey, GeneratedGroup> Generated => generated;
        public Dictionary<FormKey, string> Problems { get; } = [];
        // Lists whose startup entries depend on a Chance draw. The established view
        // treats conditional removals as applied, so checks that rely on an entry
        // being absent (isolation, sole entry, composition) must see them as
        // runtime-controlled instead.
        public Dictionary<FormKey, string> ConditionalMembership { get; } = [];
        public DiagnosticMessages Notes { get; } = [];
        public bool ImportDisabled { get; private set; }
        public bool HasInputs => byTarget.Count > 0 || byKeyword.Count > 0 || focus.Count > 0 || withheldTargets.Count > 0 || withheldKeywords.Count > 0 || Problems.Count > 0;
        // Captured EditorID references: generated records must not shadow them.
        public HashSet<string> ReferencedEditorIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Changes(FormKey list) => Physical.ContainsKey(list) || Problems.ContainsKey(list);
        // Only a changed list, or planned content holding a rule target, can have
        // an LLOS composition problem; every other list is skipped cheaply.
        public bool MayAffect(FormKey list, IEnumerable<ProjectedStaticEntry> entries) => Changes(list) || focus.ContainsKey(list) || IsGenerated(list)
            || entries.Any(e => e.Reference.GeneratedOccurrence is null && IsGenerated(e.Reference.Existing))
            || entries.Any(e => e.Reference.GeneratedOccurrence is null && (byTarget.ContainsKey(e.Reference.Existing) || withheldTargets.ContainsKey(e.Reference.Existing))
                || KeywordsOf(e).Any(k => byKeyword.ContainsKey(k) || withheldKeywords.ContainsKey(k)));

        public static LlosLists Read(LlosInputReport? inputs, Dictionary<FormKey, ILeveledItemGetter> lists, SkyPatcherLists? skyPatcher)
        {
            using var timing = PhaseTiming.Measure("llos.read");
            // Independent in-game B/F chains and B/C controls establish this pair's
            // order: .scratch/framework-startup-order/assets/result.md. Do not
            // extend a single installed-build observation to untested DLLs.
            var result = new LlosLists
            {
                skyPatcherInputs = skyPatcher?.HasInputs == true,
                SkyPatcherBeforeLlos = string.Equals(inputs?.DllSha256, LlosInputs.SourceMatchedDll, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(inputs?.DllFileName, "w_LeveledListObjectSwapper.dll", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(skyPatcher?.DllSha256, "72CE9BAEC04CB02C95D0B995989F16F7B5C47AABE841AA55BFE6AC30E8356A49", StringComparison.OrdinalIgnoreCase)
            };
            if (inputs is null || inputs.Status is "absent" or "missing-dll") return result;
            foreach (var reference in inputs.Files.Where(f => f.Status == "active").SelectMany(f => f.Rules).SelectMany(r => r.References))
                if (reference.Identifier.Length > 0 && !reference.Identifier.Contains('~')) result.ReferencedEditorIds.Add(reference.Identifier);
            var rules = inputs.Files.Where(f => f.Status == "active").SelectMany(f => f.Rules.Select(r => (File: f, Rule: r)))
                .Where(p => p.Rule.Status == "accepted").ToList();
            if (inputs.Status == "incomplete-capture")
                return result.WithoutImport(["LLOS input capture is incomplete."]);
            // Checked before the early return below: a rule that native would throw on disables the import even when
            // no rule is accepted (its target may be an ambiguous EditorID that binds to a candidate).
            if (inputs.Files.SelectMany(f => f.Rules).Any(r => r.Status == "native-exception"))
                return result.WithoutImport(["LLOS 1.3.0 would throw while loading its configuration."]);
            // A rejected rule whose reference may still bind natively is not inert, so it
            // keeps the analysis going even without accepted rules.
            if (rules.Count == 0 && !inputs.Files.Where(f => f.Status == "active").SelectMany(f => f.Rules).Any(r => r.Status is "rejected" or "invalid-target"
                && r.References.Any(x => x.Status is "editorid-cache-unavailable" or "merge-mapping-unavailable" or "ambiguous-editorid" or "outside-local-range")))
                return result;

            static FormKey Key(LlosReference reference) => FormKey.Factory(reference.FormKey!);
            result.keywordOrder = inputs.KeywordOrder;
            // A reference XEP cannot bind (EditorID cache, MergeMapper, ambiguous or
            // full load-order IDs) may still bind natively: such a rule is not inert.
            static bool Uncertain(LlosReference reference) => reference.Status is "editorid-cache-unavailable" or "merge-mapping-unavailable"
                or "ambiguous-editorid" or "outside-local-range";
            var uncertainRules = inputs.Files.Where(f => f.Status == "active").SelectMany(f => f.Rules.Select(r => (File: f, Rule: r)))
                .Where(p => p.Rule.Status is "rejected" or "invalid-target" && p.Rule.References.Any(Uncertain)).ToList();
            var keywordRules = new List<Insert>();
            var generatedRules = new List<(string Location, FormKey Target, bool Keyword, FormKey Payload, short Count, short Level, byte UseAll, ushort Protocol)>();
            var unbounded = new List<string>();
            var inserts = new List<Insert>();
            // Supported-family rules that cannot be evaluated offline yet, and other
            // families not yet interpreted: the lists they touch are withheld.
            var withheld = new Dictionary<FormKey, string>();
            var withheldLists = new Dictionary<FormKey, string>();
            // Ticket 23: a rule XEP cannot interpret (an unanalysed family, or an ambiguous target) is narrowed to its
            // targets only in the simplest case: an ordinary batch item protocol matched by form, every target an item
            // record, and an item payload or none. Its footprint is then the lists holding those items. NarrowProblem
            // gives the reason for the whole-run fallback, or null when the rule is narrowed. A list target or payload
            // reaches consumers and descendant lists, a keyword target or payload may be distributed by KID, and the
            // direct-list (200-350) and generated-group (170, 450, 750, 1070-1073) protocols edit records beyond the lists
            // holding their target.
            // With unverified ordering or unsupported SkyPatcher contents, a
            // removed target can still have reached LLOS. Otherwise the current
            // lists are precisely the entries SkyPatcher leaves for LLOS.
            var skyPatcherPresent = skyPatcher is { } sky && (sky.HasInputs || sky.Problems.Count > 0 || sky.ImportDisabled);
            string? NarrowProblem(string subject, ushort protocol, IEnumerable<int> targetTypes, LlosReference payload)
            {
                var types = targetTypes.ToArray();
                if (types.Contains(8)) return $"{subject} is a keyword, which KID can distribute at runtime.";
                if (protocol is not (<= 3 or (>= 50 and <= 53) or (>= 100 and <= 104) or (>= 150 and <= 154)))
                    return $"Protocol {protocol} is not an ordinary batch item protocol (0-3, 50-53, 100-104, 150-154), so it is not narrowed to the lists holding its target.";
                if (types.Contains(3)) return $"{subject} is a leveled list, whose consumers and descendant lists have no bounded footprint.";
                if (types.Any(t => t is not (1 or 2))) return $"{subject} is not an item record.";
                if (payload.Status is not ("absent" or "resolved")) return UnboundPayload(payload);
                if (payload.Status == "resolved")
                {
                    if (payload.NativeType == 8) return $"Payload {payload.Identifier} is a keyword, distributed by KID at runtime.";
                    if (payload.NativeType == 3) return $"Payload list {payload.Identifier} ({Key(payload)}) is not narrowed: its descendant lists have no bounded footprint.";
                    if (payload.NativeType is not (1 or 2)) return $"Payload {payload.Identifier} ({Key(payload)}) is not an item record.";
                }
                if (skyPatcherPresent && !result.SkyPatcherBeforeLlos)
                    return "SkyPatcher leveled-list inputs are present, and their native ordering with LLOS is unverified: a SkyPatcher edit may hide the target from the lists XEP sees holding it.";
                if (skyPatcherPresent && (skyPatcher!.ImportDisabled || skyPatcher.Problems.Count > 0))
                    return "SkyPatcher's earlier list contents are unproved, so the target's LLOS footprint cannot be narrowed.";
                return null;
            }
            static string UnboundPayload(LlosReference payload) => $"Payload {payload.Identifier} ({payload.Status}) has no bounded footprint.";
            // A narrowed rule withholds each target and its item payload: lists holding them keep plugin contents and
            // refuse XEP output, and XEP places no variant of them.
            void Narrow(IEnumerable<FormKey> targets, LlosReference payload, string why)
            {
                foreach (var target in targets) withheld.TryAdd(target, why);
                if (payload.Status == "resolved") withheld.TryAdd(Key(payload), why);
            }
            static string PayloadNote(LlosReference payload) => payload.Status == "resolved"
                ? $" Payload {Key(payload)} is withheld: lists holding it refuse XEP output." : "";
            // Leveled-list payloads of accepted rules that can insert, by the form or keyword a batch rule matches
            // (listPayloads), the list a focus rule names (focusPayloads) and the generated group they join
            // (generatedPayloads). Recorded before any per-rule withholding, so WithholdHiddenPayloads also sees rules whose
            // own effect is withheld. A batch rule that never fires (Chance at or below zero) or only removes is not recorded.
            var listPayloads = new Dictionary<FormKey, List<(FormKey Payload, string Location, bool UseAll)>>();
            var focusPayloads = new Dictionary<FormKey, List<(FormKey Payload, string Location, bool UseAll)>>();
            var generatedPayloads = new List<((ushort Protocol, bool Keyword, FormKey Target) Group, FormKey Payload, string Location)>();
            bool IsList(LlosReference payload) => payload.FormKey is not null && lists.ContainsKey(Key(payload));
            void RecordPayload(Dictionary<FormKey, List<(FormKey Payload, string Location, bool UseAll)>> index, FormKey key, LlosReference payload, string location,
                LlosRule? batch = null)
            {
                if (!IsList(payload) || batch is not null && (Fires(batch.Chance) is false || batch.Protocol is 104 or 154)) return;
                if (!index.TryGetValue(key, out var recorded)) index[key] = recorded = [];
                // Focus insertions ignore UseAll; a batch rule without it skips UseAll lists.
                recorded.Add((Key(payload), location, batch is null || batch.UseAll != 0));
            }
            foreach (var (file, rule) in rules)
            {
                var location = $"{file.Path} [{rule.Section}]";
                var target = rule.References[0];
                var payload = rule.References[1];
                switch (rule.Family)
                {
                    case "single-insert" or "multi-insert" or "single-swap" or "multi-swap" or "single-remove" or "multi-remove" when rule.Dispatch == "batch":
                        // Count/Level are drawn per insertion only where the protocol
                        // does not inherit them; Chance is drawn per matched entry.
                    {
                        RecordPayload(listPayloads, Key(target), payload, location, rule);
                        if (rule.MaxCount > short.MaxValue || rule.MaxLevel > short.MaxValue)
                        { withheld.TryAdd(Key(target), $"LLOS {location}: count/level exceeds the supported entry range."); continue; }
                        // Removal-only rules (104, 154) need no valid insert form.
                        // A never-firing rule still holds its native rule position (255-rule cap).
                        inserts.Add(Ranged(new(location, Key(target), payload.FormKey is null ? FormKey.Null : Key(payload), rule.Protocol,
                            (short)rule.MinCount, (short)rule.MinLevel, rule.UseAll != 0) { Inert = Fires(rule.Chance) is false }, rule));
                        break;
                    }
                    case "generated-list-swap" or "outfit-generated-swap" or "container-generated-swap" or "combined-generated-swap":
                    {
                        // Combined protocols (1070-1073) re-dispatch each accepted component as
                        // its standalone rule (HandleCombinedProtocol); input lists the accepted ones.
                        var components = rule.Family == "combined-generated-swap"
                            ? rule.Dispatch["combined:".Length..].Split('+').Select(ushort.Parse).ToArray() : [rule.Protocol];
                        foreach (var protocol in components)
                        {
                            if (IsList(payload)) generatedPayloads.Add(((protocol, false, Key(target)), Key(payload), location));
                            // Once/EachTime (UseAll 8/16) reinsert a form target into its group. An outfit or container
                            // group replaces the target in records XEP does not read as list edges, so a dropped group
                            // hides that the target list reaches them.
                            if (protocol is 450 or 750 && (rule.UseAll & 24) != 0 && lists.ContainsKey(Key(target)))
                                generatedPayloads.Add(((protocol, false, Key(target)), Key(target), location));
                            // Chance is ignored; Count/Level are drawn once at INI read time.
                            if (rule.MaxCount > short.MaxValue || rule.MaxLevel > short.MaxValue)
                            {
                                // The whole generated group is withheld: a partial list would differ natively.
                                var why = $"LLOS {location}: count/level exceeds the supported entry range.";
                                withheld.TryAdd(Key(target), why);
                                result.generatedWithheld.Add((protocol, false, Key(target)));
                                // Reinsert bits may also place the target in the unmodelled list.
                                if (protocol == 450) result.outfitWithheld.TryAdd(Key(target), why);
                                if (protocol is 450 or 750) result.withheldOutfitPayloads.UnionWith([Key(payload), Key(target)]);
                                if (protocol == 750) result.containerWithheld.Add(Key(target));
                                continue;
                            }
                            generatedRules.Add((location, Key(target), false, Key(payload), (short)rule.MinCount, (short)rule.MaxLevel, rule.UseAll, protocol));
                            if (rule.MinLevel != rule.MaxLevel) result.rangedGroups.Add((protocol, false, Key(target)));
                        }
                        break;
                    }
                    case "keyword-single-insert" or "keyword-multi-insert" or "keyword-single-swap" or "keyword-multi-swap"
                        or "keyword-single-remove" or "keyword-multi-remove" when rule.Dispatch == "keyword-batch":
                    {
                        // Same batch semantics, matched through entry keywords. KID can add
                        // the keyword to further forms at runtime: an unbounded footprint.
                        if (inputs.RuntimeKeywords.Contains(Key(target)))
                        { unbounded.Add($"LLOS {location}: keyword {rule.References[0].Identifier} is distributed by KID at runtime."); break; }
                        RecordPayload(listPayloads, Key(target), payload, location, rule);
                        if (rule.MaxCount > short.MaxValue || rule.MaxLevel > short.MaxValue)
                        { result.withheldKeywords.TryAdd(Key(target), $"LLOS {location}: count/level exceeds the supported entry range."); continue; }
                        keywordRules.Add(Ranged(new(location, Key(target), Key(payload), rule.Protocol, (short)rule.MinCount, (short)rule.MinLevel, rule.UseAll != 0)
                            { Inert = Fires(rule.Chance) is false }, rule));
                        break;
                    }
                    case "keyword-generated-list-swap":
                        if (inputs.RuntimeKeywords.Contains(Key(target)))
                        { unbounded.Add($"LLOS {location}: keyword {rule.References[0].Identifier} is distributed by KID at runtime."); break; }
                        if (IsList(payload)) generatedPayloads.Add(((rule.Protocol, true, Key(target)), Key(payload), location));
                        if (rule.MaxCount > short.MaxValue || rule.MaxLevel > short.MaxValue)
                        {
                            result.withheldKeywords.TryAdd(Key(target), $"LLOS {location}: count/level exceeds the supported entry range.");
                            result.generatedWithheld.Add((rule.Protocol, true, Key(target)));
                            continue;
                        }
                        generatedRules.Add((location, Key(target), true, Key(payload), (short)rule.MinCount, (short)rule.MaxLevel, rule.UseAll, rule.Protocol));
                        if (rule.MinLevel != rule.MaxLevel) result.rangedGroups.Add((rule.Protocol, true, Key(target)));
                        break;
                    case "direct-list-insert":
                        // The focus insert ignores Chance and UseAll; Count/Level ranges
                        // are drawn at startup.
                        RecordPayload(focusPayloads, Key(target), payload, location);
                        if (rule.MaxCount > short.MaxValue || rule.MaxLevel > short.MaxValue)
                        { withheldLists.TryAdd(Key(target), $"LLOS {location}: count/level exceeds the supported entry range."); continue; }
                        if (!result.focus.TryGetValue(Key(target), out var edits)) result.focus[Key(target)] = edits = new([], []);
                        edits.Inserts.Add(Ranged(new(location, Key(target), Key(payload), rule.Protocol, (short)rule.MinCount, (short)rule.MinLevel, rule.UseAll != 0),
                            rule with { Chance = 100f }));
                        break;
                    case "direct-list-single-remove" or "direct-list-multi-remove":
                        // Target names the list and Insert the form to remove. The
                        // removal honours UseAll and draws Chance per occurrence.
                        if (Fires(rule.Chance) is false) continue;
                        if (!result.focus.TryGetValue(Key(target), out var removalEdits)) result.focus[Key(target)] = removalEdits = new([], []);
                        if (!removalEdits.Removals.TryGetValue(Key(payload), out var removalRules)) removalEdits.Removals[Key(payload)] = removalRules = [];
                        removalRules.Add(new Insert(location, Key(target), Key(payload), rule.Protocol, 1, 1, rule.UseAll != 0) { Conditional = Fires(rule.Chance) is null });
                        result.hasUncertain |= Fires(rule.Chance) is null;
                        break;
                    default:
                    {
                        // Families not interpreted yet: a narrowed one withholds its target and payload, and the import
                        // stays enabled.
                        var why = $"LLOS {location}: protocol {rule.Protocol} ({rule.Family}) has an unanalysed footprint.";
                        if (NarrowProblem($"Target {target.Identifier}", rule.Protocol, [target.NativeType], payload) is { } problem)
                        { unbounded.Add($"{why} {problem}"); break; }
                        Narrow([Key(target)], payload, why);
                        result.Notes.Add($"{why} Target {Key(target)} is withheld: lists holding it keep plugin contents and refuse XEP output.{PayloadNote(payload)}");
                        break;
                    }
                }
            }
            foreach (var (file, rule) in uncertainRules)
            {
                var location = $"{file.Path} [{rule.Section}]";
                var target = rule.References[0];
                var payload = rule.References[1];
                var why = $"LLOS {location}: a reference XEP cannot bind ({string.Join(", ", rule.References.Where(Uncertain).Select(r => r.Status))}) may still apply natively.";
                // An ambiguous EditorID may bind to any record it names: the rule is narrowed to those candidates when
                // NarrowProblem allows. An unresolved target is skipped natively and contributes nothing. A resolved
                // target is here only for an uncertain payload, which has no bounded footprint, and any other uncertain
                // target (EditorID cache, MergeMapper, full load-order ID) may bind to any form: both fall back whole-run.
                if (target.Status == "ambiguous-editorid" && target.Candidates.Count > 0)
                {
                    if (NarrowProblem($"A candidate of EditorID {target.Identifier}", rule.Protocol, target.Candidates.Select(c => c.NativeType), payload) is { } problem)
                    { unbounded.Add($"{why} {problem}"); continue; }
                    var candidates = target.Candidates.Select(c => FormKey.Factory(c.FormKey)).ToArray();
                    Narrow(candidates, payload, why);
                    result.Notes.Add($"LLOS {location}: EditorID {target.Identifier} is ambiguous; candidates {string.Join(", ", candidates)} are withheld: "
                        + $"lists holding them keep plugin contents and refuse XEP output.{PayloadNote(payload)}");
                }
                else if (target.Status == "resolved") unbounded.Add($"{why} {UnboundPayload(payload)}");
                else if (Uncertain(target)) unbounded.Add(why);
            }
            Insert Ranged(Insert insert, LlosRule rule)
            {
                var ranged = insert with
                {
                    Conditional = Fires(rule.Chance) is null,
                    MaxCount = (short)rule.MaxCount, MaxLevel = (short)rule.MaxLevel
                };
                result.hasUncertain |= !ranged.Inert && (ranged.Conditional || ranged.RangedLevel);
                return ranged;
            }
            if (unbounded.Count > 0) return result.WithoutImport(unbounded);
            result.Generate(generatedRules, lists, inserts, keywordRules);
            foreach (var (key, why) in withheld) result.withheldTargets[key] = why;
            foreach (var group in inserts.GroupBy(i => i.Target)) result.byTarget[group.Key] = group.ToArray();
            foreach (var group in keywordRules.GroupBy(i => i.Target)) result.byKeyword[group.Key] = group.ToArray();
            // Leveled lists (generated ones included) that a rule may insert: such an insertion into an XEP-written list
            // would add a consumer edge XEP's isolation graph does not hold (ticket 25).
            result.listInserts = result.byTarget.Values.Concat(result.byKeyword.Values).SelectMany(r => r)
                .Concat(result.focus.Values.SelectMany(f => f.Inserts))
                .Where(r => !r.Inert && r.Inserts && lists.ContainsKey(r.Payload)).Select(r => r.Payload).ToHashSet();

            bool Matches(ILeveledItemGetter list) => (list.Entries ?? []).Any(e => e.Data?.Reference.FormKey is { } item
                && (result.byTarget.ContainsKey(item) || withheld.ContainsKey(item) || result.KeywordMatches(item)));
            foreach (var (key, list) in lists.ToArray())
            {
                if (list.IsDeleted || result.generated.ContainsKey(key)) continue;
                // Only an unverified order needs both possible input views.
                var skyPatcherBefore = skyPatcher?.Physical.GetValueOrDefault(key);
                var joint = skyPatcher is not null && (skyPatcherBefore is not null || skyPatcher.Problems.ContainsKey(key));
                if (!withheldLists.ContainsKey(key) && !result.focus.ContainsKey(key) && !Matches(list)
                    && !(skyPatcherBefore is not null && !result.SkyPatcherBeforeLlos && Matches(skyPatcherBefore))) continue;
                var entries = list.Entries ?? [];
                var swaps = new List<(FormKey Source, FormKey Predecessor)>();
                var run = result.Run(key, Project(entries), list.Flags, swaps);
                // An uninterpreted rule can also fire on entries LLOS itself adds.
                // Batch rules see original entries and focus insertions, never their
                // own insertions; an uninterpreted rule can fire on either.
                var batchInput = Project(entries).Concat(run.FocusAdded).ToList();
                var touchesWithheld = batchInput.Where(e => e.Reference.GeneratedOccurrence is null)
                    .Select(e => e.Reference.Existing).FirstOrDefault(withheld.ContainsKey);
                string? problem = withheldLists.GetValueOrDefault(key) ?? (touchesWithheld.IsNull ? null : withheld[touchesWithheld])
                    ?? result.Withheld(batchInput);
                var (replay, count, truncated, changes) = (run.Final, run.Added.Count, run.Truncated, run.Changed);
                var firesBefore = !result.SkyPatcherBeforeLlos && skyPatcherBefore is not null
                    && result.Run(key, Project(skyPatcherBefore.Entries ?? []), skyPatcherBefore.Flags).Changed;
                if (problem is null && result.hasUncertain)
                {
                    var possibleLow = result.Run(key, Project(entries), list.Flags, outcome: Outcome.PossibleLow).Final;
                    var possibleHigh = result.Run(key, Project(entries), list.Flags, outcome: Outcome.PossibleHigh).Final;
                    // A list that selects only from its highest eligible level lets a
                    // drawn lower level shadow the insertion behind other entries.
                    if (!list.Flags.HasFlag(LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer)
                        && !possibleLow.Select(e => (e.Reference.Existing, e.Level)).Order().SequenceEqual(possibleHigh.Select(e => (e.Reference.Existing, e.Level)).Order()))
                        problem = $"LLOS list {key}: a drawn insertion level can change which entries its highest-level selection reaches.";
                    // Entries a Chance draw may add or keep: the established view omits
                    // them. A list edge that may appear or disappear, or an authored entry
                    // that may survive a conditional removal, makes the list (and the
                    // linked list) runtime-controlled for checks that rely on absence. A
                    // possible item insertion alone only changes probability.
                    var differing = possibleHigh.Select(e => e.Reference.Existing).ToList();
                    var established = replay.Select(e => e.Reference.Existing).ToList();
                    foreach (var entry in replay) differing.Remove(entry.Reference.Existing);
                    foreach (var entry in possibleHigh) established.Remove(entry.Reference.Existing);
                    differing.AddRange(established);
                    var authored = batchInput.Select(e => e.Reference.Existing).ToHashSet();
                    differing.RemoveAll(form => !lists.ContainsKey(form) && !authored.Contains(form));
                    // Independently drawn focus levels reorder the entries batch rules
                    // visit, so a once-per-list rule may match a different occurrence.
                    var reordered = result.focus.TryGetValue(key, out var edits) && edits.Inserts.Count(r => r.RangedLevel) > 0
                        && batchInput.Any(e => (e.Reference.GeneratedOccurrence is null && result.byTarget.ContainsKey(e.Reference.Existing))
                            || result.KeywordsOf(e).Any(result.byKeyword.ContainsKey));
                    if (differing.Count > 0 || reordered)
                    {
                        result.ConditionalMembership[key] = $"LLOS Chance rules or drawn levels may change the entries of list {key} at startup.";
                        foreach (var child in differing.Where(lists.ContainsKey).Distinct())
                            result.ConditionalMembership.TryAdd(child, $"LLOS Chance rules may link or unlink list {child} from list {key} at startup.");
                    }
                }
                if (problem is null && !changes && !firesBefore) continue;
                if (problem is null && joint && !result.SkyPatcherBeforeLlos)
                    problem = $"LLOS and SkyPatcher both change list {key}; their native ordering is unverified.";
                if (problem is null && skyPatcher?.Problems.TryGetValue(key, out var skyProblem) == true)
                    problem = $"LLOS input list {key} has an unproved SkyPatcher change: {skyProblem}";
                if (problem is null && entries.Count > 255) problem = $"LLOS list {key} exceeds the native 255-entry traversal.";
                if (problem is null && entries.Any(e => e.Data is null)) problem = $"LLOS list {key} has an entry without DATA.";
                // Conditional insertions use native capacity when they fire: if they can
                // reach the cap, later rules on this list may be cut off.
                if (problem is null && result.hasUncertain && result.MayReachCap(key, Project(entries), list.Flags))
                    problem = $"LLOS list {key}: conditional insertions may reach the native 255-insertion cap, so later rules are not established.";
                result.Physical[key] = list;
                if (problem is not null) { result.Problems[key] = problem; continue; }
                var changed = list.DeepCopy();
                changed.Entries = new(replay.Select(e => e.Payload));
                result.effective[key] = changed;
                result.inserted[key] = count;
                if (swaps.Count > 0 && !truncated) result.replacementPeers[key] = [.. swaps];
                lists[key] = changed;
            }
            // Cycles can arise only through the combined insertions, so check the
            // effective graph once every list is changed.
            foreach (var key in result.effective.Keys.ToArray())
            {
                if (!result.AddsCycle(key, Project(result.effective[key].Entries ?? []), lists)) continue;
                result.Problems[key] = $"LLOS adds a list edge that forms a cycle through {key}.";
                lists[key] = result.Physical[key];
                result.effective.Remove(key);
                result.inserted.Remove(key);
                result.replacementPeers.Remove(key);
                result.ConditionalMembership.Remove(key);
            }
            result.WithholdHiddenPayloads(lists, skyPatcher, listPayloads, focusPayloads, generatedPayloads);
            if (result.SkyPatcherBeforeLlos && skyPatcherPresent)
                result.Notes.AddInformation("LLOS/SkyPatcher startup order: the captured DLLs were verified in game to apply SkyPatcher before LLOS; overlapping lists use that order.");
            foreach (var problem in result.Problems.Values.Distinct()) result.Notes.Add(problem + " Affected lists keep their plugin contents and refuse XEP output.");
            // Forms that may still exist in some list after LLOS's startup changes
            // (withheld lists keep plugin contents LLOS may still change), and forms
            // established in a list LLOS's effect is known for.
            HashSet<FormKey> Forms(IEnumerable<KeyValuePair<FormKey, ILeveledItemGetter>> source) => source
                .SelectMany(l => l.Value.Entries ?? []).Where(e => e.Data is not null).Select(e => e.Data!.Reference.FormKey).ToHashSet();
            result.mayRemain = Forms(lists);
            result.survivors = Forms(lists.Where(l => !result.Problems.ContainsKey(l.Key)));
            result.Notes.AddInformation($"LLOS effective input: {result.effective.Count} lists interpreted ({result.inserted.Values.Sum()} once-per-list insertions); {result.Problems.Count} lists withheld.");
            return result;
        }

        private LlosLists WithoutImport(IReadOnlyList<string> reasons)
        {
            var result = new LlosLists { ImportDisabled = true };
            const int shown = 10;
            result.Notes.Add($"LLOS import disabled for this run: {reasons.Count} input(s) XEP cannot analyse have an unbounded footprint. "
                + "Distribution uses plugins only, as if LLOS were absent; LLOS's own startup changes can still alter these results in game. "
                + "Triggers: " + string.Join(" ", reasons.Take(shown)) + (reasons.Count > shown ? $" (+{reasons.Count - shown} more)" : ""));
            return result;
        }

        // A leveled-list payload LLOS inserts into a withheld list, or into a generated group that is dropped, is an edge
        // XEP's consumer graph never sees: XEP could write variants into its subtree through an independent consumer, and
        // LLOS would hand them to the withheld list's consumers too. So the payload's whole subtree is withheld. Runs once
        // Problems is final. Linear: each withheld list's entries are read once, each matched form or keyword looks up its
        // rules once, each payload is seeded once and each list is expanded at most once.
        private void WithholdHiddenPayloads(Dictionary<FormKey, ILeveledItemGetter> lists, SkyPatcherLists? skyPatcher,
            Dictionary<FormKey, List<(FormKey Payload, string Location, bool UseAll)>> listPayloads,
            Dictionary<FormKey, List<(FormKey Payload, string Location, bool UseAll)>> focusPayloads,
            List<((ushort Protocol, bool Keyword, FormKey Target) Group, FormKey Payload, string Location)> generatedPayloads)
        {
            // A kept protocol 170 group is itself a list payload: its UseAll reinsertion rule puts it where its target was.
            foreach (var (key, group) in generated.Where(g => g.Value.Protocol == 170))
            {
                if (!listPayloads.TryGetValue(group.Target, out var recorded)) listPayloads[group.Target] = recorded = [];
                recorded.Add((key, $"generated {group.Identity}", true));
            }
            if (listPayloads.Count == 0 && focusPayloads.Count == 0 && generatedPayloads.Count == 0) return;
            // One reason per payload: the first rule found to insert it.
            var seeds = new Dictionary<FormKey, string>();
            foreach (var (group, payload, location) in generatedPayloads.Where(g => generatedWithheld.Contains(g.Group)))
                seeds.TryAdd(payload, $"LLOS {location}: list {payload} joins the withheld protocol {group.Protocol} generated group of {group.Target}, "
                    + "so its subtree gains consumers XEP cannot see.");
            static string Inserted(FormKey payload, string location, FormKey list)
                => $"LLOS {location}: list {payload} is inserted into withheld list {list}, so its subtree gains consumers XEP cannot see.";
            // A withheld list keeps its physical entries here, but LLOS's rules still fire on them at startup (batch rules
            // on its entries and focus insertions; include SkyPatcher's earlier
            // view only when the frameworks' order is not established).
            // Each form or keyword a batch rule may match is kept once per UseAll flag, with the first list holding it.
            var matched = new Dictionary<(FormKey Key, bool UseAllList), FormKey>();
            foreach (var (key, physical) in Physical.Where(p => !effective.ContainsKey(p.Key)))
            {
                foreach (var (payload, location, _) in focusPayloads.GetValueOrDefault(key) ?? [])
                    if (!seeds.ContainsKey(payload)) seeds[payload] = Inserted(payload, location, key);
                void Match(IEnumerable<FormKey> forms, LeveledItem.Flag flags)
                {
                    var useAllList = ((int)flags & 4) != 0;
                    foreach (var form in forms)
                    {
                        if (!matched.TryAdd((form, useAllList), key)) continue;
                        foreach (var keyword in keywordOrder.GetValueOrDefault(form) ?? []) matched.TryAdd((keyword, useAllList), key);
                    }
                }
                Match((physical.Entries ?? []).Select(e => e.Data?.Reference.FormKey ?? FormKey.Null)
                    .Concat(focus.GetValueOrDefault(key)?.Inserts.Select(i => i.Payload) ?? []), physical.Flags);
                if (!SkyPatcherBeforeLlos && skyPatcher?.Physical.GetValueOrDefault(key) is { } before)
                    Match((before.Entries ?? []).Select(e => e.Data?.Reference.FormKey ?? FormKey.Null), before.Flags);
            }
            // A batch rule without UseAll does not fire on a UseAll list (Apply).
            foreach (var ((form, useAllList), holder) in matched)
                foreach (var (payload, location, useAll) in listPayloads.GetValueOrDefault(form) ?? [])
                    if ((useAll || !useAllList) && !seeds.ContainsKey(payload)) seeds[payload] = Inserted(payload, location, holder);
            // The payload and every list LLOS may place below it; generated lists are never written, so they are only traversed.
            var pending = new Queue<(FormKey List, string Why)>(seeds.Select(s => (s.Key, s.Value)));
            var expanded = new HashSet<FormKey>();
            while (pending.TryDequeue(out var next))
            {
                if (!expanded.Add(next.List) || !lists.TryGetValue(next.List, out var list)) continue;
                if (!generated.ContainsKey(next.List)) Problems.TryAdd(next.List, next.Why);
                foreach (var child in MayHold(next.List, list, skyPatcher))
                    if (lists.ContainsKey(child)) pending.Enqueue((child, next.Why));
            }
        }

        // Every form LLOS may place in a list at startup, not only its established entries: the current (established or
        // plugin) entries, the entries LLOS reads (after SkyPatcher's edits, and SkyPatcher's earlier view), the focus
        // insertions on the list, and the payload of every batch rule that may fire on any of those entries or focus
        // insertions, conditional or not (generated-group reinsertions are batch rules too). Collected by indexed lookup, not
        // by replay: each distinct form and keyword is looked up once per UseAll gate, so the cost is linear in the entries
        // and the rules on them. A rule that never fires (Chance at or below zero) or only removes inserts nothing, and a rule
        // without UseAll skips a UseAll list (Apply). A generated list is never batch-processed.
        private HashSet<FormKey> MayHold(FormKey key, ILeveledItemGetter current, SkyPatcherLists? skyPatcher)
        {
            static IEnumerable<FormKey> Forms(ILeveledItemGetter list) => (list.Entries ?? []).Select(e => e.Data?.Reference.FormKey ?? FormKey.Null);
            var children = Forms(current).ToHashSet();
            if (generated.ContainsKey(key)) return children;
            var focusInserts = focus.GetValueOrDefault(key)?.Inserts.Select(i => i.Payload).ToArray() ?? [];
            children.UnionWith(focusInserts);
            var looked = new HashSet<(FormKey Key, bool UseAllList)>();
            void Payloads(IEnumerable<Insert> rules, bool useAllList)
            {
                foreach (var rule in rules)
                    if (!rule.Inert && rule.Inserts && (rule.UseAll || !useAllList)) children.Add(rule.Payload);
            }
            foreach (var input in new[] { Physical.GetValueOrDefault(key) ?? current,
                SkyPatcherBeforeLlos ? null : skyPatcher?.Physical.GetValueOrDefault(key) })
            {
                if (input is null) continue;
                var useAllList = ((int)input.Flags & 4) != 0;
                var forms = Forms(input).ToArray();
                children.UnionWith(forms);
                foreach (var form in forms.Concat(focusInserts))
                {
                    if (!looked.Add((form, useAllList))) continue;
                    Payloads(byTarget.GetValueOrDefault(form) ?? [], useAllList);
                    foreach (var keyword in keywordOrder.GetValueOrDefault(form) ?? [])
                        if (looked.Add((keyword, useAllList))) Payloads(byKeyword.GetValueOrDefault(keyword) ?? [], useAllList);
                }
            }
            return children;
        }

        // Manager::GenerateBatchMapLeveledList and Utility::GenerateLeveledList: one
        // list per target form or keyword from every 170 rule on it, built before the
        // batch pass (so batch rules never edit it), then swapped in by a UseAll
        // reinsertion rule appended after the INI rules for that target. Count/Level
        // are drawn once at INI read time: the established view takes the minimum
        // count and the latest level (the list draws from all levels).
        private void Generate(List<(string Location, FormKey Target, bool Keyword, FormKey Payload, short Count, short Level, byte UseAll, ushort Protocol)> rules,
            Dictionary<FormKey, ILeveledItemGetter> lists, List<Insert> inserts, List<Insert> keywordRules)
        {
            foreach (var rule in rules.Where(r => r.Protocol is 450 or 750 && generatedWithheld.Contains((r.Protocol, r.Keyword, r.Target)))) { withheldOutfitPayloads.UnionWith([rule.Payload, rule.Target]); if (rule.Protocol == 750) containerWithheld.Add(rule.Target); }
            rules = rules.Where(r => !generatedWithheld.Contains((r.Protocol, r.Keyword, r.Target))).ToList();
            if (rules.Count == 0) return;
            var used = new HashSet<uint>();
            // Identities are probed in a fixed order, so keys never depend on rule order.
            foreach (var group in rules.GroupBy(r => (r.Protocol, r.Keyword, r.Target)).OrderBy(g => g.Key.Protocol).ThenBy(g => g.Key.Keyword).ThenBy(g => g.Key.Target.ToString(), StringComparer.Ordinal))
            {
                var (protocol, keyword, target) = group.Key;
                var instruction = group.Aggregate(0, (bits, r) => bits | r.UseAll);
                var identity = $"llos-{protocol}|{(keyword ? "keyword" : "form")}|{target}";
                var id = GeneratedId(identity);
                while (!used.Add(id)) id = id >= 0xFFFFFF ? 0x800 : id + 1;
                var key = new FormKey(GeneratedMod, id);
                // Reinsert bits apply to form targets only; Once wins over EachTime.
                var once = !keyword && (instruction & 8) != 0;
                var eachTime = !keyword && !once && (instruction & 16) != 0;
                var payloads = group.ToList();
                var size = Math.Min(once ? payloads.Count + 1 : eachTime ? payloads.Count * 2 : payloads.Count, 255);
                var entries = new List<(FormKey Form, short Count, short Level)>();
                if (once || eachTime) entries.Add((target, 1, 1));
                foreach (var rule in payloads)
                {
                    if (entries.Count >= size) break;
                    entries.Add((rule.Payload, rule.Count, rule.Level));
                }
                while (entries.Count < size) entries.Add((target, 1, 1));
                lists[key] = new LeveledItem(key, SkyrimRelease.SkyrimSE)
                {
                    EditorID = $"XEP_LlosGenerated_{id:X6}",
                    Flags = LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer | LeveledItem.Flag.CalculateForEachItemInCount
                        | ((instruction & 4) != 0 ? LeveledItem.Flag.UseAll : 0),
                    Entries = new(entries.OrderBy(e => e.Level).Select(e => new LeveledItemEntry
                        { Data = new() { Reference = new FormLink<IItemGetter>(e.Form), Count = e.Count, Level = e.Level } }))
                };
                generated[key] = new(identity, target, keyword, instruction, group.Select(r => r.Location).ToList(), protocol);
                if (rangedGroups.Contains((protocol, keyword, target))) rangedGenerated.Add(key);
                // ProcessBatchOutfit swaps the target in every outfit; no list reinsertion.
                if (protocol == 450) { outfitSwaps[target] = key; continue; }
                // ProcessBatchContainerLite swaps the target in small containers and NPC inventories.
                if (protocol == 750) { containerSwaps[target] = key; continue; }
                // CountOne/LevelOne choose whether the swap keeps the target's count/level.
                var countOne = (instruction & 1) != 0;
                var levelOne = (instruction & 2) != 0;
                var reinsertion = (ushort)(countOne && levelOne ? 150 : levelOne ? 151 : countOne ? 152 : 153);
                (keyword ? keywordRules : inserts).Add(new($"LLOS generated {identity}", target, key, reinsertion, 1, 1, true));
            }
        }

        private static uint GeneratedId(string identity)
        {
            var hash = 2166136261u;
            foreach (var c in identity) { hash ^= c; hash *= 16777619u; }
            return Math.Max(0x800u, hash & 0xFFFFFF);
        }

        private static List<ProjectedStaticEntry> Project(IEnumerable<ILeveledItemEntryGetter> entries) => entries
            .Select(e => new ProjectedStaticEntry(e, new(e.Data?.Reference.FormKey ?? FormKey.Null))).ToList();

        // Manager::ProcessBatchLeveledList for once-per-list insertions: traverse
        // original entries, fire each rule once per list (UseAll lists only for
        // UseAll rules) and cap insertions so the list stays within 255 entries.
        // Manager::ProcessBatchLeveledList: traverse original entries in order;
        // each matching rule inserts and/or removes the occurrence (UseAll lists only
        // for UseAll rules); traversal stops matching once 255 insertions are
        // buffered. A list changed by any rule keeps its surviving entries plus the
        // insertions that fit in 255. Truncated insertions still change the list;
        // callers refuse them.
        private (List<ProjectedStaticEntry> Kept, List<ProjectedStaticEntry> Added, bool Changed, bool Truncated)
            Apply(List<ProjectedStaticEntry> entries, LeveledItem.Flag flags, List<(FormKey Source, FormKey Predecessor)>? swaps = null, bool unbounded = false,
                IReadOnlyCollection<ProjectedStaticEntry>? focusAdded = null, Outcome outcome = Outcome.Established)
        {
            var added = new List<ProjectedStaticEntry>();
            if (byTarget.Count == 0 && byKeyword.Count == 0) return (entries, added, false, false);
            var useAllList = ((int)flags & 4) != 0;
            var fired = new HashSet<Insert>();
            var kept = new List<ProjectedStaticEntry>();
            var changed = false;
            var ongoing = true;
            // Native capacity used; a conditional once-per-list rule may fire at any
            // match in a possible outcome but inserts at most once.
            var capacity = 0;
            var onceCapacity = new HashSet<Insert>();
            foreach (var entry in unbounded ? entries : entries.Take(255))
            {
                var keep = true;
                var inserted = 0;
                ProjectedStaticEntry? slotCandidate = null;
                void Fire(Insert rule)
                {
                    if (rule.Inert || (useAllList && !rule.UseAll) || (rule.Once && fired.Contains(rule))) return;
                    if (rule.Conditional)
                    {
                        // A failed draw leaves the counter unused, so a once-per-list rule
                        // may still fire at any later match: never mark it fired.
                        if (outcome == Outcome.Established)
                        {
                            // A possible insertion makes this occurrence no 1:1 slot.
                            if (rule.Inserts) inserted += 2;
                            if (!rule.Removes) return;
                            changed = true;
                            keep = false; // the occurrence may be removed: not established
                            return;
                        }
                        if (!rule.Inserts) return;
                        changed = true;
                        added.Add(new(rule.Entry(entry, outcome), new(rule.Payload)));
                        if (!rule.Once || onceCapacity.Add(rule)) capacity++;
                        inserted += 2; // never slot evidence
                        if (capacity >= 255 && !unbounded) ongoing = false;
                        return;
                    }
                    changed = true;
                    if (rule.Inserts)
                    {
                        added.Add(new(rule.Entry(entry, outcome), new(rule.Payload)));
                        capacity++;
                        inserted++;
                        slotCandidate = rule.Removes && !rule.RangedLevel && !rule.RangedCount ? added[^1] : null;
                    }
                    if (rule.Removes) keep = false;
                    if (rule.Once) fired.Add(rule);
                    if (capacity >= 255 && !unbounded) ongoing = false;
                }
                // Native visits only the first 255 rules on a target (Manager.cpp batch loop).
                if (ongoing && entry.Reference.GeneratedOccurrence is null && byTarget.TryGetValue(entry.Reference.Existing, out var rules))
                    foreach (var rule in rules.Take(255))
                    {
                        if (!ongoing) break;
                        Fire(rule);
                    }
                // Keyword maps follow, per keyword in the form's order, bounded by the
                // remaining insertion capacity (Manager.cpp keyword loop).
                if (ongoing && byKeyword.Count > 0)
                    foreach (var keyword in KeywordsOf(entry))
                    {
                        if (!ongoing) break;
                        if (!byKeyword.TryGetValue(keyword, out var keywordRules)) continue;
                        var size = unbounded ? keywordRules.Length : Math.Min(Math.Max(0, 255 - capacity), keywordRules.Length);
                        for (var y = 0; y < size && ongoing; y++) Fire(keywordRules[y]);
                    }
                // Slot evidence: one swap replaced this unit occurrence in place
                // (same level and unit count), and nothing else was inserted for it.
                // A focus insertion is no authored predecessor slot.
                if (swaps is not null && !keep && inserted == 1 && slotCandidate is { } replacement && added.Count <= 255
                    && focusAdded?.Contains(entry) != true
                    && entry.Count == 1 && entry.HasData && !entry.HasExtraData
                    && replacement.Count == 1 && replacement.Level == entry.Level)
                    swaps.Add((replacement.Reference.Existing, entry.Reference.Existing));
                if (keep) kept.Add(entry);
            }
            if (!changed) return (entries, added, false, false);
            if (unbounded) return (kept, added, true, false);
            var allowed = Math.Max(0, 255 - kept.Count);
            // Reaching the insertion cap in a possible outcome stops later matches.
            if (outcome != Outcome.Established && capacity >= 255) return (kept, added, true, true);
            return (kept, capacity > allowed ? added.Take(allowed).ToList() : added, true, capacity > allowed);
        }

        // LLOS's DataLoaded order for one list: focus edits on the named list, then
        // batch rules over the result (so batch rules also match focus additions).
        // Each changed stage drops extra data and sorts by level; an unchanged list
        // is returned as is.
        private (List<ProjectedStaticEntry> Final, List<ProjectedStaticEntry> Added, bool Changed, bool Truncated, bool Removed, List<ProjectedStaticEntry> FocusAdded)
            Run(FormKey list, List<ProjectedStaticEntry> entries, LeveledItem.Flag flags, List<(FormKey Source, FormKey Predecessor)>? swaps = null,
                bool unbounded = false, Outcome outcome = Outcome.Established)
        {
            var current = entries;
            var focusAdded = new List<ProjectedStaticEntry>();
            var focusChanged = false;
            var removed = false;
            var truncated = false;
            if (focus.TryGetValue(list, out var edits))
            {
                var useAllList = ((int)flags & 4) != 0;
                var fired = new HashSet<Insert>();
                var kept = new List<ProjectedStaticEntry>();
                foreach (var entry in unbounded ? entries : entries.Take(255))
                {
                    var keep = true;
                    if (entry.Reference.GeneratedOccurrence is null && edits.Removals.TryGetValue(entry.Reference.Existing, out var removals))
                        foreach (var rule in removals)
                        {
                            if (!keep) break;
                            if ((useAllList && !rule.UseAll) || (rule.Once && fired.Contains(rule))) continue;
                            // A conditional removal is applied when establishing and
                            // never when checking possible additions.
                            if (rule.Conditional && outcome != Outcome.Established) continue;
                            keep = false;
                            if (rule.Once && !rule.Conditional) fired.Add(rule);
                        }
                    if (keep) kept.Add(entry);
                    else removed = true;
                }
                // Focus insertions ignore UseAll and Chance; they fill the remaining capacity in order.
                var limit = unbounded ? edits.Inserts.Count : Math.Max(0, Math.Min(edits.Inserts.Count, 255 - kept.Count));
                truncated = edits.Inserts.Count > limit;
                focusAdded = edits.Inserts.Take(limit).Select(rule => new ProjectedStaticEntry(new LeveledItemEntry
                    { Data = new() { Reference = new FormLink<IItemGetter>(rule.Payload), Count = rule.Count,
                        Level = outcome == Outcome.PossibleLow ? rule.Level : rule.MaxLevel } }, new(rule.Payload))).ToList();
                if (removed || limit > 0)
                {
                    current = kept.Select(Sanitized).Concat(focusAdded).OrderBy(e => e.Level ?? 0).ToList();
                    focusChanged = true;
                }
            }
            var (batchKept, batchAdded, batchChanged, batchCut) = Apply(current, flags, swaps, unbounded, focusAdded, outcome);
            removed |= batchChanged && batchKept.Count != (unbounded ? current.Count : Math.Min(current.Count, 255));
            var final = batchChanged ? batchKept.Select(Sanitized).Concat(batchAdded).OrderBy(e => e.Level ?? 0).ToList() : current;
            return (final, [.. focusAdded, .. batchAdded], focusChanged || batchChanged, truncated || batchCut, removed, focusAdded);
        }

        private static ProjectedStaticEntry Sanitized(ProjectedStaticEntry entry)
        {
            if (!entry.HasExtraData) return entry;
            var payload = entry.Payload;
            payload.ExtraData = null;
            return new(payload, entry.Reference);
        }

        private bool AddsCycle(FormKey list, List<ProjectedStaticEntry> replay, Dictionary<FormKey, ILeveledItemGetter> lists)
        {
            var pending = new Stack<FormKey>(replay.Select(e => e.Reference.Existing).Where(lists.ContainsKey).Distinct());
            var seen = new HashSet<FormKey>();
            while (pending.TryPop(out var key))
            {
                if (key == list) return true;
                if (!seen.Add(key)) continue;
                foreach (var child in lists[key].Entries ?? [])
                    if (child.Data?.Reference.FormKey is { } next && lists.ContainsKey(next)) pending.Push(next);
            }
            return false;
        }

        private static bool Same(ProjectedStaticEntry a, ProjectedStaticEntry b)
            => a.Reference == b.Reference && a.Level == b.Level && a.Count == b.Count && a.HasExtraData == b.HasExtraData;

        // Remove one matching entry for each of `remove` from `from`; false if any is absent.
        private static bool Subtract(List<ProjectedStaticEntry> from, IEnumerable<ProjectedStaticEntry> remove)
        {
            foreach (var entry in remove)
            {
                var index = from.FindIndex(e => Same(e, entry));
                if (index < 0) return false;
                from.RemoveAt(index);
            }
            return true;
        }

        private bool MayReachCap(FormKey list, List<ProjectedStaticEntry> entries, LeveledItem.Flag flags)
        {
            if (!hasUncertain) return false;
            var (_, added, _, truncated, _, _) = Run(list, entries, flags, outcome: Outcome.PossibleHigh);
            return truncated;
        }

        // XEP's own entries must not open new conditional firings: a Chance rule
        // matching a variant (through its source's keywords) would change the written
        // list in some launches only.
        private string? ConditionalOnVariants(FormKey list, IEnumerable<ProjectedStaticEntry> written, LeveledItem.Flag flags)
        {
            if (!hasUncertain) return null;
            var variants = written.Where(e => e.Reference.GeneratedOccurrence is >= 0).ToList();
            if (variants.Count == 0) return null;
            var possible = Run(list, variants, flags, unbounded: true, outcome: Outcome.PossibleHigh);
            var established = Run(list, variants, flags, unbounded: true);
            var extra = possible.Added.Select(e => e.Reference.Existing).ToList();
            foreach (var entry in established.Added) extra.Remove(entry.Reference.Existing);
            return extra.Count > 0 || possible.Removed != established.Removed
                ? "a conditional LLOS rule may fire on an XEP variant at startup" : null;
        }

        // LLOS's startup result for a written list, node by node as the writer splits it. The writer splits a list
        // longer than 255 entries into a root and level sublists (OverflowLists.Plan, sublists inherit the root's
        // flags); LLOS then processes the root (with its focus edits) and each sublist separately. Each node holds its
        // physical leaves (Input) and LLOS's result for them (Final); RootEntries counts the root's startup entries,
        // branch references included. Null when LLOS's cap truncates a node or may in a possible outcome, a sublist
        // would be emptied, or a sublist would receive another level (its branch threshold would no longer match).
        // Always the bounded, native-equivalent replay: the unbounded one ignores native limits (255 rules per target or
        // keyword, the insertion cap) and serves only to infer LLOS's insertions (Codex review round 2).
        private sealed record TreeResult(List<(List<ProjectedStaticEntry> Input, List<ProjectedStaticEntry> Final)> Nodes, int RootEntries)
        {
            public List<ProjectedStaticEntry> Leaves => Nodes.SelectMany(n => n.Final).ToList();
        }
        private TreeResult? TreeRun(FormKey root, List<ProjectedStaticEntry> physical, LeveledItem.Flag flags)
        {
            if (physical.Count <= 255)
            {
                var (final, _, _, truncated, _, _) = Run(root, physical, flags);
                return truncated || MayReachCap(root, physical, flags) ? null : new([(physical, final)], final.Count);
            }
            OverflowLists.Node<ProjectedStaticEntry> tree;
            try { tree = OverflowLists.Plan(physical, static e => e.Level); }
            catch (InvalidDataException) { return null; }
            var nodes = new List<(List<ProjectedStaticEntry> Input, List<ProjectedStaticEntry> Final)>();
            var rootEntries = 0;
            var marker = 0;
            // Branch references occupy slots but never match a rule.
            List<ProjectedStaticEntry> Entries(OverflowLists.Node<ProjectedStaticEntry> node) => node.Leaves.Concat(node.Branches.Select(b => new ProjectedStaticEntry(
                new LeveledItemEntry { Data = new() { Reference = new FormLink<IItemGetter>(FormKey.Null), Level = b.Level, Count = 1 } },
                new(FormKey.Null, --marker)))).ToList();
            bool Visit(OverflowLists.Node<ProjectedStaticEntry> node, FormKey key, short? branchLevel)
            {
                var (final, _, _, truncated, _, _) = Run(key, Entries(node), flags);
                if (truncated) return false;
                if (branchLevel is null) rootEntries = final.Count;
                var nodeLeaves = final.Where(e => e.Reference.GeneratedOccurrence is not < 0).ToList();
                // An emptied sublist would leave its branch selecting nothing.
                if (branchLevel is { } level && (nodeLeaves.Count == 0 || nodeLeaves.Any(e => e.Level != level))) return false;
                nodes.Add((node.Leaves, nodeLeaves));
                return node.Branches.All(b => Visit(b.Child, FormKey.Null, b.Level));
            }
            if (!Visit(tree, root, null)) return null;
            // Conditional or drawn additions may land anywhere they could fire: no
            // possible outcome may put a different level into a sublist.
            if (!hasUncertain) return new(nodes, rootEntries);
            foreach (var outcome in new[] { Outcome.PossibleLow, Outcome.PossibleHigh })
            {
                bool Possible(OverflowLists.Node<ProjectedStaticEntry> node, FormKey key, short? branchLevel)
                {
                    var (final, _, _, truncated, _, _) = Run(key, Entries(node), flags, outcome: outcome);
                    if (truncated) return false;
                    if (branchLevel is { } level && final.Any(e => e.Reference.GeneratedOccurrence is not < 0 && e.Level != level)) return false;
                    return node.Branches.All(b => Possible(b.Child, FormKey.Null, b.Level));
                }
                if (!Possible(tree, root, null)) return null;
            }
            return new(nodes, rootEntries);
        }

        // The written list replays exactly to the planned entries.
        private bool TreeReplays(FormKey root, List<ProjectedStaticEntry> physical, LeveledItem.Flag flags, ImmutableArray<ProjectedStaticEntry> desired)
            => TreeRun(root, physical, flags)?.Leaves is { } leaves && leaves.Count == desired.Length && Subtract(leaves, desired);

        public ILeveledItemGetter? BaselineFor(FormKey list) => effective.GetValueOrDefault(list);

        // A rewritten UseAll bit changes which rules fire on the list.
        public bool PreservesUseAll(FormKey target, LeveledItem.Flag flags)
            => !Changes(target) || (((int)flags ^ (int)Physical[target].Flags) & 4) == 0;

        // Original-list override: authored physical entries plus the XEP delta.
        // LLOS replays its insertions at startup; prove that replay yields exactly
        // the planned effective entries, or else a known result KnownChange accepts.
        public ImmutableArray<ProjectedStaticEntry> ForWriting(FormKey target, ImmutableArray<ProjectedStaticEntry> desired, ILeveledItemGetter? written = null)
        {
            InvalidDataException Refuse(string reason) => new($"LLOS target {target}: {reason}. No output was installed.");
            if (IsGenerated(target)) throw Refuse("LLOS creates this list at startup, so XEP cannot write it");
            if (Problems.TryGetValue(target, out var problem)) throw Refuse("unsupported input prevents proving output composition (" + problem + ")");
            if (Withheld(desired) is { } why) throw Refuse("XEP output would add an entry matched by an uninterpreted rule (" + why + ")");
            if (ConditionalOnVariants(target, desired, written?.Flags ?? Physical.GetValueOrDefault(target)?.Flags ?? 0) is { } conditional) throw Refuse(conditional);
            if (!Physical.TryGetValue(target, out var baseline))
            {
                // An untouched list starts matching if XEP adds a target entry.
                if (TreeReplays(target, desired.ToList(), written?.Flags ?? 0, desired)) return NoGenerated(desired, Refuse);
                return KnownChange(target, written?.EditorID, desired.ToList(), written?.Flags ?? 0, desired, Refuse,
                    "XEP output would add an LLOS target entry to this list");
            }
            if (written is not null && !PreservesUseAll(target, written.Flags)) throw Refuse("XEP would change the UseAll flag LLOS rules depend on");
            var additions = desired.ToList();
            // XEP restructured the list (material family or context clone): the
            // written record is then processed by LLOS like a generated list.
            if (!Subtract(additions, Project(effective[target].Entries ?? [])))
                return WrittenByLlos(target, written?.EditorID ?? baseline.EditorID, desired, written?.Flags ?? baseline.Flags, Refuse);
            var physical = Project(baseline.Entries ?? []);
            physical.AddRange(additions);
            if (TreeReplays(target, physical, written?.Flags ?? baseline.Flags, desired)) return NoGenerated(physical.ToImmutableArray(), Refuse);
            return KnownChange(target, written?.EditorID ?? baseline.EditorID, physical, written?.Flags ?? baseline.Flags, desired, Refuse,
                "native replay would change planned output");
        }

        private ImmutableArray<ProjectedStaticEntry> NoGenerated(ImmutableArray<ProjectedStaticEntry> physical, Func<string, InvalidDataException> refuse)
            => generated.Count > 0 && physical.Any(e => e.Reference.GeneratedOccurrence is null && IsGenerated(e.Reference.Existing))
                ? throw refuse("the written list would reference a list LLOS creates at startup") : physical;

        // XEP-generated lists are processed by LLOS too. Copies of changed lists
        // carry the effective insertions; subtract them and prove the replay.
        public (ImmutableArray<ProjectedStaticEntry> Effective, ImmutableArray<ProjectedStaticEntry> Physical)
            ProjectGenerated(ILeveledItemGetter list, ImmutableArray<ProjectedStaticEntry> desired)
        {
            InvalidDataException Refuse(string why) => new($"LLOS generated list {list.EditorID} [{list.FormKey}]: {why}. No output was installed.");
            if (IsGenerated(list.FormKey)) throw Refuse("LLOS creates this list at startup, so XEP cannot write it");
            if (Withheld(desired) is { } why) throw Refuse("an entry is matched by an uninterpreted rule (" + why + ")");
            if (ConditionalOnVariants(list.FormKey, desired, list.Flags) is { } conditional) throw Refuse(conditional);
            return (desired, WrittenByLlos(list.FormKey, list.EditorID, desired, list.Flags, Refuse));
        }

        // A written list LLOS processes at startup: omit the insertions it will
        // make and prove its replay reproduces the planned entries. Where no
        // physical list replays to the plan, KnownChange writes the plan and
        // accepts LLOS's exact result.
        private ImmutableArray<ProjectedStaticEntry> WrittenByLlos(FormKey list, string? name, ImmutableArray<ProjectedStaticEntry> desired, LeveledItem.Flag flags,
            Func<string, InvalidDataException> refuse)
        {
            // Infer LLOS's own entries from the planned content, ignoring the native
            // traversal cut-off: the writer may split this list into overflow nodes.
            var (_, inserts, changed, _, removes, _) = Run(list, desired.ToList(), flags, unbounded: true);
            if (!changed)
            {
                // A copied generated list is written back as its target; LLOS swaps it again.
                var unswapped = Unswap(desired.ToList());
                var restored = !unswapped.SequenceEqual(desired);
                if (!TreeReplays(list, restored ? unswapped.ToList() : desired.ToList(), flags, desired)) throw refuse("native replay would change planned output");
                return restored ? NoGenerated(unswapped.ToImmutableArray(), refuse) : NoGenerated(desired, refuse);
            }
            // The plan may already hold LLOS's insertions (a copy of a changed list): omit
            // each one it holds, so LLOS adds it once. A payload that is itself a target
            // can make this inference fire extra rules; the replay check decides.
            var physical = desired.ToList();
            foreach (var insert in inserts)
            {
                var index = physical.FindIndex(e => Same(e, insert));
                if (index >= 0) physical.RemoveAt(index);
            }
            physical = Unswap(physical);
            if (!physical.Any(e => e.HasExtraData) && TreeReplays(list, physical, flags, desired)) return NoGenerated(physical.ToImmutableArray(), refuse);
            // The inference failed: the trimmed candidate is not the plan. Write the full planned content (unswapped)
            // and accept LLOS's result for it (Codex review round 3).
            return KnownChange(list, name, Unswap(desired.ToList()), flags, desired, refuse,
                removes ? "LLOS would remove a planned entry at startup" : "LLOS would insert entries this planned list does not contain");
        }

        // Ticket 25 (user decision, 16 October 2026): XEP proves its own content; what a deterministic LLOS rule does to
        // a written list at startup is the LLOS author's call. When every rule that may fire on the list is
        // deterministic, XEP writes the physical list and accepts LLOS's exact result even where it differs from the
        // plan, and reports the difference. The result is LLOS's replay of each node as the writer splits the list.
        // XEP's invariants still bind that result: LLOS may not insert a leveled list (a consumer edge XEP's isolation
        // graph does not hold) or an entry an uninterpreted rule matches; a removed planned form must remain in no list
        // after LLOS's startup changes (the change applies everywhere); a swap's payload must survive in a list whose
        // LLOS effect is known; and the startup root plus every entry XEP appends later (Script insertions, a Revert
        // selector) must stay within the native 255-entry cap. `unknown` names the refusal when the result cannot be
        // accepted.
        private ImmutableArray<ProjectedStaticEntry> KnownChange(FormKey list, string? name, List<ProjectedStaticEntry> physical, LeveledItem.Flag flags,
            ImmutableArray<ProjectedStaticEntry> desired, Func<string, InvalidDataException> refuse, string unknown)
        {
            InvalidDataException Refuse(string why) => refuse($"{unknown} ({why})");
            if (UncertainChange(list, physical, flags) is { } uncertain) throw Refuse(uncertain);
            // LLOS drops entry data from every list it changes.
            if (desired.Any(e => e.HasExtraData)) throw Refuse("LLOS would drop the entry data a planned entry holds");
            if (TreeRun(list, physical, flags) is not { } tree) throw refuse(unknown);
            // Entries XEP appends to this record after the proof: AddForm at runtime, or a Revert selector the writer
            // appends to the physical root (LLOS then sees it, and would cut its insertions at the cap).
            var appends = laterAppends?.Invoke(list) ?? 0;
            if (appends > 0 && tree.RootEntries + appends > 255)
                throw Refuse($"LLOS's startup changes give the list {tree.RootEntries} entries, and XEP appends {appends} more later "
                    + "(Script insertions or a Revert selector), past the native 255-entry cap");
            var final = tree.Leaves;
            var written = NoGenerated(physical.ToImmutableArray(), refuse);
            // Multiset difference between the plan and LLOS's result.
            static (StaticEntryReference, short?, short, bool) Key(ProjectedStaticEntry e) => (e.Reference, e.Level, e.Count, e.HasExtraData);
            var planned = new Dictionary<(StaticEntryReference, short?, short, bool), int>();
            foreach (var entry in desired) planned[Key(entry)] = planned.GetValueOrDefault(Key(entry)) + 1;
            var inserted = new List<ProjectedStaticEntry>();
            foreach (var entry in final)
                if (planned.GetValueOrDefault(Key(entry)) is var count and > 0) planned[Key(entry)] = count - 1;
                else inserted.Add(entry);
            var removed = new List<ProjectedStaticEntry>();
            foreach (var entry in desired)
                if (planned.GetValueOrDefault(Key(entry)) is var count and > 0) { planned[Key(entry)] = count - 1; removed.Add(entry); }
            if (inserted.FirstOrDefault(e => e.Reference.GeneratedOccurrence is null && listInserts.Contains(e.Reference.Existing)) is { } edge)
                throw Refuse($"LLOS would insert leveled list {edge.Reference.Existing}, a consumer edge XEP's isolation graph does not hold");
            if (Withheld(inserted) is { } withheld) throw Refuse("LLOS would insert an entry matched by an uninterpreted rule (" + withheld + ")");
            // A split list is reported per node: how many of its nodes LLOS changes this way.
            string Where(StaticEntryReference reference, bool gains) => tree.Nodes.Count == 1 ? ""
                : $", in {tree.Nodes.Count(n => gains ? n.Final.Count(e => e.Reference == reference) > n.Input.Count(e => e.Reference == reference)
                    : n.Final.Count(e => e.Reference == reference) < n.Input.Count(e => e.Reference == reference))} of its {tree.Nodes.Count} nodes";
            var clauses = new List<(string Kind, string Text, string Where)>();
            foreach (var entry in removed)
            {
                if (entry.Reference.GeneratedOccurrence is null && mayRemain.Contains(entry.Reference.Existing))
                    throw Refuse($"planned entry {entry.Reference.Existing} remains in another list after LLOS's startup changes, so LLOS's change does not apply everywhere");
                var index = RulesOn(list, entry, flags).Where(r => r.Removes && r.Inserts)
                    .Select(r => inserted.FindIndex(e => e.Reference.GeneratedOccurrence is null && e.Reference.Existing == r.Payload)).FirstOrDefault(i => i >= 0, -1);
                if (index < 0) { clauses.Add(("removes", $"removes {Describe(entry)} from", Where(entry.Reference, false))); continue; }
                var payload = inserted[index].Reference.Existing;
                inserted.RemoveAt(index);
                if (!survivors.Contains(payload))
                    throw Refuse($"swap payload {payload} survives in no list whose LLOS effect XEP knows");
                clauses.Add(("swaps", $"swaps {Describe(entry)} for {payload} in", Where(entry.Reference, false)));
            }
            clauses.AddRange(inserted.Select(e => ("inserts", $"inserts {Describe(e)} into", Where(e.Reference, true))));
            var distinct = clauses.Distinct().ToList();
            if (distinct.Count > 0 && startupChanges is { } sink)
            {
                // One note per list: a sentence per distinct change. A new XEP list gets its record key only when written.
                var label = name is not { Length: > 0 } ? list.ToString() : list.ModKey == FallbackPlanningMod ? name : $"{name} [{list}]";
                sink[list] = new(string.Join(" ", distinct.Select(c => $"LLOS {c.Text} list {label} at startup{c.Where}.")),
                    distinct.Count(c => c.Kind == "swaps"), distinct.Count(c => c.Kind == "removes"), distinct.Count(c => c.Kind == "inserts"));
            }
            return written;
        }

        // Rules that may fire on one entry of the list: batch rules (explicit, then keyword; native visits only the first
        // 255 rules per target and per keyword, inert ones included, as in Apply) and, unless batchOnly, focus removals
        // naming it.
        private IEnumerable<Insert> RulesOn(FormKey list, ProjectedStaticEntry entry, LeveledItem.Flag flags, bool batchOnly = false)
        {
            var useAllList = ((int)flags & 4) != 0;
            var existing = entry.Reference.GeneratedOccurrence is null;
            var rules = (existing ? (byTarget.GetValueOrDefault(entry.Reference.Existing) ?? []).Take(255) : [])
                .Concat(KeywordsOf(entry).SelectMany(k => (byKeyword.GetValueOrDefault(k) ?? []).Take(255)));
            if (existing && !batchOnly && focus.TryGetValue(list, out var edits) && edits.Removals.TryGetValue(entry.Reference.Existing, out var removals))
                rules = rules.Concat(removals);
            return rules.Where(r => !r.Inert && (r.UseAll || !useAllList));
        }

        // Why LLOS's result for the physical list is not known offline, or null when every rule that may fire is
        // deterministic. An unverified SkyPatcher order, a Chance draw or a drawn count or level
        // changes the result per launch. Checked in native order (Codex review rounds 2 and 3): focus edits apply to the
        // named record only, which is the root node when the writer splits the list. A deterministic focus removal deletes
        // its entry before batch processing, so that entry's batch rules are never visited; a conditional one is itself
        // uncertain. Batch and keyword rules are then checked on the entries batch processing visits: the surviving
        // physical entries of every node and the root's focus insertions. Indexed lookups per entry; only lists LLOS
        // changes get here.
        private string? UncertainChange(FormKey list, List<ProjectedStaticEntry> physical, LeveledItem.Flag flags)
        {
            if (skyPatcherInputs && !SkyPatcherBeforeLlos) return "SkyPatcher leveled-list inputs are present, and their native ordering with LLOS is unverified";
            static string? Problem(IEnumerable<Insert> rules)
            {
                foreach (var rule in rules)
                {
                    if (rule.Conditional) return "a conditional LLOS rule may fire on it at startup";
                    if (rule.Inserts && (rule.RangedLevel || rule.RangedCount)) return "an LLOS insertion on it draws its count or level at startup";
                }
                return null;
            }
            var visited = physical.AsEnumerable();
            if (focus.TryGetValue(list, out var edits))
            {
                if (edits.Inserts.Any(r => r.RangedLevel || r.RangedCount)) return "an LLOS focus insertion draws its count or level at startup";
                // The root node's leaves, in the order native visits them. An unsplittable list is refused by TreeRun.
                List<ProjectedStaticEntry> root;
                if (physical.Count <= 255) root = physical;
                else
                    try { root = OverflowLists.Plan(physical, static e => e.Level).Leaves; }
                    catch (InvalidDataException) { return null; }
                // Run's focus pass: the first 255 entries; a once-per-list removal fires at its first match.
                var useAllList = ((int)flags & 4) != 0;
                var fired = new HashSet<Insert>();
                var removed = new HashSet<ProjectedStaticEntry>();
                foreach (var entry in root.Take(255))
                {
                    if (entry.Reference.GeneratedOccurrence is not null || !edits.Removals.TryGetValue(entry.Reference.Existing, out var removals)) continue;
                    foreach (var rule in removals)
                    {
                        if ((useAllList && !rule.UseAll) || (rule.Once && fired.Contains(rule))) continue;
                        if (rule.Conditional) return "a conditional LLOS rule may fire on it at startup";
                        if (rule.Once) fired.Add(rule);
                        removed.Add(entry);
                        break;
                    }
                }
                visited = physical.Where(e => !removed.Contains(e)).Concat(edits.Inserts.Select(r => new ProjectedStaticEntry(new LeveledItemEntry
                    { Data = new() { Reference = new FormLink<IItemGetter>(r.Payload), Count = r.Count, Level = r.Level } }, new(r.Payload))));
            }
            foreach (var entry in visited)
                if (Problem(RulesOn(list, entry, flags, batchOnly: true)) is { } problem) return problem;
            return null;
        }

        // A form, or the XEP variant of its source.
        private static string Describe(ProjectedStaticEntry entry) => entry.Reference.GeneratedOccurrence is { } occurrence && occurrence >= 0
            ? $"the XEP variant of {generatedSources?.Invoke(occurrence).ToString() ?? "its source"}" : entry.Reference.Existing.ToString();

        // Forms that may still exist in some list after LLOS's startup changes, and forms established in a list whose
        // LLOS effect is known (Read).
        private HashSet<FormKey> survivors = [], mayRemain = [], listInserts = [];

        // A form-target generated list in written content becomes its target again:
        // each reinsertion protocol keeps exactly the count and level it does not force
        // to 1, so T at G's own count and level replays to the same G. A keyword-target
        // list has no single physical form; NoGenerated refuses it.
        private List<ProjectedStaticEntry> Unswap(List<ProjectedStaticEntry> entries)
        {
            if (generated.Count == 0) return entries;
            return entries.Select(e => e.Reference.GeneratedOccurrence is null && CanUnswap(e.Reference.Existing) && generated[e.Reference.Existing] is var group
                ? new ProjectedStaticEntry(new LeveledItemEntry { Data = new()
                    { Reference = new FormLink<IItemGetter>(group.Target), Count = e.Count, Level = e.Level ?? 1 } }, new(group.Target))
                : e).ToList();
        }

        private string? Withheld(IEnumerable<ProjectedStaticEntry> entries)
            => entries.Select(e => (e.Reference.GeneratedOccurrence is null ? withheldTargets.GetValueOrDefault(e.Reference.Existing) : null)
                ?? KeywordsOf(e).Select(k => withheldKeywords.GetValueOrDefault(k)).FirstOrDefault(w => w is not null)).FirstOrDefault(w => w is not null);

        public string? WritingProblem(FormKey target, ImmutableArray<ProjectedStaticEntry> desired, ILeveledItemGetter? written = null)
        {
            try { ForWriting(target, desired, written); return null; }
            catch (InvalidDataException ex) { return ex.Message.Replace(" No output was installed.", "", StringComparison.Ordinal); }
        }

        public string? GeneratedProblem(ILeveledItemGetter list, ImmutableArray<ProjectedStaticEntry> desired)
        {
            try { ProjectGenerated(list, desired); return null; }
            catch (InvalidDataException ex) { return ex.Message.Replace(" No output was installed.", "", StringComparison.Ordinal); }
        }
    }
}
